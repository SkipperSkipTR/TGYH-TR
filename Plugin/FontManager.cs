using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace TGYHTurkishPatch
{
    public static class FontManager
    {
        public static TMP_FontAsset CustomFont { get; private set; }
        public static AssetBundle LoadedBundle { get; private set; }

        public static TMP_FontAsset OriginalSmile { get; private set; }
        public static TMP_FontAsset OriginalFallback { get; private set; }
        public static TMP_FontAsset OriginalAltMenu { get; private set; }
        private static bool _origSaved = false;

        private static Texture _origSubtitleMaterialTex = null;
        private static bool _hasOrigSubtitleTex = false;
        private static float _origTexWidth = -1f;
        private static float _origTexHeight = -1f;
        private static float _origGradientScale = -1f;
        private static float _origScaleRatioA = -1f;
        private static float _origScaleRatioB = -1f;
        private static float _origScaleRatioC = -1f;

        public static void EnsureFontsLoaded()
        {
            if (CustomFont == null)
            {
                LoadFonts(LocalizationPlugin.FontsDirectory);
            }
        }

        public static void LoadFonts(string directory)
        {
            if (LoadedBundle != null)
            {
                // Let the old copy be collected once nothing uses it any more.
                if (CustomFont != null) CustomFont.hideFlags &= ~HideFlags.DontUnloadUnusedAsset;
                try { LoadedBundle.Unload(false); } catch { }
                LoadedBundle = null;
            }
            CustomFont = null;

            if (!Directory.Exists(directory))
                return;

            // Search for asset bundles in directory
            var files = Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                // Skip raw font / text files
                if (ext == ".otf" || ext == ".ttf" || ext == ".txt" || ext == ".md" || ext == ".json" || ext == ".csv")
                    continue;

                try
                {
                    var bundle = AssetBundle.LoadFromFile(file);
                    if (bundle != null)
                    {
                        var allAssets = bundle.LoadAllAssets();
                        if (allAssets != null && allAssets.Length > 0)
                        {
                            foreach (var obj in allAssets)
                            {
                                if (obj == null) continue;
                                if (LocalizationPlugin.VerboseLogging.Value) LocalizationPlugin.Log.LogInfo($"[FONT] Asset in bundle '{Path.GetFileName(file)}': '{obj.name}' of type {obj.GetIl2CppType().Name}");

                                var fontAsset = obj.TryCast<TMP_FontAsset>();
                                if (fontAsset != null && CustomFont == null)
                                {
                                    CustomFont = fontAsset;
                                    CustomFont.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                                    LoadedBundle = bundle;
                                    LocalizationPlugin.Log.LogInfo($"[FONT] Successfully loaded custom TMP_FontAsset '{CustomFont.name}' from {file}!");
                                }
                            }
                        }

                        if (CustomFont != null)
                        {
                            ApplyToMetaManager();
                            return;
                        }
                        bundle.Unload(true);
                    }
                }
                catch (Exception ex)
                {
                    LocalizationPlugin.Log.LogWarning($"[FONT] Error inspecting file {file} as AssetBundle: {ex.Message}");
                }
            }

            if (CustomFont == null)
            {
                LocalizationPlugin.Log.LogWarning("[FONT] No custom TMP_FontAsset found in font bundles.");
            }
        }

        public static void SaveOriginalFonts(MetaManager meta)
        {
            if (_origSaved || meta == null) return;
            try
            {
                OriginalFallback = meta.fallbackFont;
                OriginalSmile = meta.smileFont;
                OriginalAltMenu = meta.altMenuFont;
                // The mod is about to overwrite the only references to these assets. Without this
                // flag Unity's next unused-asset sweep destroys them (NotoSans went first), and
                // every language that needs them falls back to TMP's default font.
                Protect(OriginalSmile, 0);
                Protect(OriginalFallback, 0);
                Protect(OriginalAltMenu, 0);
                _origSaved = true;
                LocalizationPlugin.Log.LogInfo($"[FONT] Saved original fonts: Smile='{OriginalSmile?.name}', Fallback='{OriginalFallback?.name}'");
            }
            catch { }
        }

        /// <summary>Keeps a font asset and its fallback chain loaded while nothing in the scene references it.</summary>
        private static void Protect(TMP_FontAsset font, int depth)
        {
            if (font == null || depth > 4) return;
            font.hideFlags |= HideFlags.DontUnloadUnusedAsset;

            var fallbacks = font.fallbackFontAssetTable;
            if (fallbacks == null) return;
            for (int i = 0; i < fallbacks.Count; i++)
            {
                var fallback = fallbacks[i];
                if (fallback != null && (fallback.hideFlags & HideFlags.DontUnloadUnusedAsset) == 0)
                    Protect(fallback, depth + 1);
            }
        }

        /// <summary>Lets <paramref name="font"/> borrow glyphs it lacks from <paramref name="fallback"/>.</summary>
        private static void AddFallback(TMP_FontAsset font, TMP_FontAsset fallback)
        {
            if (font == null || fallback == null || font.Pointer == fallback.Pointer) return;

            if (font.fallbackFontAssetTable == null)
                font.fallbackFontAssetTable = new Il2CppSystem.Collections.Generic.List<TMP_FontAsset>();
            var table = font.fallbackFontAssetTable;
            for (int i = 0; i < table.Count; i++)
            {
                var existing = table[i];
                if (existing != null && existing.Pointer == fallback.Pointer) return;
            }
            table.Add(fallback);
        }

        private static IntPtr _materialMatchedFor = IntPtr.Zero;

        /// <summary>
        /// Gives the Turkish font the same look as the game's Smile font. The game's default
        /// Smile material is not a plain SDF material: it thickens the glyphs (face dilate) and
        /// softens their edge, so a font asset straight out of the editor renders thinner and
        /// crisper next to it. Copy the game's shader and settings, keeping our own atlas.
        /// </summary>
        private static void MatchOriginalMaterial()
        {
            if (CustomFont == null || OriginalSmile == null || CustomFont.Pointer == _materialMatchedFor) return;
            if (CustomFont.Pointer == OriginalSmile.Pointer) return;
            try
            {
                var source = OriginalSmile.material;
                var target = CustomFont.material;
                if (source == null || target == null) return;

                // These describe our atlas, not the look; they must survive the copy.
                var atlas = target.GetTexture(ShaderUtilities.ID_MainTex);
                float width = target.HasProperty("_TextureWidth") ? target.GetFloat("_TextureWidth") : -1f;
                float height = target.HasProperty("_TextureHeight") ? target.GetFloat("_TextureHeight") : -1f;
                float gradient = target.HasProperty("_GradientScale") ? target.GetFloat("_GradientScale") : -1f;

                target.shader = source.shader;
                target.CopyPropertiesFromMaterial(source);

                target.SetTexture(ShaderUtilities.ID_MainTex, atlas);
                if (width > 0) target.SetFloat("_TextureWidth", width);
                if (height > 0) target.SetFloat("_TextureHeight", height);
                if (gradient > 0) target.SetFloat("_GradientScale", gradient);

                _materialMatchedFor = CustomFont.Pointer;
                LocalizationPlugin.Log.LogInfo(
                    $"[FONT] Matched custom font material to '{source.name}' (shader '{source.shader.name}', " +
                    $"face dilate {target.GetFloat("_FaceDilate"):0.###}, softness {target.GetFloat("_OutlineSoftness"):0.###}).");
            }
            catch (Exception ex)
            {
                LocalizationPlugin.Log.LogWarning("[FONT] Could not match the original font material: " + ex.Message);
            }
        }

        private static string Describe(TMP_FontAsset font)
        {
            if (ReferenceEquals(font, null)) return "(never set)";
            if (font == null) return "DESTROYED";
            int fallbacks = font.fallbackFontAssetTable != null ? font.fallbackFontAssetTable.Count : 0;
            return $"'{font.name}' ({fallbacks} fallbacks)";
        }

        public static void LogFontState(string when)
        {
            if (!_origSaved) return;
            bool lost = OriginalSmile == null || OriginalFallback == null || OriginalAltMenu == null;
            if (!lost && !LocalizationPlugin.VerboseLogging.Value) return;
            LocalizationPlugin.Log.Log(lost ? BepInEx.Logging.LogLevel.Warning : BepInEx.Logging.LogLevel.Info,$"[FONT] {when}: smile={Describe(OriginalSmile)}, fallback={Describe(OriginalFallback)}, altMenu={Describe(OriginalAltMenu)}, custom={Describe(CustomFont)}");
        }

        public static void RestoreOriginalFonts()
        {
            if (!_origSaved || MetaManager.Actual == null) return;
            try
            {
                MetaManager.Actual.fallbackFont = OriginalFallback;
                MetaManager.Actual.smileFont = OriginalSmile;
                MetaManager.Actual.altMenuFont = OriginalAltMenu;

                if (SubtitleBoard.Actual != null)
                {
                    RestoreSubtitleFonts(SubtitleBoard.Actual);
                }

                RestoreFontOnAllActive();
                LocalizationPlugin.Log.LogInfo("[FONT] Restored original fonts in MetaManager.");
            }
            catch { }
        }

        public static void ApplyToMetaManager()
        {
            if (!LocalizationPlugin.IsTurkishActive || !LocalizationPlugin.EnableFontReplacements.Value) return;
            EnsureFontsLoaded();
            if (CustomFont == null) return;

            try
            {
                if (MetaManager.Actual != null)
                {
                    SaveOriginalFonts(MetaManager.Actual);
                    // Anything the Turkish font lacks comes from the game's own fonts.
                    AddFallback(CustomFont, OriginalSmile);
                    AddFallback(CustomFont, OriginalFallback);
                    MatchOriginalMaterial();
                    MetaManager.Actual.fallbackFont = CustomFont;
                    MetaManager.Actual.smileFont = CustomFont;
                    MetaManager.Actual.altMenuFont = CustomFont;

                    if (OriginalSmile != null && OriginalSmile.fallbackFontAssetTable != null)
                    {
                        if (!OriginalSmile.fallbackFontAssetTable.Contains(CustomFont))
                        {
                            OriginalSmile.fallbackFontAssetTable.Add(CustomFont);
                        }
                    }
                }

                if (SubtitleBoard.Actual != null)
                {
                    ApplyToSubtitleBoard(SubtitleBoard.Actual);
                }

                ApplyFontToAllActive();
                if (LocalizationPlugin.VerboseLogging.Value) LocalizationPlugin.Log.LogInfo("[FONT] Applied custom font to MetaManager, SubtitleBoard and UI.");
            }
            catch (Exception ex)
            {
                LocalizationPlugin.Log.LogWarning("[FONT] Error in ApplyToMetaManager: " + ex.Message);
            }
        }

        public static void ApplyToSubtitleBoard(SubtitleBoard subBoard)
        {
            if (subBoard == null || CustomFont == null) return;
            try
            {
                if (subBoard._textAllocator != null)
                {
                    ApplyToTextUIAllocator(subBoard._textAllocator);
                }

                if (subBoard._settings != null && subBoard._settings.textMeshes != null)
                {
                    var smileMat = subBoard._textAllocator != null ? subBoard._textAllocator.smileMaterialForSubtitles : null;
                    foreach (var tm in subBoard._settings.textMeshes)
                    {
                        if (tm != null)
                        {
                            tm.font = CustomFont;
                            if (smileMat != null)
                            {
                                tm.fontSharedMaterial = smileMat;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LocalizationPlugin.Log.LogWarning("[FONT] Error applying to SubtitleBoard: " + ex.Message);
            }
        }

        public static void ApplyToTextUIAllocator(TextUIAllocator alloc)
        {
            if (alloc == null || CustomFont == null) return;
            try
            {
                // 1. Update smileMaterialForSubtitles atlas texture and dimensions
                if (alloc.smileMaterialForSubtitles != null && CustomFont.atlasTexture != null)
                {
                    var mat = alloc.smileMaterialForSubtitles;
                    if (!_hasOrigSubtitleTex)
                    {
                        _origSubtitleMaterialTex = mat.mainTexture;
                        if (mat.HasProperty("_TextureWidth")) _origTexWidth = mat.GetFloat("_TextureWidth");
                        if (mat.HasProperty("_TextureHeight")) _origTexHeight = mat.GetFloat("_TextureHeight");
                        if (mat.HasProperty("_GradientScale")) _origGradientScale = mat.GetFloat("_GradientScale");
                        if (mat.HasProperty("_ScaleRatioA")) _origScaleRatioA = mat.GetFloat("_ScaleRatioA");
                        if (mat.HasProperty("_ScaleRatioB")) _origScaleRatioB = mat.GetFloat("_ScaleRatioB");
                        if (mat.HasProperty("_ScaleRatioC")) _origScaleRatioC = mat.GetFloat("_ScaleRatioC");
                        _hasOrigSubtitleTex = true;
                    }

                    mat.mainTexture = CustomFont.atlasTexture;
                    mat.SetTexture(ShaderUtilities.ID_MainTex, CustomFont.atlasTexture);

                    if (CustomFont.material != null)
                    {
                        if (CustomFont.material.HasProperty("_TextureWidth") && mat.HasProperty("_TextureWidth"))
                            mat.SetFloat("_TextureWidth", CustomFont.material.GetFloat("_TextureWidth"));
                        if (CustomFont.material.HasProperty("_TextureHeight") && mat.HasProperty("_TextureHeight"))
                            mat.SetFloat("_TextureHeight", CustomFont.material.GetFloat("_TextureHeight"));
                        if (CustomFont.material.HasProperty("_GradientScale") && mat.HasProperty("_GradientScale"))
                            mat.SetFloat("_GradientScale", CustomFont.material.GetFloat("_GradientScale"));
                        if (CustomFont.material.HasProperty("_ScaleRatioA") && mat.HasProperty("_ScaleRatioA"))
                            mat.SetFloat("_ScaleRatioA", CustomFont.material.GetFloat("_ScaleRatioA"));
                        if (CustomFont.material.HasProperty("_ScaleRatioB") && mat.HasProperty("_ScaleRatioB"))
                            mat.SetFloat("_ScaleRatioB", CustomFont.material.GetFloat("_ScaleRatioB"));
                        if (CustomFont.material.HasProperty("_ScaleRatioC") && mat.HasProperty("_ScaleRatioC"))
                            mat.SetFloat("_ScaleRatioC", CustomFont.material.GetFloat("_ScaleRatioC"));
                    }
                }

                // 2. Assign font and material to line1TM and line2TM
                var targetMat = alloc.smileMaterialForSubtitles ?? CustomFont.material;

                if (alloc.line1TM != null)
                {
                    if (alloc.line1TM.font != CustomFont)
                    {
                        alloc.line1TM.font = CustomFont;
                    }
                    if (targetMat != null && alloc.line1TM.fontSharedMaterial != targetMat)
                    {
                        alloc.line1TM.fontSharedMaterial = targetMat;
                    }
                }

                if (alloc.line2TM != null)
                {
                    if (alloc.line2TM.font != CustomFont)
                    {
                        alloc.line2TM.font = CustomFont;
                    }
                    if (targetMat != null && alloc.line2TM.fontSharedMaterial != targetMat)
                    {
                        alloc.line2TM.fontSharedMaterial = targetMat;
                    }
                }
            }
            catch (Exception ex)
            {
                LocalizationPlugin.Log.LogWarning("[FONT] Error in ApplyToTextUIAllocator: " + ex.Message);
            }
        }

        public static void RestoreSubtitleFonts(SubtitleBoard subBoard)
        {
            if (subBoard == null) return;
            try
            {
                if (subBoard._textAllocator != null)
                {
                    var alloc = subBoard._textAllocator;
                    if (alloc.smileMaterialForSubtitles != null && _hasOrigSubtitleTex && _origSubtitleMaterialTex != null)
                    {
                        var mat = alloc.smileMaterialForSubtitles;
                        mat.mainTexture = _origSubtitleMaterialTex;
                        mat.SetTexture(ShaderUtilities.ID_MainTex, _origSubtitleMaterialTex);
                        if (_origTexWidth > 0 && mat.HasProperty("_TextureWidth")) mat.SetFloat("_TextureWidth", _origTexWidth);
                        if (_origTexHeight > 0 && mat.HasProperty("_TextureHeight")) mat.SetFloat("_TextureHeight", _origTexHeight);
                        if (_origGradientScale > 0 && mat.HasProperty("_GradientScale")) mat.SetFloat("_GradientScale", _origGradientScale);
                        if (_origScaleRatioA > 0 && mat.HasProperty("_ScaleRatioA")) mat.SetFloat("_ScaleRatioA", _origScaleRatioA);
                        if (_origScaleRatioB > 0 && mat.HasProperty("_ScaleRatioB")) mat.SetFloat("_ScaleRatioB", _origScaleRatioB);
                        if (_origScaleRatioC > 0 && mat.HasProperty("_ScaleRatioC")) mat.SetFloat("_ScaleRatioC", _origScaleRatioC);
                    }

                    if (OriginalSmile != null)
                    {
                        if (alloc.line1TM != null)
                        {
                            alloc.line1TM.font = OriginalSmile;
                            if (alloc.smileMaterialForSubtitles != null)
                                alloc.line1TM.fontSharedMaterial = alloc.smileMaterialForSubtitles;
                        }
                        if (alloc.line2TM != null)
                        {
                            alloc.line2TM.font = OriginalSmile;
                            if (alloc.smileMaterialForSubtitles != null)
                                alloc.line2TM.fontSharedMaterial = alloc.smileMaterialForSubtitles;
                        }
                    }
                }

                if (subBoard._settings != null && subBoard._settings.textMeshes != null && OriginalSmile != null)
                {
                    foreach (var tm in subBoard._settings.textMeshes)
                    {
                        if (tm != null)
                        {
                            tm.font = OriginalSmile;
                            if (subBoard._textAllocator != null && subBoard._textAllocator.smileMaterialForSubtitles != null)
                            {
                                tm.fontSharedMaterial = subBoard._textAllocator.smileMaterialForSubtitles;
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private sealed class ReplacedText
        {
            public TMP_Text Text;
            public TMP_FontAsset Font;
            public Material Material;
        }

        // Texts whose font the mod replaced, with what they had before, so switching the
        // language back returns each one to its own font instead of a blanket default.
        private static readonly Dictionary<IntPtr, ReplacedText> _replacedTexts = new Dictionary<IntPtr, ReplacedText>();
        private static readonly List<IntPtr> _deadTexts = new List<IntPtr>();

        public static void ApplyFontToAllActive()
        {
            if (CustomFont == null) return;
            try
            {
                _deadTexts.Clear();
                foreach (var kvp in _replacedTexts)
                    if (kvp.Value.Text == null) _deadTexts.Add(kvp.Key);
                foreach (var ptr in _deadTexts) _replacedTexts.Remove(ptr);

                var allTmp = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<TMP_Text>());
                if (allTmp == null) return;

                foreach (var o in allTmp)
                {
                    var tmp = o.Cast<TMP_Text>();
                    if (tmp == null || tmp.gameObject == null || !tmp.gameObject.scene.isLoaded) continue;

                    // Re-assigning the same font makes TMP rebuild the text for nothing.
                    var current = tmp.font;
                    if (current != null && current.Pointer == CustomFont.Pointer) continue;

                    if (!_replacedTexts.ContainsKey(tmp.Pointer))
                    {
                        _replacedTexts[tmp.Pointer] = new ReplacedText
                        {
                            Text = tmp,
                            Font = current,
                            Material = tmp.fontSharedMaterial,
                        };
                    }
                    tmp.font = CustomFont;
                }
            }
            catch { }
        }

        public static void RestoreFontOnAllActive()
        {
            try
            {
                foreach (var entry in _replacedTexts.Values)
                {
                    var tmp = entry.Text;
                    if (tmp == null) continue;

                    var font = entry.Font != null ? entry.Font : OriginalSmile;
                    if (font == null) continue;
                    tmp.font = font;
                    if (entry.Material != null) tmp.fontSharedMaterial = entry.Material;
                }
                _replacedTexts.Clear();

                // Anything else still showing the Turkish font (set through the game's own
                // font fields) goes back to the game's default.
                if (OriginalSmile == null || CustomFont == null) return;
                var allTmp = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<TMP_Text>());
                if (allTmp == null) return;
                foreach (var o in allTmp)
                {
                    var tmp = o.Cast<TMP_Text>();
                    if (tmp == null || tmp.gameObject == null || !tmp.gameObject.scene.isLoaded) continue;
                    var current = tmp.font;
                    if (current != null && current.Pointer == CustomFont.Pointer) tmp.font = OriginalSmile;
                }
            }
            catch { }
        }
    }
    [HarmonyPatch]
    public static class FontPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MetaManager), nameof(MetaManager.GetFont))]
        public static bool MetaManager_GetFont_Prefix(ref bool isFallback, bool isMenu, ref TMP_FontAsset __result)
        {
            if (LocalizationPlugin.IsTurkishActive && FontManager.CustomFont != null)
            {
                isFallback = false;
                __result = FontManager.CustomFont;
                return false;
            }
            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MetaManager), nameof(MetaManager.Awake))]
        public static void MetaManager_Awake_Postfix(MetaManager __instance)
        {
            FontManager.EnsureFontsLoaded();
            FontManager.SaveOriginalFonts(__instance);
            if (LocalizationPlugin.IsTurkishActive)
            {
                FontManager.ApplyToMetaManager();
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MetaManager), nameof(MetaManager.Init))]
        public static void MetaManager_Init_Postfix(MetaManager __instance)
        {
            FontManager.EnsureFontsLoaded();
            FontManager.SaveOriginalFonts(__instance);
            if (LocalizationPlugin.IsTurkishActive)
            {
                FontManager.ApplyToMetaManager();
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MetaManager), "OnLanguageSet")]
        public static void MetaManager_OnLanguageSet_Postfix()
        {
            if (LocalizationPlugin.IsTurkishActive)
            {
                FontManager.ApplyToMetaManager();
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(StartMenu), nameof(StartMenu.Start))]
        public static void StartMenu_Start_FontPostfix()
        {
            try
            {
                FontManager.EnsureFontsLoaded();
                if (LocalizationPlugin.IsTurkishActive)
                {
                    FontManager.ApplyToMetaManager();
                }
            }
            catch (Exception ex)
            {
                LocalizationPlugin.Log.LogWarning($"[FONT] Error in StartMenu.Start font postfix: {ex.Message}");
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SubtitleBoard), nameof(SubtitleBoard.Awake))]
        public static void SubtitleBoard_Awake_FontPostfix(SubtitleBoard __instance)
        {
            try
            {
                FontManager.EnsureFontsLoaded();
                if (LocalizationPlugin.IsTurkishActive)
                {
                    FontManager.ApplyToSubtitleBoard(__instance);
                }
            }
            catch (Exception ex)
            {
                LocalizationPlugin.Log.LogWarning($"[FONT] Error in SubtitleBoard.Awake font postfix: {ex.Message}");
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(TextUIAllocator), nameof(TextUIAllocator.UpdateFontAsset))]
        public static void TextUIAllocator_UpdateFontAsset_Prefix(TextUIAllocator __instance, ref TMP_FontAsset fontAsset, ref bool isFallback)
        {
            if (LocalizationPlugin.IsTurkishActive && FontManager.CustomFont != null)
            {
                fontAsset = FontManager.CustomFont;
                isFallback = false;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TextUIAllocator), nameof(TextUIAllocator.UpdateFontAsset))]
        public static void TextUIAllocator_UpdateFontAsset_Postfix(TextUIAllocator __instance)
        {
            if (LocalizationPlugin.IsTurkishActive)
            {
                FontManager.ApplyToTextUIAllocator(__instance);
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(TextUIAllocator), nameof(TextUIAllocator.DisplayRoutine))]
        public static void TextUIAllocator_DisplayRoutine_Prefix(TextUIAllocator __instance)
        {
            if (LocalizationPlugin.IsTurkishActive)
            {
                FontManager.EnsureFontsLoaded();
                FontManager.ApplyToTextUIAllocator(__instance);
            }
        }
    }
}
