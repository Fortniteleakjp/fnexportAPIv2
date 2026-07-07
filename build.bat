@echo off
setlocal EnableExtensions

rem ============================================================================
rem FortnitePorting normal Windows build script
rem
rem Usage:
rem   build.bat
rem   build.bat Debug
rem   build.bat Release
rem
rem Output:
rem   FortnitePorting\bin\<Configuration>\net9.0\
rem ============================================================================

set "ROOT=%~dp0"
set "PROJECT=%ROOT%FortnitePorting\FortnitePorting.csproj"
set "NATIVE_BUILD=%ROOT%RADADecoder\shim\build.bat"
set "LIBS=%ROOT%libs"
set "CONFIG=%~1"
set "FRAMEWORK=net9.0"

if "%CONFIG%"=="" set "CONFIG=Release"

set "OUTDIR=%ROOT%FortnitePorting\bin\%CONFIG%\%FRAMEWORK%"
set "EXE=%OUTDIR%\FortnitePorting.exe"
set "NATIVE_STDOUT=%TEMP%\FortnitePorting.native.%RANDOM%.out"
set "NATIVE_STDERR=%TEMP%\FortnitePorting.native.%RANDOM%.err"

echo.
echo ============================================================
echo  FortnitePorting normal build
echo ============================================================
echo  Root      : %ROOT%
echo  Config    : %CONFIG%
echo  Framework : %FRAMEWORK%
echo  Output    : %OUTDIR%
echo.

if not exist "%PROJECT%" (
    echo ERROR: Project file not found.
    echo   %PROJECT%
    exit /b 1
)

if not exist "%NATIVE_BUILD%" (
    echo ERROR: RAD native build script not found.
    echo   %NATIVE_BUILD%
    exit /b 1
)

where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: dotnet was not found on PATH.
    exit /b 1
)

if not exist "%LIBS%" mkdir "%LIBS%"
if errorlevel 1 (
    echo ERROR: Failed to create libs directory.
    echo   %LIBS%
    exit /b 1
)

echo [1/4] Building native RAD decoder shim...
call "%NATIVE_BUILD%" "%LIBS%" >"%NATIVE_STDOUT%" 2>"%NATIVE_STDERR%"
set "NATIVE_RC=%ERRORLEVEL%"
if exist "%NATIVE_STDOUT%" type "%NATIVE_STDOUT%"
if not "%NATIVE_RC%"=="0" (
    if exist "%NATIVE_STDERR%" type "%NATIVE_STDERR%"
    echo ERROR: Native RAD decoder build failed.
    if exist "%NATIVE_STDOUT%" del "%NATIVE_STDOUT%" >nul 2>&1
    if exist "%NATIVE_STDERR%" del "%NATIVE_STDERR%" >nul 2>&1
    exit /b 1
)
if exist "%NATIVE_STDOUT%" del "%NATIVE_STDOUT%" >nul 2>&1
if exist "%NATIVE_STDERR%" del "%NATIVE_STDERR%" >nul 2>&1

if not exist "%LIBS%\rada_decode.dll" (
    echo ERROR: rada_decode.dll was not produced.
    echo   %LIBS%\rada_decode.dll
    exit /b 1
)

echo.
echo [2/4] Building FortnitePorting...
dotnet build "%PROJECT%" --configuration "%CONFIG%" --framework "%FRAMEWORK%"
if errorlevel 1 (
    echo ERROR: dotnet build failed.
    exit /b 1
)

if not exist "%EXE%" (
    echo ERROR: Built executable not found.
    echo   %EXE%
    exit /b 1
)

echo.
echo [3/4] Copying native runtime libraries...
copy /y "%LIBS%\rada_decode.dll" "%OUTDIR%\" >nul
if errorlevel 1 (
    echo ERROR: Failed to copy rada_decode.dll.
    echo Make sure FortnitePorting.exe is not running and the output directory is writable.
    exit /b 1
)
echo   copied rada_decode.dll

if exist "%LIBS%\oo2core_9_win64.dll" (
    copy /y "%LIBS%\oo2core_9_win64.dll" "%OUTDIR%\" >nul
    if errorlevel 1 (
        echo ERROR: Failed to copy oo2core_9_win64.dll.
        exit /b 1
    )
    echo   copied oo2core_9_win64.dll
) else (
    echo   skipped oo2core_9_win64.dll
)

if exist "%LIBS%\zlib-ng2.dll" (
    copy /y "%LIBS%\zlib-ng2.dll" "%OUTDIR%\" >nul
    if errorlevel 1 (
        echo ERROR: Failed to copy zlib-ng2.dll.
        exit /b 1
    )
    echo   copied zlib-ng2.dll
) else (
    echo   skipped zlib-ng2.dll
)

echo.
echo [4/4] Verifying output...
if not exist "%EXE%" (
    echo ERROR: Built executable missing after copy.
    echo   %EXE%
    exit /b 1
)

if not exist "%OUTDIR%\rada_decode.dll" (
    echo ERROR: rada_decode.dll missing from output directory.
    echo   %OUTDIR%\rada_decode.dll
    exit /b 1
)

echo.
echo Build succeeded.
echo EXE: "%EXE%"
echo Native DLL: "%OUTDIR%\rada_decode.dll"
echo.
echo Run from:
echo   %OUTDIR%
exit /b 0
