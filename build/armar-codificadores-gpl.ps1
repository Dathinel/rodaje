# Arma el complemento rodaje-codificadores-gpl: el ffmpeg.exe GPL de BtbN (fijado por SHA-256), el texto de la GPLv3 y
# el código fuente correspondiente, como .paquete instalable (módulo con protocol 0). No publica nada.
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File build\armar-codificadores-gpl.ps1 [-FuenteCompleta] [-Salida <carpeta>]
#
# Fuente: FFmpeg en el commit exacto de la compilación, los scripts de BtbN de esa release (fijan el commit de cada
# biblioteca) y x264 y x265 en sus commits. Con -FuenteCompleta baja además cada biblioteca que fijan esos scripts (un
# superconjunto de lo que lleva el exe, varios GB) a una carpeta aparte, para publicarla junto al paquete (GPL-3 §6d).
# Cada fuente se baja con git y se comprueba que el commit sea el fijado (el hash de git es la huella del contenido).
# Resistente a apagones: descarga a .part con curl -C -, todo se arma en una carpeta temporal y el .paquete se renombra.

param(
    [switch]$FuenteCompleta,
    [string]$Salida
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$PaqueteVersion = '0.1.0'
$FfmpegVersion  = 'n9.0.2-17-g2a571b6068'
$FfmpegCommit   = '2a571b606854520cf89804d8030c8b328e621689'
$BtbnRelease    = 'autobuild-2026-09-30-13-08'
$BtbnCommit     = '6c9aec5fc9a72ec3abedd1fa84db141fa18cf52b'
$ZipNombre      = "ffmpeg-$FfmpegVersion-win64-gpl-9.0.zip"
$ZipUrl         = "https://github.com/BtbN/FFmpeg-Builds/releases/download/$BtbnRelease/$ZipNombre"
$ZipSha256      = 'a0e45723c72141975f51d8666302e614711745f3102b704ca3f82c897a58d278'
$ZipBytes       = 194034585

function Paso([string]$texto) { Write-Host "[codificadores-gpl] $texto" }
function Fallar([string]$texto) { Write-Host ""; Write-Host $texto; exit 1 }
function Huella([string]$ruta) { (Get-FileHash -LiteralPath $ruta -Algorithm SHA256).Hash.ToLowerInvariant() }

$Raiz    = Split-Path -Parent $PSScriptRoot
$Entorno = if ($env:RODAJE_ENTORNO) { $env:RODAJE_ENTORNO } else { Join-Path $Raiz '.entorno' }
$Trabajo = Join-Path $Entorno 'codificadores-gpl'
if (-not $Salida) { $Salida = Join-Path $Raiz 'dist\complementos' }
New-Item -ItemType Directory -Force -Path $Trabajo, $Salida | Out-Null
if (-not (Get-Command git.exe -ErrorAction SilentlyContinue)) { Fallar 'Hace falta git para bajar el código fuente.' }

# 1. El zip de BtbN, con su huella.
$zip = Join-Path $Trabajo $ZipNombre
if (-not ((Test-Path -LiteralPath $zip) -and (Get-Item -LiteralPath $zip).Length -eq $ZipBytes)) {
    Paso "Descargando $ZipNombre (185 MB; si se corta, la próxima vez sigue donde quedó)..."
    & curl.exe --location --fail --silent --show-error --retry 3 --continue-at - --output "$zip.part" $ZipUrl
    if ($LASTEXITCODE -ne 0) { Fallar 'No se pudo descargar la compilación GPL de BtbN. Vuelve a ejecutar el script.' }
    Move-Item -LiteralPath "$zip.part" -Destination $zip -Force
}
Paso 'Comprobando la huella SHA-256...'
if ((Huella $zip) -ne $ZipSha256) {
    Remove-Item -LiteralPath $zip -Force
    Fallar 'El zip no coincide con la huella fijada y se borró. Si se repite, BtbN cambió el archivo: no se usa.'
}

# 2. Carpeta temporal del paquete.
$tmp = Join-Path $Trabajo ('armado.tmp-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
Get-ChildItem -LiteralPath $Trabajo -Directory | Where-Object { $_.Name -like 'armado.tmp-*' -or $_.Name -like 'git.tmp-*' } |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
$paquete = Join-Path $tmp 'paquete'
New-Item -ItemType Directory -Force -Path (Join-Path $paquete 'bin'), (Join-Path $paquete 'fuente') | Out-Null

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archivo = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    $interior = "ffmpeg-$FfmpegVersion-win64-gpl-9.0/"
    foreach ($par in @(@("${interior}bin/ffmpeg.exe", 'bin\ffmpeg.exe'), @("${interior}LICENSE.txt", 'COPYING.GPLv3.txt'))) {
        $entrada = $archivo.GetEntry($par[0])
        if (-not $entrada) { Fallar "El zip no trae $($par[0])." }
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entrada, (Join-Path $paquete $par[1]), $true)
    }
} finally { $archivo.Dispose() }

