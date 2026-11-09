using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace OthVr;

// The native helper OthVrNative.dll (C++, ..\..\native) and the OpenXR loader next to it, loaded from
// ModPaths.NativeDir by full path (the game doesn't look in our folders by itself).
internal static class Native
{
    const string Dll = "OthVrNative";
    static IntPtr _handle;
    static bool _tried;

    public static bool Load()
    {
        if (_tried) return _handle != IntPtr.Zero;
        _tried = true;
        string dir = ModPaths.NativeDir;
        try
        {
            // The loader first, so OthVrNative.dll's import of it is already satisfied.
            NativeLibrary.Load(Path.Combine(dir, "openxr_loader.dll"));
            _handle = NativeLibrary.Load(Path.Combine(dir, Dll + ".dll"));
            NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), Resolve);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Error($"can't load the VR helper from {dir}: {e.Message}");
            _handle = IntPtr.Zero;
            return false;
        }
    }

    static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path) => name == Dll ? _handle : IntPtr.Zero;

    [DllImport(Dll)] public static extern int OxrInit(IntPtr unityTexture, float renderScale, StringBuilder info, int infoSize);
    [DllImport(Dll)] public static extern void OxrEyeSize(out int w, out int h);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern void OxrSimulate(string pictureDir);
    [DllImport(Dll)] public static extern void OxrSimulateRate(int hz);
    [DllImport(Dll)] public static extern void OxrShutdown();
    [DllImport(Dll)] public static extern int OxrPoll();
    [DllImport(Dll)] public static extern int OxrWaitFrame(int slot, int mode, float[] quad, int flipEyes, int flipScreen, [Out] float[] views);
    [DllImport(Dll)] public static extern int OxrSetEyeTextures(IntPtr left, IntPtr right);
    [DllImport(Dll)] public static extern int OxrSetScreenTexture(IntPtr tex, int w, int h);
    [DllImport(Dll)] public static extern IntPtr OxrRenderEventFunc();
    [DllImport(Dll)] public static extern int OxrFramesSubmitted();
    [DllImport(Dll)] static extern int OxrTakeLog(StringBuilder buf, int size);

    public const int Running = 1, Visible = 2, Exit = 4, Focused = 8, HeadsetRecentered = 16;

    static readonly StringBuilder _line = new(1024);

    // The helper's messages, into our log.
    public static void FlushLog()
    {
        for (int i = 0; i < 50; i++)
        {
            _line.Clear();
            if (OxrTakeLog(_line, _line.Capacity) <= 0) return;
            Plugin.Info("openxr: " + _line);
        }
    }

    // Well-known OpenXR errors in plain words.
    public static string Explain(int result) => result switch
    {
        -2 => "the OpenXR runtime failed",
        -8 => "an OpenXR feature isn't supported by this runtime",
        -9 => "the OpenXR runtime has no DirectX 11 support",
        -13 or -17 => "the headset connection was lost",
        -35 => "no headset found (is Virtual Desktop / Link / SteamVR connected and the headset on?)",
        -38 => "the game isn't running on DirectX 11",
        -51 => "no OpenXR runtime set as active (set one in Virtual Desktop, the Meta app or SteamVR)",
        _ => $"OpenXR error {result}",
    };
}
