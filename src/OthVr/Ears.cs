using System;
using UnityEngine;
#if MELON
using Il2CppFMODUnity;
#else
using FMODUnity;
#endif

namespace OthVr;

// The game hears through an FMOD StudioListener on its camera, which faces wherever the car (or the
// cockpit camera) faces, not where you look in the headset. While the 3D view is on, our own listener
// at your VR head takes over (the game's is paused, with its settings copied), so sounds come from the
// right direction when you turn your head. In menus and with VR off the game's listener is back.
// In the cockpit seat your head is 40 cm beside the middle of the car, and the car's own sounds (engine
// low in its middle, the wheels) came almost only in the near ear: there the ears move
// [Sound] EarsToCentre of the way towards the middle of the car (like the OTH Cockpit mod does).
internal static class Ears
{
    static GameObject _go;
    static StudioListener _ours, _game;

    public static void Follow(Camera main, Vector3 pos, Quaternion rot)
    {
        if (!Settings.SoundFollowsHead.Value || main == null) { Restore(); return; }
        try
        {
            var game = main.GetComponent<StudioListener>();
            if (game == null) return;
            if (game != _game) Restore();
            if (_go == null)
            {
                _go = new GameObject("OthVr Ears");
                UnityEngine.Object.DontDestroyOnLoad(_go);
                _go.SetActive(false);
                _ours = _go.AddComponent<StudioListener>();
            }
            var car = Anchor.CockpitCarTransform;
            if (car != null)
            {
                var local = car.InverseTransformPoint(pos);
                local.x *= 1f - Mathf.Clamp01(Settings.EarsToCentre.Value);
                pos = car.TransformPoint(local);
            }
            _go.transform.SetPositionAndRotation(pos, rot);
            if (_game == null)
            {
                _game = game;
                _ours.attenuationObject = game.attenuationObject;
                _ours.nonRigidbodyVelocity = game.nonRigidbodyVelocity;
                game.enabled = false;
                _go.SetActive(true);
                Plugin.Info($"sound: listening from your VR head (game listener paused; attenuation object {(game.attenuationObject != null ? game.attenuationObject.name : "none")})");
            }
        }
        catch (Exception e) { Plugin.ErrorOnce("ears", e); Restore(); }
    }

    public static void Restore()
    {
        try
        {
            if (_go != null && _go.activeSelf) _go.SetActive(false);
            if (_game != null) _game.enabled = true;
        }
        catch (Exception e) { Plugin.ErrorOnce("ears restore", e); }
        _game = null;
    }
}
