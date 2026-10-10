using System;
using HarmonyLib;
using UnityEngine.Video;

namespace TGYHTurkishPatch
{
    [HarmonyPatch]
    public static class VideoPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(StartMenu), "Start")]
        public static void StartMenu_Start_Prefix(StartMenu __instance)
        {
            try
            {
                if (!LocalizationPlugin.IsTurkishActive) return;
                if (__instance == null) return;

                if (__instance.loopVideoPlayer != null)
                    VideoManager.ApplyReplacement(__instance.loopVideoPlayer);

                if (__instance.panDownVideoPlayer != null)
                    VideoManager.ApplyReplacement(__instance.panDownVideoPlayer);

                if (__instance.quitHot != null)
                    {
                    var vp = __instance.quitHot.GetComponent<VideoPlayer>();
                    if (vp != null)
                        VideoManager.ApplyReplacement(vp);
                }
            }
            catch (Exception ex)
            {
                LocalizationPlugin.Log.LogWarning($"[VIDEO] StartMenu hook error: {ex.Message}");
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(VideoPlayerPanel), "Start")]
        public static void VideoPlayerPanel_Start_Prefix(VideoPlayerPanel __instance)
        {
            try
            {
                if (!LocalizationPlugin.IsTurkishActive) return;
                if (__instance != null && __instance.videoPlayer != null)
                {
                    VideoManager.ApplyReplacement(__instance.videoPlayer);
                }
            }
            catch (Exception ex)
            {
                LocalizationPlugin.Log.LogWarning($"[VIDEO] VideoPlayerPanel hook error: {ex.Message}");
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PrepareVideoOnStart), "Start")]
        public static void PrepareVideoOnStart_Start_Prefix(PrepareVideoOnStart __instance)
        {
            try
            {
                if (!LocalizationPlugin.IsTurkishActive) return;
                if (__instance != null && __instance.videoPlayer != null)
                {
                    VideoManager.ApplyReplacement(__instance.videoPlayer);
                }
            }
            catch (Exception ex)
            {
                LocalizationPlugin.Log.LogWarning($"[VIDEO] PrepareVideoOnStart hook error: {ex.Message}");
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(VideoPlayer), nameof(VideoPlayer.Play))]
        public static void VideoPlayer_Play_Prefix(VideoPlayer __instance)
        {
            try
            {
                if (__instance == null) return;
                if (LocalizationPlugin.IsTurkishActive)
                {
                    VideoManager.ApplyReplacement(__instance);
                }
            }
            catch { }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(VideoPlayer), nameof(VideoPlayer.Prepare))]
        public static void VideoPlayer_Prepare_Prefix(VideoPlayer __instance)
        {
            try
            {
                if (__instance == null) return;
                if (LocalizationPlugin.IsTurkishActive)
                {
                    VideoManager.ApplyReplacement(__instance);
                }
            }
            catch { }
        }
    }
}
