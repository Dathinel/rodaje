@echo off
rem Abre Rodaje. Doble clic: elige uno o varios videos. Arrastrar videos sobre este archivo: los abre todos,
rem el primero de fondo y los demas como imagen en imagen. Tambien abre un proyecto .rodaje.
chcp 65001 >nul
setlocal
set "RAIZ=%~dp0"
if "%~1"=="" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%RAIZ%herramientas\visor.ps1"
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%RAIZ%herramientas\visor.ps1" %*
)
if errorlevel 1 pause
