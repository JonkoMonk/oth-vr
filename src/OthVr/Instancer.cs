using System;
using System.Collections.Generic;
using UnityEngine;
#if MELON
using Il2CppGPUInstancerPro;
#else
using GPUInstancerPro;
#endif

namespace OthVr;

// The game draws its trees, grass and rocks with GPU Instancer Pro, which draws only for the cameras in
// its own camera list. The eye cameras get a GPUICamera component (that puts them on the list), and
// Ensure() checks every second that they are still on it (cameras that are switched off can be dropped).
// Its occlusion culling stays off for the eyes (it would judge each eye by that eye's previous frame,
// which makes things pop in when you turn your head).
internal static class Instancer
{
    static readonly HashSet<int> _logged = new();

    public static void AddComponent(GameObject go)
    {
        try
        {
            var gpui = go.AddComponent<GPUICamera>();
            gpui._enableOcclusionCulling = false;
        }
        catch (Exception e) { Plugin.Warn($"GPU Instancer camera not added to {go.name} ({e.Message})"); }
    }

    public static void Ensure(Camera cam)
    {
        try
        {
            var rs = GPUIRenderingSystem.Instance;
            var provider = rs != null ? rs.CameraDataProvider : null;
            if (provider == null) return;
            int id = cam.GetInstanceID();
            rs._ignoreCameraIIDCollection?.Remove(id);
            if (provider.ContainsKey(id)) return;
            provider.AddCamera(cam);
            if (_logged.Add(id)) Plugin.Info($"GPU Instancer: {cam.name} added to its camera list ({Describe()})");
        }
        catch (Exception e) { Plugin.ErrorOnce("gpui ensure", e); }
    }

    public static string Describe()
    {
        try
        {
            var rs = GPUIRenderingSystem.Instance;
            if (rs == null) return "not running";
            var provider = rs.CameraDataProvider;
            if (provider == null) return "no camera list";
            var names = new List<string>();
            for (int i = 0; i < provider.Count; i++)
            {
                var d = provider.GetValueAtIndex(i);
                var c = d != null ? d.ActiveCamera : null;
                names.Add(c != null ? c.name : "?");
            }
            return $"{provider.Count} cameras: {string.Join(", ", names)}";
        }
        catch (Exception e) { return "unreadable: " + e.Message; }
    }
}
