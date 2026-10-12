# Crea la release v<versión> de Dathinel/rodaje con lo que dejó build\distribuir.ps1: instalador, portable,
# completo, SHA256SUMS.txt y las notas (Rodaje-<v>-notas.md). Antes comprueba que los hashes coinciden, que la
# etiqueta no existe y que el commit con el que se compiló el exe está en origin/main; la etiqueta va sobre él.
# Uso:
#   powershell -NoProfile -ExecutionPolicy Bypass -File build\publicar-release.ps1 [-Version 1.0.0] [-Borrador] [-Salida dist\distribucion]
[CmdletBinding()]
param(
    [string]$Version = '',
    [string]$Salida = '',
    [switch]$Borrador
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$Raiz = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'comun.psm1') -DisableNameChecking -Force
$null = Inicializar-Rodaje $Raiz
if (-not $Version) { $Version = Version-Rodaje }
if (-not $Salida) { $Salida = Join-Path $Raiz 'dist\distribucion' }
$Repo = 'Dathinel/rodaje'
$Etiqueta = "v$Version"

Titulo "Release $Etiqueta de $Repo"
$sumas = Join-Path $Salida 'SHA256SUMS.txt'
$notas = Join-Path $Salida "Rodaje-$Version-notas.md"
foreach ($f in $sumas, $notas) { if (-not (Test-Path -LiteralPath $f)) { throw "Falta ${f}: corre build\distribuir.ps1." } }

$archivos = New-Object System.Collections.Generic.List[string]
foreach ($linea in Get-Content -LiteralPath $sumas -Encoding UTF8) {
    if ($linea -notmatch '^([0-9a-f]{64})  (.+)$') { continue }
    $ruta = Join-Path $Salida $Matches[2]
    if (-not (Test-Path -LiteralPath $ruta)) { throw "Falta $ruta, que figura en SHA256SUMS.txt." }
    if ((Get-FileHash -LiteralPath $ruta -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Matches[1]) { throw "$($Matches[2]) no coincide con SHA256SUMS.txt." }
    if ((Get-Item -LiteralPath $ruta).Length -ge 2GB) { throw "$($Matches[2]) pasa de 2 GiB, el límite de un asset de GitHub." }
    $archivos.Add($ruta)
    Bien "$($Matches[2]) verificado"
}
if (-not ($archivos | Where-Object { $_ -like '*-instalador.exe' })) { throw 'SHA256SUMS.txt no incluye el instalador.' }
$archivos.Add($sumas)

& gh release view $Etiqueta -R $Repo *> $null
if ($LASTEXITCODE -eq 0) { throw "La release $Etiqueta ya existe en $Repo." }
# La etiqueta va sobre el commit con el que se compiló (InformationalVersion del exe), que debe estar en origin/main.
$producto = (Get-Item -LiteralPath (Join-Path $Raiz 'dist\Rodaje\app\Rodaje.exe')).VersionInfo.ProductVersion
if ($producto -notmatch "^$([regex]::Escape($Version))\+([0-9a-f]{7,40})$") { throw "El exe publicado dice «$producto», no $Version+<commit>." }
git -C $Raiz fetch origin main --quiet
$commit = (git -C $Raiz rev-parse $Matches[1]).Trim()
git -C $Raiz merge-base --is-ancestor $commit origin/main
if ($LASTEXITCODE -ne 0) { throw "El commit $($Matches[1]) del exe no está en origin/main: sube y vuelve a correr distribuir.ps1." }

$argumentos = @('release', 'create', $Etiqueta, '-R', $Repo, '--target', $commit, '--title', "Rodaje $Version", '--notes-file', $notas) + $archivos
if ($Borrador) { $argumentos += '--draft' }
& gh @argumentos
if ($LASTEXITCODE -ne 0) { throw 'gh release create falló.' }
Bien "Publicada: https://github.com/$Repo/releases/tag/$Etiqueta"
