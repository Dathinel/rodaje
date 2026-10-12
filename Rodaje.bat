@echo off
rem Abre Rodaje. Doble clic: abre el editor vacio. Arrastrar videos sobre este archivo: los importa y los pone
rem seguidos en la pista principal. Tambien abre un proyecto .rodaje.
chcp 65001 >nul
setlocal
set "RAIZ=%~dp0"
if "%~1"=="" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%RAIZ%build\app.ps1"
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%RAIZ%build\app.ps1" %*
)
if errorlevel 1 pause
