@echo off
rem ---------------------------------------------------------------------------------
rem  FotoToma: doble clic aqui para probar lo que ya existe.
rem
rem  Fase 1 todavia no tiene ventana: lo que hay es el nucleo del editor (la linea de
rem  tiempo y sus operaciones de edicion) con sus pruebas automaticas. Este archivo
rem  prepara el entorno propio la primera vez (el SDK de .NET dentro de .entorno\) y
rem  corre las pruebas con herramientas\probar.ps1, que explica el resultado.
rem
rem  Uso:
rem    ABRIR.bat                    corre todas las pruebas
rem    ABRIR.bat cobertura          pruebas + cobertura de FotoToma.Core
rem    ABRIR.bat aot                compila el nucleo con Native AOT y prueba el .exe nativo
rem    ABRIR.bat preparar-entorno   vuelve a descargar el SDK desde cero
rem    ABRIR.bat -Filtro Recorte    cualquier otro argumento pasa tal cual a probar.ps1
rem
rem  Variables opcionales:
rem    FOTOTOMA_ENTORNO=carpeta     usa otro entorno (por ejemplo el de la copia principal
rem                                 desde un worktree de git)
rem    FOTOTOMA_SIN_PAUSA=1         no espera una tecla al final (para lanzadores y scripts)
rem ---------------------------------------------------------------------------------
setlocal EnableExtensions
chcp 65001 >nul
title FotoToma - pruebas del nucleo

set "RAIZ=%~dp0"
if "%RAIZ:~-1%"=="\" set "RAIZ=%RAIZ:~0,-1%"
set "ENTORNO=%RAIZ%\.entorno"
if defined FOTOTOMA_ENTORNO set "ENTORNO=%FOTOTOMA_ENTORNO%"
set "DOTNET=%ENTORNO%\dotnet\dotnet.exe"
set "PROBAR=%RAIZ%\herramientas\probar.ps1"
set "PREPARAR=%RAIZ%\herramientas\preparar-entorno.ps1"
set "AOT=%RAIZ%\herramientas\publicar-aot-prueba.ps1"
set "SCRIPT=%PROBAR%"

echo.
echo  ==============================================================================
echo   FotoToma - editor de video para Windows (fase 1: el núcleo, sin ventana aún)
echo  ==============================================================================
echo.
echo   Lo que se puede probar ahora es el corazón del editor: la línea de tiempo con
echo   tiempo exacto (sin errores de redondeo entre fotogramas), las operaciones de
echo   edición (cortar, recortar, ripple, roll, slip, slide, mover, insertar,
echo   sobrescribir, levantar y borrar con ripple), deshacer/rehacer y el formato de
echo   proyecto en JSON. Todo eso se comprueba con pruebas automáticas.
echo.

rem --- PowerShell: lo usan el script de pruebas y el que prepara el entorno -----------
set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%PS%" set "PS=powershell.exe"
"%PS%" -NoProfile -ExecutionPolicy Bypass -Command "exit 0" >nul 2>nul
if errorlevel 1 goto sin_powershell

rem --- Que accion se pidio ----------------------------------------------------------
set "ACCION=%~1"
set "EXTRA="
if /i "%ACCION%"=="preparar-entorno" goto preparar_forzado
if /i "%ACCION%"=="cobertura" set "EXTRA=-Cobertura"
if /i "%ACCION%"=="cobertura" goto resto
if /i "%ACCION%"=="pruebas" goto resto
if /i "%ACCION%"=="aot" set "SCRIPT=%AOT%"
if /i "%ACCION%"=="aot" goto resto
rem Ninguna accion conocida: todos los argumentos pasan tal cual a probar.ps1.
set ARGS=%*
goto entorno

:resto
rem Se quita la primera palabra (la accion) y se juntan las demas.
set "ARGS="
shift
:juntar
if "%~1"=="" goto entorno
set ARGS=%ARGS% %1
shift
goto juntar

rem --- Entorno propio: el SDK de .NET dentro de .entorno\ ----------------------------
:entorno
if exist "%DOTNET%" goto probar
echo   Primera vez: falta el SDK de .NET del proyecto. Se va a descargar dentro de
echo   la carpeta .entorno\ (unos 280 MB, solo esta vez; no se instala nada en Windows).
echo   Según la conexión puede tardar varios minutos. No cierres esta ventana.
echo.
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%PREPARAR%"
if errorlevel 1 goto fallo_entorno
if not exist "%DOTNET%" goto fallo_entorno

:probar
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %EXTRA% %ARGS%
if errorlevel 1 goto fallo_pruebas
goto fin_bien

:preparar_forzado
echo   Se va a borrar y volver a descargar el SDK de .NET del proyecto (unos 280 MB).
echo.
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%PREPARAR%" -Forzar
if errorlevel 1 goto fallo_entorno
echo.
echo   Entorno listo. Haz doble clic en ABRIR.bat para correr las pruebas.
goto fin_bien

rem --- Mensajes de error en lenguaje sencillo ------------------------------------------
:sin_powershell
echo   No pude usar Windows PowerShell, y FotoToma lo necesita para preparar el
echo   entorno y correr las pruebas.
echo.
echo   Puede que esté deshabilitado o bloqueado por una directiva del sistema (en
echo   AtlasOS a veces se recorta). Para comprobarlo, abre "cmd" y escribe:
echo     powershell -NoProfile -Command "$PSVersionTable.PSVersion"
echo   Si eso da error, hay que reactivar Windows PowerShell 5.1 en el sistema.
goto fin_mal

:fallo_entorno
echo.
echo   No se pudo preparar el entorno (el SDK de .NET del proyecto).
echo   Arriba está el motivo. Lo más común es no tener internet en la primera vez;
echo   cuando haya conexión, vuelve a hacer doble clic en ABRIR.bat.
goto fin_mal

:fallo_pruebas
echo   Algo no salió bien: arriba está la explicación (qué falló y dónde).
echo   Los registros completos quedaron en la carpeta TestResults\ del proyecto.
goto fin_mal

:fin_bien
if not "%FOTOTOMA_SIN_PAUSA%"=="1" pause
exit /b 0

:fin_mal
echo.
if not "%FOTOTOMA_SIN_PAUSA%"=="1" pause
exit /b 1
