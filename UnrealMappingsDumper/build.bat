@echo off
REM ============================================================================
REM Builds UnrealMappingsDumper.dll (x64), the DLL the API injects into UEFN to
REM dump a .usmap mapping file for the running build.
REM
REM Usage:  build.bat [output_dir] [configuration]
REM   output_dir    : where to place UnrealMappingsDumper.dll (default: <repo>/libs)
REM   configuration : Release (default) or Debug
REM
REM MSBuild resolution order:
REM   1. msbuild already on PATH (e.g. a Developer Command Prompt)
REM   2. vswhere -> Visual Studio 2019 or newer with the C++ workload
REM
REM The API finds the DLL through USMAP_DUMPER_DLL, or next to the executable, or
REM in libs/ - the same search order the Oodle and RAD Audio libraries use.
REM ============================================================================
setlocal enabledelayedexpansion

set "DUMPERDIR=%~dp0"
set "PROJECT=%DUMPERDIR%UnrealMappingsDumper\UnrealMappingsDumper.vcxproj"

set "OUT=%~1"
if "%OUT%"=="" set "OUT=%DUMPERDIR%..\libs"
set "CONFIG=%~2"
if "%CONFIG%"=="" set "CONFIG=Release"

set "BUILDDIR=%DUMPERDIR%UnrealMappingsDumper\x64\%CONFIG%"
set "DLL=%BUILDDIR%\UnrealMappingsDumper.dll"

if not exist "%PROJECT%" (
    echo ERROR: Project file not found.
    echo   %PROJECT%
    exit /b 1
)

REM --- Resolve MSBuild ---
set "MSBUILD="
where msbuild >nul 2>&1
if not errorlevel 1 set "MSBUILD=msbuild"

REM vswhere can report several MSBuild copies (x86/amd64); the first one is taken. Its output goes
REM through a temp file because a quoted program path inside a for /f backtick block is unreliable.
if "%MSBUILD%"=="" (
    set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
    if exist "!VSWHERE!" (
        set "VSWLIST=%TEMP%\UnrealMappingsDumper.msbuild.%RANDOM%.txt"
        "!VSWHERE!" -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" > "!VSWLIST!" 2>nul
        for /f "usebackq delims=" %%i in ("!VSWLIST!") do (
            if not defined MSBUILD set "MSBUILD=%%i"
        )
        del "!VSWLIST!" >nul 2>&1
    )
)

if "%MSBUILD%"=="" (
    echo ERROR: MSBuild was not found.
    echo Install Visual Studio 2019+ with the "Desktop development with C++" workload,
    echo or run this script from a Developer Command Prompt.
    exit /b 1
)

if not exist "%OUT%" mkdir "%OUT%"
if errorlevel 1 (
    echo ERROR: Failed to create the output directory.
    echo   %OUT%
    exit /b 1
)

echo Building UnrealMappingsDumper.dll  (%CONFIG% ^| x64)  -^>  %OUT%
REM OutDir must end in a separator; the doubled backslash keeps the closing quote from being
REM escaped, which would otherwise swallow the rest of the command line into the property.
"%MSBUILD%" "%PROJECT%" -p:Configuration=%CONFIG% -p:Platform=x64 -p:OutDir="%BUILDDIR%\\" -nologo -v:minimal
if errorlevel 1 (
    echo ERROR: MSBuild failed.
    exit /b 1
)

if not exist "%DLL%" (
    echo ERROR: UnrealMappingsDumper.dll was not produced.
    echo   %DLL%
    exit /b 1
)

copy /y "%DLL%" "%OUT%\" >nul
if errorlevel 1 (
    echo ERROR: Failed to copy UnrealMappingsDumper.dll.
    echo Make sure it is not currently loaded by another process.
    exit /b 1
)

echo OK: %OUT%\UnrealMappingsDumper.dll
exit /b 0
