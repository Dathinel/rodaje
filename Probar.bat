@echo off
rem Prepara .entorno\ la primera vez y corre las pruebas del nucleo.
rem   Probar.bat                    todas las pruebas
rem   Probar.bat cobertura          pruebas + cobertura de Rodaje.Core
rem   Probar.bat aot                publica con Native AOT y ejecuta el .exe
rem   Probar.bat aceptacion         criterios de la fase 2 con video y audio reales (varios minutos)
rem   Probar.bat preparar-entorno   vuelve a descargar el SDK y FFmpeg
rem   Probar.bat -Filtro Recorte    otros argumentos pasan tal cual a probar.ps1
rem Variables: RODAJE_ENTORNO (otro entorno), RODAJE_SIN_PAUSA=1 (sin pausa final).
setlocal EnableExtensions
chcp 65001 >nul
title Rodaje - pruebas del nucleo

set "RAIZ=%~dp0"
if "%RAIZ:~-1%"=="\" set "RAIZ=%RAIZ:~0,-1%"
set "ENTORNO=%RAIZ%\.entorno"
if defined RODAJE_ENTORNO set "ENTORNO=%RODAJE_ENTORNO%"
set "DOTNET=%ENTORNO%\dotnet\dotnet.exe"
set "PROBAR=%RAIZ%\build\probar.ps1"
set "PREPARAR=%RAIZ%\build\preparar-entorno.ps1"
set "AOT=%RAIZ%\build\publicar-aot-prueba.ps1"
set "ACEPTACION=%RAIZ%\build\aceptacion.ps1"
set "SCRIPT=%PROBAR%"

echo.
echo  ==============================================================================
echo   Rodaje - editor de video nativo para Windows
echo  ==============================================================================
echo.
echo   Sin argumentos corre todas las pruebas: la línea de tiempo con tiempo exacto,
echo   las operaciones de edición con deshacer/rehacer, el formato de proyecto y la
echo   capa de medios (decodificación, GPU y audio).
echo.
echo     cobertura    pruebas + cobertura de Rodaje.Core
echo     aot          publica con Native AOT y ejecuta el .exe
echo     aceptacion   criterios de la fase 2 con video y audio reales
echo.

set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%PS%" set "PS=powershell.exe"
"%PS%" -NoProfile -ExecutionPolicy Bypass -Command "exit 0" >nul 2>nul
if errorlevel 1 goto sin_powershell

set "ACCION=%~1"
set "EXTRA="
if /i "%ACCION%"=="preparar-entorno" goto preparar_forzado
if /i "%ACCION%"=="cobertura" set "EXTRA=-Cobertura"
if /i "%ACCION%"=="cobertura" goto resto
if /i "%ACCION%"=="pruebas" goto resto
if /i "%ACCION%"=="aot" set "SCRIPT=%AOT%"
if /i "%ACCION%"=="aot" goto resto
if /i "%ACCION%"=="aceptacion" set "SCRIPT=%ACEPTACION%"
if /i "%ACCION%"=="aceptacion" goto resto
set ARGS=%*
goto entorno

:resto
rem Quita la accion y junta los demas argumentos.
set "ARGS="
shift
:juntar
if "%~1"=="" goto entorno
set ARGS=%ARGS% %1
shift
goto juntar

rem Listo = existen las marcas .sdk-<version> (de global.json) y .ffmpeg-<version> (de
rem preparar-entorno.ps1). dotnet.exe no basta: un apagon a mitad de la descompresion tambien lo deja.
:entorno
set "SDKVER="
for /f "usebackq tokens=2 delims=:, " %%v in (`findstr /i /c:"\"version\"" "%RAIZ%\global.json"`) do if not defined SDKVER set "SDKVER=%%~v"
set "FFVER="
for /f "usebackq tokens=2 delims='" %%v in (`findstr /b /c:"$FfmpegVersion = " "%PREPARAR%"`) do if not defined FFVER set "FFVER=%%v"
set "MARCA=%ENTORNO%\dotnet\.sdk-%SDKVER%"
set "MARCAFF=%ENTORNO%\ffmpeg\.ffmpeg-%FFVER%"
if exist "%MARCA%" if exist "%DOTNET%" if exist "%MARCAFF%" goto probar
if exist "%MARCA%" if exist "%DOTNET%" goto falta_ffmpeg
if exist "%DOTNET%" goto entorno_incompleto
echo   Primera vez: falta el entorno del proyecto (el SDK de .NET, unos 280 MB, y las
echo   bibliotecas de FFmpeg, unos 75 MB). Se va a descargar dentro de la carpeta
echo   .entorno\, solo esta vez; no se instala nada en Windows.
echo   Según la conexión puede tardar varios minutos. No cierres esta ventana.
echo.
goto preparar

:falta_ffmpeg
echo   Faltan las bibliotecas de FFmpeg del proyecto (o quedaron a medias). Se van a
echo   descargar dentro de .entorno\ffmpeg (unos 75 MB, solo esta vez). No cierres esta ventana.
echo.
goto preparar

:entorno_incompleto
echo   El SDK de .NET del proyecto quedó a medias (seguramente una instalación que se
echo   cortó, por ejemplo por un apagón). Se va a reinstalar dentro de .entorno\.
echo   Si hace falta descargarlo otra vez son unos 280 MB. No cierres esta ventana.
echo.

:preparar
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%PREPARAR%"
if errorlevel 1 goto fallo_entorno
if not exist "%MARCA%" goto fallo_entorno
if not exist "%DOTNET%" goto fallo_entorno
if not exist "%MARCAFF%" goto fallo_entorno

:probar
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %EXTRA% %ARGS%
if errorlevel 1 goto fallo_pruebas
goto fin_bien

:preparar_forzado
echo   Se va a borrar y volver a descargar el SDK de .NET y FFmpeg del proyecto (unos 355 MB).
echo.
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%PREPARAR%" -Forzar
if errorlevel 1 goto fallo_entorno
echo.
echo   Entorno listo. Haz doble clic en Probar.bat para correr las pruebas.
goto fin_bien

:sin_powershell
echo   No pude usar Windows PowerShell, y Rodaje lo necesita para preparar el
echo   entorno y correr las pruebas.
echo.
echo   Puede que esté deshabilitado o bloqueado por una directiva del sistema (en
echo   AtlasOS a veces se recorta). Para comprobarlo, abre "cmd" y escribe:
echo     powershell -NoProfile -Command "$PSVersionTable.PSVersion"
echo   Si eso da error, hay que reactivar Windows PowerShell 5.1 en el sistema.
goto fin_mal

:fallo_entorno
echo.
echo   No se pudo preparar el entorno (el SDK de .NET o FFmpeg del proyecto).
echo   Arriba está el motivo. Lo más común es no tener internet en la primera vez;
echo   cuando haya conexión, vuelve a hacer doble clic en Probar.bat.
goto fin_mal

:fallo_pruebas
echo   Algo no salió bien: arriba está la explicación (qué falló y dónde).
echo   Los registros completos quedaron en la carpeta TestResults\ del proyecto.
goto fin_mal

:fin_bien
if not "%RODAJE_SIN_PAUSA%"=="1" pause
exit /b 0

:fin_mal
echo.
if not "%RODAJE_SIN_PAUSA%"=="1" pause
exit /b 1
