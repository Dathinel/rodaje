# Arma el .paquete de cada módulo de IA de modules\ (menos el eco de pruebas) en dist\modulos\<id>-<versión>.paquete:
# el módulo publicado (Native AOT, o autocontenido si no admite AOT, como Traducir con LLamaSharp), su paquete.json,
# LICENCIAS.md, README.md, el LICENSE de Rodaje y licencias\ (las de sus modelos y bibliotecas). Sin modelos: el gestor
# de complementos los baja después con su SHA-256 (module.models). Orden y fecha fijos en el zip.
# Uso:
#   powershell -NoProfile -ExecutionPolicy Bypass -File build\empaquetar-modulo.ps1 [-Modulo modulo-voz,...] [-Salida dist\modulos]
[CmdletBinding()]
param(
    [string[]]$Modulo = @(),
    [string]$Salida = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$Raiz = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'comun.psm1') -DisableNameChecking -Force
$E = Inicializar-Rodaje $Raiz
if (-not $Salida) { $Salida = Join-Path $Raiz 'dist\modulos' }
New-Item -ItemType Directory -Force -Path $Salida, (Join-Path $Raiz 'TestResults') | Out-Null
$Fecha = [DateTimeOffset]::new(2026, 1, 1, 0, 0, 0, [TimeSpan]::Zero)

function Comprimir-Paquete([string]$carpeta, [string]$zip) {
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    $base = (Resolve-Path -LiteralPath $carpeta).Path.TrimEnd('\') + '\'
    $archivos = @(Get-ChildItem -LiteralPath $carpeta -Recurse -File -Force | ForEach-Object {
        [pscustomobject]@{ Ruta = $_.FullName; Nombre = $_.FullName.Substring($base.Length).Replace('\', '/') }
    } | Sort-Object { $_.Nombre } -CaseSensitive)
    $archivo = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($f in $archivos) {
            $entrada = $archivo.CreateEntry($f.Nombre, [System.IO.Compression.CompressionLevel]::Optimal)
            $entrada.LastWriteTime = $Fecha
            $destino = $entrada.Open()
            try { $origen = [System.IO.File]::OpenRead($f.Ruta); try { $origen.CopyTo($destino) } finally { $origen.Dispose() } } finally { $destino.Dispose() }
        }
    } finally { $archivo.Dispose() }
}

Usar-DotnetLocal $E
Agregar-Vswhere | Out-Null
$hechos = @()
try {
    foreach ($dir in Get-ChildItem -LiteralPath (Join-Path $Raiz 'modules') -Directory) {
        $json = Join-Path $dir.FullName 'paquete.json'
        $csproj = Get-ChildItem -LiteralPath $dir.FullName -Filter '*.csproj' | Select-Object -First 1
        if (-not (Test-Path -LiteralPath $json) -or -not $csproj) { continue }
        $m = Get-Content -LiteralPath $json -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($m.id -eq 'rodaje-eco' -or ($Modulo.Count -gt 0 -and $Modulo -notcontains $m.id)) { continue }
        if ($m.module.entry -match '[/\\]') { throw "$($m.id): module.entry tiene que estar en la raíz del paquete (los modelos van en modelos\ junto al exe)." }

        Titulo "$($m.id) $($m.version)"
        $carpeta = Join-Path $Salida ".armando\$($m.id)"
        if (Test-Path -LiteralPath $carpeta) { Remove-Item -LiteralPath $carpeta -Recurse -Force }
        $log = Join-Path $Raiz "TestResults\empaquetar-$($m.id).log"
        $r = Invocar-Dotnet $log @('publish', $csproj.FullName, '-c', 'Release', '-o', $carpeta, '--nologo', '-v', 'q', '-p:SelfContained=true')
        $entrada = Join-Path $carpeta $m.module.entry
        if ($r.Codigo -ne 0 -or -not (Test-Path -LiteralPath $entrada)) { throw "No se pudo publicar $($m.id) ($log)." }
        Get-ChildItem -LiteralPath $carpeta -Recurse -Filter '*.pdb' -File | Remove-Item -Force
        foreach ($f in 'paquete.json', 'LICENCIAS.md', 'README.md') {
            $origen = Join-Path $dir.FullName $f
            if (Test-Path -LiteralPath $origen) { Copy-Item -LiteralPath $origen -Destination $carpeta -Force }
        }
        Copy-Item -LiteralPath (Join-Path $Raiz 'LICENSE') -Destination $carpeta -Force
        $licencias = Join-Path $dir.FullName 'licencias'
        if (Test-Path -LiteralPath $licencias) {
            New-Item -ItemType Directory -Force -Path (Join-Path $carpeta 'licencias') | Out-Null
            Copy-Item -Path (Join-Path $licencias '*') -Destination (Join-Path $carpeta 'licencias') -Recurse -Force
        }
        if (Firmar-Codigo @($entrada)) { Bien "$($m.module.entry) firmado." }

        $paquete = Join-Path $Salida "$($m.id)-$($m.version).paquete"
        Comprimir-Paquete $carpeta $paquete
        Remove-Item -LiteralPath $carpeta -Recurse -Force
        $item = Get-Item -LiteralPath $paquete
        $sha = (Get-FileHash -LiteralPath $paquete -Algorithm SHA256).Hash.ToLowerInvariant()
        Bien ('{0}: {1:0.0} MB, SHA-256 {2}' -f $item.Name, ($item.Length / 1MB), $sha)
        $hechos += $paquete
    }
} finally {
    Remove-Item Env:DOTNET_ROOT -ErrorAction SilentlyContinue
    $armando = Join-Path $Salida '.armando'
    if (Test-Path -LiteralPath $armando) { Remove-Item -LiteralPath $armando -Recurse -Force }
}

if ($hechos.Count -eq 0) { throw 'No se armó ningún módulo (¿-Modulo con un id que no existe?).' }
Titulo "Listo: $($hechos.Count) módulos en $(Corto $Salida)"
