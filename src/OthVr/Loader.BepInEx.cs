using System.IO;
using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace OthVr;

// BepInEx 6 (IL2CPP) entry point. Builds OthVr.dll for BepInEx\plugins\.
[BepInPlugin(Plugin.Guid, Plugin.Name, Plugin.Version)]
public class BepInExEntry : BasePlugin
{
    public override void Load()
    {
        Plugin.SetLog(s => Log.LogInfo(s), s => Log.LogWarning(s), s => Log.LogError(s));
        ModPaths.Init("BepInEx", Paths.ConfigPath, Path.Combine(Paths.PluginPath, "OthVr"));
        Plugin.Start();
        AddComponent<VrBehaviour>();
        Plugin.Info($"loaded under BepInEx, enabled={Settings.Enabled.Value}, native files in {ModPaths.NativeDir}");
    }
}
