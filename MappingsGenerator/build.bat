@echo off
setlocal
if not "%~1"=="" set "OUTPUT=%~f1"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
if not defined VSDIR (
    echo Visual Studio with C++ tools not found.
    exit /b 1
)
set "PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;%PATH%"
call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
cd /d "%~dp0"
cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release || exit /b 1
cmake --build build || exit /b 1
if defined OUTPUT (
    if not exist "%OUTPUT%" mkdir "%OUTPUT%"
    if errorlevel 1 exit /b 1
    copy /y "%~dp0build\UEFNStaticMappingsGenerator.exe" "%OUTPUT%\UEFNStaticMappingsGenerator.exe" >nul
    if errorlevel 1 exit /b 1
)
echo.
echo Built: %~dp0build\UEFNStaticMappingsGenerator.exe
exit /b 0
