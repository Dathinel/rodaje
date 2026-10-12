# Publica Rodaje (src\Rodaje.App: WinUI 3 sin empaquetar, autocontenido, Native AOT) y lo abre. En dist\Rodaje\ queda
# solo Rodaje.exe (el lanzador, src\Rodaje.Launcher) y la carpeta app\ con el programa de verdad, que el lanzador oculta.
# Uso:
#   powershell -NoProfile -ExecutionPolicy Bypass -File herramientas\app.ps1 [video.mp4 [otro.mp4 ...] | proyecto.rodaje]
#   ... -Medir 10        abre el clip de prueba (o los archivos), reproduce 10 s, mide y muestra las cifras
#   ... -Medir 10 -Escenario efectos   antes de medir arma 3 capas con efectos y 4 transiciones, y mueve un deslizador
#   ... -Medir 10 -Escenario velocidad 3 capas del clip (1x, 2x y 0,5x) y cambia la velocidad de una mientras reproduce;
#                                      mira cuadros perdidos, tarde, gpu.composicion y edicion.deslizador_a_present (<=20 ms)
#   ... -Medir 10 -Escenario capas     las mismas 3 capas, todo a 1× (comparar la reproducción sin velocidad)
#   ... -Medir 10 -Escenario rapido    el clip a 2× en la principal (alterna 2× y 3× al editar)
#   ... -SinProxies      reproduce siempre los originales (peor caso), sin cambiar los ajustes
#   ... -SinPublicar     abre el exe ya publicado sin recompilar
#   ... -Publicar        recompila aunque src\ no haya cambiado
#   ... -SoloPublicar    recompila si hace falta y no abre la app (para dejarla lista de antemano)
#   ... -Autoprueba      abre el exe publicado con --autoprueba (clip de prueba, todas las pestañas del inspector, un
#                        segundo de reproducción) y falla si no sale con 0 o si deja rodaje-error.txt
# Los videos se importan y se ponen seguidos en la pista principal; un .rodaje se abre.
# Solo recompila si el exe falta o algún fuente de src\ es más nuevo que él. Sale con 0 si todo fue bien.
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Archivos = @(),
    [double]$Medir = 0,
    [ValidateSet('', 'efectos', 'velocidad', 'capas', 'rapido')]
    [string]$Escenario = '',
    [switch]$SinProxies,
    [switch]$SinPublicar,
    [switch]$Publicar,
    [switch]$SoloPublicar,
    [switch]$Autoprueba
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$Raiz = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'comun.psm1') -DisableNameChecking -Force
$E = Inicializar-Rodaje $Raiz
$Proyecto = Join-Path $Raiz 'src\Rodaje.App\Rodaje.App.csproj'
$Salida = Join-Path $Raiz 'dist\Rodaje'
$Carpeta = Join-Path $Salida 'app'
$Exe = Join-Path $Carpeta 'Rodaje.exe'
$Lanzador = Join-Path $Salida 'Rodaje.exe'
$FfmpegBin = $E.FfmpegBin
$Bibliotecas = @('avutil-61', 'swresample-7', 'swscale-10', 'avcodec-63', 'avformat-63')
# ffmpeg.exe (proxies) importa además estas dos; FfmpegExecutable.ExtraLibraries las exige.
$BibliotecasDelExe = @('avfilter-12', 'avdevice-63')

