# Sondeo de la fase 4: publica src\Rodaje.App (WinUI 3 sin empaquetar, autocontenido, Native AOT) en
# dist\sondeo-winui\, cuenta advertencias y tamaños, y mide arranque, ritmo y latencias (docs/sondeo-winui.md).
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File herramientas\sondeo-winui.ps1 [-SinPublicar] [-Segundos 20]
# Sale con 0 si publicó sin advertencias y todas las corridas terminaron bien.
[CmdletBinding()]
param(
    [switch]$SinPublicar,
    [double]$Segundos = 20
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$Raiz = Split-Path -Parent $PSScriptRoot
$Proyecto = Join-Path $Raiz 'src\Rodaje.App\Rodaje.App.csproj'
$Salida = Join-Path $Raiz 'dist\sondeo-winui'
$Exe = Join-Path $Salida 'Rodaje.exe'
if ($env:RODAJE_ENTORNO) { $Entorno = $env:RODAJE_ENTORNO } else { $Entorno = Join-Path $Raiz '.entorno' }
$DotnetDir = Join-Path $Entorno 'dotnet'
$Dotnet = Join-Path $DotnetDir 'dotnet.exe'
$Resultados = Join-Path $Raiz 'TestResults'
New-Item -ItemType Directory -Force -Path $Resultados | Out-Null
$Corrida = Join-Path $Resultados ('sondeo-winui-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force -Path $Corrida | Out-Null

function Titulo([string]$t) { Write-Host ''; Write-Host "  $t" -ForegroundColor Cyan }
function Bien([string]$t) { Write-Host "    $t" -ForegroundColor Green }
function Mal([string]$t) { Write-Host "    $t" -ForegroundColor Red }
function Nota([string]$t) { Write-Host "    $t" }
function MB([double]$b) { return ('{0:0.0} MB' -f ($b / 1MB)) }

# Los clips 1080p de prueba de TestResults\media-cache (de este árbol o del repositorio principal si es un worktree).
function Buscar-Clip([string]$patron) {
    for ($d = Get-Item -LiteralPath $Raiz; $d; $d = $d.Parent) {
        $cache = Join-Path $d.FullName 'TestResults\media-cache'
        if (Test-Path -LiteralPath $cache) {
            $f = Get-ChildItem -LiteralPath $cache -Filter $patron -File -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($f) { return $f.FullName }
        }
    }
    return $null
}

$ok = $true
if (-not $SinPublicar) {
    Titulo 'Publicando Rodaje.App con Native AOT (WinUI 3 autocontenido, sin MSIX)...'
    if (-not (Test-Path -LiteralPath $Dotnet)) { Mal "No encuentro el SDK en $Dotnet (define RODAJE_ENTORNO o corre ABRIR.bat)."; exit 1 }
    $env:DOTNET_ROOT = $DotnetDir
    $env:NUGET_PACKAGES = Join-Path $Entorno 'nuget'
    $env:DOTNET_CLI_HOME = $Entorno
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_CLI_FORCE_UTF8_ENCODING = '1'
    $env:PATH = "$DotnetDir;$env:PATH"
    # VsDevCmd.bat corre vswhere.exe desde la carpeta actual; añadir su carpeta al PATH evita que falle link.exe.
    $instaladorVs = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
    if (Test-Path -LiteralPath (Join-Path $instaladorVs 'vswhere.exe')) { $env:PATH = "$env:PATH;$instaladorVs" }
    if (Test-Path -LiteralPath $Salida) { Remove-Item -LiteralPath $Salida -Recurse -Force }

    $lineas = New-Object System.Collections.Generic.List[string]
    $anterior = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Dotnet publish $Proyecto -c Release -o $Salida --nologo 2>&1 | ForEach-Object { $lineas.Add("$_") }
        $codigo = $LASTEXITCODE
    } finally { $ErrorActionPreference = $anterior }
    [System.IO.File]::WriteAllLines((Join-Path $Corrida 'publicar.log'), $lineas, (New-Object System.Text.UTF8Encoding($false)))
    $avisos = @($lineas | Where-Object { $_ -match '(?i)\b(warning|advertencia|error)\s+[A-Z]{2,}\d+' } | ForEach-Object { ($_ -replace '\s+\[[^\]]+proj[^\]]*\]\s*$', '').Trim() } | Select-Object -Unique)
    if ($codigo -ne 0 -or -not (Test-Path -LiteralPath $Exe)) {
        Mal 'No se pudo publicar.'; $avisos | Select-Object -First 15 | ForEach-Object { Nota $_ }; exit 1
    }
    if ($avisos.Count -gt 0) { $ok = $false; Mal "$($avisos.Count) advertencia(s):"; $avisos | ForEach-Object { Nota $_ } }
    else { Bien 'Publicado sin advertencias (ni de recorte ni de AOT: IL2xxx/IL3xxx).' }

    # FFmpeg LGPL junto al exe, como en dist\Rodaje-visor, para que la carpeta sea copiable.
    $ffBin = Join-Path $Entorno 'ffmpeg\bin'
    $ffDestino = Join-Path $Salida 'ffmpeg'
    New-Item -ItemType Directory -Force -Path $ffDestino | Out-Null
    foreach ($b in @('avutil-61', 'swresample-7', 'swscale-10', 'avcodec-63', 'avformat-63')) {
        Copy-Item -LiteralPath (Join-Path $ffBin "$b.dll") -Destination $ffDestino -Force
    }
}

Titulo 'Tamaños'
$archivos = Get-ChildItem -LiteralPath $Salida -File -Recurse | Where-Object { $_.Extension -ne '.pdb' -and $_.Name -notmatch '^(medidas|rodaje-error)' }
$ff = ($archivos | Where-Object { $_.FullName -like (Join-Path $Salida 'ffmpeg\*') } | Measure-Object Length -Sum).Sum
$total = ($archivos | Measure-Object Length -Sum).Sum
$tamanos = @(
    "exe: $(MB (Get-Item -LiteralPath $Exe).Length)",
    "carpeta sin .pdb: $(MB $total) en $($archivos.Count) archivos",
    "  de eso FFmpeg (5 DLL LGPL): $(MB $ff)",
    "  de eso Windows App SDK y runtime de WinUI: $(MB ($total - $ff - (Get-Item -LiteralPath $Exe).Length))"
)
$tamanos | ForEach-Object { Nota $_ }

function Medir([string]$nombre, [double]$seg, [string[]]$videos) {
    $log = Join-Path $Corrida "$nombre.txt"
    $argumentos = @($videos | ForEach-Object { '"' + $_ + '"' }) + @('--medir', "$seg", '--registro', ('"' + $log + '"'))
    $p = Start-Process -FilePath $Exe -ArgumentList $argumentos -PassThru
    if (-not $p.WaitForExit([int](($seg + 60) * 1000))) { $p.Kill(); Mal "$nombre no terminó"; return $null }
    if ($p.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $log)) { Mal "$nombre terminó con código $($p.ExitCode)"; $script:ok = $false }
    if (Test-Path -LiteralPath $log) { return Get-Content -LiteralPath $log -Encoding UTF8 }
    return $null
}

