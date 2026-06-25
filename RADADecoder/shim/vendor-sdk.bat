@echo off
REM ============================================================================
REM Copies the minimal RAD Audio SDK subset from a local Unreal Engine install
REM into RADADecoder/shim/sdk so the native shim can be built (locally and in CI).
REM Usage: vendor-sdk.bat ["C:\Program Files\Epic Games\UE_5.6"]
REM ============================================================================
setlocal
set "SHIMDIR=%~dp0"
set "UE=%~1"

if "%UE%"=="" (
    for /d %%U in ("C:\Program Files\Epic Games\UE_5.*") do set "UE=%%U"
)
if "%UE%"=="" (
    echo Could not find an Unreal Engine install. Pass the UE root as the first argument.
    exit /b 1
)

set "SDK=%UE%\Engine\Source\Runtime\RadAudioCodec\SDK"
if not exist "%SDK%\Include\rada_decode.h" (
    echo RAD Audio SDK not found under "%SDK%".
    exit /b 1
)

echo Vendoring RAD Audio SDK from "%SDK%" ...
if not exist "%SHIMDIR%sdk\Include" mkdir "%SHIMDIR%sdk\Include"
if not exist "%SHIMDIR%sdk\Lib" mkdir "%SHIMDIR%sdk\Lib"

copy /y "%SDK%\Include\rada_decode.h"        "%SHIMDIR%sdk\Include\" >nul
copy /y "%SDK%\Include\rada_file_header.h"   "%SHIMDIR%sdk\Include\" >nul
copy /y "%SDK%\Include\rada_encode.h"        "%SHIMDIR%sdk\Include\" >nul
copy /y "%SDK%\Lib\radaudio_decoder_win64.lib"     "%SHIMDIR%sdk\Lib\" >nul
copy /y "%SDK%\Lib\libradaudio_decoder_linux64.a"  "%SHIMDIR%sdk\Lib\" >nul

echo Done. Vendored to "%SHIMDIR%sdk".
endlocal
