using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using xISOBurn.Util;

namespace xISOBurn.Scsi
{
    /// <summary>An optical drive seen by Windows (internal, USB or any other bus).</summary>
    internal sealed class DriveEntry
    {
        /// <summary>"E:" (what OpticalDrive.OpenAsync takes).</summary>
        public string Id;
        public string Vendor = "", Product = "", Revision = "", Bus = "";
        public string VolumeLabel = "";

        public string Model { get { return (Vendor + " " + Product).Trim(); } }

        public override string ToString()
        {
            var sb = new StringBuilder(Id);
            if (Model.Length > 0) sb.Append("  ").Append(Model);
            if (Revision.Length > 0) sb.Append(" (").Append(Revision).Append(')');
            if (Bus.Length > 0) sb.Append("  [").Append(Bus).Append(']');
            if (VolumeLabel.Length > 0) sb.Append("  - ").Append(VolumeLabel);
            return sb.ToString();
        }
    }

    /// <summary>
    /// Access to a PC optical drive through SCSI pass-through: CreateFile("\\.\E:") and
    /// DeviceIoControl(IOCTL_SCSI_PASS_THROUGH), the BUFFERED variant: the structure, the sense data and
    /// the data travel in one contiguous buffer. This is the same request the Xbox version sends through
    /// Windows.Devices.Custom, so the rest of the engine (detection, rescue, CSS, AACS) is unchanged.
    ///
    /// Works the same for internal SATA drives and external USB drives (usbstor/uaspstor). USB bridges
    /// often limit a single transfer (MaximumTransferLength, typically 64 KiB or 128 KiB): it is read from
    /// the adapter at open time and MaxTransferBytes is lowered to it.
    /// </summary>
    internal sealed class OpticalDrive : IDisposable
    {
        public const int SectorSize = 2048;

        // SCSI_PASS_THROUGH layout on 64-bit Windows (ntddscsi.h), sizeof = 56:
        //   0 USHORT Length          2 UCHAR ScsiStatus      3 UCHAR PathId
        //   4 UCHAR TargetId         5 UCHAR Lun             6 UCHAR CdbLength
        //   7 UCHAR SenseInfoLength  8 UCHAR DataIn          (9..11 padding)
        //  12 ULONG DataTransferLength                       16 ULONG TimeOutValue
        //  (20..23 padding)         24 ULONG_PTR DataBufferOffset (8 bytes on 64-bit)
        //  32 ULONG SenseInfoOffset 36 UCHAR Cdb[16]         (52..55 padding)
        // The app is built for x64 and ARM64 only (both use this layout).
        private const int SptSize = 56;
        private const int OffLength = 0, OffScsiStatus = 2, OffCdbLength = 6, OffSenseLength = 7, OffDataIn = 8;
        private const int OffTransferLength = 12, OffTimeout = 16, OffDataOffset = 24, OffSenseOffset = 32, OffCdb = 36;
        private const int SenseSize = 32;
        /// <summary>Header = structure + sense; the data starts right after it.</summary>
        public const int HeaderSize = SptSize + SenseSize; // 88

        private const byte SCSI_IOCTL_DATA_OUT = 0;
        private const byte SCSI_IOCTL_DATA_IN = 1;
        private const byte SCSI_IOCTL_DATA_UNSPECIFIED = 2;

