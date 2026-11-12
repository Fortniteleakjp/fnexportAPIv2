@echo off
setlocal EnableExtensions

rem ============================================================
rem  fnexportAPI - one-step start (Windows)
rem
rem  Usage:
rem    run.bat              -> http://localhost:3849
rem    run.bat 5000         -> http://localhost:5000
rem    run.bat 5000 Debug   -> Debug build
rem
rem  Double-clicking works too. Data (libs/, mappings/, chunk_cache/,
rem  build_history/ ...) is always kept in this folder, wherever the
rem  script is started from.
rem ============================================================

cd /d "%~dp0"

if not "%~1"=="" set "PORT=%~1"
if "%PORT%"=="" set "PORT=3849"
set "CONFIG=%~2"
if "%CONFIG%"=="" set "CONFIG=Release"

where dotnet >nul 2>&1
if errorlevel 1 goto :no_sdk
dotnet --list-sdks 2>nul | findstr /b /c:"10." >nul
if errorlevel 1 goto :no_sdk

rem Leftovers of an older self-contained build make the exe look for .NET only in its own folder
rem ("You must install or update .NET"); this build uses the installed runtime instead.
for %%F in (hostfxr.dll hostpolicy.dll) do (
    if exist "%~dp0FortnitePorting\bin\%CONFIG%\net10.0\%%F" del /f /q "%~dp0FortnitePorting\bin\%CONFIG%\net10.0\%%F" >nul 2>&1
)

echo.
echo  fnexportAPI
echo  Config  : %CONFIG%
echo  Swagger : http://localhost:%PORT%/swagger
echo  (Ctrl+C to stop. The first start downloads native libraries and the manifest.)
echo.

dotnet run --project "%~dp0FortnitePorting\FortnitePorting.csproj" -c "%CONFIG%"
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" (
    echo.
    echo The API exited with code %RC%.
    pause
)
exit /b %RC%

:no_sdk
echo ERROR: .NET 10 SDK was not found.
echo Install it from https://dotnet.microsoft.com/download/dotnet/10.0 and run this again.
pause
exit /b 1