function Publicar {
    Titulo 'Compilando Rodaje a código nativo (Native AOT, puede tardar unos minutos)...'
    if (-not (Test-Path -LiteralPath $E.Dotnet) -or -not (Test-Path -LiteralPath $E.MarcaSdk)) {
        Mal 'No encuentro el SDK de .NET del proyecto (o quedó a medias).'
        Nota "Lo busqué en: $($E.Dotnet)"
        Nota 'Haz doble clic en ABRIR.bat, que lo prepara solo, o corre herramientas\preparar-entorno.ps1.'
        return $false
    }

    Usar-DotnetLocal $E
    Agregar-Vswhere | Out-Null
    $resultados = Join-Path $Raiz 'TestResults'
    New-Item -ItemType Directory -Force -Path $resultados | Out-Null
    $log = Join-Path $resultados 'app-publicar.log'
    if (Test-Path -LiteralPath $Salida) { Remove-Item -LiteralPath $Salida -Recurse -Force }

    # Sin -r: el RID ya está en el .csproj; como propiedad global cambiaría los packages.lock.json de las bibliotecas.
    $r = Invocar-Dotnet $log @('publish', $Proyecto, '-c', 'Release', '-o', $Carpeta, '--nologo')
    $tmpLanzador = Join-Path $resultados 'lanzador'
    $l = Invocar-Dotnet (Join-Path $resultados 'app-lanzador.log') @('publish', (Join-Path $Raiz 'src\Rodaje.Launcher\Rodaje.Launcher.csproj'), '-c', 'Release', '-o', $tmpLanzador, '--nologo')
    $lineas = New-Object System.Collections.Generic.List[string]
    $lineas.AddRange([string[]]@($r.Lineas)); $lineas.AddRange([string[]]@($l.Lineas))
    $errores = Diagnosticos-De $lineas 'error'
    $advertencias = Diagnosticos-De $lineas 'warning'
    $avisos = @($errores) + @($advertencias)
    if ($r.Codigo -ne 0 -or $l.Codigo -ne 0 -or -not (Test-Path -LiteralPath $Exe)) {
        Mal 'No se pudo publicar Rodaje.'
        $texto = $lineas -join "`n"
        if ($texto -match '(?i)link\.exe|vswhere|Platform linker|desktop development with C\+\+|Desarrollo para el escritorio') {
            Nota 'Native AOT necesita el enlazador de C++ de Microsoft. Instala Visual Studio Build Tools 2022 con'
            Nota '"Desarrollo para el escritorio con C++":'
            Nota '  winget install Microsoft.VisualStudio.2022.BuildTools --override "--add Microsoft.VisualStudio.Workload.VCTools --includeRecommended --passive"'
        }
        @($avisos | Select-Object -First 15) | ForEach-Object { Nota ('  ' + (Corto $_)) }
        Nota "Registro completo: $(Corto $log)"
        return $false
    }
    if ($avisos.Count -gt 0) {
        Mal "La publicación dejó $($avisos.Count) advertencia(s), y el proyecto exige cero:"
        $avisos | Select-Object -First 15 | ForEach-Object { Nota ('  ' + (Corto $_)) }
        return $false
    }

    # Distribución: las DLL LGPL de FFmpeg van en ffmpeg\ junto al exe (FfmpegLocator las busca ahí primero),
    # con el ffmpeg.exe de la misma compilación y las dos DLL que solo él usa, para generar proxies.
    $ffmpegExe = Join-Path $FfmpegBin 'ffmpeg.exe'
    if (-not (Test-Path -LiteralPath $ffmpegExe)) {
        Mal "Falta $ffmpegExe. Corre ABRIR.bat preparar-entorno para descargar FFmpeg."
        return $false
    }
    $configuracion = (& $ffmpegExe -hide_banner -buildconf 2>$null) -join ' '
    if ($LASTEXITCODE -ne 0 -or $configuracion -notmatch '--enable-version3' -or $configuracion -match '--enable-(gpl|nonfree)\b') {
        Mal "El ffmpeg.exe de $(Corto $FfmpegBin) no es la compilación LGPL esperada; no se copia a dist."
        return $false
    }
    Copy-Item -LiteralPath (Join-Path $tmpLanzador 'Rodaje.exe') -Destination $Lanzador -Force
    $destino = Join-Path $Carpeta 'ffmpeg'
    New-Item -ItemType Directory -Force -Path $destino | Out-Null
    foreach ($b in $Bibliotecas + $BibliotecasDelExe) {
        $dll = Join-Path $FfmpegBin "$b.dll"
        if (-not (Test-Path -LiteralPath $dll)) {
            Mal "Falta $dll. Corre ABRIR.bat preparar-entorno para descargar FFmpeg."
            return $false
        }
        Copy-Item -LiteralPath $dll -Destination $destino -Force
    }
    Copy-Item -LiteralPath $ffmpegExe -Destination $destino -Force
    # ffprobe.exe de la misma compilación: la exportación comprueba con él cada archivo terminado (ExportVerifier).
    $ffprobeExe = Join-Path $FfmpegBin 'ffprobe.exe'
    if (Test-Path -LiteralPath $ffprobeExe) { Copy-Item -LiteralPath $ffprobeExe -Destination $destino -Force }
    $licenciaFfmpeg = Join-Path $E.Entorno 'ffmpeg\LICENSE.txt'
    if (Test-Path -LiteralPath $licenciaFfmpeg) { Copy-Item -LiteralPath $licenciaFfmpeg -Destination (Join-Path $destino 'LICENSE.txt') -Force }
    Copy-Item -LiteralPath (Join-Path $Raiz 'THIRD-PARTY-NOTICES.txt') -Destination $Carpeta -Force
    Copy-Item -LiteralPath (Join-Path $Raiz 'LICENSE') -Destination (Join-Path $Carpeta 'LICENSE.txt') -Force
    Copy-Item -LiteralPath (Join-Path $Raiz 'TRADEMARK.md') -Destination $Carpeta -Force
    Get-ChildItem -LiteralPath $Salida -Filter '*.pdb' -Recurse | Remove-Item -Force
    # La documentación XML de las bibliotecas solo sirve al compilar.
    Get-ChildItem -LiteralPath $Carpeta -Filter 'Rodaje.*.xml' | Remove-Item -Force

    $tamExe = (Get-Item -LiteralPath $Exe).Length
    $archivos = Get-ChildItem -LiteralPath $Salida -File -Recurse
    $tamCarpeta = ($archivos | Measure-Object -Property Length -Sum).Sum
    $tamFfmpeg = ($archivos | Where-Object { $_.FullName -like (Join-Path $destino '*') } | Measure-Object -Property Length -Sum).Sum
    Bien 'Publicado sin advertencias (ni de recorte ni de AOT).'
    Nota ("Ejecutable: {0} ({1:0.0} MB), abierto por {2} ({3:0.0} MB)" -f (Corto $Exe), ($tamExe / 1MB), (Corto $Lanzador), ((Get-Item -LiteralPath $Lanzador).Length / 1MB))
    Nota ("Carpeta completa: {0:0.0} MB en {1} archivos (FFmpeg {2:0.0} MB). Se puede copiar a otro PC tal cual." -f ($tamCarpeta / 1MB), $archivos.Count, ($tamFfmpeg / 1MB))
    return $true
}

