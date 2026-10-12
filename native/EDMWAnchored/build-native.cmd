@echo off
setlocal
pushd "%~dp0"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
set "VSROOT="
if exist "%VSWHERE%" for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSROOT=%%i"
if not defined VSROOT if exist "%VSWHERE%" for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -all -products * -property installationPath`) do if exist "%%i\VC\Auxiliary\Build\vcvarsall.bat" set "VSROOT=%%i"
if not defined VSROOT (
  echo Visual Studio C++ x86 build tools were not found.
  exit /b 2
)
set "VCVARS=%VSROOT%\VC\Auxiliary\Build\vcvars32.bat"
set "VCVARS_ARG="
if not exist "%VCVARS%" (
  set "VCVARS=%VSROOT%\VC\Auxiliary\Build\vcvarsall.bat"
  set "VCVARS_ARG=x86"
)
call "%VCVARS%" %VCVARS_ARG%
if errorlevel 1 exit /b %errorlevel%
set "DIAGNOSTIC_DEFINE="
if "%EDMW_DIAGNOSTICS%"=="1" set "DIAGNOSTIC_DEFINE=/DEDMW_ENABLE_DIAGNOSTICS"
if not exist build mkdir build
cl /nologo /O2 /W4 /WX /GS /guard:cf /EHsc /MT /c /Fo:build\ResourceAlignment.obj ResourceAlignment.cpp
if errorlevel 1 exit /b %errorlevel%
cl /nologo /O2 /W4 /WX /GS /guard:cf /EHsc /MT /Fo:build\ResourceAlignmentTests.obj /Fe:build\ResourceAlignmentTests.exe ResourceAlignmentTests.cpp build\ResourceAlignment.obj /link /MACHINE:X86 /DYNAMICBASE /NXCOMPAT /Brepro
if errorlevel 1 exit /b %errorlevel%
build\ResourceAlignmentTests.exe
if errorlevel 1 exit /b %errorlevel%
cl /nologo /O2 /W4 /WX /GS /guard:cf /EHsc /MT %DIAGNOSTIC_DEFINE% /LD /Fo:build\EDMWAnchored.obj /Fe:build\EDMWAnchored.dll EDMWAnchored.cpp build\ResourceAlignment.obj /link /DEF:EDMWAnchored.def /MACHINE:X86 /DYNAMICBASE /NXCOMPAT /Brepro
if errorlevel 1 exit /b %errorlevel%
exit /b 0
