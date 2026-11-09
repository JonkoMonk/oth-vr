# Builds the shareable VR mod zip: %USERPROFILE%\dev\oth-vr-build\dist\OTH-VR-v<version>.zip
# Inside, the folders match the game folder, once for each mod loader:
#   MelonLoader\Mods\OthVr.Melon.dll + MelonLoader\UserData\OthVr\native\*   (copy the CONTENTS of "MelonLoader" into the game folder)
#   BepInEx\BepInEx\plugins\OthVr.dll + BepInEx\BepInEx\plugins\OthVr\native\*
# plus README-VR.md, the mod's MIT licence and the OpenXR loader's Apache-2.0 licence. Fails if the account name shows up anywhere.
# No .exe in the zip: Nexus Mods quarantines zips with executables (OxrTest.exe stays a developer tool, see build.ps1).
# Usage: powershell -File package.ps1 [-Label "-prealpha"]   (label goes after the version in the zip name)
param([string]$Label = "-prealpha")
$ErrorActionPreference = "Stop"
$root = "$env:USERPROFILE\dev\oth-vr-build"
& powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "build.ps1") -NoInstall
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$version = (Select-String -Path (Join-Path $PSScriptRoot "src\OthVr\Plugin.cs") -Pattern 'Version = "([^"]+)"').Matches[0].Groups[1].Value
$stage = "$root\dist\OTH-VR-v$version$Label"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$native = "$root\native"
$natives = "$native\OthVrNative.dll", "$native\openxr_loader.dll"

New-Item -ItemType Directory -Force -Path "$stage\MelonLoader\Mods", "$stage\MelonLoader\UserData\OthVr\native" | Out-Null
Copy-Item "$root\bin\OthVr.Melon\Release\net6.0\OthVr.Melon.dll" "$stage\MelonLoader\Mods"
Copy-Item $natives "$stage\MelonLoader\UserData\OthVr\native"

New-Item -ItemType Directory -Force -Path "$stage\BepInEx\BepInEx\plugins\OthVr\native" | Out-Null
Copy-Item "$root\bin\OthVr\Release\net6.0\OthVr.dll" "$stage\BepInEx\BepInEx\plugins"
Copy-Item $natives "$stage\BepInEx\BepInEx\plugins\OthVr\native"

Copy-Item (Join-Path $PSScriptRoot "README-VR.md") $stage
Copy-Item (Join-Path $PSScriptRoot "LICENSE") "$stage\LICENSE.txt"
Copy-Item "$env:USERPROFILE\dev\tools\openxr-1.1.63\sdk\share\doc\openxr\LICENSE" "$stage\OpenXR-loader-LICENSE.txt"

$hits = Get-ChildItem $stage -Recurse -File | Select-String -Pattern $env:USERNAME -SimpleMatch -List
if ($hits) { $hits | ForEach-Object { Write-Warning "Account name in $($_.Path)" }; throw "Account name found in the package" }
if (Get-ChildItem $stage -Recurse -File -Include *.exe, *.bat, *.cmd, *.ps1, *.zip) { throw "Executable, script or nested zip in the package (Nexus quarantines those)" }

$zip = "$stage.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
# Compress-Archive on Windows PowerShell writes backslashes into the entry names, which isn't valid zip and
# shows up wrong outside Windows, so we write the entries ourselves (same as the wheel mod's package.ps1).
Add-Type -AssemblyName System.IO.Compression.FileSystem
$base = (Get-Item -LiteralPath $stage).FullName.TrimEnd('\')
$archive = [IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    foreach ($f in Get-ChildItem -LiteralPath $base -Recurse -File) {
        $entry = $f.FullName.Substring($base.Length + 1).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $entry, 'Optimal')
    }
} finally { $archive.Dispose() }
Write-Host "Package: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
