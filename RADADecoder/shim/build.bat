@echo off
REM ============================================================================
REM Builds the native RAD Audio decode shim (rada_decode.dll) for Windows x64.
REM
REM Usage:  build.bat [output_dir]
REM   output_dir : where to place rada_decode.dll (default: <repo>/libs)
REM
REM SDK resolution order:
REM   1. vendored: RADADecoder/shim/sdk        (used by CI)
REM   2. local Unreal Engine install: C:\Program Files\Epic Games\UE_5.*
REM
REM If cl.exe is not already on PATH (e.g. local dev), this script locates and
REM calls vcvars64.bat via vswhere. In CI the MSVC environment is set up first
REM (ilammy/msvc-dev-cmd) so cl.exe is already available.
REM ============================================================================
setlocal enabledelayedexpansion
set "SHIMDIR=%~dp0"

REM --- Resolve the SDK ---
set "SDK=%SHIMDIR%sdk"
if not exist "%SDK%\Include\rada_decode.h" (
    set "SDK="
    for /d %%U in ("C:\Program Files\Epic Games\UE_5.*") do set "SDK=%%U\Engine\Source\Runtime\RadAudioCodec\SDK"
)
if "%SDK%"=="" (
    echo ERROR: RAD Audio SDK not found. Run vendor-sdk.bat or install Unreal Engine 5.x.
    exit /b 1
)
if not exist "%SDK%\Lib\radaudio_decoder_win64.lib" (
    echo ERROR: radaudio_decoder_win64.lib not found under "%SDK%\Lib".
    exit /b 1
)

REM --- Output directory ---
set "OUT=%~1"
if "%OUT%"=="" set "OUT=%SHIMDIR%..\..\libs"
if not exist "%OUT%" mkdir "%OUT%"

REM --- Ensure cl.exe is available (skip if already on PATH, e.g. CI) ---
where cl >nul 2>&1
if errorlevel 1 call :setup_msvc
if errorlevel 1 exit /b 1

echo Building rada_decode.dll  (SDK: %SDK%)  ->  %OUT%
cl /nologo /O2 /MT /LD /EHsc ^
   /I"%SDK%\Include" ^
   "%SHIMDIR%rada_shim.cpp" ^
   "%SDK%\Lib\radaudio_decoder_win64.lib" ^
   /Fe:"%OUT%\rada_decode.dll" ^
   /Fo:"%SHIMDIR%rada_shim.obj" ^
   /link /MACHINE:X64
set "RC=%ERRORLEVEL%"

del "%SHIMDIR%rada_shim.obj" >nul 2>&1
del "%OUT%\rada_decode.exp" >nul 2>&1
del "%OUT%\rada_decode.lib" >nul 2>&1

if "%RC%"=="0" ( echo OK: %OUT%\rada_decode.dll ) else ( echo FAILED rc=%RC% )
exit /b %RC%

:setup_msvc
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
    echo ERROR: cl.exe not on PATH and vswhere.exe not found. Open a "Developer Command Prompt for VS" or install Visual Studio C++ tools.
    exit /b 1
)
set "VSPATH="
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSPATH=%%i"
if "%VSPATH%"=="" (
    echo ERROR: No Visual Studio C++ toolset found.
    exit /b 1
)
call "%VSPATH%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 ( echo ERROR: vcvars64.bat failed. & exit /b 1 )
goto :eof
