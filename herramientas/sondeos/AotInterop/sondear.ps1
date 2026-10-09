# Publica cada variante del sondeo con Native AOT, cuenta sus advertencias de recorte/AOT y la ejecuta.
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File sondear.ps1 -Muestras <carpeta> [-Ffmpeg <carpeta DLL>] [-Combinaciones Cargado:TerraFX,Enlazado:TerraFX]
# La carpeta de muestras debe tener corto.mp4 (H.264 + AAC 1 kHz), bench1080.mp4 y hevc10.mp4 (ver docs\sondeo-interop-aot.md).
param(
    [Parameter(Mandatory = $true)][string]$Muestras,
    [string]$Ffmpeg = '',
    [string[]]$Combinaciones = @('Cargado:Vortice', 'Cargado:TerraFX', 'Enlazado:TerraFX', 'AutoGen:TerraFX', 'NAudio:TerraFX'),
    [switch]$SinAudio
)
$ErrorActionPreference = 'Continue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
$raiz = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$proyecto = Join-Path $PSScriptRoot 'AotInterop.csproj'
$dotnet = Join-Path $raiz 'herramientas\dotnet-local.cmd'
if (-not $env:FOTOTOMA_ENTORNO -and -not (Test-Path (Join-Path $raiz '.entorno\dotnet'))) {
    # Worktree de agente (.claude\worktrees\x): usa el .entorno del repositorio principal.
    $principal = Join-Path $raiz '..\..\..\.entorno'
    if (Test-Path $principal) { $env:FOTOTOMA_ENTORNO = (Resolve-Path $principal).Path }
}
# vcvarsall.bat necesita vswhere.exe en el PATH para encontrar link.exe (ver publicar-aot-prueba.ps1).
$instaladorVs = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (Test-Path (Join-Path $instaladorVs 'vswhere.exe')) { $env:PATH = "$env:PATH;$instaladorVs" }
$argFfmpeg = @()
if ($Ffmpeg) { $argFfmpeg = @('--ffmpeg', $Ffmpeg) }

function Ejecutar([string]$exe, [string[]]$a) {
    $r = [System.Diagnostics.Stopwatch]::StartNew()
    & $exe @a 2>&1 | ForEach-Object { "    $_" } | Write-Host
    $r.Stop()
    return $r.Elapsed.TotalMilliseconds
}

foreach ($combinacion in $Combinaciones) {
    $v, $g = $combinacion -split ':'
    if (-not $g) { $g = 'Vortice' }
    $salida = Join-Path $raiz "dist\sondeo-aot\$v-$g"
    if (Test-Path $salida) { Remove-Item $salida -Recurse -Force }
    Write-Host ''
    Write-Host "=== FFmpeg: $v, D3D11/XAudio2: $g ===" -ForegroundColor Cyan
    # Sin advertencias como errores para poder contarlas; la variante elegida también se publica estricta abajo.
    $log = & $dotnet publish $proyecto -c Release "-p:Variante=$v" "-p:Graficos=$g" -p:TreatWarningsAsErrors=false -o $salida 2>&1 | ForEach-Object { "$_" }
    $codigo = $LASTEXITCODE
    $avisos = @($log | Where-Object { $_ -match '(warning|advertencia) (IL\d+|CA\d+|CS\d+|SYSLIB\d+)' } |
        ForEach-Object { ($_ -replace '\s+\[[^\]]+\.csproj[^\]]*\]\s*$', '').Trim() } | Sort-Object -Unique)
    $deAot = @($avisos | Where-Object { $_ -match '\bIL\d+' })
    Write-Host "  publicación: código $codigo, advertencias IL (recorte/AOT): $($deAot.Count), otras: $($avisos.Count - $deAot.Count)"
    $deAot | Select-Object -First 30 | ForEach-Object { Write-Host "    $_" }
    $exe = Join-Path $salida 'AotInterop.exe'
    if ($codigo -ne 0 -or -not (Test-Path $exe)) {
        $log | Where-Object { $_ -match 'error' } | Select-Object -First 10 | ForEach-Object { Write-Host "    $_" }
        continue
    }
    Write-Host ("  ejecutable: {0:0.00} MB" -f ((Get-Item $exe).Length / 1MB))
    $tiempos = 1..5 | ForEach-Object {
        $r = [System.Diagnostics.Stopwatch]::StartNew(); & $exe version @argFfmpeg | Out-Null; $r.Elapsed.TotalMilliseconds }
    Write-Host ("  arranque + carga de FFmpeg + salida (modo version), 5 corridas: {0} ms" -f (($tiempos | ForEach-Object { '{0:0}' -f $_ }) -join ', '))
    Ejecutar $exe (@('version') + $argFfmpeg) | Out-Null
    Ejecutar $exe (@('d3d') + $argFfmpeg) | Out-Null
    Ejecutar $exe (@('decodificar', (Join-Path $Muestras 'corto.mp4')) + $argFfmpeg) | Out-Null
    Ejecutar $exe (@('compartir', (Join-Path $Muestras 'corto.mp4')) + $argFfmpeg) | Out-Null
    Ejecutar $exe (@('compartir', (Join-Path $Muestras 'hevc10.mp4')) + $argFfmpeg) | Out-Null
    if ($v -ne 'NAudio') {
        Ejecutar $exe (@('medir', (Join-Path $Muestras 'bench1080.mp4')) + $argFfmpeg) | Out-Null
    }
    if (-not $SinAudio) {
        if ($v -eq 'NAudio') {
            Ejecutar $exe (@('audio-naudio') + $argFfmpeg) | Out-Null
        } else {
            Ejecutar $exe (@('audio-xaudio2', (Join-Path $Muestras 'corto.mp4')) + $argFfmpeg) | Out-Null
            Ejecutar $exe (@('audio-wasapi', (Join-Path $Muestras 'corto.mp4')) + $argFfmpeg) | Out-Null
        }
    }
}