$ffmpeg = Join-Path $paquete 'bin\ffmpeg.exe'
$conf = (& $ffmpeg -hide_banner -buildconf) -join ' '
if ($conf -notmatch '--enable-gpl' -or $conf -match '--enable-nonfree') { Fallar 'El ffmpeg.exe no es la compilación GPL esperada (o es nonfree).' }
if ((& $ffmpeg -hide_banner -encoders) -join "`n" -notmatch 'libx264' ) { Fallar 'El ffmpeg.exe no trae libx264.' }
[System.IO.File]::WriteAllText((Join-Path $paquete 'configure.txt'), (((& $ffmpeg -hide_banner -buildconf) -join "`r`n") + "`r`n"))

# 3. Código fuente con git, comprobando el commit.
function Fuente([string]$nombre, [string]$repo, [string]$commit, [string]$destino) {
    $salidaTar = Join-Path $destino "$nombre-$($commit.Substring(0, 12)).tar.gz"
    if (Test-Path -LiteralPath $salidaTar) { return $salidaTar }
    Paso "Fuente de $nombre ($repo @ $($commit.Substring(0, 12)))..."
    $git = Join-Path $Trabajo ('git.tmp-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    try {
        & git.exe init --quiet $git
        & git.exe -C $git fetch --quiet --depth 1 $repo $commit
        if ($LASTEXITCODE -ne 0) { Fallar "No se pudo bajar $nombre de $repo." }
        $real = (& git.exe -C $git rev-parse FETCH_HEAD).Trim()
        if ($real -ne $commit) { Fallar "${nombre}: se esperaba el commit $commit y llegó $real." }
        & git.exe -C $git archive --format=tar.gz --prefix="$nombre/" -o "$salidaTar.part" FETCH_HEAD
        if ($LASTEXITCODE -ne 0) { Fallar "No se pudo empaquetar la fuente de $nombre." }
        Move-Item -LiteralPath "$salidaTar.part" -Destination $salidaTar -Force
    } finally {
        Remove-Item -LiteralPath $git -Recurse -Force -ErrorAction SilentlyContinue
    }
    return $salidaTar
}

$cacheFuente = Join-Path $Trabajo 'fuente'
New-Item -ItemType Directory -Force -Path $cacheFuente | Out-Null
$btbn = Fuente 'FFmpeg-Builds' 'https://github.com/BtbN/FFmpeg-Builds.git' $BtbnCommit $cacheFuente
$fuentes = @(
    (Fuente 'FFmpeg' 'https://github.com/FFmpeg/FFmpeg.git' $FfmpegCommit $cacheFuente),
    $btbn
)

