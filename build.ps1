# Builds the VR mod (all output outside OneDrive, in %USERPROFILE%\dev\oth-vr-build) and installs it.
#   1. native\build-native.cmd: OthVrNative.dll (C++, Visual Studio Build Tools) + openxr_loader.dll (OpenXR SDK 1.1.63,
#      unpacked in %USERPROFILE%\dev\tools\openxr-1.1.63\sdk)
#   2. dotnet: OthVr.Melon.dll (MelonLoader) and OthVr.dll (BepInEx)
#   3. Install into the test copies (and with -Real also the real Steam game):
#        MelonLoader -> Mods\OthVr.Melon.dll + UserData\OthVr\native\*.dll
#        BepInEx     -> BepInEx\plugins\OthVr.dll + BepInEx\plugins\OthVr\native\*.dll
# Usage: powershell -File build.ps1 [-NoInstall] [-Real]
param(
    [switch]$NoInstall,
    [switch]$Real
)
$ErrorActionPreference = "Stop"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$root = "$env:USERPROFILE\dev\oth-vr-build"
$native = "$root\native"

& cmd /c "`"$PSScriptRoot\native\build-native.cmd`" `"$native`""
if ($LASTEXITCODE -ne 0) { throw "Native build failed" }

foreach ($proj in "OthVr.Melon", "OthVr") {
    dotnet build (Join-Path $PSScriptRoot "src\$proj\$proj.csproj") -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Build failed ($proj)" }
}

if (-not $NoInstall) {
    $games = @("$env:USERPROFILE\dev\oth-ml-test\game", "$env:USERPROFILE\dev\oth-bep-test\game")
    if ($Real) { $games += "C:\Program Files (x86)\Steam\steamapps\common\over the hill" }
    foreach ($game in $games) {
        $running = Get-Process "over the hill" -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$game\*" }
        if ($running) { Write-Warning "Skipped $game (that game is running)"; continue }
        if (Test-Path "$game\MelonLoader") {
            $dll = "$root\bin\OthVr.Melon\Release\net6.0\OthVr.Melon.dll"; $mods = "$game\Mods"; $data = "$game\UserData\OthVr\native"
        } elseif (Test-Path "$game\BepInEx\core") {
            $dll = "$root\bin\OthVr\Release\net6.0\OthVr.dll"; $mods = "$game\BepInEx\plugins"; $data = "$game\BepInEx\plugins\OthVr\native"
        } else { continue }
        New-Item -ItemType Directory -Force -Path $mods, $data | Out-Null
        Copy-Item $dll $mods -Force
        Copy-Item "$native\OthVrNative.dll", "$native\openxr_loader.dll", "$native\OxrTest.exe" $data -Force
        Write-Host "Installed into $game"
    }
}
