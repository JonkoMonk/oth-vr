using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if MELON
using Il2Cpp;
using Il2CppCinemachine;
#else
using Cinemachine;
#endif

namespace OthVr;

// The "step 0" questions about the game, answered into the log (Dump key, and once when VR starts):
// cameras, screen effects (URP volumes + renderer features), menus/HUD (canvases), camera shake
// (Cinemachine noise/impulse) and the size of the cars (is 1 unit 1 metre?).
internal static class Probe
{
    public static void Dump()
    {
        var sb = new StringBuilder("=== OTH VR dump ===\n");
        Section(sb, "screen", () =>
            sb.Append($"  {Screen.width}x{Screen.height} fullscreen {Screen.fullScreenMode}, vSync {QualitySettings.vSyncCount}, " +
                      $"targetFrameRate {Application.targetFrameRate}, graphics {SystemInfo.graphicsDeviceType} / {SystemInfo.graphicsDeviceName}, " +
                      $"colour space {QualitySettings.activeColorSpace}\n"));
        Section(sb, "cameras", () => Cameras(sb));
        Section(sb, "render pipeline", () => Pipeline(sb));
        Section(sb, "volumes (screen effects)", () => Volumes(sb));
        Section(sb, "canvases (menus, HUD)", () => Canvases(sb));
        Section(sb, "cinemachine (camera shake)", () => Cinemachine(sb));
        Section(sb, "GPU Instancer (trees, grass, rocks)", () => sb.Append($"  {Instancer.Describe()}\n"));
        Section(sb, "cars (world scale)", () => Cars(sb));
        Plugin.Info(sb.ToString());
    }

    static void Section(StringBuilder sb, string name, Action body)
    {
        sb.Append($"-- {name}\n");
        try { body(); }
        catch (Exception e) { sb.Append($"  failed: {e.GetType().Name}: {e.Message}\n"); }
    }

#pragma warning disable CS0618   // FindObjectsOfType: fine for a one-off dump
    static void Cameras(StringBuilder sb)
    {
        foreach (var cam in Camera.allCameras)
        {
            sb.Append($"  '{PathOf(cam.transform)}' tag {cam.tag} depth {cam.depth} fov {cam.fieldOfView:0.#} near {cam.nearClipPlane} far {cam.farClipPlane} " +
                      $"mask {Layers(cam.cullingMask)} target {(cam.targetTexture != null ? cam.targetTexture.name : "screen")} hdr {cam.allowHDR} msaa {cam.allowMSAA}\n");
            try
            {
                var d = CameraExtensions.GetUniversalAdditionalCameraData(cam);
                sb.Append($"    urp: type {d.renderType} post {d.renderPostProcessing} aa {d.antialiasing}/{d.antialiasingQuality} shadows {d.renderShadows} " +
                          $"renderer {d.m_RendererIndex} stack {(d.cameraStack != null ? d.cameraStack.Count : 0)} volumes {Layers(d.volumeLayerMask)} " +
                          $"trigger {(d.volumeTrigger != null ? d.volumeTrigger.name : "self")} depthTex {d.requiresDepthTexture} colorTex {d.requiresColorTexture}\n");
                if (d.cameraStack != null)
                    foreach (var c in d.cameraStack) sb.Append($"    stacked: '{(c != null ? PathOf(c.transform) : "null")}'\n");
            }
            catch (Exception e) { sb.Append($"    urp: {e.Message}\n"); }
            sb.Append($"    components: {Components(cam.gameObject)}\n");
        }
    }

    static void Pipeline(StringBuilder sb)
    {
        var rp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
        sb.Append($"  asset '{(rp != null ? rp.name : "none")}' quality level {QualitySettings.GetQualityLevel()} '{QualitySettings.names[QualitySettings.GetQualityLevel()]}'\n");
        var urp = rp != null ? rp.TryCast<UniversalRenderPipelineAsset>() : null;
        if (urp == null) return;
        sb.Append($"  renderScale {urp.renderScale} msaa {urp.msaaSampleCount} hdr {urp.supportsHDR} shadowDistance {urp.shadowDistance} cascades {urp.shadowCascadeCount} " +
                  $"upscaling {urp.upscalingFilter}\n");
        var list = urp.m_RendererDataList;
        for (int i = 0; list != null && i < list.Length; i++)
        {
            var data = list[i];
            if (data == null) continue;
            var features = new List<string>();
            foreach (var f in data.rendererFeatures)
                if (f != null) features.Add($"{f.name} ({f.GetIl2CppType().Name}, {(f.isActive ? "on" : "off")})");
            sb.Append($"  renderer {i} '{data.name}': {string.Join(", ", features)}\n");
        }
    }

