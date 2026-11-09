#if MELON
using Il2Cpp;
#endif

namespace OthVr;

// Whether the game is actually driving (not in menus, the map, camp, photo mode or paused).
// Same test as the OTH Cockpit mod uses, so the cockpit and the 3D view switch together.
internal static class Gate
{
    public static bool Driving()
    {
        try
        {
            var gi = GameInput.instance;
            if (gi == null || !gi.playerInputActions.Driving.enabled) return false;
        }
        catch { return false; }
        try
        {
            var cvm = ControllerVibrationManager.Instance;
            if (cvm != null && cvm.pauseMenuOpen) return false;
        }
        catch { }
        return true;
    }
}
