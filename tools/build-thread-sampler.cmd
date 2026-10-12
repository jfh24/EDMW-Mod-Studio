@echo off
setlocal
pushd "%~dp0"
call "%~dp0setup-native-toolchain.cmd"
if errorlevel 1 goto :finish
if not exist build mkdir build
if errorlevel 1 goto :finish
cl /nologo /O2 /W4 /WX /EHsc /MT /Fo:build\ThreadSampler.obj /Fe:build\ThreadSampler.exe ThreadSampler.cpp
:finish
set "BUILD_EXIT=%errorlevel%"
popd
exit /b %BUILD_EXIT%
