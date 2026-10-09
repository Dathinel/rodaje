@echo off
rem Ejecuta el SDK de .NET del entorno propio (.entorno\dotnet) con la caché NuGet local.
setlocal
set "RAIZ=%~dp0.."
set "ENTORNO=%RAIZ%\.entorno"
if defined FOTOTOMA_ENTORNO set "ENTORNO=%FOTOTOMA_ENTORNO%"
set "DOTNET_ROOT=%ENTORNO%\dotnet"
set "NUGET_PACKAGES=%ENTORNO%\nuget"
set "DOTNET_CLI_HOME=%ENTORNO%"
set "DOTNET_CLI_FORCE_UTF8_ENCODING=1"
set "DOTNET_MULTILEVEL_LOOKUP=0"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "PATH=%DOTNET_ROOT%;%PATH%"
rem Listo = existen las marcas .sdk-<version> (de global.json) y .ffmpeg-<version> (de preparar-entorno.ps1);
rem dotnet.exe no basta tras un apagon.
set "SDKVER="
for /f "usebackq tokens=2 delims=:, " %%v in (`findstr /i /c:"\"version\"" "%RAIZ%\global.json"`) do if not defined SDKVER set "SDKVER=%%~v"
set "FFVER="
for /f "usebackq tokens=2 delims='" %%v in (`findstr /b /c:"$FfmpegVersion = " "%~dp0preparar-entorno.ps1"`) do if not defined FFVER set "FFVER=%%v"
if not exist "%DOTNET_ROOT%\.sdk-%SDKVER%" goto preparar
if not exist "%DOTNET_ROOT%\dotnet.exe" goto preparar
if not exist "%ENTORNO%\ffmpeg\.ffmpeg-%FFVER%" goto preparar
goto ejecutar
:preparar
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0preparar-entorno.ps1" || exit /b 1
:ejecutar
"%DOTNET_ROOT%\dotnet.exe" %*
exit /b %errorlevel%
