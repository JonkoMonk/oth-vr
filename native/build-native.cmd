@echo off
rem Builds OthVrNative.dll (x64, static runtime) into %1. Needs the Visual Studio C++ Build Tools and the
rem OpenXR loader SDK in %USERPROFILE%\dev\tools\openxr-1.1.63\sdk (see build.ps1).
setlocal
set OUT=%~1
set SRC=%~dp0
set XR=%USERPROFILE%\dev\tools\openxr-1.1.63\sdk
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq delims=" %%i in (`call "%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VS=%%i"
if not defined VS (echo Visual Studio C++ Build Tools not found & exit /b 1)
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul 2>nul || exit /b 1
if not exist "%OUT%\obj" mkdir "%OUT%\obj"
fxc /nologo /T vs_5_0 /E VS /Fh "%OUT%\obj\blit_vs.h" /Vn g_blitVS "%SRC%blit.hlsl" >nul || exit /b 1
fxc /nologo /T ps_5_0 /E PS /Fh "%OUT%\obj\blit_ps.h" /Vn g_blitPS "%SRC%blit.hlsl" >nul || exit /b 1
cl /nologo /O2 /MT /EHsc /std:c++17 /W3 /LD /I "%XR%\include" /I "%OUT%\obj" /Fo"%OUT%\obj\\" "%SRC%OthVrNative.cpp" ^
   /link /OUT:"%OUT%\OthVrNative.dll" /IMPLIB:"%OUT%\obj\OthVrNative.lib" "%XR%\x64\lib\openxr_loader.lib" d3d11.lib dxgi.lib || exit /b 1
copy /y "%XR%\x64\bin\openxr_loader.dll" "%OUT%\" >nul
rem Test program (no game needed): OxrTest.exe [seconds]
cl /nologo /O2 /MT /EHsc /std:c++17 /W3 /Fo"%OUT%\obj\\" /Fe"%OUT%\OxrTest.exe" "%SRC%OxrTest.cpp" ^
   /link "%OUT%\obj\OthVrNative.lib" d3d11.lib >nul || exit /b 1
