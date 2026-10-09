# Publica el visor de la fase 2 (src\Rodaje.Visor) con Native AOT en dist\Rodaje-visor\ y lo abre.
# Uso:
#   powershell -NoProfile -ExecutionPolicy Bypass -File herramientas\visor.ps1 [-Archivo video.mp4]
#   ... -Archivo video.mp4 -Prueba 10     recorrido automático de 10 s que imprime las estadísticas
#   ... -SinPublicar                      abre el exe ya publicado sin recompilar
# Requiere link.exe (Build Tools con C++) para publicar. Sale con 0 si todo fue bien.
param(
    [string]$Archivo,
    [double]$Prueba = 0,
    [switch]$SinPublicar
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$Raiz = Split-Path -Parent $PSScriptRoot
$Proyecto = Join-Path $Raiz 'src\Rodaje.Visor\Rodaje.Visor.csproj'
$Salida = Join-Path $Raiz 'dist\Rodaje-visor'
$Exe = Join-Path $Salida 'Rodaje.Visor.exe'
if ($env:RODAJE_ENTORNO) { $Entorno = $env:RODAJE_ENTORNO } else { $Entorno = Join-Path $Raiz '.entorno' }
$DotnetDir = Join-Path $Entorno 'dotnet'
$Dotnet = Join-Path $DotnetDir 'dotnet.exe'
$FfmpegBin = Join-Path $Entorno 'ffmpeg\bin'
$Bibliotecas = @('avutil-61', 'swresample-7', 'swscale-10', 'avcodec-63', 'avformat-63')
try { $SdkVersion = (Get-Content -LiteralPath (Join-Path $Raiz 'global.json') -Raw -Encoding UTF8 | ConvertFrom-Json).sdk.version } catch { $SdkVersion = '' }
$MarcaSdk = Join-Path $DotnetDir ".sdk-$SdkVersion"

function Titulo([string]$texto) { Write-Host ''; Write-Host "  $texto" -ForegroundColor Cyan }
function Bien([string]$texto) { Write-Host "    $texto" -ForegroundColor Green }
function Mal([string]$texto) { Write-Host "    $texto" -ForegroundColor Red }
function Nota([string]$texto) { Write-Host "    $texto" }
function Corto([string]$texto) { return $texto.Replace($Raiz + '\', '').Replace($Raiz, '.') }

function Publicar {
    Titulo 'Compilando el visor a código nativo (Native AOT, puede tardar un minuto)...'
    if (-not (Test-Path -LiteralPath $Dotnet) -or -not (Test-Path -LiteralPath $MarcaSdk)) {
        Mal 'No encuentro el SDK de .NET del proyecto (o quedó a medias).'
        Nota "Lo busqué en: $Dotnet"
        Nota 'Haz doble clic en ABRIR.bat, que lo prepara solo, o corre herramientas\preparar-entorno.ps1.'
        return $false
    }

    # Las mismas variables que herramientas\dotnet-local.cmd.
    $env:DOTNET_ROOT = $DotnetDir
    $env:NUGET_PACKAGES = Join-Path $Entorno 'nuget'
    $env:DOTNET_CLI_HOME = $Entorno
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_CLI_FORCE_UTF8_ENCODING = '1'
    $env:PATH = "$DotnetDir;$env:PATH"
    # VsDevCmd.bat corre vswhere.exe desde la carpeta actual; con NoDefaultCurrentDirectoryInExePath
    # puesto falla y daña la ruta de link.exe. Añadir la carpeta al PATH del proceso lo evita.
    $instaladorVs = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
    if (Test-Path -LiteralPath (Join-Path $instaladorVs 'vswhere.exe')) { $env:PATH = "$env:PATH;$instaladorVs" }

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

    # Distribución: las DLL LGPL de FFmpeg van en ffmpeg\ junto al exe (FfmpegLocator las busca ahí primero).
    $destino = Join-Path $Salida 'ffmpeg'
    New-Item -ItemType Directory -Force -Path $destino | Out-Null
    foreach ($b in $Bibliotecas) {
        $dll = Join-Path $FfmpegBin "$b.dll"
        if (-not (Test-Path -LiteralPath $dll)) {
            Mal "Falta $dll. Corre ABRIR.bat preparar-entorno para descargar FFmpeg."
            return $false
        }
        Copy-Item -LiteralPath $dll -Destination $destino -Force
    }
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

Titulo 'Rodaje - visor de un clip (fase 2)'
if (-not $SinPublicar -or -not (Test-Path -LiteralPath $Exe)) {
    if (-not (Publicar)) { Write-Host ''; exit 1 }
}

if ($Archivo -and -not (Test-Path -LiteralPath $Archivo)) {
    Mal "No existe el archivo: $Archivo"
    exit 1
}

$argumentos = @()
if ($Archivo) { $argumentos += '"' + (Resolve-Path -LiteralPath $Archivo).Path + '"' }
# Sin DOTNET_ROOT: un exe de Native AOT no debe necesitar nada de .NET para arrancar.
Remove-Item Env:DOTNET_ROOT -ErrorAction SilentlyContinue

if ($Prueba -gt 0) {
    if (-not $Archivo) { Mal '-Prueba necesita -Archivo.'; exit 1 }
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
Bien 'Visor abierto. Teclas: Espacio, ←/→, Shift+←/→, Inicio/Fin, F, Esc, I, Ctrl+O. Detalle en docs\prueba-visor.md.'
exit 0
