# Publica el visor de la fase 3 (src\Rodaje.Visor) con Native AOT en dist\Rodaje-visor\ y lo abre.
# Uso:
#   powershell -NoProfile -ExecutionPolicy Bypass -File herramientas\visor.ps1 [video.mp4 [otro.mp4 ...] | proyecto.rodaje]
#   ... -Archivo video.mp4                un solo archivo (como en la fase 2)
#   ... a.mp4 b.mp4 -Prueba 20            recorrido automático de 20 s que imprime las estadísticas
#   ... -SinPublicar                      abre el exe ya publicado sin recompilar
# Los archivos sueltos (-Archivos) se juntan: el primero de fondo y los demás como imagen en imagen.
# Requiere link.exe (Build Tools con C++) para publicar. Sale con 0 si todo fue bien.
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$Archivo,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Archivos = @(),
    [double]$Prueba = 0,
    [switch]$SinPublicar
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$Raiz = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'comun.psm1') -DisableNameChecking -Force
$E = Inicializar-Rodaje $Raiz
$Proyecto = Join-Path $Raiz 'src\Rodaje.Visor\Rodaje.Visor.csproj'
$Salida = Join-Path $Raiz 'dist\Rodaje-visor'
$Exe = Join-Path $Salida 'Rodaje.Visor.exe'
$Entorno = $E.Entorno
$Dotnet = $E.Dotnet
$FfmpegBin = $E.FfmpegBin
$Bibliotecas = @('avutil-61', 'swresample-7', 'swscale-10', 'avcodec-63', 'avformat-63')
# ffmpeg.exe (proxies) importa además estas dos; FfmpegExecutable.ExtraLibraries las exige.
$BibliotecasDelExe = @('avfilter-12', 'avdevice-63')
$MarcaSdk = $E.MarcaSdk

function Publicar {
    Titulo 'Compilando el visor a código nativo (Native AOT, puede tardar un minuto)...'
    if (-not (Test-Path -LiteralPath $Dotnet) -or -not (Test-Path -LiteralPath $MarcaSdk)) {
        Mal 'No encuentro el SDK de .NET del proyecto (o quedó a medias).'
        Nota "Lo busqué en: $Dotnet"
        Nota 'Haz doble clic en ABRIR.bat, que lo prepara solo, o corre herramientas\preparar-entorno.ps1.'
        return $false
    }

    Usar-DotnetLocal $E
    Agregar-Vswhere | Out-Null

    $resultados = Join-Path $Raiz 'TestResults'
    New-Item -ItemType Directory -Force -Path $resultados | Out-Null
    $log = Join-Path $resultados 'visor-publicar.log'
    if (Test-Path -LiteralPath $Salida) { Remove-Item -LiteralPath $Salida -Recurse -Force }

    $lineas = New-Object System.Collections.Generic.List[string]
    $anterior = $ErrorActionPreference
    # En PowerShell 5.1 el stderr de un programa llega como ErrorRecord y 'Stop' cortaría el script.
    $ErrorActionPreference = 'Continue'
    try {
        # Sin -r: el RID ya está en el .csproj; como propiedad global cambiaría los packages.lock.json de las bibliotecas.
        & $Dotnet publish $Proyecto -c Release -o $Salida --nologo 2>&1 | ForEach-Object { $lineas.Add("$_") }
        $codigo = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $anterior
    }
    [System.IO.File]::WriteAllLines($log, $lineas, (New-Object System.Text.UTF8Encoding($false)))

    $avisos = @($lineas | Where-Object { $_ -match '(?i)\b(warning|advertencia|error)\s+[A-Z]{2,}\d+\s*:' } | Sort-Object -Unique)
    $deAot = @($avisos | Where-Object { $_ -match '(?i)\b(warning|advertencia|error)\s+IL\d+' })
    if ($codigo -ne 0 -or -not (Test-Path -LiteralPath $Exe)) {
        Mal 'No se pudo publicar el visor.'
        if ($deAot.Count -gt 0) { Nota "Hubo $($deAot.Count) aviso(s) de recorte o de AOT (código que Native AOT no puede garantizar)." }
        $texto = $lineas -join "`n"
        if ($texto -match '(?i)link\.exe|vswhere|Platform linker|desktop development with C\+\+|Desarrollo para el escritorio') {
            Nota 'Native AOT necesita el enlazador de C++ de Microsoft. Instala Visual Studio Build Tools 2022 con'
            Nota '"Desarrollo para el escritorio con C++":'
            Nota '  winget install Microsoft.VisualStudio.2022.BuildTools --override "--add Microsoft.VisualStudio.Workload.VCTools --includeRecommended --passive"'
        }
        @($avisos | Select-Object -First 10) | ForEach-Object { Nota ('  ' + (Corto $_)) }
        Nota "Registro completo: $(Corto $log)"
        return $false
    }
    if ($avisos.Count -gt 0) {
        Mal "La publicación dejó $($avisos.Count) advertencia(s), y el proyecto exige cero:"
        $avisos | Select-Object -First 10 | ForEach-Object { Nota ('  ' + (Corto $_)) }
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
    $licenciaFfmpeg = Join-Path $Entorno 'ffmpeg\LICENSE.txt'
    if (Test-Path -LiteralPath $licenciaFfmpeg) { Copy-Item -LiteralPath $licenciaFfmpeg -Destination (Join-Path $destino 'LICENSE.txt') -Force }
    Copy-Item -LiteralPath (Join-Path $Raiz 'THIRD-PARTY-NOTICES.txt') -Destination $Salida -Force
    Copy-Item -LiteralPath (Join-Path $Raiz 'LICENSE') -Destination (Join-Path $Salida 'LICENSE.txt') -Force
    Get-ChildItem -LiteralPath $Salida -Filter '*.pdb' | Remove-Item -Force

    $tamExe = (Get-Item -LiteralPath $Exe).Length
    $tamCarpeta = (Get-ChildItem -LiteralPath $Salida -File -Recurse | Measure-Object -Property Length -Sum).Sum
    Bien 'Publicado sin advertencias de recorte ni de AOT.'
    Nota ("Ejecutable: {0} ({1:0.00} MB)" -f (Corto $Exe), ($tamExe / 1MB))
    Nota ("Carpeta completa, con FFmpeg: {0:0.0} MB. Se puede copiar a otro PC tal cual." -f ($tamCarpeta / 1MB))
    return $true
}

Titulo 'Rodaje - visor de varias capas (fase 3)'
# Solo se recompila si el exe falta o algún fuente de src\ es más nuevo que él.
$hayCambios = -not (Test-Path -LiteralPath $Exe)
if (-not $hayCambios) {
    $fechaExe = (Get-Item -LiteralPath $Exe).LastWriteTimeUtc
    $hayCambios = [bool](Get-ChildItem -LiteralPath (Join-Path $Raiz 'src') -Recurse -File -Include *.cs,*.csproj,*.hlsl |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.LastWriteTimeUtc -gt $fechaExe } | Select-Object -First 1)
}
if ($hayCambios -and -not $SinPublicar -or -not (Test-Path -LiteralPath $Exe)) {
    if (-not (Publicar)) { Write-Host ''; exit 1 }
}

