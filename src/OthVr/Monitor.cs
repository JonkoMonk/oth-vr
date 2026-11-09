using System;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

namespace OthVr;

// While you drive in 3D, the monitor shows your left eye: the game camera keeps running (other code
// needs Camera.main, it carries the sound listener, and Cinemachine moves it) but only draws a tiny
// corner without effects or shadows, and a full-screen picture of the left eye sits over that, under
// the game's own HUD and menus. Everything is put back when the 3D view ends or VR goes off.
// (Not cullingMask = 0: Cinemachine only follows virtual cameras on layers the camera draws, so the
// camera, and with it the sound, would stay behind.)
internal static class Monitor
{
    static readonly Rect Tiny = new(0f, 0f, 0.05f, 0.05f);

    static Camera _dimmed;          // the game camera we shrank
    static Rect _rect;
    static bool _post, _shadows;
    static GameObject _canvas;
    static RawImage _image;

    // The game camera's own values, also while it's shrunk (the eye cameras copy these).
    public static bool PostProcessing(Camera main, UniversalAdditionalCameraData d) => main == _dimmed ? _post : d.renderPostProcessing;
    public static bool Shadows(Camera main, UniversalAdditionalCameraData d) => main == _dimmed ? _shadows : d.renderShadows;

    public static void ShowEye(Camera main, RenderTexture eye)
    {
        if (main == null || eye == null) { Restore(); return; }
        if (_dimmed != main)
        {
            Restore();
            _dimmed = main;
            _rect = main.rect;
            main.rect = Tiny;
            try
            {
                var d = CameraExtensions.GetUniversalAdditionalCameraData(main);
                _post = d.renderPostProcessing;
                _shadows = d.renderShadows;
                d.renderPostProcessing = false;
                d.renderShadows = false;
            }
            catch (Exception e) { Plugin.ErrorOnce("monitor urp", e); }
        }
        if (_canvas == null) MakeCanvas();
        if (_image == null) return;
        _image.texture = eye;
        // Fill the screen width with the eye picture, cut top and bottom to the screen's shape.
        float eyeAspect = (float)eye.width / eye.height, screenAspect = (float)Screen.width / Mathf.Max(1, Screen.height);
        float h = Mathf.Clamp01(eyeAspect / screenAspect);
        _image.uvRect = new Rect(0f, (1f - h) * 0.5f, 1f, h);
        if (!_canvas.activeSelf) _canvas.SetActive(true);
    }

    public static void Restore()
    {
        if (_canvas != null && _canvas.activeSelf) _canvas.SetActive(false);
        if (_dimmed == null) return;
        try
        {
            _dimmed.rect = _rect;
            var d = CameraExtensions.GetUniversalAdditionalCameraData(_dimmed);
            d.renderPostProcessing = _post;
            d.renderShadows = _shadows;
        }
        catch (Exception e) { Plugin.ErrorOnce("monitor restore", e); }
        _dimmed = null;
    }

    public static void Destroy()
    {
        Restore();
        if (_canvas != null) UnityEngine.Object.Destroy(_canvas);
        _canvas = null;
        _image = null;
    }

    static void MakeCanvas()
    {
        try
        {
            _canvas = new GameObject("OthVr Monitor");
            UnityEngine.Object.DontDestroyOnLoad(_canvas);
            var canvas = _canvas.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = -10000;   // under the game's HUD and menus
            var img = new GameObject("Eye");
            img.transform.SetParent(_canvas.transform, false);
            _image = img.AddComponent<RawImage>();
            _image.raycastTarget = false;
            var rt = img.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }
        catch (Exception e) { Plugin.ErrorOnce("monitor canvas", e); }
    }
}
