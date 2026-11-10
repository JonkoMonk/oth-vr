# OTH VR (pre-alpha): play *over the hill* in VR

Sit in your car in **over the hill** (Funselektor, Steam) with a PC VR headset: real 3D, look around with your head, drive with keyboard, controller or wheel. Menus and the map show on a big flat screen floating in front of you. Works with MelonLoader 0.7 or BepInEx 6.

Watch it in action: [OTH VR pre-alpha video](https://youtu.be/jyjneFaQpw0).

**Made to be used together with [OTH Cockpit](https://github.com/JonkoMonk/oth-cockpit)** ([Nexus Mods](https://www.nexusmods.com/overthehill/mods/90)): VR puts you in its driver's seat with a full interior for every car, so install both. Without it VR still works, but you only get the game's own chase camera in 3D. With [OTH Wheel](https://github.com/JonkoMonk/oth-wheel) ([Nexus Mods](https://www.nexusmods.com/overthehill/mods/83)) you drive it with a real steering wheel.

Questions or problems? Ask in the [JonkoMonk Discord](https://discord.gg/h2hXcYgkpE) or open an Issue.

## Pre-alpha

This is the first public version. It works and it's fun, but:

- **Tested on one setup only:** Meta Quest 3 through Virtual Desktop (VDXR), RTX 5070 Ti, about 50-60 fps. **SteamVR, Meta Link / Air Link and other headsets are untested.** It uses plain OpenXR, so they should work. Tell us if yours does (or doesn't).
- Menus, the map and loading screens show on a flat screen in front of you (no pointing with VR controllers).
- The tuner / mechanic screen doesn't work in VR yet: switch VR off (F7) to use it.
- The game's quick menu doesn't show in the headset while driving (F5 to the flat screen to use it), and there's no HUD or mod message in the headset while driving.
- Some cockpit proportions look off in VR (tight cabins, pillars close, a too-big steering wheel on the Rust Ox and Blast Wagon); the cockpits were built for a flat screen. Tall grass pokes through the cab.
- The Minimap mod also uses F5 and F7: change the VR keys in the settings file if you use both.

## Download and install

Get `OTH-VR-v…-prealpha.zip` from the [Releases](../../releases) page or [Nexus Mods](https://www.nexusmods.com/overthehill/mods/135). Install [OTH Cockpit](https://github.com/JonkoMonk/oth-cockpit/releases) too.

1. Close the game. Copy the **contents** of the zip's `MelonLoader` folder (or `BepInEx` folder) into the game folder.
2. Set your headset's OpenXR runtime (Virtual Desktop, Meta Horizon Link app, or SteamVR).
3. Start the game, put on the headset, press **F7**.

Full player guide (runtimes, keys, settings, uninstall): [README-VR.md](README-VR.md).

## Keys

F7 VR on/off · F11 recenter · F5 view (Auto / 3D / flat screen) · Home/End seat forward/back · PageUp/PageDown seat up/down (per car) · F6 cockpit view (OTH Cockpit).

## How it works

over the hill is an IL2CPP Unity 6 game (URP, Direct3D 11), so Unity's own VR support can't be switched on. The mod opens its own OpenXR session instead:

- `native/` is a small C++ helper (`OthVrNative.dll`): an OpenXR session on the game's own D3D11 device, one swapchain per eye plus a quad layer for the flat screen. It copies the eye textures into the swapchains on Unity's render thread (`GL.IssuePluginEvent`). `OxrTest.cpp` is a stand-alone developer test without the game (not in the release zip).
- `src/` is the mod (C#, net6.0, MelonLoader and BepInEx from one code base): two eye cameras at your head (the OTH Cockpit seat when present), each with the game's own upscaler (DLSS/FSR) and VR-safe screen effects, GPU Instancer registration so trees show in both eyes, and an FMOD listener at your head.

Build: `build.ps1 -NoInstall` (Visual Studio C++ Build Tools, the [OpenXR SDK](https://github.com/KhronosGroup/OpenXR-SDK-Source/releases) 1.1.63 in `%USERPROFILE%\dev\tools\openxr-1.1.63\sdk`, and the game's generated interop assemblies; paths in `src/Directory.Build.props`). `package.ps1` makes the release zip.

All the code was made by Claude (Anthropic's AI) in conversations with JonkoMonk, who designed, tested and decided how it should feel.

## Licence

MIT, see [LICENSE](LICENSE). The release zip includes the Khronos OpenXR loader (`openxr_loader.dll`), Apache-2.0.