$lista = @()
if ($Archivo) { $lista += $Archivo }
$lista += @($Archivos | Where-Object { $_ })
$argumentos = @()
foreach ($a in $lista) {
    if (-not (Test-Path -LiteralPath $a -PathType Leaf)) {
        Mal "No existe el archivo: $a"
        exit 1
    }
    $argumentos += '"' + (Resolve-Path -LiteralPath $a).Path + '"'
}
# Sin DOTNET_ROOT: un exe de Native AOT no debe necesitar nada de .NET para arrancar.
Remove-Item Env:DOTNET_ROOT -ErrorAction SilentlyContinue

if ($Prueba -gt 0) {
    if ($lista.Count -eq 0) { Mal '-Prueba necesita al menos un video o un proyecto.'; exit 1 }
    $registro = Join-Path $Raiz ('TestResults\visor-prueba-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.txt')
    $argumentos += @('--prueba', $Prueba.ToString([System.Globalization.CultureInfo]::InvariantCulture), '--registro', ('"' + $registro + '"'))
    Titulo "Recorrido automático de $Prueba s (la ventana se abre y se cierra sola)..."
    $p = Start-Process -FilePath $Exe -ArgumentList $argumentos -Wait -PassThru
    Write-Host ''
    if (Test-Path -LiteralPath $registro) {
        Get-Content -LiteralPath $registro -Encoding UTF8 | ForEach-Object { Nota $_ }
        Nota ''
        Nota "Registro: $(Corto $registro)"
    } else {
        Mal 'El visor no dejó registro; probablemente no pudo abrir el archivo o la GPU.'
    }
    if ($p.ExitCode -eq 0) { Bien 'El recorrido terminó bien.' } else { Mal "El recorrido terminó con código $($p.ExitCode)." }
    exit $p.ExitCode
}

if ($argumentos.Count -gt 0) { Start-Process -FilePath $Exe -ArgumentList $argumentos } else { Start-Process -FilePath $Exe }
Bien 'Visor abierto. Teclas: Espacio, ←/→, 1-9, Ctrl+1-9, Alt+flechas, +/-, R, O, Ctrl+Z/Y, Ctrl+S, I. Detalle en docs\prueba-visor.md.'
exit 0
