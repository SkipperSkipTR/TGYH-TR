using System;
using HarmonyLib;
using UnityEngine;

namespace TGYHTurkishPatch
{
    /// <summary>
    /// Adds "Türkçe" as the first entry of the Options > Language selector.
    ///
    /// The game only knows its own 12 languages (StringLanguage 0..11) and indexes arrays with
    /// that value, so Turkish never becomes the game's language: while Turkish is active the
    /// game itself runs in English and the mod overlays text, fonts and textures. The selector
    /// therefore uses its own numbering — entry 0 is Turkish, entry N is game language N - 1 —
    /// and these patches translate between the two wherever the game reads or writes the index.
    /// </summary>
    [HarmonyPatch]
    public static class LanguageMenuPatches
    {
        // Id used for Turkish by the mod's own code and saved setting; never given to the game.
        public const int TURKISH_INDEX = 12;
        public const string TURKISH_DISPLAY_NAME = "Türkçe";

        private const int TurkishOptionIndex = 0;
        private const int GameLanguageCount = 12;
        private const int LanguageNotSet = -1;

        /// <summary>The game's language, always a valid StringLanguage value.</summary>
        public static int GetGameLanguage()
        {
            int lang = MetaManager.user_lang__01;
            return (lang < 0 || lang >= GameLanguageCount) ? 0 : lang;
        }

        /// <summary>
        /// Keeps the game's own language setting valid: English underneath Turkish (so line
        /// lengths, fonts and untranslated lines follow Latin rules), and never an index the
        /// game cannot use, such as the 12 that earlier versions of this mod stored in the save.
        /// </summary>
        public static void NormalizeGameLanguage()
        {
            int lang = MetaManager.user_lang__01;
            if (lang == LanguageNotSet) return;
            if (lang < 0 || lang >= GameLanguageCount || (LocalizationPlugin.IsTurkishActive && lang != 0))
                MetaManager.user_lang__01 = 0;
        }

        /// <summary>Puts "Türkçe" at the top of the language button's list and selects the active entry.</summary>
        private static void SyncLanguageButton(OptionsMenuHookedButton btn)
        {
            if (btn == null || btn.optionStrings == null) return;

            var strings = btn.optionStrings;
            if (strings.Count == 0 || strings[0] != TURKISH_DISPLAY_NAME)
                strings.Insert(0, TURKISH_DISPLAY_NAME);

            int index = LocalizationPlugin.IsTurkishActive ? TurkishOptionIndex : GetGameLanguage() + 1;
            if (index >= strings.Count) index = strings.Count - 1;
            btn.optionIndex = index;
            btn.lastIndex = index;
        }

        /// <summary>
        /// The game selects the language button by its own language index; correct it to the
        /// selector's numbering.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(OptionsMenuHandler), nameof(OptionsMenuHandler.PopulateOptionStrings))]
        public static void OptionsMenuHandler_PopulateOptionStrings_Postfix(OptionsMenuHandler __instance)
        {
            try
            {
                if (__instance != null) SyncLanguageButton(__instance.languageOptionButton);
            }
            catch (Exception ex)
            {
                LocalizationPlugin.Log.LogWarning($"[LANG_MENU] PopulateOptionStrings error: {ex.Message}");
            }
        }

        /// <summary>
        /// Populate option strings from LocalisedTextSO objects cleanly without calling SubtitleBoard.RawStringRequest,
        /// supporting both Turkish and standard game languages.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(OptionsMenuHookedButton), nameof(OptionsMenuHookedButton.SetStringsFromLocObjects))]
        public static bool OptionsMenuHookedButton_SetStringsFromLocObjects_Prefix(OptionsMenuHookedButton __instance)
        {
            try
            {
                if (__instance == null) return false;

                int currentLang = GetGameLanguage();
                bool isTurkish = LocalizationPlugin.IsTurkishActive;

                if (__instance.optionStrings == null)
                {
                    __instance.optionStrings = new Il2CppSystem.Collections.Generic.List<string>();
                }
                else
                {
                    __instance.optionStrings.Clear();
                }

                if (__instance.optionLocTextObjects != null)
                {
                    foreach (var so in __instance.optionLocTextObjects)
                    {
                        if (so == null) continue;
                        string text = null;
                        if (isTurkish)
                        {
                            if (TextManager.TryGetUI(so, out var rep) || TextManager.TryGetByOriginal(so.lineString, out rep))
                            {
                                text = rep;
                            }
                        }
                        if (string.IsNullOrEmpty(text))
                        {
                            text = TextManager.GetOriginalStringForLanguage(so, currentLang);
                        }
                        if (string.IsNullOrEmpty(text))
                        {
                            text = so.lineString ?? "";
                        }
                        __instance.optionStrings.Add(text);
                    }
                }

                if (__instance.item == OptionsMenuHookedButton.OptionsMenuItem.Language)
                {
                    SyncLanguageButton(__instance);
                }
            }
            catch (Exception ex)
            {
                LocalizationPlugin.Log.LogWarning($"[LANG_MENU] Error in SetStringsFromLocObjects: {ex.Message}");
            }
            return false; // Skip native method completely
        }

