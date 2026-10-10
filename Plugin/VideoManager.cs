using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Video;

namespace TGYHTurkishPatch
{
    public static class VideoManager
    {
        private class OriginalVideoInfo
        {
            public VideoSource Source;
            public VideoClip Clip;
            public string Url;
        }

        // Video clip / GameObject name -> full path to replacement .mp4 or .webm
        private static readonly Dictionary<string, string> _replacementVideos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Original video configurations backed up before replacement
        private static readonly Dictionary<IntPtr, OriginalVideoInfo> _originalVideos = new Dictionary<IntPtr, OriginalVideoInfo>();

        // Tracked VideoPlayer instances
        private static readonly Dictionary<IntPtr, VideoPlayer> _trackedPlayers = new Dictionary<IntPtr, VideoPlayer>();

        public static int ReplacementCount => _replacementVideos.Count;

        public static void LoadAllVideos(string directory)
        {
            _replacementVideos.Clear();

            if (!Directory.Exists(directory))
                return;

            var files = Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext == ".mp4" || ext == ".webm" || ext == ".mov")
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    _replacementVideos[name] = Path.GetFullPath(file);
                }
            }

            LocalizationPlugin.Log.LogInfo($"Loaded {ReplacementCount} replacement video files from {directory}.");
        }

        public static bool TryGetReplacement(string targetName, out string fullPath)
        {
            if (string.IsNullOrEmpty(targetName))
            {
                fullPath = null;
                return false;
            }
            return _replacementVideos.TryGetValue(targetName, out fullPath);
        }

        public static bool ApplyReplacement(VideoPlayer player)
        {
            if (player == null || _replacementVideos.Count == 0)
                return false;

            if (!LocalizationPlugin.IsTurkishActive)
                return false;

            string targetName = null;
            string matchedPath = null;

            // 1. Try by clip name
            if (player.clip != null && !string.IsNullOrEmpty(player.clip.name))
            {
                if (TryGetReplacement(player.clip.name, out string clipPath))
                {
                    targetName = player.clip.name;
                    matchedPath = clipPath;
                }
            }

            // 2. Try by GameObject name
            if (matchedPath == null && player.gameObject != null && !string.IsNullOrEmpty(player.gameObject.name))
            {
                if (TryGetReplacement(player.gameObject.name, out string goPath))
                {
                    targetName = player.gameObject.name;
                    matchedPath = goPath;
                }
            }

            if (matchedPath != null)
            {
                // Backup original video config before first redirect
                if (!_originalVideos.ContainsKey(player.Pointer))
                {
                    _originalVideos[player.Pointer] = new OriginalVideoInfo
                    {
                        Source = player.source,
                        Clip = player.clip,
                        Url = player.url
                    };
                    _trackedPlayers[player.Pointer] = player;
                }

                return RedirectPlayer(player, targetName, matchedPath);
            }

            return false;
        }

        public static void RestoreOriginalVideos()
        {
            int restoredCount = 0;
            foreach (var kvp in _trackedPlayers)
            {
                try
                {
                    var player = kvp.Value;
                    if (player != null && _originalVideos.TryGetValue(kvp.Key, out var orig))
                    {
                        player.source = orig.Source;
                        if (orig.Source == VideoSource.VideoClip)
                        {
                            player.clip = orig.Clip;
                        }
                        else
                        {
                            player.url = orig.Url;
                        }
                        restoredCount++;
                        LocalizationPlugin.Log.LogInfo($"[VIDEO] Restored original video for '{player.name}'.");
                    }
                }
                catch (Exception ex)
                {
                    LocalizationPlugin.Log.LogWarning($"[VIDEO] Error restoring video player: {ex.Message}");
                }
            }
        }

        public static void ReapplyTurkishVideos()
        {
            foreach (var kvp in _trackedPlayers)
            {
                try
                {
                    var player = kvp.Value;
                    if (player != null)
                    {
                        ApplyReplacement(player);
                    }
                }
                catch { }
            }

            ApplyReplacementsInScene();
        }

        public static void ApplyReplacementsInScene()
        {
            if (_replacementVideos.Count == 0 || !LocalizationPlugin.IsTurkishActive) return;

            try
            {
                // Forget players that were destroyed with their scene.
                var dead = new List<IntPtr>();
                foreach (var kvp in _trackedPlayers)
                    if (kvp.Value == null) dead.Add(kvp.Key);
                foreach (var ptr in dead)
                {
                    _trackedPlayers.Remove(ptr);
                    _originalVideos.Remove(ptr);
                }

                var players = UnityEngine.Object.FindObjectsOfType<VideoPlayer>();
                if (players != null)
                {
                    foreach (var vp in players)
                    {
                        if (vp != null) ApplyReplacement(vp);
                    }
                }
            }
            catch { }
        }

        private static bool RedirectPlayer(VideoPlayer player, string name, string fullPath)
        {
            try
            {
                string url = "file:///" + fullPath.Replace('\\', '/');
                if (player.source == VideoSource.Url && player.url == url)
                    return true;

                player.source = VideoSource.Url;
                player.url = url;
                LocalizationPlugin.Log.LogInfo($"[VIDEO] Redirected VideoPlayer '{name}' to '{url}'.");
                return true;
            }
            catch (Exception ex)
            {
                LocalizationPlugin.Log.LogError($"[VIDEO] Failed to redirect VideoPlayer '{name}': {ex.Message}");
                return false;
            }
        }
    }
}
