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
cl /nologo /std:c++17 /O2 /W4 /WX /EHsc /MT /GS /Fo:build\EffectRulesTests.obj /Fe:build\EffectRulesTests.exe EffectRulesTests.cpp /link /MACHINE:X86 /DYNAMICBASE /NXCOMPAT /Brepro
if errorlevel 1 exit /b %errorlevel%
build\EffectRulesTests.exe
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++17 /O2 /W4 /WX /EHsc /MT /GS /Fo:build\UnitEffectRuntimeTests.obj /Fe:build\UnitEffectRuntimeTests.exe UnitEffectRuntimeTests.cpp /link /MACHINE:X86 /DYNAMICBASE /NXCOMPAT /Brepro
if errorlevel 1 exit /b %errorlevel%
build\UnitEffectRuntimeTests.exe
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++17 /O2 /W4 /WX /EHsc /MT /GS /LD /Fo:build\ /Fe:build\EDMWEffects.dll EDMWEffects.cpp UnitEffectNative.cpp NativeAbilityBindings.cpp /link /DEF:EDMWEffects.def /MACHINE:X86 /DYNAMICBASE /NXCOMPAT /Brepro
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++17 /O2 /W4 /WX /EHsc /MT /GS /Fo:build\EffectsLauncher.obj /Fe:build\EDMWEffectsLauncher.exe EffectsLauncher.cpp /link /MACHINE:X86 /DYNAMICBASE /NXCOMPAT /Brepro
exit /b %errorlevel%
