using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using xISOBurn.Bluray;
using xISOBurn.Util;

namespace xISOBurn
{
    /// <summary>
    /// The Blu-ray key files (the Xbox app takes them on its web page): KEYDB.cfg and the other libaacs
    /// files, BD+ player files (vm0) or cached conversion tables, or a .zip of them. The app ships no keys.
    /// </summary>
    internal sealed class KeysForm : Form
    {
        private readonly string _keys;
        private readonly Logger _log;
        private readonly ListBox _list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        private readonly Label _status = new Label { Dock = DockStyle.Fill, AutoSize = true, MaximumSize = new Size(620, 0), Padding = new Padding(0, 6, 0, 6) };

        public KeysForm(string keysFolder, Logger log)
        {
            _keys = keysFolder;
            _log = log;
            Text = "Blu-ray keys";
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(660, 440);
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
            root.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(630, 0), Dock = DockStyle.Fill,
                Text = "AACS: KEYDB.cfg (and optionally ProcessingDeviceKeysSimple.txt, HostKeyCertificate.txt). " +
                       "BD+: the libbdplus vm0 player files, or cached conversion tables (*.bin). A .zip of them also works. " +
                       "xISOBurn ships no keys.\nFolder: " + keysFolder,
            });
            root.Controls.Add(_list);
            root.Controls.Add(_status);
            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            var add = new Button { Text = "Add files...", AutoSize = true };
            var open = new Button { Text = "Open folder", AutoSize = true };
            var delete = new Button { Text = "Delete all", AutoSize = true };
            var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
            add.Click += (s, e) => AddFiles();
            open.Click += (s, e) =>
            {
                Directory.CreateDirectory(_keys);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _keys + "\"") { UseShellExecute = true });
            };
            delete.Click += (s, e) => DeleteAll();
            buttons.Controls.AddRange(new Control[] { add, open, delete, close });
            root.Controls.Add(buttons);
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);
            CancelButton = close;
            Refill();
        }

        private void Refill()
        {
            _list.Items.Clear();
            foreach (var f in KeyStore.List(_keys))
                _list.Items.Add(f.Key + "   (" + f.Value.ToString("N0") + " bytes)");
            if (_list.Items.Count == 0) _list.Items.Add("(no key files)");
        }

        private void AddFiles()
        {
            using (var d = new OpenFileDialog { Multiselect = true, Title = "Blu-ray key files", Filter = "Key files (*.cfg;*.txt;*.bin;*.zip;*.map)|*.cfg;*.txt;*.bin;*.zip;*.map|All files (*.*)|*.*" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                var done = new System.Text.StringBuilder();
                foreach (var path in d.FileNames)
                {
                    try
                    {
                        Directory.CreateDirectory(_keys);
                        // Install moves (or unzips and deletes) the file it gets: give it a copy.
                        string temp = Path.Combine(_keys, Guid.NewGuid().ToString("N") + ".upload");
                        File.Copy(path, temp, true);
                        string r = KeyStore.Install(_keys, Path.GetFileName(path), temp);
                        if (File.Exists(temp)) File.Delete(temp);
                        _log.Info("Keys: " + r);
                        done.AppendLine(r);
                    }
                    catch (Exception ex)
                    {
                        _log.Info("Keys: " + Path.GetFileName(path) + " not installed: " + ex.Message);
                        done.AppendLine(Path.GetFileName(path) + ": " + ex.Message);
                    }
                }
                _status.Text = done.ToString().Trim();
                Refill();
            }
        }

        private void DeleteAll()
        {
            if (MessageBox.Show(this, "Delete every key file in " + _keys + "?", "Blu-ray keys",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            try
            {
                KeyStore.DeleteAll(_keys);
                _log.Info("Keys: every key file deleted.");
                _status.Text = "Every key file was deleted.";
            }
            catch (Exception ex)
            {
                _status.Text = "Could not delete: " + ex.Message;
            }
            Refill();
        }
    }
}
