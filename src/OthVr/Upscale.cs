using System;
using UnityEngine;
#if MELON
using Il2CppTND.Upscaling.Framework;
#else
using TND.Upscaling.Framework;
#endif

namespace OthVr;

// The game smooths and sharpens its picture with an upscaler (TND: DLSS / FSR / XeSS) on its camera.
// Each eye gets its own, set like the game's (or as chosen in [View] Upscaler / UpscalerQuality):
// the eye renders at a lower resolution and the upscaler rebuilds it sharp and without jagged edges.
internal static class Upscale
{
    // Before the eye's GameObject is switched on (the component sets itself up in OnEnable).
    public static void Add(GameObject eye)
    {
        if (Settings.Upscaler.Value == EyeUpscaler.Off) return;
        try { eye.AddComponent<TNDUpscaler>(); }
        catch (Exception e) { Plugin.Warn($"upscaler not added to {eye.name} ({e.Message})"); }
    }

    // True when the eye upscales (then it also does the anti-aliasing).
    public static bool Apply(Camera main, Camera eye)
    {
        try
        {
            var up = eye.GetComponent<TNDUpscaler>();
            if (up == null) return false;
            var game = main != null ? main.GetComponent<TNDUpscaler>() : null;
            var name = Settings.Upscaler.Value switch
            {
                EyeUpscaler.DLSS => UpscalerName.DLSS4,
                EyeUpscaler.FSR => UpscalerName.FSR3,
                _ => game != null ? game.GetSelectedUpscaler() : UpscalerName.None,
            };
            var quality = Settings.UpscalerQuality.Value switch
            {
                EyeUpscalerQuality.NativeAA => UpscalerQuality.NativeAA,
                EyeUpscalerQuality.Quality => UpscalerQuality.Quality,
                EyeUpscalerQuality.Balanced => UpscalerQuality.Balanced,
                EyeUpscalerQuality.Performance => UpscalerQuality.Performance,
                _ => game != null ? game.GetQuality() : UpscalerQuality.Quality,
            };
            if (up.GetSelectedUpscaler() != name) up.SetUpscaler(name);
            if (up.GetQuality() != quality) up.SetQuality(quality);
            if (game != null)
            {
                up.SetSharpening(game.GetSharpening());
                up.SetSharpness(game.GetSharpness());
            }
            var active = up.GetActiveUpscaler();
            return active != UpscalerName.None && quality != UpscalerQuality.Off;
        }
        catch (Exception e) { Plugin.ErrorOnce("upscaler", e); return false; }
    }

    public static string Describe(Camera cam)
    {
        try
        {
            var up = cam != null ? cam.GetComponent<TNDUpscaler>() : null;
            return up == null ? "none" : $"{up.GetActiveUpscaler()} ({up.GetSelectedUpscaler()} selected) {up.GetQuality()} render scale {up.GetRenderScale():0.00}";
        }
        catch (Exception e) { return "unreadable: " + e.Message; }
    }
}
