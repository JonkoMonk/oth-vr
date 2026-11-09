using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
namespace OthVr;

// VR on/off (F7), recenter (F11), view mode (F5), dump (Insert).
// Every frame while VR is on:
//   LateUpdate  wait for the headset (paces the game), get both eyes' poses, place the two eye cameras
//               at your head (Anchor) or, in menus, ask for the flat-screen picture;
//   Unity renders the eye cameras into render textures;
//   end of frame (coroutine, after everything incl. menus is drawn): capture the flat screen if needed and
//               queue the native helper's render-thread step, which copies the textures to the headset and
//               ends the OpenXR frame. (OnGUI does it instead if the end-of-frame step never runs.)
public class VrBehaviour : MonoBehaviour
{
    public VrBehaviour(IntPtr ptr) : base(ptr) { }

    bool _on;                       // OpenXR started
    bool _autoStartTried;
    float _nextReload;
    int _toggleVk, _recenterVk, _viewVk, _dumpVk;
    string _keyNames;
    int _flags;                     // last OxrPoll result

    GameObject _rig;
    Transform _head;                // between the eyes (screen-effect volumes are judged from here)
    readonly Camera[] _eyes = new Camera[2];
    readonly RenderTexture[] _eyeRts = new RenderTexture[2];
    RenderTexture _screenRt;
    bool _captureScreen;
    bool _endOfFrameWorks;          // the end-of-frame coroutine has run
    Camera _copiedFrom;
    float _nextCopy, _nextInstancerCheck;
    string _lastUpscaler;

    IntPtr _renderEvent;
    CommandBuffer _cb;              // only if GL.IssuePluginEvent isn't usable
    int _slot;
    bool _pending;                  // a frame was waited for; its render-thread step isn't queued yet
    readonly float[] _views = new float[22];
    readonly float[] _quad = new float[9];

    bool _wantRecenter;
    Vector3 _centerPos;             // head position at recenter (Unity axes, headset tracking space)
    float _centerYaw;               // head heading at recenter, degrees
    int _vsyncWas, _fpsWas;
    bool _dumpedMenu, _dumpedDriving, _simShot;

    string _info = "", _message = "";
    float _messageUntil;
    int _framesAtLastCount;
    float _countTime, _fps;
    int _lastMode = -1;

    void Start()
    {
        try
        {
#if MELON
            MelonLoader.MelonCoroutines.Start(EndOfFrame());
#else
            StartCoroutine(BepInEx.Unity.IL2CPP.Utils.Collections.CollectionExtensions.WrapToIl2Cpp(EndOfFrame()));
#endif
        }
        catch (Exception e) { Plugin.ErrorOnce("coroutine", e); }
    }

    IEnumerator EndOfFrame()
    {
        var wait = new WaitForEndOfFrame();
        while (true)
        {
            yield return wait;
            try
            {
                if (!_endOfFrameWorks) { _endOfFrameWorks = true; Plugin.Info("end-of-frame step runs"); }
                if (_on && _pending) FinishFrame();
            }
            catch (Exception e) { Plugin.ErrorOnce("end of frame", e); }
        }
    }

    void FinishFrame()
    {
        // The finished game picture with its menus (only valid once the frame is completely drawn).
        if (_captureScreen && _screenRt != null)
            ScreenCapture.CaptureScreenshotIntoRenderTexture(_screenRt);
        _captureScreen = false;
        Submit();
    }

