# Arma el .paquete de cada módulo de IA de modules\ (menos el eco de pruebas) en dist\modulos\<id>-<versión>.paquete:
# el módulo publicado (Native AOT, o autocontenido si no admite AOT, como Traducir con LLamaSharp), su paquete.json,
# LICENCIAS.md, README.md, el LICENSE de Rodaje y licencias\ (las de sus modelos y bibliotecas). Sin modelos: el gestor
# de complementos los baja después con su SHA-256 (module.models). Orden y fecha fijos en el zip.
# En Linux y macOS (pwsh, D-066) arma el del sistema que corre: <id>-<versión>-<rid>.paquete con «platform» y el formato 2
# en paquete.json y el ejecutable sin .exe; los módulos que todavía no compilan para ese RID se saltan.
# Uso:
#   powershell -NoProfile -ExecutionPolicy Bypass -File build\empaquetar-modulo.ps1 [-Modulo modulo-voz,...] [-Salida dist\modulos]
#   pwsh build/empaquetar-modulo.ps1 [-Modulo modulo-audio-ia] [-Salida dist/modulos]
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
if (-not $Salida) { $Salida = Join-Path (Join-Path $Raiz 'dist') 'modulos' }
# El RID es el del anfitrión, como lo elige cada .csproj: «-r» llegaría a las bibliotecas y cambiaría sus lockfiles.
$Rid = if ($IsLinux) { 'linux-x64' } elseif ($IsMacOS) { 'osx-arm64' } else { 'win-x64' }
$EnWindows = $Rid -eq 'win-x64'
New-Item -ItemType Directory -Force -Path $Salida, (Join-Path $Raiz 'TestResults') | Out-Null
$Fecha = [DateTimeOffset]::new(2026, 1, 1, 0, 0, 0, [TimeSpan]::Zero)

function Comprimir-Paquete([string]$carpeta, [string]$zip) {
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    $sep = [System.IO.Path]::DirectorySeparatorChar
    $base = (Resolve-Path -LiteralPath $carpeta).Path.TrimEnd($sep) + $sep
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
        if (-not $EnWindows -and (Get-Content -LiteralPath $csproj.FullName -Raw -Encoding UTF8) -notmatch "<RuntimeIdentifiers>[^<]*$Rid") {
            Aviso "$($m.id): todavía no compila para $Rid; se salta."
            continue
        }
        $entry = if ($EnWindows) { $m.module.entry } else { $m.module.entry -replace '\.exe$', '' }

        Titulo "$($m.id) $($m.version) ($Rid)"
        $carpeta = Join-Path (Join-Path $Salida '.armando') $m.id
        if (Test-Path -LiteralPath $carpeta) { Remove-Item -LiteralPath $carpeta -Recurse -Force }
        $log = Join-Path (Join-Path $Raiz 'TestResults') "empaquetar-$($m.id).log"
        $r = Invocar-Dotnet $log @('publish', $csproj.FullName, '-c', 'Release', '-o', $carpeta, '--nologo', '-v', 'q', '-p:SelfContained=true')
        $entrada = Join-Path $carpeta $entry
        if ($r.Codigo -ne 0 -or -not (Test-Path -LiteralPath $entrada)) { throw "No se pudo publicar $($m.id) ($log)." }
        Get-ChildItem -LiteralPath $carpeta -Recurse -Filter '*.pdb' -File | Remove-Item -Force
        # Los símbolos que Native AOT deja aparte en Linux (.dbg) y macOS (.dSYM) tampoco viajan.
        Get-ChildItem -LiteralPath $carpeta -Recurse -Filter '*.dbg' -File | Remove-Item -Force
        Get-ChildItem -LiteralPath $carpeta -Recurse -Filter '*.dSYM' -Directory | Remove-Item -Recurse -Force
        if (-not $EnWindows) {
            # Formato 2 con «platform»: un editor que solo entiende el 1 rechaza el paquete entero (docs/formato-paquete.md).
            $manifiesto = Get-Content -LiteralPath $json -Raw -Encoding UTF8 | ConvertFrom-Json
            $manifiesto.formatVersion = 2
            $manifiesto.module.entry = $entry
            $manifiesto | Add-Member -NotePropertyName platform -NotePropertyValue $Rid -Force
            [System.IO.File]::WriteAllText((Join-Path $carpeta 'paquete.json'), ($manifiesto | ConvertTo-Json -Depth 20),
                (New-Object System.Text.UTF8Encoding($false)))
        }
        foreach ($f in @('paquete.json', 'LICENCIAS.md', 'README.md') | Where-Object { $EnWindows -or $_ -ne 'paquete.json' }) {
            $origen = Join-Path $dir.FullName $f
            if (Test-Path -LiteralPath $origen) { Copy-Item -LiteralPath $origen -Destination $carpeta -Force }
        }
        Copy-Item -LiteralPath (Join-Path $Raiz 'LICENSE') -Destination $carpeta -Force
        $licencias = Join-Path $dir.FullName 'licencias'
        if (Test-Path -LiteralPath $licencias) {
            New-Item -ItemType Directory -Force -Path (Join-Path $carpeta 'licencias') | Out-Null
            Copy-Item -Path (Join-Path $licencias '*') -Destination (Join-Path $carpeta 'licencias') -Recurse -Force
        }
        if ($EnWindows -and (Firmar-Codigo @($entrada))) { Bien "$entry firmado." }

        $paquete = Join-Path $Salida $(if ($EnWindows) { "$($m.id)-$($m.version).paquete" } else { "$($m.id)-$($m.version)-$Rid.paquete" })
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
