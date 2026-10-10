using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace TGYHTurkishPatch
{
    public static class TextManager
    {
        // Subtitle replacements by int ID
        private static readonly Dictionary<int, string> _subtitleReplacements = new Dictionary<int, string>();

        // UI text replacements by stringID
        private static readonly Dictionary<int, string> _uiReplacementsById = new Dictionary<int, string>();

        // UI text replacements by ScriptableObject name
        private static readonly Dictionary<string, string> _uiReplacementsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Fallback text replacements by exact original English string (also trimmed)
        private static readonly Dictionary<string, string> _textReplacementsByOriginal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static int SubtitleCount => _subtitleReplacements.Count;
        public static int UICount => _uiReplacementsById.Count + _uiReplacementsByName.Count;
        public static int FallbackCount => _textReplacementsByOriginal.Count;

        public static void LoadAllTexts(string directory)
        {
            _subtitleReplacements.Clear();
            _uiReplacementsById.Clear();
            _uiReplacementsByName.Clear();
            _textReplacementsByOriginal.Clear();

            if (!Directory.Exists(directory))
                return;

            var files = Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext == ".csv")
                {
                    LoadCsv(file);
                }
                else if (ext == ".json")
                {
                    LoadJson(file);
                }
            }

            LocalizationPlugin.Log.LogInfo($"Loaded text translations: {SubtitleCount} subtitles, {UICount} UI strings, {FallbackCount} original string mappings.");
        }

        private static void LoadCsv(string filePath)
        {
            try
            {
                var lines = File.ReadAllLines(filePath, Encoding.UTF8);
                if (lines.Length == 0) return;

                int idCol = 0;
                int origCol = -1;
                int transCol = -1;
                int nameCol = -1;
                bool isHeaderChecked = false;

                foreach (var rawLine in lines)
                {
                    var line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith("#") || line.StartsWith("//"))
                        continue;

                    var parts = ParseCsvLine(line);
                    if (parts.Count < 2)
                        continue;

                    if (!isHeaderChecked)
                    {
                        isHeaderChecked = true;
                        string firstCol = parts[0].Trim().ToLowerInvariant();
                        if (firstCol == "id" || firstCol == "key" || firstCol == "instance_id")
                        {
                            // Detect column indices from header
                            for (int i = 0; i < parts.Count; i++)
                            {
                                string h = parts[i].Trim().ToLowerInvariant();
                                if (h == "id" || h == "key") idCol = i;
                                else if (h == "name" || h == "asset_name") nameCol = i;
                                else if (h == "original_english" || h == "en_linestring" || h == "en" || h == "original" || h == "english") origCol = i;
                                else if (h == "translated_text" || h == "translation" || h == "translated" || h == "target" || h == "turkish" || h == "tr") transCol = i;
                            }

                            // If standard 4-column template without explicit translation column header
                            if (transCol == -1 && parts.Count >= 4)
                            {
                                origCol = 2;
                                transCol = 3;
                            }
                            else if (transCol == -1 && parts.Count == 2)
                            {
                                origCol = 0;
                                transCol = 1;
                            }
                            continue;
                        }
                        else
                        {
                            // No header line, assign based on column count
                            if (parts.Count >= 4)
                            {
                                origCol = 2;
                                transCol = 3;
                            }
                            else
                            {
                                origCol = 0;
                                transCol = parts.Count - 1;
                            }
                        }
                    }

                    string idOrKey = (idCol >= 0 && idCol < parts.Count) ? parts[idCol].Trim() : "";
                    string orig = (origCol >= 0 && origCol < parts.Count) ? parts[origCol].Trim() : "";
                    string trans = (transCol >= 0 && transCol < parts.Count) ? parts[transCol].Trim() : parts[parts.Count - 1].Trim();
                    string name = (nameCol >= 0 && nameCol < parts.Count) ? parts[nameCol].Trim() : "";

                    if (string.IsNullOrEmpty(trans))
                        continue;

                    // If trans is identical to original English, it is untranslated template text - skip it!
                    if (!string.IsNullOrEmpty(orig) && trans.Equals(orig, StringComparison.Ordinal))
                        continue;

                    RegisterTranslation(idOrKey, orig, trans, name);
                }

                LocalizationPlugin.Log.LogInfo($"Loaded CSV: {Path.GetFileName(filePath)}");
            }
            catch (Exception ex)
            {
                LocalizationPlugin.Log.LogError($"Error reading CSV {filePath}: {ex.Message}");
            }
        }

        private static void LoadJson(string filePath)
        {
            try
            {
                var content = File.ReadAllText(filePath, Encoding.UTF8);

                // Simple JSON parser for "key": "value" pairs
                int index = 0;
                while (index < content.Length)
                {
                    int quote1 = content.IndexOf('"', index);
                    if (quote1 == -1) break;
                    int quote2 = FindMatchingQuote(content, quote1 + 1);
                    if (quote2 == -1) break;

                    string key = UnescapeJsonString(content.Substring(quote1 + 1, quote2 - quote1 - 1));

                    int colon = content.IndexOf(':', quote2 + 1);
                    if (colon == -1) break;

                    int valStart = colon + 1;
                    while (valStart < content.Length && char.IsWhiteSpace(content[valStart])) valStart++;
                    if (valStart >= content.Length) break;

                    if (content[valStart] == '"')
                    {
                        int quote4 = FindMatchingQuote(content, valStart + 1);
                        if (quote4 == -1) break;

                        string val = UnescapeJsonString(content.Substring(valStart + 1, quote4 - valStart - 1));
                        if (!string.IsNullOrEmpty(val) && !val.Equals(key, StringComparison.Ordinal))
                        {
                            RegisterTranslation(key, key, val);
                        }
                        index = quote4 + 1;
                    }
                    else
                    {
                        index = colon + 1;
                    }
                }

                LocalizationPlugin.Log.LogInfo($"Loaded JSON: {Path.GetFileName(filePath)}");
            }
            catch (Exception ex)
            {
                LocalizationPlugin.Log.LogError($"Error reading JSON {filePath}: {ex.Message}");
            }
        }

        private static void RegisterTranslation(string idOrKey, string originalEnglish, string translatedText, string assetName = null)
        {
            if (string.IsNullOrEmpty(translatedText)) return;

            // 1. Register by integer ID if present
            if (int.TryParse(idOrKey, out int id) && id != 0)
            {
                _subtitleReplacements[id] = translatedText;
                _uiReplacementsById[id] = translatedText;
            }
            else if (!string.IsNullOrEmpty(idOrKey))
            {
                _uiReplacementsByName[idOrKey] = translatedText;
                _textReplacementsByOriginal[idOrKey] = translatedText;
            }

            // 2. Register by Asset Name if present
            if (!string.IsNullOrEmpty(assetName))
            {
                _uiReplacementsByName[assetName] = translatedText;
            }

            // 3. Register by original English string
            if (!string.IsNullOrEmpty(originalEnglish))
            {
                _textReplacementsByOriginal[originalEnglish] = translatedText;
                string trimmed = originalEnglish.Trim();
                if (trimmed != originalEnglish)
                {
                    _textReplacementsByOriginal[trimmed] = translatedText;
                }

                // Also support HTML line breaks conversion
                if (originalEnglish.Contains("<br>"))
                {
                    _textReplacementsByOriginal[originalEnglish.Replace("<br>", "\n")] = translatedText;
                }
            }
        }

        public static bool TryGetSubtitle(int id, out string replacement)
        {
            return _subtitleReplacements.TryGetValue(id, out replacement);
        }

        public static bool TryGetSubtitle(int id, StringLanguage lang, out string replacement)
        {
            return _subtitleReplacements.TryGetValue(id, out replacement);
        }

        public static bool TryGetUI(LocalisedTextSO so, out string replacement)
        {
            replacement = null;
            if (so == null) return false;

            // 1. Try by stringID
            if (so.stringID != 0 && _uiReplacementsById.TryGetValue(so.stringID, out replacement))
                return true;

            // 2. Try by ScriptableObject name
            if (!string.IsNullOrEmpty(so.name) && _uiReplacementsByName.TryGetValue(so.name, out replacement))
                return true;

            // 3. Try by original English string
            if (!string.IsNullOrEmpty(so.lineString))
            {
                if (_textReplacementsByOriginal.TryGetValue(so.lineString, out replacement))
                    return true;
                if (_textReplacementsByOriginal.TryGetValue(so.lineString.Trim(), out replacement))
                    return true;
            }

            return false;
        }

        public static bool TryGetByOriginal(string original, out string replacement)
        {
            replacement = null;
            if (string.IsNullOrEmpty(original))
                return false;

            if (_textReplacementsByOriginal.TryGetValue(original, out replacement))
                return true;

            string trimmed = original.Trim();
            if (_textReplacementsByOriginal.TryGetValue(trimmed, out replacement))
                return true;

            return false;
        }

        public static string GetOriginalStringForLanguage(LocalisedTextSO so, int langIndex)
        {
            if (so == null) return "";
            switch (langIndex)
            {
                case 0: return so.lineString ?? "";
                case 1: return !string.IsNullOrEmpty(so.dialectString) ? so.dialectString : (so.lineString ?? "");
                case 2: return !string.IsNullOrEmpty(so.frenchString) ? so.frenchString : (so.lineString ?? "");
                case 3: return !string.IsNullOrEmpty(so.germanString) ? so.germanString : (so.lineString ?? "");
                case 4: return !string.IsNullOrEmpty(so.euroSpanishString) ? so.euroSpanishString : (so.lineString ?? "");
                case 5: return !string.IsNullOrEmpty(so.latamSpanishString) ? so.latamSpanishString : (so.lineString ?? "");
                case 6: return !string.IsNullOrEmpty(so.chineseSimplifiedString) ? so.chineseSimplifiedString : (so.lineString ?? "");
                case 7: return !string.IsNullOrEmpty(so.chineseTraditionalString) ? so.chineseTraditionalString : (so.lineString ?? "");
                case 8: return !string.IsNullOrEmpty(so.russianString) ? so.russianString : (so.lineString ?? "");
                case 9: return !string.IsNullOrEmpty(so.koreanString) ? so.koreanString : (so.lineString ?? "");
                case 10: return !string.IsNullOrEmpty(so.japaneseString) ? so.japaneseString : (so.lineString ?? "");
                case 11: return !string.IsNullOrEmpty(so.brazilPortugueseString) ? so.brazilPortugueseString : (so.lineString ?? "");
                default: return so.lineString ?? "";
            }
        }

        public static void UpdateMenuPanel(MenuPanelStringManager menu, int langIndex = -1)
        {
            if (menu == null || menu.gameObject == null) return;

            bool isTurkish = (langIndex == 12) || (langIndex < 0 && LocalizationPlugin.IsTurkishActive);

            try
            {
                if (!isTurkish)
                {
                    // The game's own Refresh knows which font each of its languages needs
                    // (Noto for Chinese, Japanese, Korean and Russian); it runs unpatched
                    // whenever Turkish is off.
                    menu.Refresh();
                    return;
                }

                if (menu.strings != null)
                {
                    foreach (var item in menu.strings)
                    {
                        if (item == null || item.textObject == null) continue;
                        ApplyTurkishToMenuText(item.textMesh, item.textObject);
                    }
                }

                if (menu.uIstrings != null)
                {
                    foreach (var item in menu.uIstrings)
                    {
                        if (item == null || item.textObject == null) continue;
                        ApplyTurkishToMenuText(item.textMesh, item.textObject);
                    }
                }
            }
            catch (Exception ex)
            {
                LocalizationPlugin.Log.LogWarning($"[TEXT] Error in UpdateMenuPanel: {ex.Message}");
            }
        }

        private static void ApplyTurkishToMenuText(TMPro.TMP_Text textMesh, LocalisedTextSO textObject)
        {
            if (textMesh == null) return;

            string current = textMesh.text;
            string text;
            if (!TryGetUI(textObject, out text) &&
                !TryGetByOriginal(textObject.lineString, out text) &&
                !(!string.IsNullOrEmpty(current) && TryGetByOriginal(current, out text)))
            {
                // No translation. If the text is a leftover from another game language, show
                // English instead; otherwise leave it alone.
                text = null;
                for (int lang = 1; lang < 12 && text == null; lang++)
                {
                    if (GetOriginalStringForLanguage(textObject, lang) == current)
                        text = textObject.lineString;
                }
            }
            if (!string.IsNullOrEmpty(text) && text != current)
                textMesh.text = text;

            var font = FontManager.CustomFont;
            if (font != null)
            {
                var currentFont = textMesh.font;
                if (currentFont == null || currentFont.Pointer != font.Pointer) textMesh.font = font;
            }
        }
        public static void RefreshAllActiveMenus(int langIndex = -1)
        {
            try
            {
                var allMenus = Resources.FindObjectsOfTypeAll(Il2CppType.Of<MenuPanelStringManager>());
                if (allMenus != null)
                {
                    foreach (var o in allMenus)
                    {
                        var menu = o.Cast<MenuPanelStringManager>();
                        if (menu != null && menu.gameObject != null && menu.gameObject.activeInHierarchy && menu.gameObject.scene.name != null)
                        {
                            UpdateMenuPanel(menu, langIndex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LocalizationPlugin.Log.LogWarning($"[TEXT] Error refreshing menus: {ex.Message}");
            }
        }

        public static void RestoreOriginalTranslations(int langIndex)
        {
            try
            {
                RefreshAllActiveMenus(langIndex);
                FontManager.RestoreOriginalFonts();
                LocalizationPlugin.Log.LogInfo($"[TEXT] Restored original strings for language index {langIndex}.");
            }
            catch (Exception ex)
            {
                LocalizationPlugin.Log.LogWarning($"[TEXT] Error restoring translations: {ex.Message}");
            }
        }

        public static void ApplyTranslations()
        {
            if (SubtitleCount == 0 && UICount == 0 && FallbackCount == 0)
                return;

            RefreshAllActiveMenus(12);
            FontManager.ApplyToMetaManager();
        }

        public static void ApplyToMenuPanelDirect(MenuPanelStringManager menu)
        {
            UpdateMenuPanel(menu, 12);
        }

        public static void ApplyReplacementsToAllLoaded()
        {
            ApplyTranslations();
        }





        private static List<string> ParseCsvLine(string line)
        {
            var result = new List<string>();
            var cur = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        cur.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    result.Add(cur.ToString());
                    cur.Clear();
                }
                else
                {
                    cur.Append(c);
                }
            }
            result.Add(cur.ToString());
            return result;
        }

        private static int FindMatchingQuote(string s, int start)
        {
            for (int i = start; i < s.Length; i++)
            {
                if (s[i] == '\\')
                {
                    i++; // skip escaped char
                    continue;
                }
                if (s[i] == '"')
                    return i;
            }
            return -1;
        }

        private static string UnescapeJsonString(string s)
        {
            return s.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t").Replace("\\\\", "\\");
        }
    }
}