Titulo 'Rodaje'
$hayCambios = $Publicar -or -not (Test-Path -LiteralPath $Exe) -or -not (Test-Path -LiteralPath $Lanzador)
if (-not $hayCambios) {
    $fechaExe = (Get-Item -LiteralPath $Exe).LastWriteTimeUtc
    $hayCambios = [bool](Get-ChildItem -LiteralPath (Join-Path $Raiz 'src') -Recurse -File -Include *.cs, *.csproj, *.xaml, *.hlsl, *.manifest |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.LastWriteTimeUtc -gt $fechaExe } | Select-Object -First 1)
}
if (($hayCambios -and -not $SinPublicar) -or -not (Test-Path -LiteralPath $Exe)) {
    if (-not (Publicar)) { Write-Host ''; exit 1 }
}
if ($SoloPublicar) { Bien 'Rodaje está compilado y listo para abrir.'; exit 0 }

$argumentos = @()
foreach ($a in @($Archivos | Where-Object { $_ })) {
    if (-not (Test-Path -LiteralPath $a -PathType Leaf)) {
        Mal "No existe el archivo: $a"
        exit 1
    }
    $argumentos += '"' + (Resolve-Path -LiteralPath $a).Path + '"'
}
# Sin DOTNET_ROOT: un exe de Native AOT no debe necesitar nada de .NET para arrancar.
Remove-Item Env:DOTNET_ROOT -ErrorAction SilentlyContinue