        /// <summary>
        /// The game's Redraw picks the font, sets the text and positions the arrows itself; it
        /// only needs protecting from an index outside the list, which it would throw on.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(OptionsMenuHookedButton), nameof(OptionsMenuHookedButton.Redraw))]
        public static bool OptionsMenuHookedButton_Redraw_Prefix(OptionsMenuHookedButton __instance)
        {
            try
            {
                if (__instance == null || __instance.optionText == null) return false;

                var strings = __instance.optionStrings;
                if (strings == null || strings.Count == 0) return false;

                int index = __instance.optionIndex;
                if (index < 0) __instance.optionIndex = 0;
                else if (index >= strings.Count) __instance.optionIndex = strings.Count - 1;
                return true;
            }
            catch (Exception ex)
            {
                LocalizationPlugin.Log.LogWarning($"[LANG_MENU] Error in Redraw prefix: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// When the Options menu opens, re-apply the menu strings for the active language.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(OptionsMenuHandler), nameof(OptionsMenuHandler.OpenOptions))]
        public static void OptionsMenuHandler_OpenOptions_Postfix(OptionsMenuHandler __instance)
        {
            try
            {
                TextManager.RefreshAllActiveMenus();
            }
            catch { }
        }

        /// <summary>
        /// A new entry was chosen on the language button. Entry 0 switches the mod to Turkish;
        /// any other entry is handed to the game as the real language it stands for, so the
        /// game's own switching code (fonts, subtitles, menu refresh) does the work.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(OptionsMenuHandler), nameof(OptionsMenuHandler.ProcessInputFromHookedButton))]
        public static bool OptionsMenuHandler_ProcessInputFromHookedButton_Prefix(
            OptionsMenuHandler __instance,
            OptionsMenuHookedButton.OptionsMenuItem item,
            ref int newIndex)
        {
            if (item != OptionsMenuHookedButton.OptionsMenuItem.Language) return true;

            try
            {
                if (newIndex == TurkishOptionIndex)
                {
                    LocalizationPlugin.Log.LogInfo("[LANG_MENU] Selected Türkçe from options menu!");
                    LocalizationPlugin.SetTurkishActive(true);
                    if (__instance != null)
                    {
                        __instance.PopulateOptionStrings();
                        __instance.RedrawAllLRButtons();
                    }
                    return false;
                }

                int gameLanguage = Math.Min(newIndex - 1, GameLanguageCount - 1);
                LocalizationPlugin.Log.LogInfo($"[LANG_MENU] Selected game language {gameLanguage} from options menu.");
                // Set the language first so everything SetTurkishActive restores uses the new one.
                MetaManager.user_lang__01 = gameLanguage;
                LocalizationPlugin.SetTurkishActive(false);
                newIndex = gameLanguage;
                return true;
            }
            catch (Exception ex)
            {
                LocalizationPlugin.Log.LogError($"[LANG_MENU] Error changing language: {ex}");
                return false;
            }
        }

        /// <summary>
        /// The game's direct language setter. It takes a real game language, so reaching it
        /// means Turkish is being switched off.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(OptionsMenuHandler), nameof(OptionsMenuHandler.ProcessLanguageChange))]
        public static bool OptionsMenuHandler_ProcessLanguageChange_Prefix(int newIndex)
        {
            if (newIndex < 0 || newIndex >= GameLanguageCount) return false;
            try
            {
                MetaManager.user_lang__01 = newIndex;
                LocalizationPlugin.SetTurkishActive(false);
            }
            catch (Exception ex)
            {
                LocalizationPlugin.Log.LogError($"[LANG_MENU] Error changing language: {ex}");
            }
            return true;
        }

        /// <summary>
        /// Runs whenever the game applies its language (on load and on change): make sure the
        /// value it is about to index with is one it supports.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MetaManager), "OnLanguageSet")]
        public static void MetaManager_OnLanguageSet_Prefix()
        {
            NormalizeGameLanguage();
        }

        /// <summary>
        /// Repair an out-of-range language left in the save before the game validates it, so
        /// the save is not treated as corrupt.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MetaManager), "UserLangIsValid")]
        public static void MetaManager_UserLangIsValid_Prefix()
        {
            NormalizeGameLanguage();
        }
    }
}
