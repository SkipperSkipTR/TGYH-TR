using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace TGYH_TR_Installer
{
    public partial class App : Application
    {
        /// <summary>
        /// A plugin package placed next to the installer is used instead of downloading one
        /// (offline installs, or trying a package before publishing it).
        /// </summary>
        public static string FindLocalPackage()
        {
            try
            {
                return Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "TGYHTurkishPatch*.zip")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private void OnStartup(object sender, StartupEventArgs e)
        {
            // Unattended use:  TGYH_TR.exe --install "<game folder>" [--package <zip>]
            //                  TGYH_TR.exe --uninstall "<game folder>"
            // Writes its progress to %TEMP%\TGYH_TR.log and exits with 0 on success.
            string[] args = e.Args;
            bool install = args.Length >= 2 && args[0] == "--install";
            bool uninstall = args.Length >= 2 && args[0] == "--uninstall";
            if (!install && !uninstall)
            {
                new MainWindow().Show();
                return;
            }

            string log = Path.Combine(Path.GetTempPath(), "TGYH_TR.log");
            int exitCode = 0;
            try
            {
                File.WriteAllText(log, "");
                if (!PatchInstaller.IsGameFolder(args[1]))
                    throw new DirectoryNotFoundException("Not the game folder: " + args[1]);

                var installer = new PatchInstaller(args[1])
                {
                    Status = text => File.AppendAllText(log, text + Environment.NewLine),
                    LocalPackage = FindLocalPackage(),
                };
                int packageArg = Array.IndexOf(args, "--package");
                if (packageArg >= 0 && packageArg + 1 < args.Length) installer.LocalPackage = args[packageArg + 1];

                if (install) installer.InstallAsync().GetAwaiter().GetResult();
                else installer.Uninstall();
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(log, "ERROR: " + ex + Environment.NewLine); } catch { }
                exitCode = 1;
            }
            Shutdown(exitCode);
        }
    }
}
