using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OthVr;

// The mod's own hotkeys, read straight from Windows (works no matter how the game handles input),
// and only while the game window is in front.
internal static class Keys
{
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    static readonly uint _pid = (uint)Process.GetCurrentProcess().Id;
    static readonly Dictionary<int, bool> _was = new();

    public const int NumPad1 = 0x61, NumPad2 = 0x62, NumPad3 = 0x63, NumPad4 = 0x64, NumPad6 = 0x66,
                     NumPad7 = 0x67, NumPad8 = 0x68, NumPad9 = 0x69, Add = 0x6B, Subtract = 0x6D;

    public static bool GameHasFocus()
    {
        var h = GetForegroundWindow();
        return h != IntPtr.Zero && GetWindowThreadProcessId(h, out var pid) != 0 && pid == _pid;
    }

    public static bool IsDown(int vk) => vk != 0 && (GetAsyncKeyState(vk) & 0x8000) != 0;

    // True once per press (call every frame for each key you care about).
    public static bool Pressed(int vk)
    {
        bool down = GameHasFocus() && IsDown(vk);
        _was.TryGetValue(vk, out bool was);
        _was[vk] = down;
        return down && !was;
    }

    // "F8" -> 0x77, "Insert" -> 0x2D. 0 = unknown.
    public static int Parse(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length >= 2 && (name[0] == 'F' || name[0] == 'f') && int.TryParse(name.Substring(1), out int f) && f >= 1 && f <= 24)
            return 0x70 + f - 1;
        if (name.Length == 1 && char.IsLetterOrDigit(name[0])) return char.ToUpperInvariant(name[0]);
        return name.ToLowerInvariant() switch
        {
            "insert" => 0x2D, "delete" => 0x2E, "home" => 0x24, "end" => 0x23, "pageup" => 0x21, "pagedown" => 0x22,
            "pause" => 0x13, "scrolllock" => 0x91,
            _ => 0,
        };
    }
}
