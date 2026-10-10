using System;
using HarmonyLib;

namespace TGYHTurkishPatch
{
    [HarmonyPatch]
    public static class TextPatches
    {
        #region SubtitleBoard Hooks

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SubtitleBoard), nameof(SubtitleBoard.Awake))]
        public static void SubtitleBoard_Awake_Postfix(SubtitleBoard __instance)
        {
            try
            {
                if (LocalizationPlugin.IsTurkishActive)
                {
                    TextManager.ApplyTranslations();
                }
            }
            catch (Exception ex)
            {
                LocalizationPlugin.Log.LogWarning($"[TEXT] Error in SubtitleBoard.Awake postfix: {ex.Message}");
            }
        }

        #endregion

        #region StartMenu Hooks

        [HarmonyPostfix]
        [HarmonyPatch(typeof(StartMenu), nameof(StartMenu.Start))]
        public static void StartMenu_Start_Postfix()
        {
            try
            {
                if (LocalizationPlugin.IsTurkishActive)
                {
                    TextManager.ApplyTranslations();
                }
                else
                {
                    int lang = MetaManager.Actual != null ? MetaManager.Actual.GetLanguage() : 0;
                    TextManager.RefreshAllActiveMenus(lang);
                }
            }
            catch (Exception ex)
            {
                LocalizationPlugin.Log.LogWarning($"[TEXT] Error in StartMenu.Start postfix: {ex.Message}");
            }
        }

        #endregion

        #region MenuPanelStringManager Hooks

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MenuPanelStringManager), nameof(MenuPanelStringManager.Refresh))]
        public static bool MenuPanelStringManager_Refresh_Prefix(MenuPanelStringManager __instance)
        {
            // Only Turkish needs the mod's own refresh; the game handles its languages itself.
            if (!LocalizationPlugin.IsTurkishActive)
                return true;

            try
            {
                if (__instance == null || __instance.gameObject == null)
                    return false;

                TextManager.UpdateMenuPanel(__instance, LanguageMenuPatches.TURKISH_INDEX);
            }
            catch (Exception ex)
            {
                LocalizationPlugin.Log.LogWarning($"[TEXT] Error in MenuPanelStringManager.Refresh prefix: {ex.Message}");
            }
            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MenuPanelStringManager), nameof(MenuPanelStringManager.Start))]
        public static void MenuPanelStringManager_Start_Postfix(MenuPanelStringManager __instance)
        {
            try
            {
                if (LocalizationPlugin.IsTurkishActive &&
                    __instance != null && __instance.gameObject != null && __instance.gameObject.activeInHierarchy)
                {
                    TextManager.UpdateMenuPanel(__instance, LanguageMenuPatches.TURKISH_INDEX);
                }
            }
            catch { }
        }

        #endregion

        #region SubtitleStringLookup Fallback Hooks

        [HarmonyPrefix]
        [HarmonyPatch(typeof(SubtitleStringLookup), nameof(SubtitleStringLookup.GetLineFromId))]
        public static bool SubtitleStringLookup_GetLineFromId_Prefix(int id, ref string __result)
        {
            if (LocalizationPlugin.IsTurkishActive && TextManager.TryGetSubtitle(id, out var replacement))
            {
                __result = replacement;
                return false;
            }
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(SubtitleStringLookup), nameof(SubtitleStringLookup.GetLineFromIdAndSpecificLanguage))]
        public static bool SubtitleStringLookup_GetLineFromIdAndSpecificLanguage_Prefix(int id, StringLanguage lang, ref string __result)
        {
            if (LocalizationPlugin.IsTurkishActive && TextManager.TryGetSubtitle(id, lang, out var replacement))
            {
                __result = replacement;
                return false;
            }
            return true;
        }

        #endregion

        #region Cutscene Subtitles Hook

        // Cutscene line object -> its untranslated text.
        private static readonly System.Collections.Generic.Dictionary<IntPtr, string> _originalCutsceneLines =
            new System.Collections.Generic.Dictionary<IntPtr, string>();

        [HarmonyPrefix]
        [HarmonyPatch(typeof(VideoPlayerPanel), nameof(VideoPlayerPanel.Activate))]
        public static void VideoPlayerPanel_Activate_Prefix(VideoPlayerPanel __instance)
        {
            try
            {
                if (__instance == null || __instance.subtitlesWrapper == null || __instance.subtitlesWrapper.lines == null)
                    return;

                // The lines live in a shared asset, so a translation written here stays for the
                // whole session. Remember each original and put it back once Turkish is off.
                bool turkish = LocalizationPlugin.IsTurkishActive;
                foreach (var line in __instance.subtitlesWrapper.lines)
                {
                    if (line == null) continue;

                    if (!_originalCutsceneLines.TryGetValue(line.Pointer, out var original))
                    {
                        if (!turkish) continue;
                        original = line.lineString;
                        _originalCutsceneLines[line.Pointer] = original;
                    }

                    if (turkish &&
                        (TextManager.TryGetSubtitle(line.dialogueID, out var replacement) ||
                         TextManager.TryGetByOriginal(original, out replacement)))
                    {
                        line.lineString = replacement;
                    }
                    else
                    {
                        line.lineString = original;
                    }
                }
            }
            catch { }
        }

        #endregion
    }
}