if ($Autoprueba) {
    $errorTxt = Join-Path $Carpeta 'rodaje-error.txt'
    Remove-Item -LiteralPath $errorTxt -ErrorAction SilentlyContinue
    # El módulo eco con AOT (no necesita el runtime de .NET) para el paso de IA: Rodaje lo encuentra porque corre desde el repositorio.
    Usar-DotnetLocal $E
    Agregar-Vswhere | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $Raiz 'TestResults') | Out-Null
    $eco = Invocar-Dotnet (Join-Path $Raiz 'TestResults\app-modulo-eco.log') @('publish', (Join-Path $Raiz 'modules\Rodaje.Module.Echo\Rodaje.Module.Echo.csproj'), '-c', 'Release', '-r', 'win-x64', '-o', (Join-Path $Raiz 'modules\Rodaje.Module.Echo\bin\aot'), '--nologo', '-v', 'q')
    if ($eco.Codigo -ne 0) { Nota 'No se pudo compilar el módulo eco: la autoprueba sigue sin el paso de IA.' }
    Remove-Item Env:DOTNET_ROOT -ErrorAction SilentlyContinue
    Titulo 'Autoprueba del exe publicado (la ventana se abre y se cierra sola)...'
    $reloj = [System.Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath $Exe -ArgumentList (@('--autoprueba') + $argumentos) -PassThru
    if (-not $p.WaitForExit(90000)) { $p.Kill(); Mal 'Rodaje no terminó la autoprueba en 90 s.'; exit 1 }
    $codigo = $p.ExitCode
    if (Test-Path -LiteralPath $errorTxt) {
        Mal "Rodaje dejó $(Corto $errorTxt):"
        Get-Content -LiteralPath $errorTxt -Encoding UTF8 | Select-Object -First 15 | ForEach-Object { Nota ('  ' + $_) }
        exit 1
    }
    if ($codigo -ne 0) { Mal "La autoprueba terminó con código $codigo."; exit 1 }
    Bien ("El exe de Native AOT arrancó, abrió todas las pestañas del inspector, reprodujo y cerró en {0:0.0} s." -f $reloj.Elapsed.TotalSeconds)
    exit 0
}

if ($Medir -gt 0) {
    $registro = Join-Path $Raiz ('TestResults\app-medir-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.txt')
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $registro) | Out-Null
    $argumentos += @('--medir', $Medir.ToString([System.Globalization.CultureInfo]::InvariantCulture), '--registro', ('"' + $registro + '"'))
    if ($Escenario) { $argumentos += @('--escenario', $Escenario) }
    if ($SinProxies) { $argumentos += '--sin-proxies' }
    Titulo "Midiendo $Medir s de reproducción (la ventana se abre y se cierra sola)..."
    $p = Start-Process -FilePath $Exe -ArgumentList $argumentos -PassThru
    if (-not $p.WaitForExit([int](($Medir + 90) * 1000))) { $p.Kill(); Mal 'Rodaje no terminó a tiempo.'; exit 1 }
    if (Test-Path -LiteralPath $registro) {
        Get-Content -LiteralPath $registro -Encoding UTF8 | ForEach-Object { Nota $_ }
        Nota ''
        Nota "Registro: $(Corto $registro)"
    } else {
        Mal 'Rodaje no dejó registro; mira dist\Rodaje\app\rodaje-error.txt.'
    }
    if ($p.ExitCode -eq 0) { Bien 'La medida terminó bien.' } else { Mal "La medida terminó con código $($p.ExitCode)." }
    exit $p.ExitCode
}

if ($argumentos.Count -gt 0) { Start-Process -FilePath $Lanzador -ArgumentList $argumentos } else { Start-Process -FilePath $Lanzador }
Bien 'Rodaje abierto. Importa videos (Ctrl+I o arrastrándolos) y haz doble clic en uno para ponerlo en la pista principal.'
exit 0
