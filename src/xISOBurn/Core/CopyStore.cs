using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace xISOBurn.Core
{
    internal sealed class StoreEntry
    {
        public string Name;
        public long Size;
    }

    /// <summary>
    /// Where the copies (.iso, .map, .sha256) are written: a folder on the PC (any drive, internal or
    /// external). The setting (CopyConfig.OutputFolder) is the folder path, or "" for the default
    /// folder (Documents\xISOBurn).
    /// </summary>
    internal sealed class CopyStore
    {
        private readonly string _folder;

        public CopyStore(string folder) { _folder = folder; }

        public string Folder { get { return _folder; } }
        public string Description { get { return _folder; } }

        /// <summary>The destination is FAT/FAT32 (no files of 4 GiB or more).</summary>
        public bool LimitedTo4GiB
        {
            get
            {
                try { return new DriveInfo(Path.GetPathRoot(_folder)).DriveFormat.StartsWith("FAT", StringComparison.OrdinalIgnoreCase); }
                catch (Exception) { return false; }
            }
        }

        /// <summary>The default folder for the copies: Documents\xISOBurn.</summary>
        public static string DefaultFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "xISOBurn"); }
        }

        public static string Resolve(string outputFolder)
        {
            return string.IsNullOrWhiteSpace(outputFolder) ? DefaultFolder : outputFolder.Trim();
        }

        private string P(string name) { return Path.Combine(_folder, name); }

        public string PathOf(string name) { return P(name); }

        /// <summary>How a file of this store is shown in the log: its full path.</summary>
        public string DisplayPath(string name) { return P(name); }

        /// <summary>Opens or creates a file for reading and writing; others may read it at the same time.</summary>
        public Task<Stream> OpenWriteAsync(string name)
        {
            // bufferSize 1 = no .NET-level buffering: every write reaches the OS at once.
            return Task.FromResult<Stream>(new FileStream(P(name), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1));
        }

        /// <summary>Opens a file for reading (shared with a writer); null if it does not exist.</summary>
        public Task<Stream> OpenReadAsync(string name)
        {
            string p = P(name);
            if (!File.Exists(p)) return Task.FromResult<Stream>(null);
            return Task.FromResult<Stream>(new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16));
        }

        /// <summary>-1 if missing.</summary>
        public Task<long> SizeAsync(string name)
        {
            var fi = new FileInfo(P(name));
            return Task.FromResult(fi.Exists ? fi.Length : -1L);
        }

        public Task DeleteAsync(string name)
        {
            if (File.Exists(P(name))) File.Delete(P(name));
            return Task.CompletedTask;
        }

        /// <summary>Renames 'from' to 'to', replacing 'to' if it exists.</summary>
        public Task ReplaceAsync(string from, string to)
        {
            File.Move(P(from), P(to), true);
            return Task.CompletedTask;
        }

        public Task<List<StoreEntry>> ListAsync(string extension)
        {
            var list = new List<StoreEntry>();
            foreach (var f in Directory.GetFiles(_folder, "*" + extension))
                list.Add(new StoreEntry { Name = Path.GetFileName(f), Size = new FileInfo(f).Length });
            return Task.FromResult(list);
        }

        /// <summary>-1 if unknown.</summary>
        public Task<long> FreeBytesAsync()
        {
            try { return Task.FromResult(new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_folder))).AvailableFreeSpace); }
            catch (Exception) { return Task.FromResult(-1L); }
        }

        public async Task<bool> ExistsAsync(string name) { return await SizeAsync(name) >= 0; }

        public async Task<string> ReadTextAsync(string name)
        {
            using (var s = await OpenReadAsync(name))
            {
                if (s == null) return null;
                using (var r = new StreamReader(s, Encoding.UTF8)) return await r.ReadToEndAsync();
            }
        }

        /// <summary>Writes to a temporary file, then replaces 'name' with it (a crash never leaves half a file).</summary>
        public async Task WriteTextAtomicAsync(string name, string text)
        {
            string tmp = Path.GetFileNameWithoutExtension(name) + ".tmp" + Path.GetExtension(name);
            var bytes = new UTF8Encoding(false).GetBytes(text);
            using (var s = await OpenWriteAsync(tmp))
            {
                s.SetLength(0);
                await s.WriteAsync(bytes, 0, bytes.Length);
                await s.FlushAsync();
            }
            await ReplaceAsync(tmp, name);
        }

        /// <summary>
        /// The store for a setting; creates the folder, or throws with a readable message. With a
        /// 'subfolder' (an audio CD: "Artist - Album", from the engine) the copy goes into that folder of
        /// the destination, so its .bin, .cue, .map and checksums stay together.
        /// </summary>
        public static Task<CopyStore> OpenAsync(string outputFolder, string appFolder, string subfolder = null)
        {
            string folder = Resolve(outputFolder);
            if (!string.IsNullOrWhiteSpace(subfolder)) folder = Path.Combine(folder, subfolder);
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                throw new IOException("The destination " + folder + " is not available (" + ex.Message + "). " +
                                      "Is the drive connected? Choose another destination folder.", ex);
            }
            return Task.FromResult(new CopyStore(folder));
        }

        public static string FormatBytes(long bytes)
        {
            return bytes < 0 ? "?" : (bytes / 1073741824.0).ToString("0.0") + " GiB";
        }
    }
}
