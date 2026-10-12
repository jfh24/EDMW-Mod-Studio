@echo off
rem Run inside the caller's setlocal scope so the compiler environment is retained.
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" set "VSWHERE=%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe"
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
exit /b %errorlevel%
