@echo off
rem Ejecuta el SDK de .NET del entorno propio (.entorno\dotnet) con la caché NuGet local.
rem Uso: herramientas\dotnet-local.cmd test   (cualquier argumento de dotnet)
setlocal
set "RAIZ=%~dp0.."
set "DOTNET_ROOT=%RAIZ%\.entorno\dotnet"
set "NUGET_PACKAGES=%RAIZ%\.entorno\nuget"
set "DOTNET_CLI_HOME=%RAIZ%\.entorno"
set "DOTNET_MULTILEVEL_LOOKUP=0"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "PATH=%DOTNET_ROOT%;%PATH%"
if not exist "%DOTNET_ROOT%\dotnet.exe" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0preparar-entorno.ps1" || exit /b 1
)
"%DOTNET_ROOT%\dotnet.exe" %*
exit /b %errorlevel%
