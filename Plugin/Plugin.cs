using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace TGYHTurkishPatch
{
    [BepInPlugin(Guid, Name, Version)]
    public class LocalizationPlugin : BasePlugin
    {
        public const string Guid = "com.tgyh.turkishpatch";
        public const string Name = "TGYH Turkish Patch";
        public const string Version = "3.0.0";

        public static LocalizationPlugin Instance { get; private set; }
        public new static ManualLogSource Log { get; private set; }
        public static Harmony HarmonyInstance { get; private set; }

        public static string ModDirectory { get; private set; }
        public static string TextsDirectory => Path.Combine(ModDirectory, "texts");
        public static string TexturesDirectory => Path.Combine(ModDirectory, "textures");
        public static string VideosDirectory => Path.Combine(ModDirectory, "videos");
        public static string FontsDirectory => Path.Combine(ModDirectory, "fonts");

        // Configuration entries
        public static ConfigEntry<bool> ModEnabled { get; private set; }
        public static ConfigEntry<bool> EnableTextReplacements { get; private set; }
        public static ConfigEntry<bool> EnableTextureReplacements { get; private set; }
        public static ConfigEntry<bool> EnableVideoReplacements { get; private set; }
        public static ConfigEntry<bool> EnableFontReplacements { get; private set; }
        public static ConfigEntry<KeyCode> ReloadHotKey { get; private set; }
        public static ConfigEntry<bool> VerboseLogging { get; private set; }
        public static ConfigEntry<bool> EnableTextureCache { get; private set; }
        public static ConfigEntry<bool> KeepOriginalTextures { get; private set; }
        public static ConfigEntry<int> TextureWorkerThreads { get; private set; }
        public static ConfigEntry<float> TextureScanInterval { get; private set; }
        public static bool IsTurkishActive { get; private set; } = false;

        public override void Load()
        {
            Instance = this;
            Log = base.Log;
            HarmonyInstance = new Harmony(Guid);

            ModDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrEmpty(ModDirectory))
            {
                ModDirectory = Path.Combine(Paths.PluginPath, "TGYHTurkishPatch");
            }

            // Ensure all mod folders exist
            Directory.CreateDirectory(TextsDirectory);
            Directory.CreateDirectory(TexturesDirectory);
            Directory.CreateDirectory(VideosDirectory);
            Directory.CreateDirectory(FontsDirectory);

            InitConfig();

            if (!ModEnabled.Value)
            {
                Log.LogWarning(Name + " is disabled via configuration.");
                return;
            }

            Log.LogInfo("=== Initializing " + Name + " v" + Version + " ===");

            // Pre-load modded assets
            ReloadAssets();

            // Apply Harmony patches
            ApplyPatches();

            Log.LogInfo(Name + " initialized successfully!");
        }

        private void InitConfig()
        {
            ModEnabled = Config.Bind("General", "Enabled", true, "Enable or disable the localization mod.");
            EnableTextReplacements = Config.Bind("Features", "EnableTextReplacements", true, "Enable text / subtitle / UI string replacements.");
            EnableTextureReplacements = Config.Bind("Features", "EnableTextureReplacements", true, "Enable texture replacements (loose .png or prebuilt .tgyc files).");
            EnableVideoReplacements = Config.Bind("Features", "EnableVideoReplacements", true, "Enable loose MP4 / WebM video replacements.");
            EnableFontReplacements = Config.Bind("Features", "EnableFontReplacements", true, "Enable custom font replacement.");

            ReloadHotKey = Config.Bind("Controls", "ReloadHotKey", KeyCode.F9, "Hotkey to reload mod texts, textures, videos and fonts from disk without restarting (F1-F12, A-Z or Alpha0-Alpha9).");
            VerboseLogging = Config.Bind("Debug", "VerboseLogging", false, "Log detailed asset replacement events.");

            EnableTextureCache = Config.Bind("Textures", "EnableDiskCache", true, "Store each replacement texture in GPU-ready form under cache/textures after its first use, so later loads skip PNG decoding. Entries are rebuilt automatically when a PNG changes.");
            KeepOriginalTextures = Config.Bind("Textures", "KeepOriginals", true, "Keep a copy of each replaced texture in video memory so switching the language back restores it instantly. Disable to save VRAM; originals then return when the texture is next reloaded.");
            TextureWorkerThreads = Config.Bind("Textures", "WorkerThreads", Math.Max(1, Math.Min(3, Environment.ProcessorCount - 2)), "Background threads that prepare replacement textures (1-8). Applied at game start.");
            TextureScanInterval = Config.Bind("Textures", "ScanInterval", 0.1f, "Seconds between checks for newly loaded textures.");

            // Read saved language selection (12 = Turkish, default to 12 if first run)
            int savedLang = PlayerPrefs.GetInt("TGY_CustomLanguage", 12);
            IsTurkishActive = (savedLang == 12);
            Log.LogInfo($"[LANGUAGE] Initial active language: {(IsTurkishActive ? "Türkçe (12)" : $"Standard ({savedLang})")}");
        }

        public static void SetTurkishActive(bool active)
        {
            if (IsTurkishActive == active) return;
            IsTurkishActive = active;

            Log.LogInfo($"[LANGUAGE] Turkish active state changed to: {active}");

            // Persist setting
            PlayerPrefs.SetInt("TGY_CustomLanguage", active ? LanguageMenuPatches.TURKISH_INDEX : LanguageMenuPatches.GetGameLanguage());
            PlayerPrefs.Save();

            if (active)
            {
                // The game itself runs in English underneath Turkish.
                LanguageMenuPatches.NormalizeGameLanguage();

                if (EnableFontReplacements != null && EnableFontReplacements.Value)
                {
                    FontManager.ApplyToMetaManager();
                }

                if (EnableTextReplacements != null && EnableTextReplacements.Value)
                {
                    TextManager.ApplyTranslations();
                }

                if (EnableTextureReplacements != null && EnableTextureReplacements.Value)
                {
                    TextureManager.ApplyTurkishTextures();
                }

                if (EnableVideoReplacements != null && EnableVideoReplacements.Value)
                {
                    VideoManager.ReapplyTurkishVideos();
                }

                // Let the game run its own language-changed pass too: it refreshes every
                // registered menu panel (not just the visible ones) and clears stale subtitles.
                try
                {
                    if (MetaManager.Actual != null) MetaManager.Actual.OnLanguageSet();
                }
                catch (Exception ex)
                {
                    Log.LogWarning($"[LANGUAGE] OnLanguageSet failed: {ex.Message}");
                }
            }
            else
            {
                FontManager.RestoreOriginalFonts();
                TextManager.RestoreOriginalTranslations(LanguageMenuPatches.GetGameLanguage());

                if (EnableTextureReplacements != null && EnableTextureReplacements.Value)
                {
                    TextureManager.RestoreOriginalTextures();
                }

                if (EnableVideoReplacements != null && EnableVideoReplacements.Value)
                {
                    VideoManager.RestoreOriginalVideos();
                }
            }
        }

        public static void ReloadAssets()
        {
            Log.LogInfo("Loading modded assets from disk...");

            if (EnableTextReplacements.Value)
            {
                TextManager.LoadAllTexts(TextsDirectory);
            }

            if (EnableTextureReplacements.Value)
            {
                TextureManager.LoadAllTextures(TexturesDirectory);
            }

            if (EnableVideoReplacements.Value)
            {
                VideoManager.LoadAllVideos(VideosDirectory);
            }

            if (EnableFontReplacements.Value)
            {
                FontManager.LoadFonts(FontsDirectory);
            }
        }

        private void ApplyPatches()
        {
            try
            {
                // Always patch game loop for hotkey and scene loading detection
                HarmonyInstance.PatchAll(typeof(GameLoopPatches));
                Log.LogInfo("GameLoop patches applied (hotkeys & scene hooks).");
            }
            catch (Exception ex)
            {
                Log.LogError("Error applying GameLoop patches: " + ex);
            }

            try
            {
                // Always patch language menu so Turkish option is available in Options
                HarmonyInstance.PatchAll(typeof(LanguageMenuPatches));
                Log.LogInfo("LanguageMenu patches applied.");
            }
            catch (Exception ex)
            {
                Log.LogError("Error applying LanguageMenu patches: " + ex);
            }

            try
            {
                if (EnableTextReplacements.Value)
                {
                    HarmonyInstance.PatchAll(typeof(TextPatches));
                    Log.LogInfo("Text patches applied.");
                }
            }
            catch (Exception ex)
            {
                Log.LogError("Error applying Text patches: " + ex);
            }

            try
            {
                if (EnableVideoReplacements.Value)
                {
                    HarmonyInstance.PatchAll(typeof(VideoPatches));
                    Log.LogInfo("Video patches applied.");
                }
            }
            catch (Exception ex)
            {
                Log.LogError("Error applying Video patches: " + ex);
            }

            try
            {
                if (EnableFontReplacements.Value)
                {
                    HarmonyInstance.PatchAll(typeof(FontPatches));
                    Log.LogInfo("Font patches applied.");
                }
            }
            catch (Exception ex)
            {
                Log.LogError("Error applying Font patches: " + ex);
            }
        }
    }
}
