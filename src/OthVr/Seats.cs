using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace OthVr;

// Your VR seat per car: forward/back and up/down on top of the cockpit's eye point, saved in the
// settings file as [SeatPerCar] DingDing = right, up, forward (metres). Keys while in the cockpit:
// Home/End = forward/back, PageUp/PageDown = up/down (see [Keys]).
internal static class Seats
{
    const float Step = 0.02f;
    static readonly Dictionary<string, ConfigEntry<string>> _entries = new();

    public static Vector3 Get(string car)
    {
        var e = Entry(car);
        if (e == null) return Vector3.zero;
        var p = e.Value.Split(',');
        if (p.Length != 3) return Vector3.zero;
        var ci = CultureInfo.InvariantCulture;
        return float.TryParse(p[0], NumberStyles.Float, ci, out var x) && float.TryParse(p[1], NumberStyles.Float, ci, out var y) &&
               float.TryParse(p[2], NumberStyles.Float, ci, out var z) ? new Vector3(x, y, z) : Vector3.zero;
    }

    // dir: (0,0,1) forward, (0,1,0) up... one step. Returns the new offset.
    public static Vector3 Nudge(string car, Vector3 dir)
    {
        var v = Get(car) + dir * Step;
        v = new Vector3((float)Math.Round(v.x, 3), (float)Math.Round(v.y, 3), (float)Math.Round(v.z, 3));
        var e = Entry(car);
        if (e != null)
        {
            e.Value = string.Format(CultureInfo.InvariantCulture, "{0}, {1}, {2}", v.x, v.y, v.z);
            Settings.Saved();
        }
        return v;
    }

    static ConfigEntry<string> Entry(string car)
    {
        if (string.IsNullOrEmpty(car) || Settings.Config == null) return null;
        if (!_entries.TryGetValue(car, out var e))
        {
            e = Settings.Config.Bind("SeatPerCar", car, "0, 0, 0",
                $"Your VR seat in the {car}: right, up, forward in metres, on top of the cockpit's eye point (Home/End/PageUp/PageDown in the cockpit).");
            _entries[car] = e;
        }
        return e;
    }
}
