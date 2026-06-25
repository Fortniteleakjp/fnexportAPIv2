@echo off
REM ============================================================================
REM Full local build (Windows):
REM   1. Build the native RAD Audio decoder (libs\rada_decode.dll)
REM   2. Publish FortnitePorting as a single self-contained win-x64 .exe
REM   3. Copy rada_decode.dll next to the published .exe
REM
REM Output: FortnitePorting\bin\Release\net9.0\win-x64\publish\
REM ============================================================================
setlocal
set "ROOT=%~dp0"

echo === [1/3] Building native rada_decode.dll ===
call "%ROOT%RADADecoder\shim\build.bat" "%ROOT%libs"
if errorlevel 1 (
    echo Native build failed.
    exit /b 1
)

echo.
echo === [2/3] Publishing single-file win-x64 executable ===
dotnet publish "%ROOT%FortnitePorting\FortnitePorting.csproj" -c Release -r win-x64
if errorlevel 1 (
    echo dotnet publish failed.
    exit /b 1
)

echo.
echo === [3/3] Bundling native libraries next to the executable ===
set "PUBDIR=%ROOT%FortnitePorting\bin\Release\net9.0\win-x64\publish"
copy /y "%ROOT%libs\rada_decode.dll" "%PUBDIR%\" >nul
if exist "%ROOT%libs\oo2core_9_win64.dll" copy /y "%ROOT%libs\oo2core_9_win64.dll" "%PUBDIR%\" >nul
if exist "%ROOT%libs\zlib-ng2.dll" copy /y "%ROOT%libs\zlib-ng2.dll" "%PUBDIR%\" >nul

echo.
echo Build complete.
echo   EXE : %PUBDIR%\FortnitePorting.exe
echo   DLL : %PUBDIR%\rada_decode.dll
echo Note: oo2core_9_win64.dll and zlib-ng2.dll are required at runtime; place them
echo       next to the .exe (they are downloaded/obtained separately, not in this repo).
endlocal
