using System.IO;
using Il2CppInterop.Runtime.Injection;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

[assembly: MelonInfo(typeof(OthVr.MelonEntry), OthVr.Plugin.Name, OthVr.Plugin.Version, OthVr.Plugin.Author)]
[assembly: MelonGame("Funselektor", "over the hill")]

namespace OthVr;

// MelonLoader 0.7 entry point. Builds OthVr.Melon.dll for Mods\.
public class MelonEntry : MelonMod
{
    public override void OnInitializeMelon()
    {
        var log = LoggerInstance;
        Plugin.SetLog(s => log.Msg(s), s => log.Warning(s), s => log.Error(s));
        var dataDir = Path.Combine(MelonEnvironment.UserDataDirectory, "OthVr");
        ModPaths.Init("MelonLoader", dataDir, dataDir);
        Plugin.Start();
    }

    // Unity objects only once the engine is fully up.
    public override void OnLateInitializeMelon()
    {
        ClassInjector.RegisterTypeInIl2Cpp<VrBehaviour>();
        var go = new GameObject("OthVr");
        Object.DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.HideAndDontSave;
        go.AddComponent<VrBehaviour>();
        Plugin.Info($"loaded under MelonLoader, enabled={Settings.Enabled.Value}, native files in {ModPaths.NativeDir}");
    }
}
