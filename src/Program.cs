using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace EasierProgression
{
    static class Program
    {
        public static readonly string SettingsFile =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int processId);

        /// <summary>
        /// GrimDawnEasierProgression.exe                     settings window
        /// GrimDawnEasierProgression.exe --build             build with the saved settings, no window
        ///     [--settings file.json] [--out file.arz]       (--out: only write the .arz there, for testing)
        /// </summary>
        [STAThread]
        static int Main(string[] args)
        {
            // Dots in numbers and "1,234" counts regardless of the Windows region settings.
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;

            if (args.Contains("--build")) return CliBuild(args);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(SettingsFile));
            return 0;
        }

        public static Settings LoadSettings(string path)
        {
            var s = Settings.Load(path);
            if (!GameData.IsGameDir(s.GameDir)) s.GameDir = GameData.FindGameDir();
            return s;
        }

        static string Arg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        static int CliBuild(string[] args)
        {
            AttachConsole(-1); // print into the console we were started from, if any
            try
            {
                string file = Arg(args, "--settings") ?? SettingsFile;
                string outFile = Arg(args, "--out");
                var s = LoadSettings(file);
                Builder.Build(s, Console.WriteLine, outFile);
                if (outFile == null) s.Save(file);
                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("ERROR: " + e.Message);
                return 1;
            }
        }
    }
}
