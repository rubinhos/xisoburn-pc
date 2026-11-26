using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using xISOBurn.Core;

namespace xISOBurn
{
    /// <summary>The settings dialog: the same options and rules as the Xbox app (CopyConfig.FromForm).</summary>
    internal sealed class SettingsForm : Form
    {
        private sealed class Choice
        {
            public string Label, Tag;
            public override string ToString() { return Label; }
        }

        private readonly CopyConfig _current;
        public CopyConfig Result { get; private set; }

        private readonly TextBox _outputName = new TextBox { Width = 260 };
        private readonly TextBox _outputFolder = new TextBox { Width = 360 };
        private readonly CheckBox _writeImage = new CheckBox { Text = "Write the image (off = only test the drive and build the map)", AutoSize = true };
        private readonly CheckBox _decryptCss = new CheckBox { Text = "Decrypt CSS (DVD-Video) and CPPM (DVD-Audio)", AutoSize = true };
        private readonly CheckBox _decryptBluray = new CheckBox { Text = "Decrypt Blu-ray (AACS / BD+, with the key files)", AutoSize = true };
        private readonly CheckBox _markDecrypted = new CheckBox { Text = "Mark the Blu-ray image as decrypted (for VLC and Kodi)", AutoSize = true };
        private readonly CheckBox _notify = new CheckBox { Text = "Notify me when a copy finishes or stops (Windows notification and sound)", AutoSize = true };
        private readonly CheckBox _cdLookup = new CheckBox { Text = "Audio CDs: look up the album and track names on MusicBrainz (internet) when the disc has no CD-TEXT", AutoSize = true };
        private readonly CheckBox _eject = new CheckBox { Text = "Eject the disc when the copy is complete (not when sectors are left unreadable)", AutoSize = true };
        private readonly ComboBox _sectorsPerRead = Combo();
        private readonly NumericUpDown _timeout = new NumericUpDown { Minimum = 5, Maximum = 120, Width = 80 };
        private readonly CheckBox _skip = new CheckBox { Text = "Skip ahead over areas with many errors", AutoSize = true };
        private readonly NumericUpDown _retryPasses = new NumericUpDown { Minimum = 0, Maximum = 100, Width = 80 };
        private readonly ComboBox _retrySpeed = Combo();
        private readonly ComboBox _driveRetry = Combo();
        private readonly TextBox _start = new TextBox { Width = 120 };
        private readonly TextBox _limit = new TextBox { Width = 120 };
        private readonly Label _error = new Label { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(560, 0) };

        private static ComboBox Combo() { return new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 }; }

        private static void Fill(ComboBox box, string selected, params string[] labelTagPairs)
        {
            box.Items.Clear();
            int pick = 0;
            for (int i = 0; i + 1 < labelTagPairs.Length; i += 2)
            {
                int index = box.Items.Add(new Choice { Label = labelTagPairs[i], Tag = labelTagPairs[i + 1] });
                if (labelTagPairs[i + 1] == selected) pick = index;
            }
            box.SelectedIndex = pick;
        }

        private static string TagOf(ComboBox box) { return box.SelectedItem is Choice c ? c.Tag : ""; }

