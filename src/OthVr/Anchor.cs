using UnityEngine;

namespace OthVr;

// Where your head sits in the game world when you sit straight (after recentering):
//  - with the OTH Cockpit mod's view on: its seat point in the car. The cockpit mod moves its camera
//    for mouse look and "look into turns"; that is taken out again, since in VR your real head looks.
//  - otherwise: the game's own camera (chase camera), level, turning only left/right with it.
// Nothing in the cockpit mod has to change for this: it is found by its camera's name.
internal static class Anchor
{
    const string CockpitCameraName = "OthCockpit Camera";
    // OthCockpit's Look turns the head about this neck point (eye-relative, see its Look.cs).
    static readonly Vector3 Neck = new(0f, -0.07f, -0.10f);

    static Transform _cockpitCam;
    static float _nextSearch;

    public static string Source { get; private set; } = "";

    // The car whose cockpit you sit in ("DingDing"), empty outside the cockpit.
    public static string CockpitCar { get; private set; } = "";

    public static bool InCockpit => CockpitCamera() != null;

    // The car you sit in with the cockpit view on (null otherwise).
    public static Transform CockpitCarTransform
    {
        get { var cam = CockpitCamera(); return cam != null ? cam.parent : null; }
    }

    public static bool Get(Camera main, bool horizonLock, out Vector3 pos, out Quaternion rot)
    {
        var cam = CockpitCamera();
        if (cam != null && cam.parent != null)
        {
            var car = cam.parent;
            CockpitCar = car.name.Replace("(Clone)", "").Replace("Car_", "").Trim();
            var seat = cam.localPosition - (-Neck + cam.localRotation * Neck) + Seats.Get(CockpitCar);
            pos = car.TransformPoint(seat);
            rot = horizonLock ? Level(car.rotation) : Smooth(car.rotation);
            Source = "cockpit seat";
            return true;
        }
        CockpitCar = "";
        if (main != null)
        {
            pos = main.transform.position;
            rot = Level(main.transform.rotation);
            Source = "game camera";
            return true;
        }
        pos = Vector3.zero;
        rot = Quaternion.identity;
        Source = "none";
        return false;
    }

    static Quaternion _tilt = Quaternion.identity;

    // The car's heading exactly, its pitch/roll through a low-pass filter (BumpSmoothing seconds):
    // quick rocking over bumps is calmed, slopes and banking still come through.
    static Quaternion Smooth(Quaternion car)
    {
        var level = Level(car);
        var tilt = Quaternion.Inverse(level) * car;
        float tau = Settings.BumpSmoothing.Value;
        _tilt = tau <= 0f ? tilt : Quaternion.Slerp(_tilt, tilt, 1f - Mathf.Exp(-Time.unscaledDeltaTime / tau));
        return level * _tilt;
    }

    // Only the left/right heading of a rotation.
    public static Quaternion Level(Quaternion q)
    {
        var f = q * Vector3.forward;
        f.y = 0f;
        return f.sqrMagnitude < 1e-6f ? Quaternion.identity : Quaternion.LookRotation(f.normalized, Vector3.up);
    }

    // The cockpit mod's camera while its view is on (null when off or not installed).
    static Transform CockpitCamera()
    {
        if (_cockpitCam != null) return _cockpitCam.gameObject.activeInHierarchy ? _cockpitCam : null;
        if (Time.unscaledTime < _nextSearch) return null;
        _nextSearch = Time.unscaledTime + 0.5f;
        var go = GameObject.Find(CockpitCameraName);   // finds active objects only
        if (go == null) return null;
        _cockpitCam = go.transform;
        Plugin.Info("cockpit view found: VR sits you in its seat");
        return _cockpitCam;
    }
}