$clip = Buscar-Clip 'h264-1080p-23976-aac-*.mp4'
if (-not $clip) { Mal 'No encuentro el clip h264-1080p-23976-aac-*.mp4 en TestResults\media-cache (corre las pruebas una vez).'; exit 1 }

Titulo 'Arranque hasta el primer cuadro: 1 corrida tras publicar + 4 calientes (2 s cada una)'
$primeros = @()
for ($i = 1; $i -le 5; $i++) {
    $r = Medir "arranque-$i" 2 @($clip)
    $linea = $r | Where-Object { $_ -match '^arranque\.primer_cuadro_ms: (\d+)' } | Select-Object -First 1
    if ($linea -match '(\d+)$') { $primeros += [int]$Matches[1]; Nota "corrida ${i}: primer cuadro a los $($Matches[1]) ms del inicio del proceso" }
}

Titulo "Reproducción de 1080p, 1 capa, $Segundos s"
$una = Medir 'una-capa' $Segundos @($clip)
$una | ForEach-Object { Nota $_ }

$otros = @('hevc-1080p-25-*.mkv', 'h264-1080p-23976-desfase-*.mp4', 'hevc10-1080p-25-desfase-*.mkv') | ForEach-Object { Buscar-Clip $_ } | Where-Object { $_ }
if ($otros.Count -eq 3) {
    Titulo "Reproducción de 1080p, 4 capas (fondo y 3 imagen en imagen), $Segundos s"
    $cuatro = Medir 'cuatro-capas' $Segundos (@($clip) + $otros)
    $cuatro | ForEach-Object { Nota $_ }
}

$resumen = @($tamanos) + @("primer cuadro (ms, corrida 1 y calientes): $($primeros -join ', ')")
[System.IO.File]::WriteAllLines((Join-Path $Corrida 'resumen.txt'), $resumen, (New-Object System.Text.UTF8Encoding($false)))
Write-Host ''
Nota "Registros: $Corrida"
if ($ok) { Bien 'RESULTADO: correcto.'; exit 0 } else { Mal 'RESULTADO: hubo fallos (arriba).'; exit 1 }
