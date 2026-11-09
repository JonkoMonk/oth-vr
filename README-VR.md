# OTH VR (pre-alpha): play over the hill in VR

Sit in your car in **over the hill** with a PC VR headset: real 3D, look around with your head, drive with keyboard, controller or wheel. Menus and the map show on a big flat screen floating in front of you.

## Pre-alpha: read this first

This is the first public version. It works and it's fun, but expect rough edges.

- **Tested on one setup only:** Meta Quest 3 through **Virtual Desktop** (its own OpenXR runtime, VDXR), RTX 5070 Ti. **Other headsets and runtimes (SteamVR, Meta Link / Air Link, Pico, Index, ...) are untested.** It uses plain OpenXR, so they should work, but nobody has tried yet. Please tell us if yours works.
- **About 50-60 fps** on that PC (a Quest 3 at full resolution). You need a strong graphics card. Your headset's own smoothing (Virtual Desktop SSW, Meta ASW, SteamVR motion smoothing) fills in the missing frames.
- **Made to be used together with [OTH Cockpit](https://www.nexusmods.com/overthehill/mods/90)** (free, same author): VR puts you in its driver's seat with a full interior for every car. Install both. Without it VR still works, but you only get the game's own chase camera in 3D, with no interior.

## Known limits

- **Menus, the map and loading screens show on a flat screen** in front of you. Use mouse, controller or wheel as usual; no pointing with the VR controllers. Small text (like the OTH Wheel F3 menu) is hard to read on it.
- **The tuner / mechanic screen doesn't work in VR yet.** Switch VR off (F7) to use it, then back on.
- **The game's quick menu (radial menu) doesn't show in the headset while driving.** Switch to the flat screen with F5 to use it.
- **No HUD in the headset while driving**: no compass, notifications or mod messages. The cockpit dials show speed, revs and fuel; everything else still shows on your monitor.
- **Some cockpit proportions look off in VR**: the cabins feel tight, pillars sit close to your shoulder, and on some cars (Rust Ox, Blast Wagon) the steering wheel is too big. The cockpits were built for a flat screen; we're fixing them car by car. Move your seat with Home/End/PageUp/PageDown meanwhile.
- **Tall grass pokes through the cab** when you drive through it.
- **Key clash with the Minimap mod:** it uses **F5** and **F7** too. Change the VR keys in the settings file (`[Keys]`, see below) if you use both.

## Install

Needs a mod loader in the game: **MelonLoader 0.7** (what most over the hill mods use) or **BepInEx 6**. The [OTH Wheel](https://github.com/JonkoMonk/oth-wheel/releases) installer can set up MelonLoader for you.

1. Close the game.
2. Open the zip. MelonLoader: copy the **contents** of the `MelonLoader` folder (`Mods` and `UserData`) into the game folder (Steam: right-click over the hill > Manage > Browse local files). BepInEx: the contents of the `BepInEx` folder.
3. Set your headset's OpenXR runtime (below), start the game, put on your headset, press **F7**.

Uninstall: delete `Mods\OthVr.Melon.dll` and `UserData\OthVr` (BepInEx: `BepInEx\plugins\OthVr.dll`, `BepInEx\plugins\OthVr`, `BepInEx\config\jonkomonk.oth.vr.cfg`).

## Pick your headset's OpenXR runtime

Windows uses one "active OpenXR runtime". Set it in your headset's PC software:

| Headset | Set as OpenXR runtime in | Tested |
|---|---|---|
| Quest via Virtual Desktop | Virtual Desktop Streamer (VDXR) | yes (Quest 3) |
| Quest via Link / Air Link | Meta Horizon Link app > Settings > General > OpenXR Runtime | no |
| Valve Index, Vive, Bigscreen Beyond, Pico (SteamVR) | SteamVR > Settings > OpenXR > Set SteamVR as OpenXR runtime | no |
| Pimax, Varjo | their own app | no |

Quick check without the game: run `OxrTest.exe` in the mod's `native` folder. You should see red in the left eye, blue in the right, then a green screen.

## Keys

| Key | What it does |
|---|---|
| F7 | VR on / off |
| F11 | Recenter: sit straight, look ahead, press (or use your headset's own recenter) |
| F5 | View: Auto (3D while driving, the flat screen in menus) > always 3D > always flat screen |
| Home / End | Seat forward / back (in the cockpit, saved per car) |
| PageUp / PageDown | Seat up / down |
| F6 | Cockpit view on / off (OTH Cockpit mod) |

## Settings

`jonkomonk.oth.vr.cfg` (MelonLoader: `UserData\OthVr\`, BepInEx: `BepInEx\config\`). It appears after the first start; changes apply within 2 seconds while the game runs.

- `[Keys]`: change any key above (e.g. if the Minimap mod uses F5/F7).
- `[View] UpscalerQuality`: Quality (default), Balanced, Performance = faster; NativeAA = sharpest, slowest.
- `[View] RenderScale`: lower (0.8) = faster, higher = sharper. Applies when VR is switched on.
- `[Performance] ShadowDistance`, `ShadowCascades`, `Fog`, `ViewDistance`: lower / Light = faster (0 / Game = as in the game).
- `[Comfort] HorizonLock = true`: the horizon stays level when the car tilts. `BumpSmoothing` softens quick shakes.
- `[Sound] FollowHead`: sounds stay in place when you turn your head.

## Feedback

Bugs, your headset, your fps: the comments on Nexus Mods, [GitHub Issues](https://github.com/JonkoMonk/oth-vr/issues), or the [JonkoMonk Discord](https://discord.gg/h2hXcYgkpE). A log helps: `MelonLoader\Latest.log` (or `BepInEx\LogOutput.log`).

MIT licence. The included `openxr_loader.dll` is the Khronos OpenXR loader (Apache-2.0, see `OpenXR-loader-LICENSE.txt`).