        private const uint IOCTL_SCSI_PASS_THROUGH = 0x0004D004;
        private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
        private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
        private const uint OPEN_EXISTING = 3;
        private const int ERROR_ACCESS_DENIED = 5;
        /// <summary>HRESULT_FROM_WIN32(ERROR_ACCESS_DENIED), as stored in ScsiResult.HResult.</summary>
        public const int AccessDeniedHResult = unchecked((int)0x80070005);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
                                                         uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] inBuffer, int inSize,
                                                   byte[] outBuffer, int outSize, out int returned, IntPtr overlapped);

        private readonly SafeFileHandle _handle;
        private readonly byte[] _in;
        private readonly byte[] _out;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        /// <summary>Largest data transfer this object accepts (buffer size, limited by the adapter).</summary>
        public int MaxTransferBytes { get; private set; }
        /// <summary>The adapter's own limit, or 0 if Windows did not say.</summary>
        public int AdapterMaxTransferBytes { get; private set; }
        public string DeviceId { get; private set; }
        public DriveEntry Info { get; private set; }

        private OpticalDrive(SafeFileHandle handle, string id, int maxTransferBytes, int adapterMax, DriveEntry info)
        {
            _handle = handle;
            DeviceId = id;
            AdapterMaxTransferBytes = adapterMax;
            MaxTransferBytes = adapterMax > 0 ? Math.Min(maxTransferBytes, adapterMax) : maxTransferBytes;
            Info = info;
            _in = new byte[HeaderSize + maxTransferBytes];
            _out = new byte[HeaderSize + maxTransferBytes];
        }

        private static string DevicePath(string id) { return @"\\.\" + id.TrimEnd('\\'); }

        private static int HResultFromWin32(int err) { return err <= 0 ? err : unchecked((int)(0x80070000u | (uint)(err & 0xFFFF))); }

        /// <summary>Every optical drive letter, with the model and bus when Windows tells them.</summary>
        public static Task<IReadOnlyList<DriveEntry>> FindAsync()
        {
            var list = new List<DriveEntry>();
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.CDRom) continue;
                var e = new DriveEntry { Id = d.Name.TrimEnd('\\') };
                try { if (d.IsReady) e.VolumeLabel = d.VolumeLabel ?? ""; } catch (Exception) { }
                // Querying the device descriptor needs no access rights (0 = query only).
                using (var h = CreateFileW(DevicePath(e.Id), 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
                    if (!h.IsInvalid) QueryDevice(h, e);
                list.Add(e);
            }
            return Task.FromResult<IReadOnlyList<DriveEntry>>(list);
        }

        /// <summary>Opens the drive. Throws UnauthorizedAccessException if Windows refuses pass-through.</summary>
        public static async Task<OpticalDrive> OpenAsync(string deviceId, int maxTransferBytes)
        {
            var h = CreateFileW(DevicePath(deviceId), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                h.Dispose();
                if (err == ERROR_ACCESS_DENIED)
                    throw new UnauthorizedAccessException("Windows refused to open drive " + deviceId + " for SCSI commands (access denied). Run xISOBurn as administrator.");
                throw new IOException("Could not open drive " + deviceId + ": " + new Win32Exception(err).Message, HResultFromWin32(err));
            }
            var info = new DriveEntry { Id = deviceId };
            QueryDevice(h, info);
            int adapterMax = QueryAdapterMaxTransfer(h);
            var drive = new OpticalDrive(h, deviceId, maxTransferBytes, adapterMax, info);

            // TEST UNIT READY: "not ready" is fine (no disc), but an access-denied IOCTL means this user
            // may not send SCSI commands at all.
            var r = await drive.SendAsync(ScsiCommands.TestUnitReady(), 0, null, 0, 10);
            if (!r.IoctlOk && r.HResult == AccessDeniedHResult)
            {
                drive.Dispose();
                throw new UnauthorizedAccessException("Windows refused SCSI pass-through on drive " + deviceId + " (access denied). Run xISOBurn as administrator.");
            }
            return drive;
        }

        /// <summary>Opens the first optical drive. Throws if there is none.</summary>
        public static async Task<OpticalDrive> FindAndOpenAsync(int maxTransferBytes)
        {
            var devices = await FindAsync();
            if (devices.Count == 0)
                throw new InvalidOperationException("No optical drive found.");
            return await OpenAsync(devices[0].Id, maxTransferBytes);
        }

        // STORAGE_PROPERTY_QUERY { PropertyId, QueryType = PropertyStandardQuery (0), AdditionalParameters[1] }
        private static byte[] PropertyQuery(int propertyId)
        {
            var q = new byte[12];
            Bytes.PutLE32(q, 0, (uint)propertyId);
            return q;
        }

        private static readonly string[] BusNames =
        {
            "", "SCSI", "ATAPI", "ATA", "IEEE 1394", "SSA", "Fibre", "USB", "RAID", "iSCSI", "SAS", "SATA", "SD", "MMC",
            "Virtual", "File-backed virtual", "Spaces", "NVMe", "SCM", "UFS",
        };

        /// <summary>STORAGE_DEVICE_DESCRIPTOR: vendor, product, revision and bus type.</summary>
        private static void QueryDevice(SafeFileHandle h, DriveEntry e)
        {
            var outBuf = new byte[1024];
            int got;
            var q = PropertyQuery(0 /* StorageDeviceProperty */);
            if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, q, q.Length, outBuf, outBuf.Length, out got, IntPtr.Zero) || got < 32) return;
            Func<int, string> str = off =>
            {
                int p = (int)Bytes.LE32(outBuf, off);
                if (p <= 0 || p >= got) return "";
                int end = p;
                while (end < got && outBuf[end] != 0) end++;
                return Encoding.ASCII.GetString(outBuf, p, end - p).Trim();
            };
            e.Vendor = str(12);
            e.Product = str(16);
            e.Revision = str(20);
            int bus = (int)Bytes.LE32(outBuf, 28);
            e.Bus = bus > 0 && bus < BusNames.Length ? BusNames[bus] : "";
        }

        /// <summary>STORAGE_ADAPTER_DESCRIPTOR: the largest single transfer in whole sectors (0 if unknown).</summary>
        private static int QueryAdapterMaxTransfer(SafeFileHandle h)
        {
            var outBuf = new byte[64];
            int got;
            var q = PropertyQuery(1 /* StorageAdapterProperty */);
            if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, q, q.Length, outBuf, outBuf.Length, out got, IntPtr.Zero) || got < 16) return 0;
            long maxLen = Bytes.LE32(outBuf, 8);
            long pages = Bytes.LE32(outBuf, 12);
            // A transfer that is not page-aligned can need one page more than its length.
            if (pages > 1) maxLen = Math.Min(maxLen, (pages - 1) * 4096);
            if (maxLen < SectorSize || maxLen > int.MaxValue) return 0;
            return (int)(maxLen / SectorSize * SectorSize);
        }

        /// <summary>
        /// READ(10) of 'count' sectors into buf, split into transfers the adapter accepts (USB bridges
        /// often take at most 64 KiB at once). True if every sector came back.
        /// </summary>
        public async Task<bool> ReadSectorsAsync(long lba, int count, byte[] buf, uint timeoutSeconds)
        {
            int perRead = Math.Max(1, MaxTransferBytes / SectorSize);
            int done = 0;
            while (done < count)
            {
                int n = Math.Min(perRead, count - done);
                var r = await SendAsync(ScsiCommands.Read10((uint)(lba + done), n), n * SectorSize, buf, done * SectorSize, timeoutSeconds);
                if (!r.Success || r.DataLength != n * SectorSize) return false;
                done += n;
            }
            return true;
        }

        /// <summary>
        /// Sends a CDB. If dataLength > 0 it is a read command (DATA IN) and the data is copied to
        /// dest[destOffset..]. Never throws: errors come back in the ScsiResult.
        /// </summary>
        public async Task<ScsiResult> SendAsync(byte[] cdb, int dataLength, byte[] dest, int destOffset, uint timeoutSeconds)
        {
            if (cdb.Length == 0 || cdb.Length > 16) throw new ArgumentException("A CDB must be 1..16 bytes");
            if (dataLength < 0 || dataLength > MaxTransferBytes) throw new ArgumentOutOfRangeException("dataLength");

            await _gate.WaitAsync();
            try
            {
                var result = SendCore(cdb, dataLength, dest, destOffset, timeoutSeconds, null);

                // A CHECK CONDITION without sense data: ask for it explicitly (REQUEST SENSE, 0x03).
                if (!result.Success && result.IoctlOk && result.Sense == null && cdb[0] != 0x03)
                {
                    var sense = new byte[18];
                    var rs = SendCore(ScsiCommands.RequestSense(18), 18, sense, 0, 10, null);
                    if (rs.Success && rs.DataLength >= 8)
                    {
                        var parsed = SenseInfo.Parse(sense, rs.DataLength);
                        if (parsed != null && parsed.Key != 0)
                        {
                            parsed.FromRequestSense = true;
                            result.Sense = parsed;
                        }
                    }
                }
                return result;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Sends a CDB that carries data TO the drive (DATA OUT), e.g. MODE SELECT. With the buffered
        /// pass-through the outgoing data sits in the input buffer right after the header.
        /// </summary>
        public async Task<ScsiResult> SendOutAsync(byte[] cdb, byte[] data, uint timeoutSeconds)
        {
            if (cdb.Length == 0 || cdb.Length > 16) throw new ArgumentException("A CDB must be 1..16 bytes");
            if (data == null || data.Length == 0 || data.Length > MaxTransferBytes) throw new ArgumentOutOfRangeException("data");
            await _gate.WaitAsync();
            try
            {
                return SendCore(cdb, 0, null, 0, timeoutSeconds, data);
            }
            finally
            {
                _gate.Release();
            }
        }

        private ScsiResult SendCore(byte[] cdb, int dataLength, byte[] dest, int destOffset, uint timeoutSeconds, byte[] outData)
        {
            var sw = Stopwatch.StartNew();
            var result = new ScsiResult();
            try
            {
                Array.Clear(_in, 0, HeaderSize);
                Array.Clear(_out, 0, HeaderSize);
                Bytes.PutLE16(_in, OffLength, SptSize);
                _in[OffCdbLength] = (byte)cdb.Length;
                _in[OffSenseLength] = SenseSize;
                if (outData != null)
                {
                    _in[OffDataIn] = SCSI_IOCTL_DATA_OUT;
                    dataLength = outData.Length;
                    Buffer.BlockCopy(outData, 0, _in, HeaderSize, outData.Length);
                }
                else
                {
                    _in[OffDataIn] = dataLength > 0 ? SCSI_IOCTL_DATA_IN : SCSI_IOCTL_DATA_UNSPECIFIED;
                }
                Bytes.PutLE32(_in, OffTransferLength, (uint)dataLength);
                Bytes.PutLE32(_in, OffTimeout, timeoutSeconds);
                Bytes.PutLE64(_in, OffDataOffset, HeaderSize);
                Bytes.PutLE32(_in, OffSenseOffset, SptSize);
                Buffer.BlockCopy(cdb, 0, _in, OffCdb, cdb.Length);

                // Input = header (+ outgoing data); output = header + room for the incoming data.
                int inSize = HeaderSize + (outData != null ? dataLength : 0);
                int outSize = HeaderSize + (outData != null ? 0 : dataLength);
                int received;
                if (!DeviceIoControl(_handle, IOCTL_SCSI_PASS_THROUGH, _in, inSize, _out, outSize, out received, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    result.IoctlOk = false;
                    result.HResult = HResultFromWin32(err);
                    result.IoctlError = new Win32Exception(err).Message + " (Win32 error " + err + ")";
                    return result;
                }
                result.IoctlOk = true;

                if (received < SptSize)
                {
                    result.IoctlOk = false;
                    result.IoctlError = "short response (" + received + " bytes)";
                    return result;
                }

                result.ScsiStatus = _out[OffScsiStatus];
                int senseLen = Math.Min((int)_out[OffSenseLength], SenseSize);
                uint senseOff = Bytes.LE32(_out, OffSenseOffset);
                if (result.ScsiStatus != 0 && senseLen > 0 && senseOff >= SptSize && senseOff + senseLen <= HeaderSize)
                {
                    var sense = new byte[senseLen];
                    Buffer.BlockCopy(_out, (int)senseOff, sense, 0, senseLen);
                    result.Sense = SenseInfo.Parse(sense, senseLen);
                }

                if (result.ScsiStatus == 0 && dataLength > 0 && outData == null)
                {
                    uint transferred = Bytes.LE32(_out, OffTransferLength);
                    long available = (long)received - HeaderSize;
                    if (transferred > dataLength || transferred > available)
                    {
                        result.IoctlOk = false;
                        result.IoctlError = "invalid DataTransferLength (" + transferred + ", received " + received + ")";
                        return result;
                    }
                    result.DataLength = (int)transferred;
                    if (dest != null)
                        Buffer.BlockCopy(_out, HeaderSize, dest, destOffset, (int)transferred);
                }
            }
            catch (Exception ex)
            {
                result.IoctlOk = false;
                result.HResult = ex.HResult;
                result.IoctlError = ex.Message;
            }
            finally
            {
                result.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            }
            return result;
        }

        public void Dispose()
        {
            _handle.Dispose();
        }
    }
}