# x264 y x265 en los commits que fijan los scripts de esa release.
$scripts = Join-Path $Trabajo 'scripts-btbn'
if (-not (Test-Path -LiteralPath (Join-Path $scripts 'scripts.d'))) {
    New-Item -ItemType Directory -Force -Path $scripts | Out-Null
    & tar.exe -xzf $btbn -C $scripts --strip-components=1
}
function Fijado([string]$archivo) {
    $texto = Get-Content -LiteralPath $archivo -Raw
    $pares = @()
    foreach ($n in @('', '2', '3')) {
        $r = [regex]::Match($texto, "(?m)^SCRIPT_REPO$n=`"([^`"]+)`"")
        $c = [regex]::Match($texto, "(?m)^SCRIPT_COMMIT$n=`"([0-9a-f]{40})`"")
        if ($r.Success -and $c.Success) { $pares += ,@($r.Groups[1].Value, $c.Groups[1].Value) }
    }
    return ,$pares
}
foreach ($lib in @('x264', 'x265')) {
    $par = (Fijado (Join-Path $scripts "scripts.d\50-$lib.sh"))[0]
    $fuentes += Fuente $lib $par[0] $par[1] $cacheFuente
}
foreach ($f in $fuentes) { Copy-Item -LiteralPath $f -Destination (Join-Path $paquete 'fuente') }

if ($FuenteCompleta) {
    $completa = Join-Path $Salida "rodaje-codificadores-gpl-$PaqueteVersion-fuente-completa"
    New-Item -ItemType Directory -Force -Path $completa | Out-Null
    foreach ($s in Get-ChildItem -LiteralPath (Join-Path $scripts 'scripts.d') -Recurse -Filter '*.sh') {
        $i = 0
        foreach ($par in (Fijado $s.FullName)) {
            $nombre = $s.BaseName -replace '^\d+-', ''
            if ($i -gt 0) { $nombre += "-$i" }
            Fuente $nombre $par[0] $par[1] $completa | Out-Null
            $i++
        }
    }
    Paso "Fuente completa en $completa (se publica junto al paquete)."
}

# 4. Manifiesto, avisos y lista de huellas.
$manifiesto = [ordered]@{
    formatVersion = 1; id = 'rodaje-codificadores-gpl'; version = $PaqueteVersion; kind = 'module'
    name = 'Codificadores GPL (x264 y x265)'
    description = 'Añade x264 y x265 en Exportar → Avanzado con un ffmpeg.exe GPL aparte. Incluye su código fuente y la licencia GPLv3.'
    author = 'Daniel (Dathinel); FFmpeg de BtbN/FFmpeg-Builds'; license = 'GPL-3.0-or-later'; apps = @('rodaje')
    module = [ordered]@{ entry = 'bin/ffmpeg.exe'; protocol = 0; functions = @('libx264', 'libx265'); runtime = 'cpu'; vramMb = 0 }
}
[System.IO.File]::WriteAllText((Join-Path $paquete 'paquete.json'), ($manifiesto | ConvertTo-Json -Depth 4), (New-Object System.Text.UTF8Encoding $false))

$lineas = @(
    '# Codificadores GPL para Rodaje',
    '',
    "ffmpeg.exe de la compilación win64-gpl-9.0 de BtbN/FFmpeg-Builds ($FfmpegVersion, release $BtbnRelease, zip SHA-256 $ZipSha256),",
    'sin modificar. Licencia: GNU GPL versión 3 o posterior (COPYING.GPLv3.txt); su línea de configure está en configure.txt.',
    'Lleva x264 y x265 (GPL-2.0-or-later) y otras bibliotecas libres compiladas dentro.',
    '',
    'Rodaje lo ejecuta como un proceso aparte por una tubería: no se enlaza con el editor.',
    '',
    '## Código fuente correspondiente',
    '',
    "- FFmpeg, commit $FfmpegCommit",
    "- BtbN/FFmpeg-Builds, commit $BtbnCommit (tag $BtbnRelease): los scripts fijan el commit de cada biblioteca",
    '- x264 y x265 en los commits de esos scripts',
    "- El resto de las bibliotecas: «rodaje-codificadores-gpl-$PaqueteVersion-fuente-completa», publicado junto a este paquete.",
    '',
    '## Huellas SHA-256',
    ''
)
foreach ($f in Get-ChildItem -LiteralPath $paquete -Recurse -File | Sort-Object FullName) {
    $lineas += "- ``$($f.FullName.Substring($paquete.Length + 1).Replace('\', '/'))`` $(Huella $f.FullName)"
}
[System.IO.File]::WriteAllLines((Join-Path $paquete 'LEEME.md'), $lineas, (New-Object System.Text.UTF8Encoding $false))

# 5. El .paquete (zip), entero o nada.
$destino = Join-Path $Salida "rodaje-codificadores-gpl-$PaqueteVersion.paquete"
Paso 'Comprimiendo el paquete...'
[System.IO.Compression.ZipFile]::CreateFromDirectory($paquete, "$destino.part", [System.IO.Compression.CompressionLevel]::Optimal, $false)
Move-Item -LiteralPath "$destino.part" -Destination $destino -Force
Remove-Item -LiteralPath $tmp -Recurse -Force
Paso "Listo: $destino ($([math]::Round((Get-Item -LiteralPath $destino).Length / 1MB)) MB, SHA-256 $(Huella $destino))."
