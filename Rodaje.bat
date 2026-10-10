@echo off
rem Abre Rodaje. Doble clic: elige un video. Arrastrar un video sobre este archivo: lo abre directamente.
chcp 65001 >nul
setlocal
set "RAIZ=%~dp0"
if "%~1"=="" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%RAIZ%herramientas\visor.ps1"
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%RAIZ%herramientas\visor.ps1" -Archivo "%~1"
)
if errorlevel 1 pause
