using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TGYH_TR_Installer
{
    /// <summary>Looks for the game in the usual Steam and Epic Games locations.</summary>
    public static class GameLocator
    {
        private const string SteamFolderName = "Thank Goodness You're Here!";

        public static string Find()
        {
            foreach (var candidate in Candidates())
            {
                try
                {
                    if (PatchInstaller.IsGameFolder(candidate)) return candidate;
                }
                catch { }
            }
            return null;
        }

        private static IEnumerable<string> Candidates()
        {
            foreach (var dir in SteamLibraries())
                yield return Path.Combine(dir, "steamapps", "common", SteamFolderName);
            foreach (var dir in EpicInstallLocations())
                yield return dir;
        }

        private static IEnumerable<string> SteamLibraries()
        {
            var result = new List<string>();
            try
            {
                string steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
                if (string.IsNullOrEmpty(steam)) return result;
                steam = steam.Replace('/', '\\');
                result.Add(steam);

                string libraries = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (File.Exists(libraries))
                {
                    foreach (Match m in Regex.Matches(File.ReadAllText(libraries), "\"path\"\\s+\"([^\"]+)\""))
                        result.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
                }
            }
            catch { }
            return result;
        }

        private static IEnumerable<string> EpicInstallLocations()
        {
            var result = new List<string>();
            try
            {
                string manifests = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Epic", "EpicGamesLauncher", "Data", "Manifests");
                if (!Directory.Exists(manifests)) return result;

                foreach (string file in Directory.GetFiles(manifests, "*.item"))
                {
                    var m = Regex.Match(File.ReadAllText(file), "\"InstallLocation\"\\s*:\\s*\"([^\"]+)\"");
                    if (m.Success) result.Add(Regex.Unescape(m.Groups[1].Value));
                }
            }
            catch { }
            return result;
        }
    }
}
