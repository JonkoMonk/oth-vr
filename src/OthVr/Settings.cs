using System;
using System.IO;

namespace OthVr;

public enum ViewMode { Auto, Always3D, AlwaysScreen }
public enum EyeAntiAliasing { Game, None, FXAA, SMAA }
public enum EffectMode { Off, StereoSafe, Game }
public enum EyeUpscaler { Game, DLSS, FSR, Off }
public enum FogMode { Game, Light, Off }
public enum EyeUpscalerQuality { Game, NativeAA, Quality, Balanced, Performance }

// Everything the player can tune lives in jonkomonk.oth.vr.cfg (folder: see ModPaths).
// The file is re-read automatically while the game runs, so edits apply within ~2 seconds.
internal static class Settings
{
    static ConfigFile _cfg;
    public static ConfigFile Config => _cfg;
    static DateTime _lastWrite;

    public static ConfigEntry<bool> Enabled, StartInVr, ShowStatus, Simulate, SoundFollowsHead, PostProcessing, VrSafeEffects, EyeAdaptation, Purkinje, SunFlares, MonitorShowsEye, FlipEyes, FlipScreen, HorizonLock;
    public static ConfigEntry<string> ToggleKey, RecenterKey, ViewKey, DumpKey, SeatForwardKey, SeatBackKey, SeatUpKey, SeatDownKey;
    public static ConfigEntry<ViewMode> View;
    public static ConfigEntry<EyeAntiAliasing> AntiAliasing;
    public static ConfigEntry<EffectMode> Hbao, LightScattering;
    public static ConfigEntry<EyeUpscaler> Upscaler;
    public static ConfigEntry<FogMode> Fog;
    public static ConfigEntry<int> ShadowCascades;
    public static ConfigEntry<EyeUpscalerQuality> UpscalerQuality;
    public static ConfigEntry<int> SimulateHz;
    public static ConfigEntry<float> RenderScale, WorldScale, NearClip, ScreenDistance, ScreenWidth, ShadowDistance, ViewDistance, BumpSmoothing, EarsToCentre;