        public SettingsForm(CopyConfig current)
        {
            _current = current;
            Text = "Settings";
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Padding = new Padding(12), Dock = DockStyle.Fill };
            Action<string, Control> row = (label, control) =>
            {
                grid.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 6, 8, 0) });
                grid.Controls.Add(control);
            };
            Action<Control> wide = control =>
            {
                grid.Controls.Add(control);
                grid.SetColumnSpan(control, 2);
            };

            var folderRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            var browse = new Button { Text = "Browse...", AutoSize = true };
            browse.Click += (s, e) =>
            {
                using (var d = new FolderBrowserDialog { ShowNewFolderButton = true, SelectedPath = CopyStore.Resolve(_outputFolder.Text) })
                    if (d.ShowDialog(this) == DialogResult.OK) _outputFolder.Text = d.SelectedPath;
            };
            folderRow.Controls.Add(_outputFolder);
            folderRow.Controls.Add(browse);

            row("File name", _outputName);
            wide(new Label { Text = "Empty uses the disc's volume name, or \"Artist - Album\" for an audio CD.", AutoSize = true, ForeColor = SystemColors.GrayText });
            row("Destination", folderRow);
            wide(new Label { Text = "Empty uses " + CopyStore.DefaultFolder + ".", AutoSize = true, ForeColor = SystemColors.GrayText });
            wide(_writeImage);
            wide(_decryptCss);
            wide(_decryptBluray);
            wide(_markDecrypted);
            wide(_cdLookup);
            wide(_eject);
            wide(_notify);
            row("Sectors per read", _sectorsPerRead);
            row("Timeout per command (s)", _timeout);
            wide(_skip);
            row("Retry passes", _retryPasses);
            row("Drive speed for retries", _retrySpeed);
            row("Drive's own retries", _driveRetry);
            row("First sector", _start);
            row("Sectors (0 = to the end)", _limit);
            wide(_error);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
            var ok = new Button { Text = "Save", AutoSize = true };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            ok.Click += (s, e) => Save();
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            wide(buttons);
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(grid);

            _outputName.Text = current.OutputName;
            _outputFolder.Text = current.OutputFolder;
            _writeImage.Checked = current.WriteImage;
            _decryptCss.Checked = current.DecryptCss;
            _decryptBluray.Checked = current.DecryptBluray;
            _markDecrypted.Checked = current.MarkBlurayDecrypted;
            _notify.Checked = current.NotifyWhenDone;
            _cdLookup.Checked = current.CdLookupOnline;
            _eject.Checked = current.EjectWhenDone;
            Fill(_sectorsPerRead, current.SectorsPerRead.ToString(),
                 "16 (32 KiB)", "16", "32 (64 KiB, default)", "32", "48 (96 KiB)", "48", "64 (128 KiB)", "64");
            _timeout.Value = Math.Max(5, Math.Min(120, current.TimeoutSeconds));
            _skip.Checked = current.SkipOnErrors;
            _retryPasses.Value = Math.Max(0, Math.Min(100, current.RetryPasses));
            Fill(_retrySpeed, current.RetryReadSpeedKBps.ToString(),
                 "Do not change", "0", "DVD 1x (1385 kB/s, default)", "1385", "DVD 2x (2770 kB/s)", "2770", "DVD 4x (5540 kB/s)", "5540",
                 "BD 1x (4495 kB/s)", "4495", "BD 2x (8990 kB/s)", "8990");
            Fill(_driveRetry, current.DriveReadRetryCount.ToString(),
                 "Do not change (default)", "-1", "0 (fastest failures)", "0", "1", "1", "4", "4", "8", "8");
            _start.Text = current.StartSector.ToString();
            _limit.Text = current.SectorLimit.ToString();
        }

        private void Save()
        {
            var form = new Dictionary<string, string>
            {
                { "OutputName", _outputName.Text },
                { "OutputFolder", _outputFolder.Text },
                { "WriteImage", _writeImage.Checked ? "true" : "false" },
                { "DecryptCss", _decryptCss.Checked ? "true" : "false" },
                { "DecryptBluray", _decryptBluray.Checked ? "true" : "false" },
                { "MarkBlurayDecrypted", _markDecrypted.Checked ? "true" : "false" },
                { "NotifyWhenDone", _notify.Checked ? "true" : "false" },
                { "CdLookupOnline", _cdLookup.Checked ? "true" : "false" },
                { "EjectWhenDone", _eject.Checked ? "true" : "false" },
                { "SectorsPerRead", TagOf(_sectorsPerRead) },
                { "TimeoutSeconds", ((int)_timeout.Value).ToString() },
                { "SkipOnErrors", _skip.Checked ? "true" : "false" },
                { "RetryPasses", ((int)_retryPasses.Value).ToString() },
                { "RetryReadSpeedKBps", TagOf(_retrySpeed) },
                { "DriveReadRetryCount", TagOf(_driveRetry) },
                { "StartSector", _start.Text },
                { "SectorLimit", _limit.Text },
            };
            string error;
            var cfg = CopyConfig.FromForm(_current, form, out error);
            if (cfg == null)
            {
                _error.Text = error;
                return;
            }
            Result = cfg;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