    void Update()
    {
        float now = Time.unscaledTime;
        try
        {
            if (now >= _nextReload)
            {
                _nextReload = now + 2f;
                if (Settings.ReloadIfChanged())
                {
                    Plugin.Info("config reloaded");
                    _nextCopy = 0f;
                    Comfort.Destroy();
                    if (_on && Settings.VrSafeEffects.Value) Comfort.Create();
                }
                string names = Settings.ToggleKey.Value + Settings.RecenterKey.Value + Settings.ViewKey.Value + Settings.DumpKey.Value;
                if (_keyNames != names)
                {
                    _keyNames = names;
                    _toggleVk = Keys.Parse(Settings.ToggleKey.Value);
                    _recenterVk = Keys.Parse(Settings.RecenterKey.Value);
                    _viewVk = Keys.Parse(Settings.ViewKey.Value);
                    _dumpVk = Keys.Parse(Settings.DumpKey.Value);
                }
            }
            bool toggle = Keys.Pressed(_toggleVk), recenter = Keys.Pressed(_recenterVk), view = Keys.Pressed(_viewVk), dump = Keys.Pressed(_dumpVk);
            if (dump) { Probe.Dump(); Say("Cameras, effects and menus written to the log.", 3f); }
            // The step-0 facts into the log once per game: in the main menu, and the first time you drive.
            if (!_dumpedMenu && now > 25f) { _dumpedMenu = true; Probe.Dump(); }
            if (!_dumpedDriving && now > 5f && Gate.Driving()) { _dumpedDriving = true; Probe.Dump(); }

            if (!Settings.Enabled.Value) { if (_on) StopVr("switched off in the settings"); return; }
            if (toggle) { if (_on) StopVr("key"); else StartVr(); }
            else if (!_on && !_autoStartTried && Settings.StartInVr.Value && now > 10f) { _autoStartTried = true; StartVr(); }
            if (!_on) return;

            Native.FlushLog();
            // A frame whose end-of-frame step didn't run (window hidden, ...) still has to be ended.
            if (_pending) Submit();
            _flags = Native.OxrPoll();
            Native.FlushLog();
            if ((_flags & Native.Exit) != 0) { StopVr("the headset ended the session"); return; }

            SeatKeys();
            if (recenter) { _wantRecenter = true; Say("Recentered: straight ahead is where you look now.", 2f); }
            // The headset's own recenter (Quest button) moves its tracking space: our F11 offset no longer fits.
            if ((_flags & Native.HeadsetRecentered) != 0) { ResetCenter(); Plugin.Info("headset recentered: using its centre"); }
            if (view)
            {
                Settings.View.Value = (ViewMode)(((int)Settings.View.Value + 1) % 3);
                Settings.Saved();
                Say($"VR view: {Describe(Settings.View.Value)}", 3f);
            }
            CountFps(now);
            if (Settings.Simulate.Value && !_simShot && Native.OxrFramesSubmitted() > 1000)
            {
                _simShot = true;
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(ModPaths.DataDir, "sim_monitor.png"));
                Plugin.Info("simulate: monitor screenshot sim_monitor.png");
            }
        }
        catch (Exception e) { Plugin.ErrorOnce("update", e); Say(e.Message); }
    }

    void LateUpdate()
    {
        if (!_on) return;
        try
        {
            if ((_flags & Native.Running) == 0 || _pending) { EyesOn(false); Monitor.Restore(); Ears.Restore(); return; }
            var main = Camera.main;
            bool driving = Gate.Driving();
            var mode = Settings.View.Value;
            bool want3D = main != null && (mode == ViewMode.Always3D || (mode == ViewMode.Auto && driving));
            if (!want3D) PrepareScreen();

            int r = Native.OxrWaitFrame(_slot, want3D ? 0 : 1, want3D ? null : _quad,
                                        Settings.FlipEyes.Value ? 1 : 0, Settings.FlipScreen.Value ? 1 : 0, _views);
            if (r < 0) { EyesOn(false); Monitor.Restore(); Ears.Restore(); return; }
            _pending = true;
            if ((want3D ? 0 : 1) != _lastMode)
            {
                _lastMode = want3D ? 0 : 1;
                Plugin.Info($"showing {(want3D ? "3D" : "the flat screen")} (view {Settings.View.Value}, driving {driving})");
            }
            if (r == 1 && _wantRecenter && PoseValid()) Recenter();
            if (r == 1 && want3D) PlaceEyes(main);
            else { EyesOn(false); Ears.Restore(); }
            if (want3D && _eyes[0].enabled && Settings.MonitorShowsEye.Value) Monitor.ShowEye(main, _eyeRts[0]);
            else Monitor.Restore();
        }
        catch (Exception e) { Plugin.ErrorOnce("frame", e); }
    }

    // After every camera of this frame has rendered (IMGUI draws last).
    void OnGUI()
    {
        try
        {
            if (_on && _pending && !_endOfFrameWorks && Event.current.type == EventType.Repaint) FinishFrame();
            Overlay();
        }
        catch (Exception e) { Plugin.ErrorOnce("ongui", e); }
    }

    void Submit()
    {
        try
        {
            if (_cb == null) GL.IssuePluginEvent(_renderEvent, _slot);
            else
            {
                _cb.Clear();
                _cb.IssuePluginEvent(_renderEvent, _slot);
                Graphics.ExecuteCommandBuffer(_cb);
            }
        }
        catch (Exception e) when (_cb == null)
        {
            // In case this game build lacks GL.IssuePluginEvent: a command buffer does the same.
            Plugin.Warn($"GL.IssuePluginEvent failed ({e.Message}), using a command buffer");
            _cb = new CommandBuffer { name = "OthVr submit" };
            Submit();
            return;
        }
        _slot = (_slot + 1) & 3;
        _pending = false;
    }

    void OnApplicationQuit()
    {
        if (_on) StopVr("game closing");
    }

    // ------------------------------------------------------------------ start / stop

    void StartVr()
    {
        if (!Native.Load()) { Say("VR helper files are missing (see the log)."); return; }
        _renderEvent = Native.OxrRenderEventFunc();
        Native.OxrSimulate(Settings.Simulate.Value ? ModPaths.DataDir : null);
        Native.OxrSimulateRate(Settings.SimulateHz.Value);

        // Any texture leads the helper to the game's Direct3D 11 device.
        var probe = MakeRt(16, 16, 0, "OthVr probe");
        var info = new StringBuilder(1024);
        int r = Native.OxrInit(probe.GetNativeTexturePtr(), Settings.RenderScale.Value, info, info.Capacity);
        Native.FlushLog();
        probe.Release();
        Destroy(probe);
        if (r != 0)
        {
            Plugin.Warn($"VR didn't start: {Native.Explain(r)} ({r})");
            Say($"VR didn't start: {Native.Explain(r)}", 8f);
            return;
        }
        _info = info.ToString();
        Plugin.Info("VR on: " + _info);

        Native.OxrEyeSize(out int w, out int h);
        _rig = new GameObject("OthVr Rig");
        DontDestroyOnLoad(_rig);
        _head = new GameObject("OthVr Head").transform;
        _head.SetParent(_rig.transform, false);
        for (int e = 0; e < 2; e++)
        {
            _eyeRts[e] = MakeRt(w, h, 24, e == 0 ? "OthVr Left" : "OthVr Right");
            var go = new GameObject(e == 0 ? "OthVr Left Eye" : "OthVr Right Eye");
            go.SetActive(false);
            go.transform.SetParent(_rig.transform, false);
            _eyes[e] = go.AddComponent<Camera>();
            _eyes[e].enabled = false;
            _eyes[e].targetTexture = _eyeRts[e];
            Instancer.AddComponent(go);
            Upscale.Add(go);
            go.SetActive(true);
        }
        r = Native.OxrSetEyeTextures(_eyeRts[0].GetNativeTexturePtr(), _eyeRts[1].GetNativeTexturePtr());
        Native.FlushLog();
        if (r != 0) { StopVr("can't use the eye textures"); return; }

        // The headset sets the pace: no monitor vsync, no frame cap.
        _vsyncWas = QualitySettings.vSyncCount;
        _fpsWas = Application.targetFrameRate;
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;

        if (Settings.VrSafeEffects.Value) Comfort.Create();
        _on = true;
        _pending = false;
        ResetCenter();
        _copiedFrom = null;
        _lastMode = -1;
        _framesAtLastCount = Native.OxrFramesSubmitted();
        Say($"VR on. Sit straight, look ahead and press {Settings.RecenterKey.Value} to recenter. {Settings.ToggleKey.Value} = VR off.", 8f);
    }

    void StopVr(string why)
    {
        _on = false;
        _pending = false;
        try
        {
            Native.OxrSetEyeTextures(IntPtr.Zero, IntPtr.Zero);
            Native.OxrShutdown();
            Native.FlushLog();
        }
        catch (Exception e) { Plugin.ErrorOnce("shutdown", e); }
        Monitor.Destroy();
        Ears.Restore();
        Comfort.Destroy();
        RestoreShadowDistance();
        if (_rig != null) Destroy(_rig);
        _rig = null;
        for (int e = 0; e < 2; e++)
        {
            if (_eyeRts[e] != null) { _eyeRts[e].Release(); Destroy(_eyeRts[e]); }
            _eyeRts[e] = null;
            _eyes[e] = null;
        }
        if (_screenRt != null) { _screenRt.Release(); Destroy(_screenRt); }
        _screenRt = null;
        QualitySettings.vSyncCount = _vsyncWas;
        Application.targetFrameRate = _fpsWas;
        Plugin.Info($"VR off ({why})");
        Say($"VR off ({why}).", 4f);
    }

    static RenderTexture MakeRt(int w, int h, int depth, string name)
    {
        var desc = new RenderTextureDescriptor(w, h, RenderTextureFormat.ARGB32, depth);
        desc.sRGB = true;
        desc.msaaSamples = 1;
        desc.useMipMap = false;
        // DontUnloadUnusedAsset: otherwise the game's scene changes throw our textures away.
        var rt = new RenderTexture(desc) { name = name, hideFlags = HideFlags.DontUnloadUnusedAsset };
        rt.Create();
        return rt;
    }

    // ------------------------------------------------------------------ 3D

    // In flat-screen frames the runtime may not have located the head (all zeros).
    bool PoseValid()
    {
        var q = XrRot(0);
        return q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w > 0.5f;
    }

    // Home/End/PageUp/PageDown in the cockpit: your seat in this car (saved per car).
    void SeatKeys()
    {
        var dir = Keys.Pressed(Keys.Parse(Settings.SeatForwardKey.Value)) ? Vector3.forward :
                  Keys.Pressed(Keys.Parse(Settings.SeatBackKey.Value)) ? Vector3.back :
                  Keys.Pressed(Keys.Parse(Settings.SeatUpKey.Value)) ? Vector3.up :
                  Keys.Pressed(Keys.Parse(Settings.SeatDownKey.Value)) ? Vector3.down : Vector3.zero;
        if (dir == Vector3.zero) return;
        string car = Anchor.CockpitCar;
        if (car.Length == 0) { Say("Seat keys work in the cockpit view (F6).", 3f); return; }
        var v = Seats.Nudge(car, dir);
        Say($"Seat in the {car}: {v.z * 100:0} cm forward, {v.y * 100:0} cm up", 2f);
        Plugin.Info($"seat {car}: {v}");
    }

    // Straight ahead = where the headset's tracking space says (it is set by the headset's own recenter).
    // No automatic recenter: the moment it would happen, your head may be turned towards the monitor.
    void ResetCenter()
    {
        _centerPos = Vector3.zero;
        _centerYaw = 0f;
        _wantRecenter = false;
    }

    void Recenter()
    {
        // Both eyes look the same way; the head is between them.
        var p = (XrPos(0) + XrPos(1)) * 0.5f;
        var fwd = XrRot(0) * Vector3.forward;
        _centerPos = p;
        _centerYaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
        _wantRecenter = false;
        Plugin.Info($"recentered at {p} heading {_centerYaw:0}");
    }

    void PlaceEyes(Camera main)
    {
        if (!Anchor.Get(main, Settings.HorizonLock.Value, out var anchorPos, out var anchorRot)) { EyesOn(false); return; }
        if (main != _copiedFrom || Time.unscaledTime >= _nextCopy) CopyCameraSettings(main);

        var undoYaw = Quaternion.Euler(0f, -_centerYaw, 0f);
        float scale = Mathf.Max(0.1f, Settings.WorldScale.Value);
        var headSum = Vector3.zero;
        for (int e = 0; e < 2; e++)
        {
            var rel = undoYaw * (XrPos(e) - _centerPos) * scale;
            var rot = anchorRot * (undoYaw * XrRot(e));
            var pos = anchorPos + anchorRot * rel;
            headSum += pos;
            var cam = _eyes[e];
            cam.transform.SetPositionAndRotation(pos, rot);
            cam.nearClipPlane = Settings.NearClip.Value;
            int o = e * 11;
            cam.projectionMatrix = Projection(_views[o + 7], _views[o + 8], _views[o + 9], _views[o + 10], cam.nearClipPlane, cam.farClipPlane);
        }
        _head.SetPositionAndRotation(headSum * 0.5f, anchorRot);
        Ears.Follow(main, headSum * 0.5f, _eyes[0].transform.rotation);
        EyesOn(true);
        if (Time.unscaledTime >= _nextInstancerCheck)
        {
            _nextInstancerCheck = Time.unscaledTime + 1f;
            Instancer.Ensure(_eyes[0]);
            Instancer.Ensure(_eyes[1]);
        }
    }

    void EyesOn(bool on)
    {
        for (int e = 0; e < 2; e++)
            if (_eyes[e] != null && _eyes[e].enabled != on) _eyes[e].enabled = on;
    }

    // The eyes look like the game's camera: same layers, distance, effects and anti-aliasing.
    void CopyCameraSettings(Camera main)
    {
        bool changed = main != _copiedFrom;
        _copiedFrom = main;
        ApplyShadowDistance();
        _nextCopy = Time.unscaledTime + 2f;
        UniversalAdditionalCameraData src = null;
        try { src = CameraExtensions.GetUniversalAdditionalCameraData(main); } catch (Exception e) { Plugin.ErrorOnce("urp source", e); }
        for (int e = 0; e < 2; e++)
        {
            var cam = _eyes[e];
            bool was = cam.enabled;
            cam.CopyFrom(main);
            cam.rect = new Rect(0f, 0f, 1f, 1f);   // the game camera may be shrunk (Monitor)
            if (Settings.ViewDistance.Value > 0f) cam.farClipPlane = Mathf.Max(50f, Settings.ViewDistance.Value);
            cam.targetTexture = _eyeRts[e];
            cam.depth = main.depth - 1;
            cam.enabled = was;
            if (src == null) continue;
            try
            {
                var dst = CameraExtensions.GetUniversalAdditionalCameraData(cam);
                dst.renderType = CameraRenderType.Base;
                dst.renderPostProcessing = Monitor.PostProcessing(main, src) && Settings.PostProcessing.Value;
                // With an upscaler the eye needs no extra anti-aliasing (it does that itself).
                bool upscaled = Upscale.Apply(main, cam);
                dst.antialiasing = upscaled ? AntialiasingMode.None : Settings.AntiAliasing.Value switch
                {
                    EyeAntiAliasing.SMAA => AntialiasingMode.SubpixelMorphologicalAntiAliasing,
                    EyeAntiAliasing.FXAA => AntialiasingMode.FastApproximateAntialiasing,
                    EyeAntiAliasing.None => AntialiasingMode.None,
                    _ => src.antialiasing,
                };
                dst.antialiasingQuality = AntialiasingQuality.High;
                dst.renderShadows = Monitor.Shadows(main, src);
                // The comfort volume's layer (if the game camera doesn't already listen to every layer).
                dst.volumeLayerMask = src.volumeLayerMask | (Settings.VrSafeEffects.Value ? Comfort.Mask : 0);
                dst.volumeTrigger = _head;
                dst.requiresColorOption = src.requiresColorOption;
                dst.requiresDepthOption = src.requiresDepthOption;
                dst.stopNaN = src.stopNaN;
                dst.dithering = src.dithering;
                dst.SetRenderer(src.m_RendererIndex);
            }
            catch (Exception ex) { Plugin.ErrorOnce("urp copy", ex); }
        }
        string up = $"eye upscaler: {Upscale.Describe(_eyes[0])}; game camera: {Upscale.Describe(main)}";
        if (up != _lastUpscaler) { _lastUpscaler = up; Plugin.Info(up); }
        if (changed) Plugin.Info($"eye cameras copy '{main.name}' (far {main.farClipPlane}, post {(src != null && Monitor.PostProcessing(main, src))}, " +
                    $"aa {(src != null ? src.antialiasing.ToString() : "?")})");
    }

    // [Performance] ShadowDistance: the pipeline's shadow distance while VR is on (put back afterwards).
    float _gameShadowDistance = -1f;
    int _gameCascades = 4;

    void ApplyShadowDistance()
    {
        try
        {
            var urp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline?.TryCast<UniversalRenderPipelineAsset>();
            if (urp == null) return;
            float want = Settings.ShadowDistance.Value;
            int cascades = Settings.ShadowCascades.Value;
            if (want <= 0f && cascades <= 0) { RestoreShadowDistance(); return; }
            if (_gameShadowDistance < 0f) { _gameShadowDistance = urp.shadowDistance; _gameCascades = urp.shadowCascadeCount; }
            float distance = want > 0f ? want : _gameShadowDistance;
            int count = cascades > 0 ? Mathf.Clamp(cascades, 1, 4) : _gameCascades;
            if (urp.shadowDistance != distance) urp.shadowDistance = distance;
            if (urp.shadowCascadeCount != count) urp.shadowCascadeCount = count;
        }
        catch (Exception e) { Plugin.ErrorOnce("shadow distance", e); }
    }

    void RestoreShadowDistance()
    {
        if (_gameShadowDistance < 0f) return;
        try
        {
            var urp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline?.TryCast<UniversalRenderPipelineAsset>();
            if (urp != null) { urp.shadowDistance = _gameShadowDistance; urp.shadowCascadeCount = _gameCascades; }
        }
        catch (Exception e) { Plugin.ErrorOnce("shadow restore", e); }
        _gameShadowDistance = -1f;
    }

    // OpenXR's view angles (radians; left/down negative) as a Unity projection matrix.
    static Matrix4x4 Projection(float left, float right, float up, float down, float near, float far)
    {
        float l = Mathf.Tan(left) * near, r = Mathf.Tan(right) * near, t = Mathf.Tan(up) * near, b = Mathf.Tan(down) * near;
        var m = new Matrix4x4();
        m.m00 = 2f * near / (r - l);
        m.m02 = (r + l) / (r - l);
        m.m11 = 2f * near / (t - b);
        m.m12 = (t + b) / (t - b);
        m.m22 = -(far + near) / (far - near);
        m.m23 = -2f * far * near / (far - near);
        m.m32 = -1f;
        return m;
    }

    // OpenXR is right-handed (forward = -z), Unity left-handed (forward = +z): mirror z.
    Vector3 XrPos(int eye)
    {
        int o = eye * 11;
        return new Vector3(_views[o], _views[o + 1], -_views[o + 2]);
    }

    Quaternion XrRot(int eye)
    {
        int o = eye * 11;
        return new Quaternion(-_views[o + 3], -_views[o + 4], _views[o + 5], _views[o + 6]);
    }

    // ------------------------------------------------------------------ flat screen (menus, map, loading)

    // The game's finished picture (with its menus) goes on a big screen in front of where you sat at recenter.
    void PrepareScreen()
    {
        int w = Screen.width, h = Screen.height;
        if (w < 16 || h < 16) return;
        if (_screenRt == null || _screenRt.width != w || _screenRt.height != h)
        {
            if (_screenRt != null) { _screenRt.Release(); Destroy(_screenRt); }
            _screenRt = MakeRt(w, h, 0, "OthVr Screen");
            int r = Native.OxrSetScreenTexture(_screenRt.GetNativeTexturePtr(), w, h);
            Native.FlushLog();
            if (r != 0) Plugin.Warn($"flat screen: {Native.Explain(r)}");
        }
        _captureScreen = true;   // in OnGUI, once this frame is drawn

        var yaw = Quaternion.Euler(0f, _centerYaw, 0f);
        var centre = _centerPos + yaw * new Vector3(0f, 0f, Mathf.Max(0.5f, Settings.ScreenDistance.Value));
        float width = Mathf.Max(0.3f, Settings.ScreenWidth.Value);
        // back to OpenXR axes
        _quad[0] = centre.x; _quad[1] = centre.y; _quad[2] = -centre.z;
        _quad[3] = -yaw.x; _quad[4] = -yaw.y; _quad[5] = yaw.z; _quad[6] = yaw.w;
        _quad[7] = width;
        _quad[8] = width * h / w;
    }

    // ------------------------------------------------------------------ messages

    static string Describe(ViewMode m) => m switch
    {
        ViewMode.Auto => "3D while driving, flat screen in menus",
        ViewMode.Always3D => "always 3D",
        _ => "always the flat screen",
    };

    void CountFps(float now)
    {
        if (now - _countTime < 1f) return;
        int n = Native.OxrFramesSubmitted();
        _fps = (n - _framesAtLastCount) / (now - _countTime);
        if (Settings.Simulate.Value && (int)now % 5 == 0) Plugin.Info($"simulate: {_fps:0.0} fps");
        _framesAtLastCount = n;
        _countTime = now;
    }

    void Say(string text, float seconds = 6f)
    {
        _message = text;
        _messageUntil = Time.unscaledTime + seconds;
    }

    GUIStyle _style;

    void Overlay()
    {
        bool msg = Time.unscaledTime < _messageUntil;
        bool status = _on && Settings.ShowStatus.Value;
        if (!msg && !status) return;
        if (_style == null)
        {
            _style = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperRight };
            _style.normal.textColor = Color.cyan;
        }
        string text = msg ? _message : "";
        if (status)
        {
            string state = (_flags & Native.Running) == 0 ? "waiting for the headset" :
                           (_flags & Native.Visible) == 0 ? "headset not showing the game" :
                           _lastMode == 0 ? $"3D ({Anchor.Source})" : "flat screen";
            text += (text.Length > 0 ? "\n" : "") + $"VR: {state}, {_fps:0} fps";
        }
        // top right: the game has its own text at the bottom left
        GUI.Label(new Rect(Screen.width - 820, 12, 800, 70), text, _style);
    }
}
