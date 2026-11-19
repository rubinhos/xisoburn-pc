using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using xISOBurn.Bluray;
using xISOBurn.Core;
using xISOBurn.Scsi;
using xISOBurn.Util;

namespace xISOBurn
{
    /// <summary>
    /// The main window: the same buttons and flow as the Xbox app (Detect disc, Copy / resume, Pause,
    /// Retry failed, Scan disc, Settings), plus a drive selector, since a PC can have several optical
    /// drives (internal and USB), and the destination folder.
    /// </summary>
    internal sealed class MainForm : Form
    {
        private const int MaxUiLines = 3000;

        /// <summary>Settings, logs and Blu-ray keys: %LOCALAPPDATA%\xISOBurn.</summary>
        private readonly string _folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "xISOBurn");
        private readonly Logger _log;

        private OpticalDrive _drive;
        private DiscProbe _probe;
        private CancellationTokenSource _cts;
        private bool _busy;
        private bool _closing;
        private long _failedSectors;
        private string _rescueLine = "";
        private bool _refusalShown;

        // Controls
        private readonly ComboBox _driveBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 520 };
        private readonly Button _refreshButton = new Button { Text = "Refresh", AutoSize = true };
        private readonly Label _destLabel = new Label { AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        private readonly Button _destButton = new Button { Text = "Change...", AutoSize = true };
        private readonly Button _openButton = new Button { Text = "Open folder", AutoSize = true };
        private readonly Button _detectButton = new Button { Text = "Detect disc", AutoSize = true };
        private readonly Button _copyButton = new Button { Text = "Copy / resume", AutoSize = true };
        private readonly Button _cancelButton = new Button { Text = "Pause / cancel", AutoSize = true, Enabled = false };
        private readonly Button _retryButton = new Button { Text = "Retry failed", AutoSize = true, Enabled = false };
        private readonly Button _scanButton = new Button { Text = "Scan disc", AutoSize = true };
        private readonly Button _settingsButton = new Button { Text = "Settings...", AutoSize = true };
        private readonly Button _keysButton = new Button { Text = "Blu-ray keys...", AutoSize = true };
        private readonly TextBox _discText = ReadOnlyBox(7);
        private readonly ProgressBar _bar = new ProgressBar { Dock = DockStyle.Fill, Maximum = 100000, Height = 22 };
        private readonly TextBox _progressText = ReadOnlyBox(3);
        private readonly Label _statusText = new Label { Dock = DockStyle.Fill, AutoSize = true, MaximumSize = new Size(1400, 0), Padding = new Padding(0, 4, 0, 4) };
        private readonly Label _configText = new Label { Dock = DockStyle.Fill, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(1400, 0) };
        private readonly ListBox _logList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true, SelectionMode = SelectionMode.MultiExtended };

        private static TextBox ReadOnlyBox(int lines)
        {
            return new TextBox
            {
                Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical,
                Height = lines * 17 + 8, BackColor = SystemColors.Window, TabStop = false,
            };
        }

        public MainForm()
        {
            Directory.CreateDirectory(_folder);
            Text = "xISOBurn" + (IsAdministrator() ? " (administrator)" : "");
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(980, 760);
            MinimumSize = new Size(760, 560);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
            BuildLayout();

            _log = new Logger(_folder, "xisoburn", AddLine);
            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            _log.Info("xISOBurn for Windows " + (ver != null ? ver.ToString(3) : "") + " started. Data folder: " + _folder);

            string msg;
            var cfg = CopyConfig.Load(_folder, out msg);
            _log.Info(msg);
            ShowConfig(cfg);
            _statusText.Text = "Choose the drive, insert a disc and choose \"Detect disc\", then \"Copy / resume\".";

            _refreshButton.Click += async (s, e) => await RefreshDrivesAsync();
            _driveBox.SelectionChangeCommitted += (s, e) => OnDriveChosen();
            _destButton.Click += (s, e) => ChooseDestination();
            _openButton.Click += (s, e) => OpenDestination();
            _detectButton.Click += async (s, e) => await DetectClickAsync();
            _copyButton.Click += async (s, e) => await RunRescueAsync(false);
            _retryButton.Click += async (s, e) => await RunRescueAsync(true);
            _scanButton.Click += async (s, e) => await ScanAsync();
            _cancelButton.Click += (s, e) => CancelRun();
            _settingsButton.Click += (s, e) => OpenSettings();
            _keysButton.Click += (s, e) => { using (var f = new KeysForm(BluRaySession.KeysFolder(_folder), _log)) f.ShowDialog(this); };
            _logList.KeyDown += (s, e) => { if (e.Control && e.KeyCode == Keys.C) CopyLogSelection(); };

            Shown += async (s, e) => await RefreshDrivesAsync();
        }

        // ------------------------------------------------------------ layout

        private void BuildLayout()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var driveRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            driveRow.Controls.Add(new Label { Text = "Drive:", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
            driveRow.Controls.Add(_driveBox);
            driveRow.Controls.Add(_refreshButton);

            var destRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            destRow.Controls.Add(new Label { Text = "Copies go to:", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
            destRow.Controls.Add(_destLabel);
            destRow.Controls.Add(_destButton);
            destRow.Controls.Add(_openButton);

            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 6) };
            buttons.Controls.AddRange(new Control[] { _detectButton, _copyButton, _cancelButton, _retryButton, _scanButton, _settingsButton, _keysButton });

            var disc = new GroupBox { Text = "Disc", Dock = DockStyle.Fill, Height = 150, Padding = new Padding(6) };
            disc.Controls.Add(_discText);

            var progress = new GroupBox { Text = "Progress", Dock = DockStyle.Fill, Height = 110, Padding = new Padding(6) };
            var pl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            pl.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            pl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            pl.Controls.Add(_bar, 0, 0);
            pl.Controls.Add(_progressText, 0, 1);
            progress.Controls.Add(pl);

            var log = new GroupBox { Text = "Log", Dock = DockStyle.Fill, Padding = new Padding(6) };
            log.Controls.Add(_logList);
            _logList.Font = new Font("Consolas", 9f);

            root.Controls.Add(driveRow);
            root.Controls.Add(destRow);
            root.Controls.Add(buttons);
            root.Controls.Add(disc);
            root.Controls.Add(progress);
            root.Controls.Add(_statusText);
            root.Controls.Add(log);
            root.Controls.Add(_configText);
            for (int i = 0; i < root.Controls.Count; i++)
                root.RowStyles.Add(root.Controls[i] == log ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            Controls.Add(root);
        }

        private void ShowConfig(CopyConfig cfg)
        {
            _destLabel.Text = CopyStore.Resolve(cfg.OutputFolder);
            _configText.Text = cfg.Describe();
        }

        // ------------------------------------------------------------ log

        private void AddLine(string line)
        {
            if (_closing || IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(AddLine), line); } catch (Exception) { }
                return;
            }
            _logList.BeginUpdate();
            if (_logList.Items.Count >= MaxUiLines) _logList.Items.RemoveAt(0);
            _logList.Items.Add(line);
            _logList.EndUpdate();
            _logList.TopIndex = Math.Max(0, _logList.Items.Count - 1);
        }

        private void CopyLogSelection()
        {
            var lines = new List<string>();
            foreach (var item in _logList.SelectedItems) lines.Add(item.ToString());
            if (lines.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, lines));
        }

        // ------------------------------------------------------------ drives

        private sealed class DriveItem
        {
            public DriveEntry Entry;
            public override string ToString() { return Entry.ToString(); }
        }

        private async Task RefreshDrivesAsync()
        {
            if (_busy) return;
            string msg;
            var cfg = CopyConfig.Load(_folder, out msg);
            string wanted = _driveBox.SelectedItem is DriveItem cur ? cur.Entry.Id : cfg.DriveId;
            IReadOnlyList<DriveEntry> drives;
            try { drives = await Task.Run(() => OpticalDrive.FindAsync()); }
            catch (Exception ex)
            {
                _log.Info("Could not list the optical drives: " + ex.Message);
                return;
            }
            _driveBox.Items.Clear();
            int select = -1;
            foreach (var d in drives)
            {
                int i = _driveBox.Items.Add(new DriveItem { Entry = d });
                if (string.Equals(d.Id, wanted, StringComparison.OrdinalIgnoreCase)) select = i;
            }
            if (drives.Count == 0)
            {
                _statusText.Text = "No optical drive found. Connect the drive (USB) and choose \"Refresh\".";
                _log.Info("No optical drive found.");
            }
            else
            {
                _driveBox.SelectedIndex = select >= 0 ? select : 0;
                _log.Info("Optical drives: " + string.Join(" | ", drives));
            }
            var chosen = SelectedDriveId();
            if (_drive != null && !string.Equals(_drive.DeviceId, chosen, StringComparison.OrdinalIgnoreCase)) CloseDrive();
            SetBusy(false, false);
        }

        private string SelectedDriveId()
        {
            return _driveBox.SelectedItem is DriveItem d ? d.Entry.Id : null;
        }

        private void OnDriveChosen()
        {
            string id = SelectedDriveId();
            if (id == null) return;
            if (_drive != null && !string.Equals(_drive.DeviceId, id, StringComparison.OrdinalIgnoreCase)) CloseDrive();
            _probe = null;
            _failedSectors = 0;
            _rescueLine = "";
            _discText.Text = "";
            SetBusy(false, false);
            string msg;
            var cfg = CopyConfig.Load(_folder, out msg);
            if (!string.Equals(cfg.DriveId, id, StringComparison.OrdinalIgnoreCase))
            {
                cfg.DriveId = id;
                cfg.Save(_folder);
            }
            _statusText.Text = "Drive " + id + " chosen. Insert a disc and choose \"Detect disc\".";
        }

        // USB drives come and go: refresh the list when Windows reports a device change.
        private const int WM_DEVICECHANGE = 0x0219;
        private System.Windows.Forms.Timer _deviceTimer;

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_DEVICECHANGE && !_busy && !_closing)
            {
                if (_deviceTimer == null)
                {
                    _deviceTimer = new System.Windows.Forms.Timer { Interval = 1500 };
                    _deviceTimer.Tick += async (s, e) => { _deviceTimer.Stop(); await RefreshDrivesAsync(); };
                }
                _deviceTimer.Stop();
                _deviceTimer.Start();
            }
        }

        private async Task<bool> EnsureDriveAsync()
        {
            string id = SelectedDriveId();
            if (id == null)
            {
                _statusText.Text = "No optical drive selected. Connect the drive and choose \"Refresh\".";
                return false;
            }
            if (_drive != null) return true;
            try
            {
                _drive = await Task.Run(() => OpticalDrive.OpenAsync(id, CopyConfig.MaxSectorsPerRead * OpticalDrive.SectorSize));
                _log.Info("Drive opened: " + _drive.Info +
                          (_drive.AdapterMaxTransferBytes > 0 ? " | largest transfer " + (_drive.AdapterMaxTransferBytes / 1024) + " KiB" : ""));
                return true;
            }
            catch (UnauthorizedAccessException ex)
            {
                _log.Info("Could not open the drive: " + ex.Message);
                _statusText.Text = "Windows refused access to the drive. Run xISOBurn as administrator.";
                OfferElevation();
                return false;
            }
            catch (Exception ex)
            {
                _log.Info("Could not open the drive: 0x" + ex.HResult.ToString("X8") + " " + ex.Message);
                _statusText.Text = "Could not open drive " + id + " (see log).";
                return false;
            }
        }

        private void CloseDrive()
        {
            if (_drive != null)
            {
                _drive.Dispose();
                _drive = null;
            }
        }

        private static bool IsAdministrator()
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch (Exception) { return false; }
        }

        private void OfferElevation()
        {
            if (IsAdministrator()) return;
            var answer = MessageBox.Show(this,
                "Windows did not allow xISOBurn to send commands to the drive as a standard user.\n\n" +
                "Restart xISOBurn as administrator?", "xISOBurn", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath ?? Application.ExecutablePath) { UseShellExecute = true, Verb = "runas" });
                _closing = true;
                Close();
            }
            catch (Exception ex)
            {
                _log.Info("Restart as administrator was cancelled or failed: " + ex.Message);
            }
        }

        // ------------------------------------------------------------ busy state / sleep

        private void SetBusy(bool busy, bool running)
        {
            _busy = busy;
            bool haveDrive = _driveBox.Items.Count > 0;
            _driveBox.Enabled = !busy;
            _refreshButton.Enabled = !busy;
            _destButton.Enabled = !busy;
            _detectButton.Enabled = !busy && haveDrive;
            _copyButton.Enabled = !busy && haveDrive;
            _retryButton.Enabled = !busy && haveDrive && _failedSectors > 0;
            _scanButton.Enabled = !busy && haveDrive;
            _settingsButton.Enabled = !busy;
            _keysButton.Enabled = !busy;
            _cancelButton.Enabled = running;
        }

        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint flags);
        private const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001;

        /// <summary>Keeps the PC from going to sleep while a copy or a scan runs (called on the UI thread).</summary>
        private static void KeepAwake(bool on)
        {
            SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED : ES_CONTINUOUS);
        }

        // ------------------------------------------------------------ detect

        private async Task<bool> DetectAsync(uint timeout)
        {
            if (!await EnsureDriveAsync()) return false;
            var drive = _drive;
            _probe = await Task.Run(() => DiscProbe.RunAsync(drive, timeout, _log));
            if (!_probe.Ready && _probe.NotReadyReason.Contains("IOCTL"))
                CloseDrive(); // the handle may have gone stale (USB drive unplugged); reopen next time
            await CheckPreviousRescueAsync();
            return _probe.Ready && _probe.TotalSectors > 0;
        }

        private async Task CheckPreviousRescueAsync()
        {
            _failedSectors = 0;
            _rescueLine = "";
            if (_probe != null && _probe.Ready && _probe.TotalSectors > 0)
            {
                string msg;
                var cfg = CopyConfig.Load(_folder, out msg);
                string mapName = RescueJob.BaseNameFor(cfg, _probe) + ".map";
                long total = _probe.TotalSectors;
                SectorStateMap map = null;
                try
                {
                    var store = await CopyStore.OpenAsync(cfg.OutputFolder, _folder);
                    string text = await store.ReadTextAsync(mapName);
                    if (text != null)
                        map = await Task.Run(() => { string reason; return SectorStateMap.Parse(text, total, out reason); });
                }
                catch (Exception ex)
                {
                    _log.Info("Could not read the previous map: " + ex.Message);
                }
                if (map != null)
                    SetRescueState(map.Count(SectorState.Ok), map.Count(SectorState.Bad), map.Count(SectorState.Pending),
                                   map.Count(SectorState.Untried), total);
            }
            ShowDisc();
        }

        private void SetRescueState(long ok, long bad, long pending, long untried, long total)
        {
            _failedSectors = bad + pending;
            _rescueLine = "Previous rescue: " + (ok * 100.0 / total).ToString("0.000") + "% read" +
                          (_failedSectors > 0 ? ", " + _failedSectors.ToString("N0") + " failed sector(s)" : "") +
                          (untried > 0 ? ", " + untried.ToString("N0") + " not tried" : "") +
                          (untried == 0 && _failedSectors == 0 ? " (complete)" : "");
            _log.Info(_rescueLine);
            _retryButton.Enabled = !_busy && _failedSectors > 0;
        }

        private void ShowDisc()
        {
            if (_probe == null) return;
            string text = string.Join("\r\n", _probe.Describe());
            if (_rescueLine.Length > 0) text += "\r\n" + _rescueLine;
            _discText.Text = text;
        }

        private async Task DetectClickAsync()
        {
            if (_busy) return;
            SetBusy(true, false);
            try
            {
                bool ok = await DetectAsync(20);
                if (_closing) return;
                if (ok) _statusText.Text = "Disc detected. Choose \"Copy / resume\".";
                else if (_drive != null || _statusText.Text.Length == 0) _statusText.Text = "No disc detected (see log).";
            }
            catch (Exception ex)
            {
                _log.Info("ERROR while detecting: " + ex);
                _statusText.Text = "Error: " + ex.Message;
            }
            finally
            {
                if (!_closing) SetBusy(false, false);
            }
        }

        // ------------------------------------------------------------ copy

        private async Task RunRescueAsync(bool extraRetryPass)
        {
            if (_busy) return;
            SetBusy(true, false);
            bool awake = false;
            try
            {
                // Reload the config on every copy, so edits made in config.json take effect.
                string msg;
                var cfg = CopyConfig.Load(_folder, out msg);
                _log.Info(msg);
                ShowConfig(cfg);

                // Detect again: the disc may have been swapped since the last detection.
                if (!await DetectAsync((uint)cfg.TimeoutSeconds))
                {
                    if (_drive != null) _statusText.Text = "No disc ready to copy (see log).";
                    return;
                }

                // USB bridges often take at most 64 KiB per command.
                int maxSpr = Math.Max(1, _drive.MaxTransferBytes / OpticalDrive.SectorSize);
                if (cfg.SectorsPerRead > maxSpr)
                {
                    _log.Info("Sectors per read lowered from " + cfg.SectorsPerRead + " to " + maxSpr + ": the drive's connection accepts at most " +
                              (_drive.MaxTransferBytes / 1024) + " KiB per read.");
                    cfg.SectorsPerRead = maxSpr;
                }

                KeepAwake(true);
                awake = true;
                _cts = new CancellationTokenSource();
                SetBusy(true, true);
                _statusText.Text = "Copying to " + CopyStore.Resolve(cfg.OutputFolder) +
                                   (_probe.Cpst == 1 && cfg.DecryptCss ? " (CSS disc: decrypting the video)" : "") +
                                   (_probe.Aacs && cfg.DecryptBluray ? " (AACS disc: decrypting with the key files, see the log)" : "") +
                                   "... \"Pause / cancel\" stops it; progress is kept.";
                _bar.Value = 0;
                _refusalShown = false;

                var progress = new Progress<RescueProgress>(UpdateProgress);
                var job = new RescueJob(_drive, _probe, cfg, _log, _folder, extraRetryPass);
                var token = _cts.Token;
                var result = await Task.Run(() => job.RunAsync(progress, token));
                if (_closing) return;

                SetRescueState(result.Ok, result.Bad, result.Pending, result.Untried, _probe.TotalSectors);
                ShowDisc();

                string counts = result.Ok.ToString("N0") + " ok, " + result.Bad.ToString("N0") + " bad, " +
                                result.Pending.ToString("N0") + " pending, " + result.Untried.ToString("N0") + " not tried";
                string status;
                if (extraRetryPass && !result.Cancelled && result.Error == null)
                    status = "Retry done: recovered " + result.RecoveredThisRun.ToString("N0") + " of " +
                             result.RetriedThisRun.ToString("N0") + " sector(s); " + (result.Bad + result.Pending).ToString("N0") +
                             " still failed." + (result.Sha256 != null ? " SHA-256 " + result.Sha256 : "");
                else if (result.Finished)
                    status = "Finished: " + counts + "." + (result.Sha256 != null ? " SHA-256 " + result.Sha256 : "") +
                             (result.Bad > 0 ? " \"Retry failed\" tries the bad sectors once more." : "");
                else if (result.Cancelled)
                    status = "Paused: " + counts + ". Choose \"Copy / resume\" to continue.";
                else if (result.Error != null)
                    status = "Stopped: " + result.Error + " (" + counts + ")";
                else
                    status = "Range done: " + counts + ".";
                if (result.CssDecrypting)
                    status += " CSS: " + result.CssDescrambled.ToString("N0") + " sector(s) decrypted" +
                              (result.CssLeftScrambled > 0 ? ", " + result.CssLeftScrambled.ToString("N0") + " left scrambled (see log)" : "") + ".";
                if (result.BlurayDecrypting)
                    status += " Blu-ray: " + result.BlurayUnitsDecrypted.ToString("N0") + " unit(s) decrypted" +
                              (result.BlurayUnitsFailed > 0 ? ", " + result.BlurayUnitsFailed.ToString("N0") + " not decrypted (see log)" : "") + ".";
                else if (_probe.Aacs && cfg.DecryptBluray)
                    status += " Blu-ray: not decrypted (no usable keys; see the log and \"Blu-ray keys\").";
                if (result.RefusedThisRun >= RescueJob.RefusalNoticeAfter)
                    status += " " + RefusalMessage(result.RefusedThisRun);
                _statusText.Text = status;
            }
            catch (Exception ex)
            {
                _log.Info("ERROR while copying: " + ex);
                if (!_closing) _statusText.Text = "Error: " + ex.Message;
            }
            finally
            {
                if (awake) KeepAwake(false);
                if (_cts != null) { _cts.Dispose(); _cts = null; }
                if (!_closing) SetBusy(false, false);
                else _busy = false;
            }
        }

        private void UpdateProgress(RescueProgress p)
        {
            if (_closing) return;
            double okPct = p.Total > 0 ? p.Ok * 100.0 / p.Total : 0;
            double phasePct = p.PhaseTotal > 0 ? p.PhaseDone * 100.0 / p.PhaseTotal : 100;
            _bar.Value = Math.Max(0, Math.Min(_bar.Maximum, (int)(okPct * 1000)));
            _progressText.Text =
                p.Phase + ": " + p.PhaseDone.ToString("N0") + " / " + p.PhaseTotal.ToString("N0") + " (" + phasePct.ToString("0.0") + "%) | LBA " + p.CurrentLba.ToString("N0") + "\r\n" +
                "Speed: " + (p.BytesPerSecond / 1048576.0).ToString("0.00") + " MiB/s | Elapsed: " + RescueJob.FormatTime(p.Elapsed) +
                " | Phase remaining: " + (p.Remaining.HasValue ? RescueJob.FormatTime(p.Remaining.Value) : "-") + "\r\n" +
                "Disc: " + okPct.ToString("0.000") + "% read | bad " + p.Bad.ToString("N0") + " | pending " + p.Pending.ToString("N0") +
                " | not tried " + p.Untried.ToString("N0") +
                (p.Refused > 0 ? " | refused " + p.Refused.ToString("N0") : "");
            Text = "xISOBurn - " + okPct.ToString("0.0") + "%" + (IsAdministrator() ? " (administrator)" : "");
            if (p.Refused >= RescueJob.RefusalNoticeAfter && !_refusalShown)
            {
                _refusalShown = true;
                _statusText.Text = RefusalMessage(p.Refused) + " \"Pause / cancel\" stops the copy.";
            }
        }

        private string RefusalMessage(long refused)
        {
            return "The drive refused " + refused.ToString("N0") + " read(s) at once: that is copy protection or a " +
                   "region mismatch, not disc damage" +
                   (_probe != null && _probe.Cpst == 1 ? " (check the CSS section of the log: the disc's region must match the drive's region)" : "") +
                   ". Retrying will not recover those sectors.";
        }

        // ------------------------------------------------------------ scan

        private async Task ScanAsync()
        {
            if (_busy) return;
            SetBusy(true, false);
            bool awake = false;
            try
            {
                string msg;
                var cfg = CopyConfig.Load(_folder, out msg);
                if (!await DetectAsync((uint)cfg.TimeoutSeconds))
                {
                    if (_drive != null) _statusText.Text = "No disc ready to scan (see log).";
                    return;
                }
                KeepAwake(true);
                awake = true;
                _cts = new CancellationTokenSource();
                SetBusy(true, true);
                _statusText.Text = "Scanning (reads ~90 spots once each, writes nothing)... \"Pause / cancel\" stops it.";
                var progress = new Progress<string>(s => { if (!_closing) _progressText.Text = s; });
                var scan = new DiscScan(_drive, _probe, _log, (uint)cfg.TimeoutSeconds);
                var token = _cts.Token;
                string summary = await Task.Run(() => scan.RunAsync(progress, token));
                if (!_closing) _statusText.Text = summary;
            }
            catch (OperationCanceledException)
            {
                if (!_closing) _statusText.Text = "Scan cancelled.";
            }
            catch (Exception ex)
            {
                _log.Info("ERROR while scanning: " + ex);
                if (!_closing) _statusText.Text = "Error: " + ex.Message;
            }
            finally
            {
                if (awake) KeepAwake(false);
                if (_cts != null) { _cts.Dispose(); _cts = null; }
                if (!_closing) SetBusy(false, false);
                else _busy = false;
            }
        }

        private void CancelRun()
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
                _statusText.Text = "Cancelling... (finishing the current block)";
            }
        }

        // ------------------------------------------------------------ settings and destination

        private void OpenSettings()
        {
            if (_busy) return;
            string msg;
            var cfg = CopyConfig.Load(_folder, out msg);
            using (var f = new SettingsForm(cfg))
            {
                if (f.ShowDialog(this) != DialogResult.OK || f.Result == null) return;
                SaveSettings(f.Result);
            }
        }

        private void SaveSettings(CopyConfig cfg)
        {
            cfg.Save(_folder);
            string msg;
            cfg = CopyConfig.Load(_folder, out msg); // normalises the values exactly as a run will see them
            ShowConfig(cfg);
            _log.Info("Settings saved: " + cfg.Describe());
            _statusText.Text = "Settings saved. They apply from the next run.";
            if (_probe != null) { var ignored = CheckPreviousRescueAsync(); }
        }

        private void ChooseDestination()
        {
            if (_busy) return;
            string msg;
            var cfg = CopyConfig.Load(_folder, out msg);
            using (var d = new FolderBrowserDialog
            {
                Description = "Folder for the copies (.iso, .map, .sha256). Blu-ray images need NTFS or exFAT (files over 4 GiB).",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
                SelectedPath = CopyStore.Resolve(cfg.OutputFolder),
            })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                cfg.OutputFolder = string.Equals(d.SelectedPath, CopyStore.DefaultFolder, StringComparison.OrdinalIgnoreCase) ? "" : d.SelectedPath;
                SaveSettings(cfg);
                _statusText.Text = "Copies go to " + CopyStore.Resolve(cfg.OutputFolder) + ".";
            }
        }

        private void OpenDestination()
        {
            string msg;
            var cfg = CopyConfig.Load(_folder, out msg);
            string folder = CopyStore.Resolve(cfg.OutputFolder);
            try
            {
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _statusText.Text = "Cannot open " + folder + ": " + ex.Message;
            }
        }

        // ------------------------------------------------------------ closing

        protected override async void OnFormClosing(FormClosingEventArgs e)
        {
            if (_closing || !_busy)
            {
                _closing = true;
                base.OnFormClosing(e);
                CloseDrive();
                return;
            }
            e.Cancel = true;
            bool running = _cts != null && !_cts.IsCancellationRequested;
            var answer = MessageBox.Show(this,
                running ? "A copy or scan is running. It will be paused; \"Copy / resume\" continues it next time.\n\nQuit xISOBurn?"
                        : "xISOBurn is busy with the drive. Quit anyway?",
                "Quit xISOBurn?", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
            _log.Info("Quit chosen while busy.");
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                // Let the copy stop at the end of the current block so the .map is saved.
                _cts.Cancel();
                _statusText.Text = "Pausing before quitting...";
                for (int i = 0; i < 300 && _busy; i++) await Task.Delay(100);
            }
            _closing = true;
            Close();
        }
    }
}
