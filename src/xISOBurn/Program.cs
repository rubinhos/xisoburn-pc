using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using xISOBurn.Bluray;
using xISOBurn.Core;
using xISOBurn.Util;

namespace xISOBurn
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            ApplicationConfiguration.Initialize();
            if (args.Length == 2 && args[0] == "--screenshots")
            {
                Screenshots(args[1]);
                return;
            }
            Application.Run(new MainForm());
        }

        /// <summary>
        /// For the README (run by .github/workflows/screenshots.yml): saves the main window, the settings
        /// and the Blu-ray keys dialogs as PNG files in 'folder', then exits. The windows are the real
        /// ones, as they look on that machine (on the build runner there is no optical drive).
        /// </summary>
        private static void Screenshots(string folder)
        {
            Directory.CreateDirectory(folder);
            using (var main = new MainForm())
            {
                main.StartPosition = FormStartPosition.Manual;
                main.Location = new Point(0, 0);
                main.Show();
                Settle(4000); // the drive list and the first log lines
                Save(main, Path.Combine(folder, "pc-main.png"));

                string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "xISOBurn");
                string msg;
                using (var settings = new SettingsForm(CopyConfig.Load(data, out msg)))
                {
                    settings.StartPosition = FormStartPosition.Manual;
                    settings.Location = new Point(40, 40);
                    settings.Show(main);
                    Settle(1000);
                    Save(settings, Path.Combine(folder, "pc-settings.png"));
                }
                using (var log = new Logger(Path.GetTempPath(), "xisoburn-screenshots", null))
                using (var keys = new KeysForm(BluRaySession.KeysFolder(data), log))
                {
                    keys.StartPosition = FormStartPosition.Manual;
                    keys.Location = new Point(40, 40);
                    keys.Show(main);
                    Settle(1000);
                    Save(keys, Path.Combine(folder, "pc-bluray-keys.png"));
                }
                main.Close();
            }
        }

        private static void Settle(int ms)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(50);
            }
        }

        private static void Save(Form f, string path)
        {
            using (var bmp = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                bmp.Save(path, ImageFormat.Png);
            }
        }
    }
}
