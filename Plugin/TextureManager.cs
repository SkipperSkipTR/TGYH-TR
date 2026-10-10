using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace TGYHTurkishPatch
{
    /// <summary>
    /// Replaces the pixels of game textures with loose PNGs, matched by texture name.
    ///
    /// Nothing is decoded up front. Loaded Texture2D objects are polled a few times a second;
    /// when one matches a PNG, a worker thread turns that PNG into the texture's own GPU format
    /// (cached on disk afterwards) and the main thread only has to upload the bytes.
    /// </summary>
    public static class TextureManager
    {
        private enum TextureState { Pending, Replaced, Original, Failed }

        private sealed class Tracked
        {
            public Texture2D Texture;
            public string Name;
            public string PngPath;
            public Texture2D Backup;
            public bool BackupTried;
            public TextureState State;
        }

        // Max main-thread time spent uploading finished textures per frame.
        private const double ApplyBudgetMs = 6.0;
        // Every Nth scan also drops destroyed textures and forgets which pointers were rejected.
        private const int ScansPerPrune = 50;
        private const string BackupName = "TGY_OriginalBackup";

        private static readonly Dictionary<string, string> _replacementPngPaths =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<IntPtr, Tracked> _tracked = new Dictionary<IntPtr, Tracked>();
        // IL2CPP object pointers already checked and found to have no replacement.
        private static readonly HashSet<IntPtr> _rejected = new HashSet<IntPtr>();
        private static readonly List<IntPtr> _scratch = new List<IntPtr>();
        // Atlas names resolved (or ruled out) by picture this session: name -> replacement key.
        private static readonly Dictionary<string, string> _fingerprintMatches =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _fingerprintMisses =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Stopwatch _clock = Stopwatch.StartNew();

        private static string _texturesDirectory;
        private static string _cacheDirectory;
        private static bool _keepOriginals = true;
        private static long _scanIntervalMs = 100;
        private static long _nextScanAt;
        private static int _scansSincePrune;
        private static bool _scanRequested;
        private static bool _copyTextureChecked;
        private static bool _copyTextureSupported;
        private static Il2CppSystem.Type _texture2DType;

        private static int _statReplaced, _statPrebuilt, _statFromCache, _statEncoded, _statFallback, _statFailed;
        private static bool _statsDirty;
        private static long _nextStatsAt;

        public static int ReplacementCount => _replacementPngPaths.Count;

        #region Name normalization
        public static string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            string s = name;
            if (s.EndsWith("(Clone)", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(0, s.Length - 7);
            s = s.Replace('|', '_');
            return s.Trim();
        }

        public static string ExtractCleanTextureName(string rawName)
        {
            if (string.IsNullOrEmpty(rawName)) return rawName;
            string clean = rawName;

            int cabIdx = clean.LastIndexOf("-CAB-", StringComparison.OrdinalIgnoreCase);
            if (cabIdx > 0) return clean.Substring(0, cabIdx);

            int dotAssetsIdx = clean.LastIndexOf(".assets-", StringComparison.OrdinalIgnoreCase);
            if (dotAssetsIdx > 0)
            {
                int dashBeforeAssets = clean.LastIndexOf('-', dotAssetsIdx);
                if (dashBeforeAssets > 0) return clean.Substring(0, dashBeforeAssets);
            }
            int saIdx = clean.LastIndexOf("-sharedassets", StringComparison.OrdinalIgnoreCase);
            if (saIdx > 0) return clean.Substring(0, saIdx);

            int resIdx = clean.LastIndexOf("-resources.assets", StringComparison.OrdinalIgnoreCase);
            if (resIdx > 0) return clean.Substring(0, resIdx);

            int hashIdx = clean.LastIndexOf(" #", StringComparison.Ordinal);
            if (hashIdx > 0 && hashIdx + 2 < clean.Length &&
                long.TryParse(clean.Substring(hashIdx + 2), out _))
                return clean.Substring(0, hashIdx);

            return clean;
        }
        #endregion

        #region Indexing

        private static void IndexKey(string key, string fullPath)
        {
            if (string.IsNullOrEmpty(key)) return;
            string norm = NormalizeName(key);
            if (!string.IsNullOrEmpty(norm))
                _replacementPngPaths[norm] = fullPath;
        }

        private static bool IsPrebuilt(string path)
        {
            return path.EndsWith(TextureCacheFile.Extension, StringComparison.OrdinalIgnoreCase);
        }

        private static int GetFolderPriority(string filePath, string rootDir)
        {
            try
            {
                string rel = Path.GetRelativePath(rootDir, filePath).Replace('\\', '/').ToLowerInvariant();
                if (!rel.Contains('/')) return 3;
                if (rel.StartsWith("sharedassets/")) return 2;
                if (rel.StartsWith("compbundles/")) return 1;
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// (Re)builds the name -> PNG index. Only file names are read here; safe to call from
        /// plugin Load() and again for a hot reload.
        /// </summary>
        public static void LoadAllTextures(string directory)
        {
            _texturesDirectory = directory;
            _keepOriginals = LocalizationPlugin.KeepOriginalTextures.Value;
            _scanIntervalMs = Math.Max(20, (long)(LocalizationPlugin.TextureScanInterval.Value * 1000f));
            _cacheDirectory = null;
            if (LocalizationPlugin.EnableTextureCache.Value)
            {
                try
                {
                    _cacheDirectory = Path.Combine(LocalizationPlugin.ModDirectory, "cache", "textures");
                    Directory.CreateDirectory(_cacheDirectory);
                }
                catch (Exception ex)
                {
                    _cacheDirectory = null;
                    LocalizationPlugin.Log.LogWarning($"[TEXTURE] Disk cache unavailable: {ex.Message}");
                }
            }

            TextureLoadQueue.CancelAll();
            _replacementPngPaths.Clear();
            _fingerprintMisses.Clear();

            if (Directory.Exists(directory))
            {
                var found = new List<string>(Directory.GetFiles(directory, "*.png", SearchOption.AllDirectories));
                found.AddRange(Directory.GetFiles(directory, "*" + TextureCacheFile.Extension, SearchOption.AllDirectories));
                var files = found.ToArray();
                // Later entries win, so sort lowest priority first. Within a folder a PNG beats a
                // prebuilt file of the same name (the PNG is the editable source); remaining ties
                // are broken by path to keep the choice stable from run to run.
                Array.Sort(files, (a, b) =>
                {
                    int byPriority = GetFolderPriority(a, directory).CompareTo(GetFolderPriority(b, directory));
                    if (byPriority != 0) return byPriority;
                    int byKind = IsPrebuilt(b).CompareTo(IsPrebuilt(a));
                    return byKind != 0 ? byKind : string.CompareOrdinal(a, b);
                });

                foreach (var file in files)
                {
                    try
                    {
                        var rawName = Path.GetFileNameWithoutExtension(file);
                        var fullPath = Path.GetFullPath(file);
                        IndexKey(rawName, fullPath);

                        var clean = ExtractCleanTextureName(rawName);
                        if (!string.IsNullOrEmpty(clean)) IndexKey(clean, fullPath);
                    }
                    catch (Exception ex)
                    {
                        LocalizationPlugin.Log.LogError($"[TEXTURE] Index error '{file}': {ex.Message}");
                    }
                }

                TextureFingerprint.Load(Path.Combine(directory, "signatures.txt"), _replacementPngPaths.ContainsKey);

                // Atlases recognised by picture earlier in this session keep their match.
                foreach (var kvp in _fingerprintMatches)
                {
                    if (!_replacementPngPaths.ContainsKey(kvp.Key) &&
                        _replacementPngPaths.TryGetValue(kvp.Value, out string matched))
                        _replacementPngPaths[kvp.Key] = matched;
                }

                LocalizationPlugin.Log.LogInfo(
                    $"Indexed {_replacementPngPaths.Count} texture keys from {files.Length} texture files; " +
                    $"{TextureFingerprint.Count} atlases can also be recognised by picture.");
            }

            TextureLoadQueue.Start(Math.Max(1, Math.Min(8, LocalizationPlugin.TextureWorkerThreads.Value)));
            CleanCacheInBackground();
            RebindTracked();
        }

        /// <summary>After a hot reload: re-apply textures already on screen, or put them back if their PNG is gone.</summary>
        private static void RebindTracked()
        {
            _rejected.Clear();
            _scanRequested = true;
            if (_tracked.Count == 0) return;

            _scratch.Clear();
            foreach (var kvp in _tracked)
            {
                var entry = kvp.Value;
                if (!IsAlive(entry.Texture)) { _scratch.Add(kvp.Key); continue; }

                if (_replacementPngPaths.TryGetValue(NormalizeName(entry.Name), out string path))
                {
                    entry.PngPath = path;
                    if (LocalizationPlugin.IsTurkishActive) Queue(entry);
                }
                else
                {
                    if (entry.State == TextureState.Replaced) Restore(entry);
                    _scratch.Add(kvp.Key);
                }
            }
            foreach (var ptr in _scratch) Forget(ptr);
        }

        #endregion

        #region Per-frame update

        public static void RequestScan() => _scanRequested = true;

        /// <summary>Call once per frame on the main thread.</summary>
        public static void Update()
        {
            ApplyFinishedJobs();

            if (!LocalizationPlugin.IsTurkishActive || _replacementPngPaths.Count == 0) return;

            long now = _clock.ElapsedMilliseconds;
            if (_scanRequested || now >= _nextScanAt)
            {
                _scanRequested = false;
                _nextScanAt = now + _scanIntervalMs;
                try { Scan(); }
                catch (Exception ex)
                {
                    LocalizationPlugin.Log.LogError($"[TEXTURE] Scan error: {ex.Message}");
                    _nextScanAt = now + 5000;
                }
            }
        }

        /// <summary>
        /// Finds textures that appeared since the last scan. Walks the IL2CPP array by raw
        /// pointer so textures already seen cost one hash lookup and no allocation.
        /// </summary>
        private static unsafe void Scan()
        {
            if (++_scansSincePrune >= ScansPerPrune) Prune();

            if (_texture2DType == null) _texture2DType = Il2CppType.Of<Texture2D>();
            var all = Resources.FindObjectsOfTypeAll(_texture2DType);
            if (all == null) return;

            int count = all.Length;
            IntPtr* elements = (IntPtr*)((byte*)all.Pointer + 4 * IntPtr.Size);
            for (int i = 0; i < count; i++)
            {
                IntPtr ptr = elements[i];
                if (ptr == IntPtr.Zero || _tracked.ContainsKey(ptr) || !_rejected.Add(ptr)) continue;

                var tex = new Texture2D(ptr);
                string name = tex.name;
                if (string.IsNullOrEmpty(name)) continue;

                string normalized = NormalizeName(name);
                if (!_replacementPngPaths.TryGetValue(normalized, out string path))
                {
                    path = ResolveByFingerprint(tex, normalized);
                    if (path == null) continue;
                }
                else if (LocalizationPlugin.VerboseLogging.Value &&
                         TextureFingerprint.TryMeasure(tex, normalized, out double distance))
                {
                    LocalizationPlugin.Log.LogInfo($"[TEXTURE] Fingerprint check '{name}': distance {distance:0.00}");
                }

                _rejected.Remove(ptr);
                var entry = new Tracked { Texture = tex, Name = name, PngPath = path };
                _tracked[ptr] = entry;
                Queue(entry);
            }
        }

        /// <summary>
        /// An atlas whose name differs from a replacement only by its build-specific hash: use
        /// that replacement if the loaded picture is the one it was made for.
        /// </summary>
        private static string ResolveByFingerprint(Texture2D tex, string normalizedName)
        {
            if (TextureFingerprint.Count == 0 || _fingerprintMisses.Contains(normalizedName)) return null;

            string key = TextureFingerprint.FindMatch(tex, normalizedName, out double distance);
            if (key == null || !_replacementPngPaths.TryGetValue(key, out string path))
            {
                _fingerprintMisses.Add(normalizedName);
                if (!double.IsNaN(distance))
                    LocalizationPlugin.Log.LogInfo(
                        $"[TEXTURE] '{normalizedName}' resembles a replaced atlas by name but its picture differs (distance {distance:0.0}); left as is.");
                return null;
            }

            _fingerprintMatches[normalizedName] = key;
            _replacementPngPaths[normalizedName] = path;
            LocalizationPlugin.Log.LogInfo(
                $"[TEXTURE] '{normalizedName}' recognised as '{key}' by picture (distance {distance:0.00}).");
            return path;
        }

        private static void Prune()
        {
            _scansSincePrune = 0;
            // Destroyed textures free their IL2CPP object, and the address can be handed to a new one.
            _rejected.Clear();

            _scratch.Clear();
            foreach (var kvp in _tracked)
                if (!IsAlive(kvp.Value.Texture)) _scratch.Add(kvp.Key);
            foreach (var ptr in _scratch) Forget(ptr);
        }

        private static void Forget(IntPtr ptr)
        {
            if (!_tracked.TryGetValue(ptr, out var entry)) return;
            _tracked.Remove(ptr);
            if (IsAlive(entry.Backup))
            {
                try { UnityEngine.Object.Destroy(entry.Backup); } catch { }
            }
            entry.Backup = null;
        }

        private static bool IsAlive(Texture2D tex)
        {
            return !ReferenceEquals(tex, null) && tex.Pointer != IntPtr.Zero && tex.m_CachedPtr != IntPtr.Zero;
        }

        #endregion

        #region Queueing

        private static void Queue(Tracked entry)
        {
            try
            {
                var tex = entry.Texture;
                var job = new TextureJob
                {
                    Target = tex.Pointer,
                    PngPath = entry.PngPath,
                    Width = tex.width,
                    Height = tex.height,
                    // Only single-mip textures take the raw path; Unity's loader rebuilds mip chains.
                    Format = tex.mipmapCount == 1 ? ToRawFormat(tex.format) : RawFormat.None,
                };
                job.Prebuilt = IsPrebuilt(entry.PngPath);
                if (_cacheDirectory != null && job.Format != RawFormat.None && !job.Prebuilt)
                    job.CacheFile = Path.Combine(_cacheDirectory, CacheStem(entry.PngPath) + "_" + (int)job.Format + ".tgyc");

                entry.State = TextureState.Pending;
                TextureLoadQueue.Enqueue(job);
            }
            catch (Exception ex)
            {
                entry.State = TextureState.Failed;
                LocalizationPlugin.Log.LogError($"[TEXTURE] Could not queue '{entry.Name}': {ex.Message}");
            }
        }

        private static RawFormat ToRawFormat(TextureFormat format)
        {
            switch (format)
            {
                case TextureFormat.RGBA32: return RawFormat.RGBA32;
                case TextureFormat.ARGB32: return RawFormat.ARGB32;
                case TextureFormat.BGRA32: return RawFormat.BGRA32;
                case TextureFormat.RGB24: return RawFormat.RGB24;
                case TextureFormat.RGB565: return RawFormat.RGB565;
                case TextureFormat.Alpha8: return RawFormat.Alpha8;
                case TextureFormat.DXT1:
                case TextureFormat.DXT1Crunched: return RawFormat.BC1;
                case TextureFormat.DXT5:
                case TextureFormat.DXT5Crunched: return RawFormat.BC3;
                default: return RawFormat.None;
            }
        }

        private static TextureFormat ToUnityFormat(RawFormat format)
        {
            switch (format)
            {
                case RawFormat.RGBA32: return TextureFormat.RGBA32;
                case RawFormat.ARGB32: return TextureFormat.ARGB32;
                case RawFormat.BGRA32: return TextureFormat.BGRA32;
                case RawFormat.RGB24: return TextureFormat.RGB24;
                case RawFormat.RGB565: return TextureFormat.RGB565;
                case RawFormat.Alpha8: return TextureFormat.Alpha8;
                case RawFormat.BC1: return TextureFormat.DXT1;
                default: return TextureFormat.DXT5;
            }
        }

        /// <summary>Crunched textures are plain DXT once on the GPU.</summary>
        private static TextureFormat UncrunchedFormat(TextureFormat format)
        {
            if (format == TextureFormat.DXT1Crunched) return TextureFormat.DXT1;
            if (format == TextureFormat.DXT5Crunched) return TextureFormat.DXT5;
            return format;
        }

        private static string CacheStem(string pngPath)
        {
            string key = pngPath;
            if (_texturesDirectory != null && pngPath.StartsWith(_texturesDirectory, StringComparison.OrdinalIgnoreCase))
                key = pngPath.Substring(_texturesDirectory.Length);
            key = key.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

            ulong hash = 14695981039346656037UL; // FNV-1a
            foreach (char c in key)
            {
                hash = (hash ^ (byte)c) * 1099511628211UL;
                hash = (hash ^ (byte)(c >> 8)) * 1099511628211UL;
            }
            return hash.ToString("x16");
        }

        /// <summary>Deletes cache entries whose PNG no longer exists.</summary>
        private static void CleanCacheInBackground()
        {
            string dir = _cacheDirectory;
            if (dir == null) return;

            var live = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in _replacementPngPaths.Values) live.Add(CacheStem(path));

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    foreach (var file in Directory.GetFiles(dir))
                    {
                        string name = Path.GetFileName(file);
                        if (!name.EndsWith(".tgyc", StringComparison.OrdinalIgnoreCase)) continue;
                        if (name.Length > 16 && live.Contains(name.Substring(0, 16))) continue;
                        try { File.Delete(file); } catch { }
                    }
                }
                catch { }
            });
        }

        #endregion

        #region Applying

        private static void ApplyFinishedJobs()
        {
            if (_tracked.Count == 0 && TextureLoadQueue.Outstanding == 0) return;

            long start = _clock.ElapsedTicks;
            while (TextureLoadQueue.TryDequeueDone(out var job))
            {
                if (_tracked.TryGetValue(job.Target, out var entry) && entry.State == TextureState.Pending)
                {
                    if (!IsAlive(entry.Texture)) Forget(job.Target);
                    else if (!LocalizationPlugin.IsTurkishActive) entry.State = TextureState.Original;
                    else Apply(entry, job);
                }

                if ((_clock.ElapsedTicks - start) * 1000.0 / Stopwatch.Frequency >= ApplyBudgetMs) break;
            }

            if (_statsDirty && _clock.ElapsedMilliseconds >= _nextStatsAt && TextureLoadQueue.Outstanding == 0)
            {
                _statsDirty = false;
                _nextStatsAt = _clock.ElapsedMilliseconds + 5000;
                LocalizationPlugin.Log.LogInfo(
                    $"[TEXTURE] {_statReplaced} replaced so far ({_statPrebuilt} prebuilt, {_statFromCache} from cache, {_statEncoded} encoded, " +
                    $"{_statFallback} via LoadImage, {_statFailed} failed); {_tracked.Count} tracked.");
            }
        }

        private static void Apply(Tracked entry, TextureJob job)
        {
            _statsDirty = true;
            bool verbose = LocalizationPlugin.VerboseLogging.Value;
            try
            {
                if (job.Note != null && (verbose || job.Note.StartsWith("texture cache disabled")))
                    LocalizationPlugin.Log.LogInfo($"[TEXTURE] '{entry.Name}': {job.Note}");

                if (job.Error != null)
                {
                    entry.State = TextureState.Failed;
                    _statFailed++;
                    LocalizationPlugin.Log.LogWarning($"[TEXTURE] '{entry.Name}' failed: {job.Error} ({entry.PngPath})");
                    return;
                }

                var tex = entry.Texture;
                EnsureBackup(entry);

                string how;
                if (job.Raw != null && ApplyRaw(tex, job))
                {
                    how = job.Prebuilt ? "prebuilt" : (job.FromCache ? "cache" : "encoded");
                    if (job.Prebuilt) _statPrebuilt++; else if (job.FromCache) _statFromCache++; else _statEncoded++;
                }
                else if (job.Prebuilt)
                {
                    throw new InvalidOperationException("prebuilt texture does not fit the game's texture");
                }
                else
                {
                    // Format/size the raw path can't handle: let Unity decode the PNG itself.
                    byte[] png = job.PngBytes ?? File.ReadAllBytes(entry.PngPath);
                    if (!ImageConversion.LoadImage(tex, new Il2CppStructArray<byte>(png), !tex.isReadable))
                        throw new InvalidOperationException("ImageConversion.LoadImage rejected the PNG");
                    how = "LoadImage";
                    _statFallback++;
                }

                entry.State = TextureState.Replaced;
                _statReplaced++;
                if (verbose)
                    LocalizationPlugin.Log.LogInfo($"[TEXTURE] Replaced '{entry.Name}' ({job.Width}x{job.Height}, {how}).");
            }
            catch (Exception ex)
            {
                entry.State = TextureState.Failed;
                _statFailed++;
                LocalizationPlugin.Log.LogError($"[TEXTURE] Replace error '{entry.Name}': {ex.Message}");
            }
        }

        /// <summary>Uploads pre-encoded bytes. Returns false if this texture needs the LoadImage fallback.</summary>
        private static unsafe bool ApplyRaw(Texture2D tex, TextureJob job)
        {
            if (tex.width != job.Width || tex.height != job.Height || tex.mipmapCount != 1) return false;

            TextureFormat uploadFormat = ToUnityFormat(job.Format);

            // Readable textures can take the data directly, which also keeps their CPU copy in sync.
            if (tex.isReadable && tex.format == uploadFormat)
            {
                fixed (byte* data = job.Raw)
                    tex.LoadRawTextureData((IntPtr)data, job.Raw.Length);
                tex.Apply(false, false);
                return true;
            }

            // Non-readable (and crunched) textures have no CPU data to overwrite: upload into a
            // scratch texture of the same GPU format and copy it across on the GPU.
            if (!CopyTextureSupported) return false;

            var upload = new Texture2D(job.Width, job.Height, uploadFormat, 1, IsLinear(tex));
            try
            {
                if (upload.graphicsFormat != tex.graphicsFormat) return false;
                fixed (byte* data = job.Raw)
                    upload.LoadRawTextureData((IntPtr)data, job.Raw.Length);
                upload.Apply(false, true);
                Graphics.CopyTexture(upload, tex);
                return true;
            }
            finally
            {
                UnityEngine.Object.Destroy(upload);
            }
        }

        private static bool IsLinear(Texture2D tex)
        {
            return !GraphicsFormatUtility.IsSRGBFormat(tex.graphicsFormat);
        }

        private static bool CopyTextureSupported
        {
            get
            {
                if (!_copyTextureChecked)
                {
                    _copyTextureChecked = true;
                    try { _copyTextureSupported = SystemInfo.copyTextureSupport != UnityEngine.Rendering.CopyTextureSupport.None; }
                    catch { _copyTextureSupported = false; }
                }
                return _copyTextureSupported;
            }
        }

        #endregion

        #region Restore / Reapply

        /// <summary>Keeps a GPU-only copy of the original pixels so the language can be switched back.</summary>
        private static void EnsureBackup(Tracked entry)
        {
            if (!_keepOriginals || entry.BackupTried) return;
            entry.BackupTried = true;
            if (!CopyTextureSupported) return;

            var tex = entry.Texture;
            Texture2D backup = null;
            try
            {
                backup = new Texture2D(tex.width, tex.height, UncrunchedFormat(tex.format), tex.mipmapCount, IsLinear(tex));
                if (backup.graphicsFormat != tex.graphicsFormat)
                {
                    UnityEngine.Object.Destroy(backup);
                    return;
                }
                backup.name = BackupName;
                backup.hideFlags = HideFlags.HideAndDontSave;
                backup.Apply(false, true); // drop the CPU copy before filling the GPU side
                Graphics.CopyTexture(tex, backup);
                entry.Backup = backup;
            }
            catch (Exception ex)
            {
                if (backup != null) UnityEngine.Object.Destroy(backup);
                LocalizationPlugin.Log.LogWarning(
                    $"[TEXTURE] Backup failed for '{entry.Name}' ({tex.format}): {ex.Message}");
            }
        }

        private static bool Restore(Tracked entry)
        {
            var tex = entry.Texture;
            var backup = entry.Backup;
            if (!IsAlive(tex) || !IsAlive(backup)) return false;
            try
            {
                // LoadImage may have changed the size or format; a GPU copy then no longer fits.
                if (backup.width != tex.width || backup.height != tex.height ||
                    backup.mipmapCount != tex.mipmapCount || backup.graphicsFormat != tex.graphicsFormat)
                    return false;
                Graphics.CopyTexture(backup, tex);
                return true;
            }
            catch { return false; }
        }

        public static void RestoreOriginalTextures()
        {
            TextureLoadQueue.CancelAll();

            int restored = 0, stuck = 0;
            foreach (var entry in _tracked.Values)
            {
                if (entry.State == TextureState.Replaced)
                {
                    if (Restore(entry)) restored++; else stuck++;
                }
                if (entry.State != TextureState.Failed) entry.State = TextureState.Original;
            }

            if (restored == 0 && stuck == 0) return;
            LocalizationPlugin.Log.LogInfo(stuck == 0
                ? $"[TEXTURE] Restored {restored} textures."
                : $"[TEXTURE] Restored {restored} textures; {stuck} keep the modded image until they are reloaded.");
        }

        public static void ApplyTurkishTextures()
        {
            int queued = 0;
            foreach (var entry in _tracked.Values)
            {
                if (entry.State != TextureState.Original || !IsAlive(entry.Texture)) continue;
                Queue(entry);
                queued++;
            }
            _scanRequested = true;
            if (queued > 0)
                LocalizationPlugin.Log.LogInfo($"[TEXTURE] Re-applying {queued} modded textures.");
        }

        #endregion
    }
}
