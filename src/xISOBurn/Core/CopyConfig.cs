using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace xISOBurn.Core
{
    /// <summary>
    /// Settings, stored in %LOCALAPPDATA%\xISOBurn\config.json. If the file does not exist it is created
    /// with the defaults; after every successful load it is rewritten with all current keys (unknown or
    /// obsolete keys are dropped), so the file always documents what the app actually uses. The file is
    /// read again before every run.
    /// </summary>
    internal sealed class CopyConfig
    {
        public const string FileName = "config.json";
        public const int MaxSectorsPerRead = 64;

        /// <summary>Sectors per READ(10) in good areas. Lowered automatically to what the drive's bus accepts.</summary>
        public int SectorsPerRead = 32;
        /// <summary>Maximum time per SCSI command (seconds).</summary>
        public int TimeoutSeconds = 20;
        /// <summary>
        /// Pass 1: when errors are dense, jump ahead and come back later ("fill" pass), so the easy data
        /// is secured first. false = read strictly in order.
        /// </summary>
        public bool SkipOnErrors = true;
        /// <summary>Extra passes over sectors that are still bad after the scrape pass (1 attempt per sector per pass).</summary>
        public int RetryPasses = 2;
        /// <summary>
        /// Drive read speed (kB/s) for the scrape and retry passes; 0 = do not change. 1385 = DVD 1x.
        /// Reading slowly often recovers scratched sectors. Experimental: the drive may ignore it.
        /// </summary>
        public int RetryReadSpeedKBps = 1385;
        /// <summary>
        /// Drive-internal read retry count (MODE page 01h, byte 3) during pass 1; -1 = do not change.
        /// Experimental and volatile; the original value is restored at the end of every run.
        /// </summary>
        public int DriveReadRetryCount = -1;
        /// <summary>First sector of the work range (for testing a range). The image always covers the whole disc.</summary>
        public long StartSector = 0;
        /// <summary>Number of sectors in the work range (0 = up to the end of the disc).</summary>
        public long SectorLimit = 0;
        /// <summary>false = only read and build the map, without writing the image (drive test).</summary>
        public bool WriteImage = true;
        /// <summary>File name without extension. Empty = the disc's volume label.</summary>
        public string OutputName = "";
        /// <summary>CSS-protected DVD-Video: authenticate with the drive and decrypt the video while copying.</summary>
        public bool DecryptCss = true;
        /// <summary>Blu-ray: decrypt AACS (and apply BD+ fixups) while copying, with the key files in the keys folder.</summary>
        public bool DecryptBluray = true;
        /// <summary>Blu-ray: rename AACS (and BDSVM) in the image so libbluray players (VLC, Kodi) open it without keys.</summary>
        public bool MarkBlurayDecrypted = false;
        /// <summary>Folder for the copies; "" = Documents\xISOBurn.</summary>
        public string OutputFolder = "";
        /// <summary>The drive used last ("E:"); "" = the first optical drive.</summary>
        public string DriveId = "";
        /// <summary>A Windows notification, a sound and a flashing taskbar button when a copy finishes or stops.</summary>
        public bool NotifyWhenDone = true;

        /// <summary>
        /// Applies the settings form on top of 'current'. Keys are the config.json names; a boolean is on
        /// only when its value is "true". Returns null, with a message, if a field is invalid; 'current' is
        /// never modified.
        /// </summary>
        public static CopyConfig FromForm(CopyConfig current, IDictionary<string, string> f, out string error)
        {
            error = null;
            var cfg = (CopyConfig)current.MemberwiseClone();
            Func<string, string> get = k => { string v; return f.TryGetValue(k, out v) && v != null ? v.Trim() : ""; };
            Func<string, bool> flag = k => get(k) == "true";

            cfg.WriteImage = flag("WriteImage");
            cfg.DecryptCss = flag("DecryptCss");
            cfg.DecryptBluray = flag("DecryptBluray");
            cfg.MarkBlurayDecrypted = flag("MarkBlurayDecrypted");
            if (f.ContainsKey("OutputFolder")) cfg.OutputFolder = get("OutputFolder");
            cfg.SkipOnErrors = flag("SkipOnErrors");
            cfg.OutputName = get("OutputName");
            cfg.NotifyWhenDone = flag("NotifyWhenDone");

            int timeout, passes, spr, speed, driveRetries;
            long start, limit;
            if (!int.TryParse(get("SectorsPerRead"), out spr) || spr < 1 || spr > MaxSectorsPerRead) error = "Sectors per read must be 1 to " + MaxSectorsPerRead + ".";
            else if (!int.TryParse(get("TimeoutSeconds"), out timeout) || timeout < 5 || timeout > 120) error = "Timeout must be 5 to 120 seconds.";
            else if (!int.TryParse(get("RetryPasses"), out passes) || passes < 0 || passes > 100) error = "Retry passes must be 0 to 100.";
            else if (!int.TryParse(get("RetryReadSpeedKBps"), out speed) || speed < 0 || speed > 0xFFFF) error = "The retry speed must be 0 (do not change) to 65535 kB/s.";
            else if (!int.TryParse(get("DriveReadRetryCount"), out driveRetries) || driveRetries < -1 || driveRetries > 255) error = "The drive's retries must be -1 (do not change) to 255.";
            else if (!long.TryParse(get("StartSector"), out start) || start < 0) error = "First sector must be 0 or more.";
            else if (!long.TryParse(get("SectorLimit"), out limit) || limit < 0) error = "Sectors must be 0 (whole disc) or more.";
            else if (cfg.OutputName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) error = "The file name contains characters that are not allowed.";
            else if (cfg.OutputFolder.Length > 0 && !Path.IsPathFullyQualified(cfg.OutputFolder)) error = "The destination must be a full folder path, such as D:\\ISOs.";
            else
            {
                cfg.SectorsPerRead = spr;
                cfg.TimeoutSeconds = timeout;
                cfg.RetryPasses = passes;
                cfg.RetryReadSpeedKBps = speed;
                cfg.DriveReadRetryCount = driveRetries;
                cfg.StartSector = start;
                cfg.SectorLimit = limit;
                return cfg;
            }
            return null;
        }

        public static CopyConfig Load(string folder, out string message)
        {
            var cfg = new CopyConfig();
            string path = Path.Combine(folder, FileName);
            if (!File.Exists(path))
            {
                cfg.Clamp();
                cfg.Save(folder);
                message = "config.json created with the default values.";
                return cfg;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(File.ReadAllText(path));
            }
            catch (Exception)
            {
                message = "config.json is invalid (malformed JSON); using the defaults. The file was not changed.";
                return cfg;
            }

            using (doc)
            {
                try
                {
                    var o = doc.RootElement;
                    if (o.ValueKind != JsonValueKind.Object) throw new FormatException("the file is not a JSON object");
                    Func<string, JsonElement?> el = k => { JsonElement v; return o.TryGetProperty(k, out v) ? v : (JsonElement?)null; };
                    Func<string, long, long> num = (k, d) => { var v = el(k); return v.HasValue ? (long)v.Value.GetDouble() : d; };
                    Func<string, bool, bool> boo = (k, d) => { var v = el(k); return v.HasValue ? v.Value.GetBoolean() : d; };
                    Func<string, string, string> str = (k, d) => { var v = el(k); return v.HasValue ? v.Value.GetString() ?? "" : d; };

                    cfg.SectorsPerRead = (int)num("SectorsPerRead", cfg.SectorsPerRead);
                    cfg.TimeoutSeconds = (int)num("TimeoutSeconds", cfg.TimeoutSeconds);
                    cfg.SkipOnErrors = boo("SkipOnErrors", cfg.SkipOnErrors);
                    cfg.RetryPasses = (int)num("RetryPasses", cfg.RetryPasses);
                    cfg.RetryReadSpeedKBps = (int)num("RetryReadSpeedKBps", cfg.RetryReadSpeedKBps);
                    cfg.DriveReadRetryCount = (int)num("DriveReadRetryCount", cfg.DriveReadRetryCount);
                    cfg.StartSector = num("StartSector", cfg.StartSector);
                    cfg.SectorLimit = num("SectorLimit", cfg.SectorLimit);
                    cfg.WriteImage = boo("WriteImage", cfg.WriteImage);
                    cfg.OutputName = str("OutputName", cfg.OutputName);
                    cfg.DecryptCss = boo("DecryptCss", cfg.DecryptCss);
                    cfg.DecryptBluray = boo("DecryptBluray", cfg.DecryptBluray);
                    cfg.MarkBlurayDecrypted = boo("MarkBlurayDecrypted", cfg.MarkBlurayDecrypted);
                    cfg.OutputFolder = str("OutputFolder", cfg.OutputFolder);
                    cfg.DriveId = str("DriveId", cfg.DriveId);
                    cfg.NotifyWhenDone = boo("NotifyWhenDone", cfg.NotifyWhenDone);
                }
                catch (Exception ex)
                {
                    // GetDouble/GetBoolean/GetString throw if a value has the wrong type (e.g. a number in quotes).
                    message = "config.json has a value of the wrong type (" + ex.Message + "); using the defaults. The file was not changed.";
                    return new CopyConfig();
                }
            }
            bool adjusted = cfg.Clamp();
            try { cfg.Save(folder); } catch (Exception) { /* keep going with the values in memory */ }
            message = adjusted ? "config.json loaded (some out-of-range values were adjusted)." : "config.json loaded.";
            return cfg;
        }

        /// <summary>Keeps values within safe ranges. Returns true if anything was adjusted.</summary>
        private bool Clamp()
        {
            int spr = Math.Max(1, Math.Min(MaxSectorsPerRead, SectorsPerRead));
            int to = Math.Max(5, Math.Min(120, TimeoutSeconds));
            int rp = Math.Max(0, Math.Min(100, RetryPasses));
            int rs = RetryReadSpeedKBps <= 0 ? 0 : Math.Max(176, Math.Min(0xFFFF, RetryReadSpeedKBps));
            int rc = DriveReadRetryCount < 0 ? -1 : Math.Min(255, DriveReadRetryCount);
            long ss = Math.Max(0, StartSector);
            long sl = Math.Max(0, SectorLimit);
            bool changed = spr != SectorsPerRead || to != TimeoutSeconds || rp != RetryPasses || rs != RetryReadSpeedKBps ||
                           rc != DriveReadRetryCount || ss != StartSector || sl != SectorLimit;
            SectorsPerRead = spr; TimeoutSeconds = to; RetryPasses = rp; RetryReadSpeedKBps = rs;
            DriveReadRetryCount = rc; StartSector = ss; SectorLimit = sl;
            OutputName = OutputName ?? ""; OutputFolder = OutputFolder ?? ""; DriveId = DriveId ?? "";
            return changed;
        }

        private static string Q(string s) { return JsonSerializer.Serialize(s ?? ""); }

        public void Save(string folder)
        {
            // Written by hand so it stays readable and keeps a fixed order.
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"SectorsPerRead\": ").Append(SectorsPerRead.ToString(inv)).Append(",\n");
            sb.Append("  \"TimeoutSeconds\": ").Append(TimeoutSeconds.ToString(inv)).Append(",\n");
            sb.Append("  \"SkipOnErrors\": ").Append(SkipOnErrors ? "true" : "false").Append(",\n");
            sb.Append("  \"RetryPasses\": ").Append(RetryPasses.ToString(inv)).Append(",\n");
            sb.Append("  \"RetryReadSpeedKBps\": ").Append(RetryReadSpeedKBps.ToString(inv)).Append(",\n");
            sb.Append("  \"DriveReadRetryCount\": ").Append(DriveReadRetryCount.ToString(inv)).Append(",\n");
            sb.Append("  \"StartSector\": ").Append(StartSector.ToString(inv)).Append(",\n");
            sb.Append("  \"SectorLimit\": ").Append(SectorLimit.ToString(inv)).Append(",\n");
            sb.Append("  \"WriteImage\": ").Append(WriteImage ? "true" : "false").Append(",\n");
            sb.Append("  \"OutputName\": ").Append(Q(OutputName)).Append(",\n");
            sb.Append("  \"DecryptCss\": ").Append(DecryptCss ? "true" : "false").Append(",\n");
            sb.Append("  \"DecryptBluray\": ").Append(DecryptBluray ? "true" : "false").Append(",\n");
            sb.Append("  \"MarkBlurayDecrypted\": ").Append(MarkBlurayDecrypted ? "true" : "false").Append(",\n");
            sb.Append("  \"OutputFolder\": ").Append(Q(OutputFolder)).Append(",\n");
            sb.Append("  \"DriveId\": ").Append(Q(DriveId)).Append(",\n");
            sb.Append("  \"NotifyWhenDone\": ").Append(NotifyWhenDone ? "true" : "false").Append("\n");
            sb.Append("}\n");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, FileName), sb.ToString(), new UTF8Encoding(false));
        }

        public string Describe()
        {
            return "block " + SectorsPerRead + " sectors | timeout " + TimeoutSeconds + " s | skip on errors: " + (SkipOnErrors ? "yes" : "no") +
                   " | retry passes " + RetryPasses +
                   " | retry speed " + (RetryReadSpeedKBps > 0 ? RetryReadSpeedKBps + " kB/s" : "unchanged") +
                   " | drive retries " + (DriveReadRetryCount >= 0 ? DriveReadRetryCount.ToString() : "unchanged") +
                   " | range " + StartSector + "+" + (SectorLimit > 0 ? SectorLimit.ToString() : "end") +
                   " | write image: " + (WriteImage ? "yes" : "no") +
                   " | decrypt CSS: " + (DecryptCss ? "yes" : "no") +
                   " | decrypt Blu-ray: " + (DecryptBluray ? (MarkBlurayDecrypted ? "yes, marked" : "yes") : "no") +
                   " | destination: " + CopyStore.Resolve(OutputFolder) +
                   " | notify when done: " + (NotifyWhenDone ? "yes" : "no");
        }
    }
}
