@echo off
setlocal
pushd "%~dp0"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
set "VSROOT="
if exist "%VSWHERE%" for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSROOT=%%i"
if not defined VSROOT exit /b 2
call "%VSROOT%\VC\Auxiliary\Build\vcvars32.bat"
if errorlevel 1 exit /b %errorlevel%
if not exist build mkdir build
cl /nologo /std:c++17 /O2 /W4 /WX /EHsc /MT /GS /LD /Fo:build\ /Fe:build\EDMWRelayBridge.dll EDMWRelayBridge.cpp /link /MACHINE:X86 /DYNAMICBASE /NXCOMPAT /Brepro ws2_32.lib iphlpapi.lib user32.lib
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++17 /O2 /W4 /WX /EHsc /MT /GS /Fo:build\RelayLauncher.obj /Fe:build\EDMWRelayLauncher.exe RelayLauncher.cpp /link /MACHINE:X86 /DYNAMICBASE /NXCOMPAT /Brepro bcrypt.lib
exit /b %errorlevel%
