# Publica Rodaje (src\Rodaje.App: WinUI 3 sin empaquetar, autocontenido, Native AOT) en dist\Rodaje\ y lo abre.
# Uso:
#   powershell -NoProfile -ExecutionPolicy Bypass -File herramientas\app.ps1 [video.mp4 [otro.mp4 ...] | proyecto.rodaje]
#   ... -Medir 10        abre el clip de prueba (o los archivos), reproduce 10 s, mide y muestra las cifras
#   ... -Medir 10 -Escenario efectos   antes de medir arma 3 capas con efectos y 4 transiciones, y mueve un deslizador
#   ... -SinPublicar     abre el exe ya publicado sin recompilar
#   ... -Publicar        recompila aunque src\ no haya cambiado
#   ... -SoloPublicar    recompila si hace falta y no abre la app (para dejarla lista de antemano)
# Los videos se importan y se ponen seguidos en la pista principal; un .rodaje se abre.
# Solo recompila si el exe falta o algún fuente de src\ es más nuevo que él. Sale con 0 si todo fue bien.
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Archivos = @(),
    [double]$Medir = 0,
    [ValidateSet('', 'efectos')]
    [string]$Escenario = '',
    [switch]$SinPublicar,
    [switch]$Publicar,
    [switch]$SoloPublicar
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$Raiz = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'comun.psm1') -DisableNameChecking -Force
$E = Inicializar-Rodaje $Raiz
$Proyecto = Join-Path $Raiz 'src\Rodaje.App\Rodaje.App.csproj'
$Salida = Join-Path $Raiz 'dist\Rodaje'
$Exe = Join-Path $Salida 'Rodaje.exe'
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
    $r = Invocar-Dotnet $log @('publish', $Proyecto, '-c', 'Release', '-o', $Salida, '--nologo')
    $errores = Diagnosticos-De $r.Lineas 'error'
    $advertencias = Diagnosticos-De $r.Lineas 'warning'
    $avisos = @($errores) + @($advertencias)
    if ($r.Codigo -ne 0 -or -not (Test-Path -LiteralPath $Exe)) {
        Mal 'No se pudo publicar Rodaje.'
        $texto = $r.Lineas -join "`n"
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
    $destino = Join-Path $Salida 'ffmpeg'
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
    $licenciaFfmpeg = Join-Path $E.Entorno 'ffmpeg\LICENSE.txt'
    if (Test-Path -LiteralPath $licenciaFfmpeg) { Copy-Item -LiteralPath $licenciaFfmpeg -Destination (Join-Path $destino 'LICENSE.txt') -Force }
    Copy-Item -LiteralPath (Join-Path $Raiz 'THIRD-PARTY-NOTICES.txt') -Destination $Salida -Force
    Copy-Item -LiteralPath (Join-Path $Raiz 'LICENSE') -Destination (Join-Path $Salida 'LICENSE.txt') -Force
    Get-ChildItem -LiteralPath $Salida -Filter '*.pdb' -Recurse | Remove-Item -Force

    $tamExe = (Get-Item -LiteralPath $Exe).Length
    $archivos = Get-ChildItem -LiteralPath $Salida -File -Recurse
    $tamCarpeta = ($archivos | Measure-Object -Property Length -Sum).Sum
    $tamFfmpeg = ($archivos | Where-Object { $_.FullName -like (Join-Path $destino '*') } | Measure-Object -Property Length -Sum).Sum
    Bien 'Publicado sin advertencias (ni de recorte ni de AOT).'
    Nota ("Ejecutable: {0} ({1:0.0} MB)" -f (Corto $Exe), ($tamExe / 1MB))
    Nota ("Carpeta completa: {0:0.0} MB en {1} archivos (FFmpeg {2:0.0} MB). Se puede copiar a otro PC tal cual." -f ($tamCarpeta / 1MB), $archivos.Count, ($tamFfmpeg / 1MB))
    return $true
}

Titulo 'Rodaje'
$hayCambios = $Publicar -or -not (Test-Path -LiteralPath $Exe)
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

if ($Medir -gt 0) {
    $registro = Join-Path $Raiz ('TestResults\app-medir-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.txt')
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $registro) | Out-Null
    $argumentos += @('--medir', $Medir.ToString([System.Globalization.CultureInfo]::InvariantCulture), '--registro', ('"' + $registro + '"'))
    if ($Escenario) { $argumentos += @('--escenario', $Escenario) }
    Titulo "Midiendo $Medir s de reproducción (la ventana se abre y se cierra sola)..."
    $p = Start-Process -FilePath $Exe -ArgumentList $argumentos -PassThru
    if (-not $p.WaitForExit([int](($Medir + 90) * 1000))) { $p.Kill(); Mal 'Rodaje no terminó a tiempo.'; exit 1 }
    if (Test-Path -LiteralPath $registro) {
        Get-Content -LiteralPath $registro -Encoding UTF8 | ForEach-Object { Nota $_ }
        Nota ''
        Nota "Registro: $(Corto $registro)"
    } else {
        Mal 'Rodaje no dejó registro; mira dist\Rodaje\rodaje-error.txt.'
    }
    if ($p.ExitCode -eq 0) { Bien 'La medida terminó bien.' } else { Mal "La medida terminó con código $($p.ExitCode)." }
    exit $p.ExitCode
}

if ($argumentos.Count -gt 0) { Start-Process -FilePath $Exe -ArgumentList $argumentos } else { Start-Process -FilePath $Exe }
Bien 'Rodaje abierto. Importa videos (Ctrl+I o arrastrándolos) y haz doble clic en uno para ponerlo en la pista principal.'
exit 0
