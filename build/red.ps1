# Compila dathinel-red (el transporte de la colaboración, Dathinel/sala en externo\sala) en release, con --locked y SIN el
# feature prueba, y lo deja en dist\Rodaje\app\modulos\red\ con sus licencias: de ahí lo toman el portable, la completa y
# el instalador. Rodaje lo busca en modulos\red\ junto a su exe (CollabHost.FindRed).
#
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File build\red.ps1 [-Destino <carpeta app>] [-SinComprobar]
# Necesita el submódulo externo\sala (git submodule update --init externo/sala). El Rust sale de externo\sala\.entorno
# (build\preparar-entorno.ps1 de Sala: rustup con huellas fijadas, nada global).
[CmdletBinding()]
param(
    [string]$Destino = '',
    [switch]$SinComprobar
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$Raiz = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'comun.psm1') -DisableNameChecking -Force
if (-not $Destino) { $Destino = Join-Path $Raiz 'dist\Rodaje\app' }

$Sala = Join-Path $Raiz 'externo\sala'
$Red = Join-Path $Sala 'red'
if (-not (Test-Path -LiteralPath (Join-Path $Red 'Cargo.toml'))) {
    throw "No está el submódulo de Sala en $Sala (git submodule update --init externo/sala)."
}

Titulo 'dathinel-red (colaboración)'
$entornoSala = if ($env:SALA_ENTORNO) { $env:SALA_ENTORNO } else { Join-Path $Sala '.entorno' }   # SALA_ENTORNO: compartir el de otra copia de Sala
$env:SALA_ENTORNO = $entornoSala
if (-not (Test-Path -LiteralPath (Join-Path $entornoSala 'cargo\bin\cargo.exe'))) {
    Nota 'Preparando el Rust de Sala en externo\sala\.entorno...'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Sala 'build\preparar-entorno.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo preparar el entorno de Rust de Sala.' }
}

$env:RUSTUP_HOME = Join-Path $entornoSala 'rustup'
$env:CARGO_HOME = Join-Path $entornoSala 'cargo'
$env:PATH = (Join-Path $env:CARGO_HOME 'bin') + [System.IO.Path]::PathSeparator + $env:PATH
$env:CARGO_TARGET_DIR = Join-Path $Red 'target'

Push-Location $Red
try {
    # Sin --features: el relé local de pruebas y releInseguro no existen en lo que se distribuye.
    & cargo build --release --locked
    if ($LASTEXITCODE -ne 0) { throw 'cargo build --release --locked falló.' }
    if (-not $SinComprobar) {
        # Inventario de licencias y avisos de seguridad de los crates (nada GPL): la misma política que la CI de Sala.
        $deny = Join-Path $env:CARGO_HOME 'bin\cargo-deny.exe'
        if (Test-Path -LiteralPath $deny) {
            & $deny --locked check licenses bans sources advisories
            if ($LASTEXITCODE -ne 0) { throw 'cargo deny no pasa: no se distribuye dathinel-red.' }
        } else { Aviso 'Sin cargo-deny en el entorno de Sala: no se comprobaron las licencias de los crates.' }
    }
} finally { Pop-Location }

$exe = Join-Path $env:CARGO_TARGET_DIR 'release\dathinel-red.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "No se generó $exe." }

# Lo distribuido no lleva el relé de pruebas: sin --rele-prueba arranca como cualquier módulo y, sin la tubería, se niega (64).
cmd /c "`"$exe`" --rele-prueba < nul > nul 2>&1"
if ($LASTEXITCODE -ne 64) { throw "dathinel-red --rele-prueba salió con $LASTEXITCODE y no con 64: ¿lleva el feature prueba? No se distribuye." }

$modulo = Join-Path $Destino 'modulos\red'
New-Item -ItemType Directory -Force -Path $modulo | Out-Null
Copy-Item -LiteralPath $exe -Destination (Join-Path $modulo 'dathinel-red.exe') -Force
Copy-Item -LiteralPath (Join-Path $Sala 'LICENSE') -Destination (Join-Path $modulo 'LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $Sala 'docs\licencias-crates.md') -Destination (Join-Path $modulo 'LICENCIAS-CRATES.md') -Force
Copy-Item -LiteralPath (Join-Path $Sala 'docs\internet.md') -Destination (Join-Path $modulo 'INTERNET.md') -Force

if (Firmar-Codigo @((Join-Path $modulo 'dathinel-red.exe'))) { Bien 'dathinel-red.exe firmado.' }
else { Aviso 'Sin RODAJE_CERTIFICADO: dathinel-red.exe va sin firmar.' }

$hash = (Get-FileHash -LiteralPath (Join-Path $modulo 'dathinel-red.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
$tam = [math]::Round((Get-Item -LiteralPath (Join-Path $modulo 'dathinel-red.exe')).Length / 1MB, 1)
Bien "dathinel-red.exe ($tam MB, sha256 $hash) en $modulo"
