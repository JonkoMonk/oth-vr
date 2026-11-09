# OTH VR changelog

## 0.2.0-prealpha (unreleased)

First public version.

- VR for over the hill on PC headsets through OpenXR (MelonLoader 0.7 or BepInEx 6). Tested on a Quest 3 through Virtual Desktop (VDXR); other headsets untested.
- 3D while driving, the flat game on a big floating screen in menus, the map and loading (F5 cycles Auto / always 3D / always screen).
- Sits you in the OTH Cockpit driver's seat when that mod is installed; seat forward/back/up/down per car (Home/End/PageUp/PageDown).
- F7 VR on/off, F11 recenter (or the headset's own recenter).
- Per-eye DLSS/FSR upscaling (the game's own upscaler) and SMAA; motion blur, depth of field, vignette and similar effects off in the headset; effects that differ between the eyes (sun flares, sun rays, HBAO smoothing) off or made stereo-safe.
- Trees, grass and rocks (GPU Instancer) in both eyes.
- Sound follows your head; in the cockpit the ears sit nearer the middle of the car so the engine isn't only in one ear.
- Optional horizon lock and bump smoothing; performance switches for shadows, fog and view distance.
- `OxrTest.exe`: checks your headset's OpenXR runtime without the game.