    public static void Bind(ConfigFile cfg)
    {
        _cfg = cfg;

        Enabled = cfg.Bind("General", "Enabled", true, "Master switch. false = the mod does nothing.");
        StartInVr = cfg.Bind("General", "StartInVr", false,
            "Switch VR on by itself as soon as the game has started (otherwise press the Toggle key).");
        ShowStatus = cfg.Bind("General", "ShowStatus", true,
            "Show a line of VR status (headset, frame rate) at the top right of the monitor while VR is on.");

        ToggleKey = cfg.Bind("Keys", "Toggle", "F7",
            "Key to switch VR on and off. Not F3/F6/F8/F9/F10: the wheel and cockpit mods use those (F9/F10 in the wheel setup wizard).");
        RecenterKey = cfg.Bind("Keys", "Recenter", "F11", "Key to set where 'straight ahead' is: sit normally, look forward, press it.");
        ViewKey = cfg.Bind("Keys", "View", "F5",
            "Key to cycle the view: Auto (3D while driving, a big flat screen in menus) -> always 3D -> always the flat screen.");
        DumpKey = cfg.Bind("Keys", "Dump", "Insert",
            "Key to write the game's cameras, screen effects and menus to the log (for working on the mod).");

        SeatForwardKey = cfg.Bind("Keys", "SeatForward", "Home", "In the cockpit: move your VR seat 2 cm forward (saved per car).");
        SeatBackKey = cfg.Bind("Keys", "SeatBack", "End", "In the cockpit: move your VR seat 2 cm back.");
        SeatUpKey = cfg.Bind("Keys", "SeatUp", "PageUp", "In the cockpit: move your VR seat 2 cm up.");
        SeatDownKey = cfg.Bind("Keys", "SeatDown", "PageDown", "In the cockpit: move your VR seat 2 cm down.");

        View = cfg.Bind("View", "Mode", ViewMode.Auto,
            "Auto = 3D while driving, the flat game on a big screen in menus, the map and loading. " +
            "Always3D / AlwaysScreen force one of them.");
        AntiAliasing = cfg.Bind("View", "AntiAliasing", EyeAntiAliasing.SMAA,
            "Smooth jagged edges in the headset. SMAA = sharp and cheap (best for VR), FXAA = cheapest but softer, " +
            "None = off, Game = whatever the game camera uses (it relies on its upscaler, which the eyes don't have).");
        Upscaler = cfg.Bind("View", "Upscaler", EyeUpscaler.Game,
            "The game's upscaler for each eye: renders fewer pixels and rebuilds a sharp, smooth picture (big fps win). " +
            "Game = the one chosen in the game's settings, DLSS (NVIDIA), FSR (any card), Off = none (then AntiAliasing is used). " +
            "Applies when VR is switched on.");
        UpscalerQuality = cfg.Bind("View", "UpscalerQuality", EyeUpscalerQuality.Quality,
            "NativeAA = full resolution, only smoothing (sharpest, slowest); Quality; Balanced; Performance (fastest). Game = as in the game.");
        RenderScale = cfg.Bind("View", "RenderScale", 1.0f,
            "Sharpness: 1 = the resolution the headset asks for. Lower (0.7) = faster, higher = sharper. Applies when VR is switched on.");
        WorldScale = cfg.Bind("View", "WorldScale", 1.0f,
            "How big the world feels. Above 1 = you are bigger (the car feels smaller), below 1 = the car feels bigger.");
        NearClip = cfg.Bind("View", "NearClip", 0.02f, "Closest distance the eyes can see, metres.");
        PostProcessing = cfg.Bind("View", "PostProcessing", true,
            "Use the game's screen effects (bloom, colour grading...) in the headset.");
        VrSafeEffects = cfg.Bind("View", "VrSafeEffects", true,
            "Switch off the effects that feel wrong in a headset: motion blur, depth of field, vignette, chromatic aberration, " +
            "film grain, lens dirt, frame bands. Bloom, fog, colour and shadows stay.");
        // Effects that can come out different in each eye (one eye lighter, the sun uneven). Only for the headset.
        EyeAdaptation = cfg.Bind("Effects", "EyeAdaptation", true,
            "The game's automatic brightness. Each eye measures its own; in tests both stayed equal. false = fixed brightness (darker).");
        Purkinje = cfg.Bind("Effects", "Purkinje", true, "The game's blue tint in the dark (same in both eyes).");
        SunFlares = cfg.Bind("Effects", "SunFlares", false, "Lens flares from the sun (placed per eye, so they don't line up).");
        Hbao = cfg.Bind("Effects", "Hbao", EffectMode.StereoSafe,
            "Shading in corners and under things. StereoSafe = without its frame-to-frame smoothing (that made one eye lighter), " +
            "Game = as in the game, Off = none (fastest, flatter).");
        LightScattering = cfg.Bind("Effects", "LightScattering", EffectMode.Off,
            "Sun rays through fog. Worked out from the sun's spot on each eye's screen, so the eyes don't match. " +
            "StereoSafe = without moving noise, Game = as in the game, Off = none.");
        MonitorShowsEye = cfg.Bind("View", "MonitorShowsEye", true,
            "While driving in 3D the monitor shows your left eye instead of the normal game picture (saves drawing the world a third time). " +
            "false = the monitor shows the normal game.");
        FlipEyes = cfg.Bind("View", "FlipEyes", true, "Turn the 3D picture upside down (only change this if the world is upside down).");
        FlipScreen = cfg.Bind("View", "FlipScreen", false, "Turn the flat screen upside down (only change this if menus are upside down).");

        ShadowDistance = cfg.Bind("Performance", "ShadowDistance", 0f,
            "Shadows up to this many metres while VR is on (the game uses 250 on Ultra). Lower = faster. 0 = as in the game.");
        ShadowCascades = cfg.Bind("Performance", "ShadowCascades", 0,
            "Shadow detail levels while VR is on (the game uses 4 on Ultra; every eye draws them all). 1-4, lower = faster. 0 = as in the game.");
        Fog = cfg.Bind("Performance", "Fog", FogMode.Game,
            "The game's volumetric fog in the headset. Game = as in the game, Light = coarser (about 20% faster in tests, but the sky loses some of its warm haze), Off = none (fastest).");
        ViewDistance = cfg.Bind("Performance", "ViewDistance", 0f,
            "How far the headset can see, metres (the game uses up to 5000). Lower = faster. 0 = as in the game.");

        SoundFollowsHead = cfg.Bind("Sound", "FollowHead", true,
            "In the 3D view you hear from your VR head: turn your head and the sounds stay where they are. " +
            "false = the game's own listening position (the car's direction).");
        EarsToCentre = cfg.Bind("Sound", "EarsToCentre", 0.7f,
            "In the cockpit seat, hear from this far towards the middle of the car (0 = from your head: the car then sounds " +
            "almost only on one side; 1 = right in the middle: the same in both ears).");

        ScreenDistance = cfg.Bind("Screen", "Distance", 2.5f, "How far away the flat screen floats, metres.");
        ScreenWidth = cfg.Bind("Screen", "Width", 3.2f, "How wide the flat screen is, metres.");

        Simulate = cfg.Bind("Debug", "Simulate", false,
            "For working on the mod without a headset: a pretend headset; the pictures it would get are saved as sim_*.bmp " +
            "next to this file, plus a screenshot of the monitor.");

        BumpSmoothing = cfg.Bind("Comfort", "BumpSmoothing", 0.15f,
            "Smooths the car's quick tilting and shaking over bumps, seconds (slopes and turns still come through). " +
            "0 = every bump exactly as the car moves. Try 0.3 if bumps feel disorienting.");
        SimulateHz = cfg.Bind("Debug", "SimulateHz", 90, "Simulate: the pretend headset's refresh rate. 0 = no limit (to measure speed).");

        HorizonLock = cfg.Bind("Comfort", "HorizonLock", false,
            "true = the horizon stays level when the car tilts and bounces (gentler on the stomach). " +
            "false = your view moves with the car, like a real drive.");

        _lastWrite = File.Exists(cfg.ConfigFilePath) ? File.GetLastWriteTimeUtc(cfg.ConfigFilePath) : DateTime.MinValue;
    }

    // True when the file was edited outside the game and has been re-read.
    public static bool ReloadIfChanged()
    {
        var path = _cfg.ConfigFilePath;
        if (!File.Exists(path)) { _cfg.Save(); _lastWrite = File.GetLastWriteTimeUtc(path); return false; }
        var t = File.GetLastWriteTimeUtc(path);
        if (t == _lastWrite) return false;
        _lastWrite = t;
        _cfg.Reload();
        return true;
    }

    // After the mod itself changed a value: don't treat our own save as an outside edit.
    public static void Saved()
    {
        var path = _cfg.ConfigFilePath;
        if (File.Exists(path)) _lastWrite = File.GetLastWriteTimeUtc(path);
    }
}
