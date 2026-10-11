# Arma el .paquete de esta carpeta (o de otra con su paquete.json). Solo necesita Windows PowerShell 5.1.
# Uso:
#   powershell -NoProfile -ExecutionPolicy Bypass -File empaquetar.ps1 [-Contenido <carpeta>] [-Salida <carpeta>]
# Por defecto empaqueta la carpeta del script y deja el resultado en su dist\.
# Copia las fuentes (sin los .ps1, README.md, .gitignore ni dist\), corre generar.ps1 si lo hay (las LUT) y comprime todo
# con orden y fechas fijas: las mismas fuentes dan el mismo archivo byte a byte. Deja <id>-<versión>.paquete en
# -Salida e imprime su SHA-256. Sale con 0 si todo fue bien.
param(
    [string]$Contenido = '',
    [string]$Salida = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

if (-not $Contenido) { $Contenido = $PSScriptRoot }
if (-not $Salida) { $Salida = Join-Path $Contenido 'dist' }
$Contenido = [System.IO.Path]::GetFullPath($Contenido)
$Salida = [System.IO.Path]::GetFullPath($Salida)

$manifiesto = Join-Path $Contenido 'paquete.json'
if (-not (Test-Path -LiteralPath $manifiesto)) { Write-Host "  Falta paquete.json en $Contenido" -ForegroundColor Red; exit 1 }
$datos = [System.IO.File]::ReadAllText($manifiesto) | ConvertFrom-Json
$nombre = "$($datos.id)-$($datos.version).paquete"

$temporal = Join-Path ([System.IO.Path]::GetTempPath()) ("rodaje-empaquetar-" + [guid]::NewGuid().ToString('N').Substring(0, 10))
try {
    New-Item -ItemType Directory -Force -Path $temporal | Out-Null
    $base = $Contenido.TrimEnd('\') + '\'
    foreach ($archivo in Get-ChildItem -LiteralPath $Contenido -Recurse -File) {
        $relativa = $archivo.FullName.Substring($base.Length)
        if ($archivo.Extension -eq '.ps1' -or $relativa -in @('README.md', '.gitignore') -or $relativa.StartsWith('dist\') -or $archivo.FullName.StartsWith($Salida.TrimEnd('\') + '\')) { continue }
        $destino = Join-Path $temporal $relativa
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destino) | Out-Null
        Copy-Item -LiteralPath $archivo.FullName -Destination $destino
    }

    $generar = Join-Path $Contenido 'generar.ps1'
    if (Test-Path -LiteralPath $generar) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $generar -Destino $temporal
        if ($LASTEXITCODE -ne 0) { throw "generar.ps1 falló con el código $LASTEXITCODE." }
    }

    foreach ($elemento in $datos.items) {
        if ($elemento.file -and -not (Test-Path -LiteralPath (Join-Path $temporal $elemento.file))) {
            throw "Falta $($elemento.file), que nombra el elemento $($elemento.id)."
        }
    }

    Add-Type -AssemblyName System.IO.Compression
    New-Item -ItemType Directory -Force -Path $Salida | Out-Null
    $paquete = Join-Path $Salida $nombre
    $fecha = New-Object System.DateTimeOffset (2026, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
    $tbase = $temporal.TrimEnd('\') + '\'
    $entradas = Get-ChildItem -LiteralPath $temporal -Recurse -File |
        ForEach-Object { [pscustomobject]@{ Ruta = $_.FullName; Nombre = $_.FullName.Substring($tbase.Length).Replace('\', '/') } } |
        Sort-Object -Property Nombre -CaseSensitive
    $flujo = [System.IO.File]::Create($paquete)
    try {
        $zip = New-Object System.IO.Compression.ZipArchive ($flujo, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($e in $entradas) {
                $entrada = $zip.CreateEntry($e.Nombre, [System.IO.Compression.CompressionLevel]::Optimal)
                $entrada.LastWriteTime = $fecha
                $destinoZip = $entrada.Open()
                try {
                    $bytes = [System.IO.File]::ReadAllBytes($e.Ruta)
                    $destinoZip.Write($bytes, 0, $bytes.Length)
                } finally { $destinoZip.Dispose() }
            }
        } finally { $zip.Dispose() }
    } finally { $flujo.Dispose() }

    $hash = (Get-FileHash -LiteralPath $paquete -Algorithm SHA256).Hash.ToLowerInvariant()
    $tamano = (Get-Item -LiteralPath $paquete).Length
    Write-Host "  $paquete" -ForegroundColor Green
    Write-Host ("    {0} archivos, {1:0.0} MB, SHA-256 {2}" -f @($entradas).Count, ($tamano / 1MB), $hash)
    exit 0
} catch {
    Write-Host "  No se pudo armar el paquete: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
} finally {
    if (Test-Path -LiteralPath $temporal) { Remove-Item -LiteralPath $temporal -Recurse -Force -ErrorAction SilentlyContinue }
}
