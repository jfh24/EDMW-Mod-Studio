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
if "%~1"=="--build-only" goto compile_runtime
cl /nologo /std:c++17 /O2 /W4 /WX /EHsc /MT /GS /Fo:build\EffectRulesTests.obj /Fe:build\EffectRulesTests.exe EffectRulesTests.cpp /link /MACHINE:X86 /DYNAMICBASE /NXCOMPAT /Brepro
if errorlevel 1 exit /b %errorlevel%
build\EffectRulesTests.exe
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++17 /O2 /W4 /WX /EHsc /MT /GS /Fo:build\UnitEffectRuntimeTests.obj /Fe:build\UnitEffectRuntimeTests.exe UnitEffectRuntimeTests.cpp /link /MACHINE:X86 /DYNAMICBASE /NXCOMPAT /Brepro
if errorlevel 1 exit /b %errorlevel%
build\UnitEffectRuntimeTests.exe
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++17 /O2 /W4 /WX /EHsc /MT /GS /Fo:build\UnitEffectActionTests.obj /Fe:build\UnitEffectActionTests.exe UnitEffectActionTests.cpp /link /MACHINE:X86 /DYNAMICBASE /NXCOMPAT /Brepro
if errorlevel 1 exit /b %errorlevel%
build\UnitEffectActionTests.exe
if errorlevel 1 exit /b %errorlevel%
rem Declare the test's execution level so Windows does not detect its name as an installer.
cl /nologo /std:c++17 /O2 /W4 /WX /EHsc /MT /GS /Fo:build\UnitEffectInstallRulesTests.obj /Fe:build\UnitEffectInstallRulesTests.exe UnitEffectInstallRulesTests.cpp /link /MACHINE:X86 /DYNAMICBASE /NXCOMPAT /Brepro /MANIFEST:EMBED /MANIFESTUAC:"level='asInvoker' uiAccess='false'"
if errorlevel 1 exit /b %errorlevel%
build\UnitEffectInstallRulesTests.exe
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++17 /O2 /W4 /WX /EHsc /MT /GS /Fo:build\UnitEffectSpawnNativeTests.obj /Fe:build\UnitEffectSpawnNativeTests.exe UnitEffectSpawnNativeTests.cpp /link /MACHINE:X86 /DYNAMICBASE /NXCOMPAT /Brepro
if errorlevel 1 exit /b %errorlevel%
build\UnitEffectSpawnNativeTests.exe
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++17 /O2 /W4 /WX /EHsc /MT /GS /Fo:build\UnitEffectAbilityCostsTests.obj /Fe:build\UnitEffectAbilityCostsTests.exe UnitEffectAbilityCostsTests.cpp /link /MACHINE:X86 /DYNAMICBASE /NXCOMPAT /Brepro
if errorlevel 1 exit /b %errorlevel%
build\UnitEffectAbilityCostsTests.exe
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++17 /O2 /W4 /WX /EHsc /MT /GS /Fo:build\UnitEffectProductionTests.obj /Fe:build\UnitEffectProductionTests.exe UnitEffectProductionTests.cpp /link /MACHINE:X86 /DYNAMICBASE /NXCOMPAT /Brepro
if errorlevel 1 exit /b %errorlevel%
build\UnitEffectProductionTests.exe
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++17 /O2 /W4 /WX /EHsc /MT /GS /Fo:build\UnitEffectDescriptionsTests.obj /Fe:build\UnitEffectDescriptionsTests.exe UnitEffectDescriptionsTests.cpp /link /MACHINE:X86 /DYNAMICBASE /NXCOMPAT /Brepro
if errorlevel 1 exit /b %errorlevel%
build\UnitEffectDescriptionsTests.exe
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++17 /O2 /W4 /WX /EHsc /MT /GS /Fo:build\UnitEffectModsPanelTests.obj /Fe:build\UnitEffectModsPanelTests.exe UnitEffectModsPanelTests.cpp /link /MACHINE:X86 /DYNAMICBASE /NXCOMPAT /Brepro
if errorlevel 1 exit /b %errorlevel%
build\UnitEffectModsPanelTests.exe
if errorlevel 1 exit /b %errorlevel%
:compile_runtime
cl /nologo /std:c++17 /O2 /W4 /WX /EHsc /MT /GS /LD /Fo:build\ /Fe:build\EDMWEffects.dll EDMWEffects.cpp UnitEffectNative.cpp UnitEffectExtras.cpp UnitEffectProduction.cpp NativeAbilityBindings.cpp UnitEffectModsPanel.cpp UnitEffectActionsNative.cpp UnitEffectSpawnNative.cpp UnitEffectAbilityCostsNative.cpp /link /DEF:EDMWEffects.def /MACHINE:X86 /DYNAMICBASE /NXCOMPAT /Brepro
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++17 /O2 /W4 /WX /EHsc /MT /GS /Fo:build\EffectsLauncher.obj /Fe:build\EDMWEffectsLauncher.exe EffectsLauncher.cpp /link /MACHINE:X86 /DYNAMICBASE /NXCOMPAT /Brepro
exit /b %errorlevel%
