using System.IO;

namespace OthVr;

// Where the mod keeps its files. Set by the loader entry point before anything else runs.
//   BepInEx:     BepInEx\config\jonkomonk.oth.vr.cfg  +  BepInEx\plugins\OthVr\ (native helper)
//   MelonLoader: UserData\OthVr\jonkomonk.oth.vr.cfg  +  UserData\OthVr\ (native helper)
// The native files (OthVrNative.dll, openxr_loader.dll) live in <DataDir>\native.
internal static class ModPaths
{
    public static string Loader { get; private set; }
    public static string SettingsFile { get; private set; }
    public static string DataDir { get; private set; }
    public static string NativeDir => Path.Combine(DataDir, "native");

    public static void Init(string loader, string settingsDir, string dataDir)
    {
        Loader = loader;
        SettingsFile = Path.Combine(settingsDir, Plugin.Guid + ".cfg");
        DataDir = dataDir;
    }
}