    static void Volumes(StringBuilder sb)
    {
        foreach (var v in UnityEngine.Object.FindObjectsOfType<Volume>())
        {
            sb.Append($"  '{PathOf(v.transform)}' global {v.isGlobal} priority {v.priority} weight {v.weight} enabled {v.enabled && v.gameObject.activeInHierarchy} " +
                      $"layer {LayerMask.LayerToName(v.gameObject.layer)}\n");
            var profile = v.sharedProfile;
            if (profile == null) continue;
            foreach (var c in profile.components)
                if (c != null) sb.Append($"    {c.GetIl2CppType().Name}{(c.active ? "" : " (off)")}\n");
        }
    }

    static void Canvases(StringBuilder sb)
    {
        foreach (var c in UnityEngine.Object.FindObjectsOfType<Canvas>())
        {
            if (!c.isRootCanvas) continue;
            sb.Append($"  '{PathOf(c.transform)}' {c.renderMode} order {c.sortingOrder} camera {(c.worldCamera != null ? c.worldCamera.name : "-")} " +
                      $"active {c.enabled && c.gameObject.activeInHierarchy} scaler {Components(c.gameObject)}\n");
        }
    }

    static void Cinemachine(StringBuilder sb)
    {
        foreach (var b in UnityEngine.Object.FindObjectsOfType<CinemachineVirtualCameraBase>())
        {
            sb.Append($"  '{PathOf(b.transform)}' {b.GetIl2CppType().Name} priority {b.Priority} enabled {b.isActiveAndEnabled}\n");
            var shake = new List<string>();
            foreach (var c in b.GetComponentsInChildren<Component>(true))
            {
                string n = c != null ? c.GetIl2CppType().Name : "";
                if (n.Contains("Noise") || n.Contains("Perlin") || n.Contains("Impulse") || n.Contains("Shake"))
                    shake.Add($"{n} on '{c.name}'");
            }
            if (shake.Count > 0) sb.Append($"    shake: {string.Join(", ", shake)}\n");
        }
        foreach (var s in UnityEngine.Object.FindObjectsOfType<CinemachineImpulseSource>())
            sb.Append($"  impulse source '{PathOf(s.transform)}'\n");
    }

    static void Cars(StringBuilder sb)
    {
        foreach (var vi in UnityEngine.Object.FindObjectsOfType<VehicleInput>())
        {
            var t = vi.transform;
            var b = new Bounds();
            bool any = false;
            foreach (var r in t.GetComponentsInChildren<MeshRenderer>())
            {
                if (r == null || !r.enabled) continue;
                // car-local bounds of each part, roughly (axis-aligned in car space)
                var lb = r.bounds;
                var c = t.InverseTransformPoint(lb.center);
                var e = t.InverseTransformVector(lb.extents);
                var part = new Bounds(c, new Vector3(Mathf.Abs(e.x), Mathf.Abs(e.y), Mathf.Abs(e.z)) * 2f);
                if (!any) { b = part; any = true; } else b.Encapsulate(part);
            }
            var size = b.size;
            sb.Append($"  '{t.name}' scale {t.lossyScale} size L {size.z:0.00} W {size.x:0.00} H {size.y:0.00} (from {b.min.y:0.00} to {b.max.y:0.00}) " +
                      $"(car-local, game units)\n");
        }
    }
#pragma warning restore CS0618

    static string Components(GameObject go)
    {
        var names = new List<string>();
        foreach (var c in go.GetComponents<Component>())
            if (c != null) names.Add(c.GetIl2CppType().Name);
        return string.Join(", ", names);
    }

    static string Layers(int mask)
    {
        if (mask == -1) return "all";
        var names = new List<string>();
        for (int i = 0; i < 32; i++)
            if ((mask & (1 << i)) != 0) names.Add(LayerMask.LayerToName(i) is { Length: > 0 } n ? n : i.ToString());
        return "[" + string.Join(",", names) + "]";
    }

    static string PathOf(Transform t)
    {
        var parts = new List<string>();
        for (int i = 0; t != null && i < 6; t = t.parent, i++) parts.Insert(0, t.name);
        return string.Join("/", parts);
    }
}
