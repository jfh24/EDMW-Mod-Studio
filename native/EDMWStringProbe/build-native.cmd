@echo off
setlocal
pushd "%~dp0"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
set "VSROOT="
if exist "%VSWHERE%" for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSROOT=%%i"
if not defined VSROOT exit /b 2
call "%VSROOT%\VC\Auxiliary\Build\vcvarsall.bat" x86
if errorlevel 1 exit /b %errorlevel%
if not exist build mkdir build
cl /nologo /O2 /W4 /WX /GS /guard:cf /EHsc /MT /Fo:build\EDMWStringProbe.obj /Fe:build\EDMWStringProbe.exe EDMWStringProbe.cpp User32.lib /link /MACHINE:X86 /DYNAMICBASE /NXCOMPAT /Brepro
exit /b %errorlevel%
