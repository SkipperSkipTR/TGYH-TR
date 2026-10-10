using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TGYHTurkishPatch
{
    [HarmonyPatch]
    public static class GameLoopPatches
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        // GetAsyncKeyState is a Windows API (also provided by Wine/Proton). Native macOS and
        // Linux builds simply have no reload hotkey.
        private static readonly bool HasKeyPolling = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        private static bool _lastReloadKey = false;
        private static bool _didInitialTextApply = false;

        /// <summary>
        /// Windows virtual-key code for the configured hotkey. The game reads input through
        /// Rewired, so the key is polled from the OS instead of Unity's legacy Input class.
        /// </summary>
        private static int ToVirtualKey(KeyCode key)
        {
            if (key >= KeyCode.F1 && key <= KeyCode.F12) return 0x70 + (key - KeyCode.F1);
            if (key >= KeyCode.A && key <= KeyCode.Z) return 0x41 + (key - KeyCode.A);
            if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9) return 0x30 + (key - KeyCode.Alpha0);
            return 0;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Rewired.InputManager_Base), "DoUpdate")]
        public static void InputManager_DoUpdate_Prefix()
        {
            if (!_didInitialTextApply)
            {
                try
                {
                    if (SubtitleBoard.Actual != null)
                    {
                        _didInitialTextApply = true;
                        if (LocalizationPlugin.IsTurkishActive)
                            TextManager.ApplyTranslations();
                    }
                }
                catch { }
            }

            if (LocalizationPlugin.EnableTextureReplacements.Value)
            {
                try { TextureManager.Update(); }
                catch (Exception ex) { LocalizationPlugin.Log.LogError($"[TEXTURE] Update error: {ex.Message}"); }
            }

            // Hot-reload modded assets. GetAsyncKeyState sees keys pressed in any window, so
            // only react while the game has focus.
            int vk = HasKeyPolling ? ToVirtualKey(LocalizationPlugin.ReloadHotKey.Value) : 0;
            bool keyDown = vk != 0 && (GetAsyncKeyState(vk) & 0x8000) != 0 && Application.isFocused;
            if (keyDown && !_lastReloadKey)
            {
                try
                {
                    LocalizationPlugin.Log.LogInfo("[HOTKEY] Hot-reloading modded assets...");
                    LocalizationPlugin.ReloadAssets();
                    if (LocalizationPlugin.IsTurkishActive)
                    {
                        TextManager.ApplyReplacementsToAllLoaded();
                        VideoManager.ApplyReplacementsInScene();
                    }
                    LocalizationPlugin.Log.LogInfo("[HOTKEY] Hot-reload complete!");
                }
                catch (Exception ex)
                {
                    LocalizationPlugin.Log.LogError($"[HOTKEY] Hot-reload failed: {ex}");
                }
            }
            _lastReloadKey = keyDown;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SceneManager), nameof(SceneManager.Internal_SceneLoaded))]
        public static void SceneManager_Internal_SceneLoaded_Postfix(Scene scene, LoadSceneMode mode)
        {
            try
            {
                if (LocalizationPlugin.VerboseLogging != null && LocalizationPlugin.VerboseLogging.Value)
                {
                    LocalizationPlugin.Log.LogInfo($"Scene loaded: '{scene.name}' (mode: {mode})");
                }

                if (LocalizationPlugin.EnableTextReplacements != null && LocalizationPlugin.EnableTextReplacements.Value && LocalizationPlugin.IsTurkishActive)
                {
                    TextManager.ApplyReplacementsToAllLoaded();
                }

                FontManager.LogFontState($"scene '{scene.name}'");

                // Pick up the new scene's textures on the next frame instead of the next poll.
                TextureManager.RequestScan();

                if (LocalizationPlugin.EnableVideoReplacements != null && LocalizationPlugin.EnableVideoReplacements.Value && LocalizationPlugin.IsTurkishActive)
                {
                    VideoManager.ApplyReplacementsInScene();
                }
            }
            catch { }
        }
    }
}
