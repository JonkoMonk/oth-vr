using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if MELON
using BeautifyEffect = Il2CppBeautify.Universal.Beautify;
using HbaoEffect = Il2CppHorizonBasedAmbientOcclusion.Universal.HBAO;
using LightScatteringEffect = Il2CppOccaSoftware.LSPP.Runtime.LightScatteringPostProcess;
using ButoFog = Il2CppOccaSoftware.Buto.Runtime.ButoVolumetricFog;
using ButoFogMode = Il2CppOccaSoftware.Buto.Runtime.VolumetricFogMode;
#else
using BeautifyEffect = Beautify.Universal.Beautify;
using HbaoEffect = HorizonBasedAmbientOcclusion.Universal.HBAO;
using LightScatteringEffect = OccaSoftware.LSPP.Runtime.LightScatteringPostProcess;
using ButoFog = OccaSoftware.Buto.Runtime.ButoVolumetricFog;
using ButoFogMode = OccaSoftware.Buto.Runtime.VolumetricFogMode;
#endif

namespace OthVr;

// Screen effects that feel wrong in a headset (motion blur, depth of field, vignette, chromatic
// aberration, film grain, lens dirt, frame bands, blur) switched off for the eye cameras only:
// a global volume of top priority on a layer that only the eye cameras listen to. The flat game
// on the monitor keeps every effect. Bloom, fog, colour grading, AO and sharpening stay.
internal static class Comfort
{
    static GameObject _go;
    public static int Layer { get; private set; } = -1;

    // The layer mask the eye cameras add to the game camera's volume mask (0 = none).
    public static int Mask => Layer >= 0 ? 1 << Layer : 0;

    public static void Create()
    {
        if (_go != null) return;
        Layer = FreeLayer();
        if (Layer < 0) { Plugin.Warn("comfort: no free layer, screen effects stay as in the game"); return; }
        var done = new List<string>();
        try
        {
            _go = new GameObject("OthVr Comfort Volume") { layer = Layer };
            UnityEngine.Object.DontDestroyOnLoad(_go);
            var vol = _go.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.priority = 10000f;
            vol.weight = 1f;
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            vol.sharedProfile = profile;

            Try(done, "motion blur", () =>
            {
                var mb = profile.Add(Il2CppType.Of<MotionBlur>(), true).Cast<MotionBlur>();
                Set(mb.intensity, 0f);
            });
            Try(done, "beautify", () =>
            {
                var b = profile.Add(Il2CppType.Of<BeautifyEffect>(), true).Cast<BeautifyEffect>();
                Set(b.depthOfField, false);
                Set(b.vignettingOuterRing, 0f);
                Set(b.vignettingInnerRing, 0f);
                Set(b.chromaticAberrationIntensity, 0f);
                Set(b.filmGrainEnabled, false);
                Set(b.lensDirtIntensity, 0f);
                Set(b.frame, false);
                Set(b.blurIntensity, 0f);
                // Effects that can come out different in each eye ([Effects] section):
                if (!Settings.EyeAdaptation.Value) Set(b.eyeAdaptation, false);    // each eye measures its own brightness
                if (!Settings.Purkinje.Value) Set(b.purkinje, false);
                if (!Settings.SunFlares.Value) Set(b.sunFlaresIntensity, 0f);      // placed from the sun's spot on each eye's screen
            });
            if (Settings.Hbao.Value != EffectMode.Game)
                Try(done, $"hbao {Settings.Hbao.Value}", () =>
                {
                    var h = profile.Add(Il2CppType.Of<HbaoEffect>(), true).Cast<HbaoEffect>();
                    if (Settings.Hbao.Value == EffectMode.Off) Set(h.intensity, 0f);
                    else Set(h.temporalFilterEnabled, false);   // blends each eye with its own past frames
                });
            if (Settings.Fog.Value != FogMode.Game)
                Try(done, $"fog {Settings.Fog.Value}", () =>
                {
                    var f = profile.Add(Il2CppType.Of<ButoFog>(), true).Cast<ButoFog>();
                    if (Settings.Fog.Value == FogMode.Off) { f.mode.overrideState = true; f.mode.value = ButoFogMode.Off; }
                    else { f.gridPixelSize.overrideState = true; f.gridPixelSize.value = 24; }   // the game: ~12 screen pixels per cell
                });
            if (Settings.LightScattering.Value != EffectMode.Game)
                Try(done, $"light scattering {Settings.LightScattering.Value}", () =>
                {
                    var l = profile.Add(Il2CppType.Of<LightScatteringEffect>(), true).Cast<LightScatteringEffect>();
                    if (Settings.LightScattering.Value == EffectMode.Off) Set(l.fogDensity, 0f);
                    else Set(l.animateSamplingOffset, false);   // moving noise, different per eye
                });
            Plugin.Info($"comfort volume on layer {Layer}: {string.Join(", ", done)}");
        }
        catch (Exception e) { Plugin.ErrorOnce("comfort", e); }
    }

    public static void Destroy()
    {
        if (_go != null) UnityEngine.Object.Destroy(_go);
        _go = null;
    }

    static void Try(List<string> done, string what, Action a)
    {
        try { a(); done.Add(what); }
        catch (Exception e) { Plugin.Warn($"comfort: {what} not changed ({e.GetType().Name}: {e.Message})"); }
    }

    static void Set(BoolParameter p, bool v) { p.overrideState = true; p.value = v; }
    static void Set(FloatParameter p, float v) { p.overrideState = true; p.value = v; }

    // A layer nobody named (Unity has 32; the game uses about 20).
    static int FreeLayer()
    {
        for (int i = 31; i >= 8; i--)
            if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) return i;
        return -1;
    }
}
