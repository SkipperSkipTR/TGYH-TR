using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace TGYH_TR_Installer
{
    /// <summary>What is currently in a game folder.</summary>
    public sealed class GameState
    {
        public string GameVersion;
        public bool Is64Bit;
        public bool BepInExInstalled;
        public bool BepInExIsOurs;          // installed by this tool, so uninstall may remove it
        public bool UnsupportedBepInEx;     // a BepInEx that is not the IL2CPP flavour
        public bool PatchInstalled;
        public string PatchVersion;         // null when unknown
        public bool LegacyPatchInstalled;   // the old asset-replacement patch ("Yedek" backup folder)
    }

    /// <summary>
    /// Installs the Turkish patch: restores files changed by the old asset-replacement patch,
    /// downloads BepInEx 6 (IL2CPP) from the BepInEx build server if the game doesn't have it,
    /// then downloads the newest plugin package from this project's GitHub releases.
    /// No UI in here; progress is reported through the callbacks.
    /// </summary>
    public sealed class PatchInstaller
    {
        public const string GameExeName = "Thank Goodness You're Here!.exe";
        public const string Repository = "SkipperSkipTR/TGYH-TR";
        public const string PluginFolderName = "TGYHTurkishPatch";

        // The BepInEx bleeding-edge build the plugin is developed and tested against.
        private const string BepInExBuild = "788";
        private const string BepInExCommit = "5b766a3";
        private const string BepInExVersion = "6.0.0-be." + BepInExBuild;

        private const string PackagePattern = @"^TGYHTurkishPatch.*\.zip$";
        private const string OwnershipMarker = "installed-by-tgyh-tr.txt";
        private const string LegacyBackupFolder = "Yedek";
        private static readonly string[] LegacyPluginFolders = { "TGYPatcher" };
        private static readonly string[] LoaderFiles = { "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt" };
        private static readonly string[] LoaderFolders = { "BepInEx", "dotnet" };

        private readonly string _gameDir;

        /// <summary>Status line for the user.</summary>
        public Action<string> Status = _ => { };
        /// <summary>0-100 for the current step.</summary>
        public Action<int> Progress = _ => { };
        /// <summary>Optional: install from this package instead of downloading one.</summary>
        public string LocalPackage;

        static PatchInstaller()
        {
            // .NET Framework 4.7.2 does not enable TLS 1.2/1.3 by default on every system.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;
        }

        public PatchInstaller(string gameDir)
        {
            _gameDir = Path.GetFullPath(gameDir);
        }

        private string InGame(params string[] parts) => Path.Combine(new[] { _gameDir }.Concat(parts).ToArray());

        /// <summary>
        /// Texture file names are long; inside a deep Steam library the full path can pass the
        /// classic 260-character limit. The \\?\ form is exempt from it.
        /// </summary>
        private static string Long(string path)
        {
            return path.StartsWith(@"\\") ? path : @"\\?\" + path;
        }
        private string PluginDir => InGame("BepInEx", "plugins", PluginFolderName);

        public static bool IsGameFolder(string dir)
        {
            return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, GameExeName));
        }

        public GameState Inspect()
        {
            var state = new GameState();

            var versionFile = Directory.GetFiles(_gameDir, "VERSION *.txt").FirstOrDefault();
            var match = versionFile == null ? Match.Empty : Regex.Match(Path.GetFileName(versionFile), @"VERSION (\d+(\.\d+)+)");
            state.GameVersion = match.Success ? match.Groups[1].Value : null;

            state.Is64Bit = IsPe64(InGame(GameExeName));
            state.BepInExInstalled = File.Exists(InGame("BepInEx", "core", "BepInEx.Unity.IL2CPP.dll"));
            state.UnsupportedBepInEx = !state.BepInExInstalled && Directory.Exists(InGame("BepInEx", "core"));
            state.BepInExIsOurs = File.Exists(InGame("BepInEx", OwnershipMarker));
            state.PatchInstalled = File.Exists(Path.Combine(PluginDir, PluginFolderName + ".dll"));
            string versionTxt = Path.Combine(PluginDir, "version.txt");
            if (state.PatchInstalled && File.Exists(versionTxt))
                state.PatchVersion = File.ReadAllText(versionTxt).Trim();
            state.LegacyPatchInstalled = Directory.Exists(InGame(LegacyBackupFolder));
            return state;
        }

        private static bool IsPe64(string exePath)
        {
            using (var fs = File.OpenRead(exePath))
            using (var reader = new BinaryReader(fs))
            {
                fs.Seek(0x3C, SeekOrigin.Begin);
                fs.Seek(reader.ReadInt32() + 4, SeekOrigin.Begin);
                return reader.ReadUInt16() == 0x8664; // IMAGE_FILE_MACHINE_AMD64
            }
        }

        public async Task InstallAsync()
        {
            var state = Inspect();
            if (state.UnsupportedBepInEx)
                throw new InvalidOperationException(
                    "Oyun klasöründe farklı bir BepInEx sürümü var. Yama BepInEx 6 (IL2CPP) gerektirir; " +
                    "önce mevcut BepInEx'i kaldırın.");

            if (state.LegacyPatchInstalled)
            {
                Status("Eski yama kaldırılıyor, özgün dosyalar geri yükleniyor...");
                RestoreLegacyBackup();
            }

            if (!state.BepInExInstalled)
                await InstallBepInExAsync(state.Is64Bit).ConfigureAwait(false);
            EnsureBepInExConfig();

            string package = LocalPackage;
            bool downloaded = false;
            if (package == null)
            {
                package = await DownloadLatestPackageAsync().ConfigureAwait(false);
                downloaded = true;
            }

            Status("Yama dosyaları kuruluyor...");
            RemovePlugin();
            ExtractZip(package, _gameDir);
            if (downloaded) TryDelete(package);

            if (!File.Exists(Path.Combine(PluginDir, PluginFolderName + ".dll")))
                throw new InvalidDataException("Yama paketi beklenen dosyaları içermiyor.");

            Progress(100);
            Status("Yama kuruldu. Oyunun ilk açılışı 1-2 dakika sürebilir.");
        }

        public void Uninstall()
        {
            var state = Inspect();

            Status("Yama siliniyor...");
            RemovePlugin();

            // Only take BepInEx out if this tool put it there; someone with other mods keeps theirs.
            if (state.BepInExIsOurs)
            {
                foreach (var folder in LoaderFolders)
                    if (Directory.Exists(InGame(folder))) Directory.Delete(Long(InGame(folder)), true);
                foreach (var file in LoaderFiles)
                    TryDelete(InGame(file));
            }

            if (state.LegacyPatchInstalled) RestoreLegacyBackup();

            Progress(100);
            Status("Yama silindi.");
        }

        private void RemovePlugin()
        {
            foreach (var name in LegacyPluginFolders.Concat(new[] { PluginFolderName }))
            {
                string dir = InGame("BepInEx", "plugins", name);
                if (Directory.Exists(dir)) Directory.Delete(Long(dir), true);
            }
        }

        /// <summary>
        /// The old patch replaced game asset files and kept the originals in "Yedek". The plugin
        /// works on the original files, so they have to go back.
        /// </summary>
        private void RestoreLegacyBackup()
        {
            string backup = InGame(LegacyBackupFolder);
            if (!Directory.Exists(backup)) return;

            foreach (string file in Directory.GetFiles(backup, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(_gameDir, file.Substring(backup.Length + 1));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            }
            Directory.Delete(backup, true);
        }

        private async Task InstallBepInExAsync(bool is64Bit)
        {
            string arch = is64Bit ? "x64" : "x86";
            string file = $"BepInEx-Unity.IL2CPP-win-{arch}-{BepInExVersion}+{BepInExCommit}.zip";
            string url = $"https://builds.bepinex.dev/projects/bepinex_be/{BepInExBuild}/{Uri.EscapeDataString(file)}";
            string temp = Path.Combine(Path.GetTempPath(), file);

            Status($"BepInEx {BepInExVersion} ({arch}) indiriliyor...");
            await DownloadAsync(url, temp).ConfigureAwait(false);

            Status("BepInEx kuruluyor...");
            ExtractZip(temp, _gameDir);
            TryDelete(temp);

            if (!File.Exists(InGame("BepInEx", "core", "BepInEx.Unity.IL2CPP.dll")))
                throw new InvalidDataException("BepInEx paketi beklenen dosyaları içermiyor.");

            File.WriteAllText(InGame("BepInEx", OwnershipMarker),
                "BepInEx was installed by the TGYH Turkish patch installer; uninstalling the patch removes it.\r\n");

        }

        /// <summary>
        /// Two BepInEx defaults have to be off for this game. Forwarding Unity's log makes
        /// BepInEx inject a class into the game, which crashes it at startup ("Class::Init
        /// signatures have been exhausted" followed by an access violation). And players
        /// shouldn't get a console window. BepInEx fills in every setting not written here.
        /// </summary>
        private void EnsureBepInExConfig()
        {
            string config = InGame("BepInEx", "config", "BepInEx.cfg");
            if (!File.Exists(config))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(config));
                File.WriteAllText(config,
                    "[Logging]\r\n\r\nUnityLogListening = false\r\n\r\n[Logging.Console]\r\n\r\nEnabled = false\r\n");
                return;
            }

            // An existing file (BepInEx already ran once, or the player's own): only the crashing setting.
            string text = File.ReadAllText(config);
            string updated = Regex.Replace(text, @"^(\s*UnityLogListening\s*=\s*)true\s*$", "${1}false",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (updated != text) File.WriteAllText(config, updated);
        }

        /// <summary>Finds the newest release carrying a plugin package and downloads it.</summary>
        private async Task<string> DownloadLatestPackageAsync()
        {
            Status("Son yama sürümü aranıyor...");
            string json;
            using (var client = CreateClient())
                json = await client.GetStringAsync($"https://api.github.com/repos/{Repository}/releases?per_page=30").ConfigureAwait(false);

            var releases = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<List<Dictionary<string, object>>>(json);
            foreach (var release in releases) // newest first
            {
                if (release.TryGetValue("draft", out var draft) && draft is bool isDraft && isDraft) continue;
                if (!(release["assets"] is System.Collections.IEnumerable assets)) continue;

                foreach (Dictionary<string, object> asset in assets)
                {
                    string name = asset["name"] as string;
                    if (name == null || !Regex.IsMatch(name, PackagePattern, RegexOptions.IgnoreCase)) continue;

                    string temp = Path.Combine(Path.GetTempPath(), name);
                    Status($"Yama indiriliyor ({release["tag_name"]})...");
                    await DownloadAsync((string)asset["browser_download_url"], temp).ConfigureAwait(false);

                    // GitHub publishes a SHA-256 for every uploaded asset.
                    if (asset.TryGetValue("digest", out var digest) && digest is string expected && expected.StartsWith("sha256:"))
                    {
                        Status("İndirilen dosya doğrulanıyor...");
                        if (!string.Equals(Sha256(temp), expected.Substring(7), StringComparison.OrdinalIgnoreCase))
                        {
                            TryDelete(temp);
                            throw new InvalidDataException("İndirilen yama dosyası bozuk. Lütfen tekrar deneyin.");
                        }
                    }
                    return temp;
                }
            }
            throw new InvalidOperationException("GitHub'da yayınlanmış bir yama paketi bulunamadı.");
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TGYH-TR-Installer");
            return client;
        }

        private async Task DownloadAsync(string url, string target)
        {
            Progress(0);
            using (var client = CreateClient())
            using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? -1;
                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    var buffer = new byte[1 << 16];
                    long done = 0;
                    int read, lastPercent = -1;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                    {
                        output.Write(buffer, 0, read);
                        done += read;
                        if (total > 0)
                        {
                            int percent = (int)(done * 100 / total);
                            if (percent != lastPercent) Progress(lastPercent = percent);
                        }
                    }
                }
            }
        }

        private void ExtractZip(string zipPath, string destination)
        {
            string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                long total = Math.Max(1, zip.Entries.Sum(e => e.Length));
                long done = 0;
                int lastPercent = -1;
                foreach (var entry in zip.Entries)
                {
                    string target = Path.GetFullPath(Path.Combine(root, entry.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Paket, oyun klasörünün dışına yazmaya çalışıyor: " + entry.FullName);

                    if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                    {
                        Directory.CreateDirectory(Long(target));
                        continue;
                    }

                    Directory.CreateDirectory(Long(Path.GetDirectoryName(target)));
                    entry.ExtractToFile(Long(target), true);
                    done += entry.Length;
                    int percent = (int)(done * 100 / total);
                    if (percent != lastPercent) Progress(lastPercent = percent);
                }
            }
        }

        private static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
