# Compila el ffmpeg.exe GPL del complemento rodaje-codificadores-gpl (D-034) con los scripts de BtbN/FFmpeg-Builds en el
# mismo commit que la compilación LGPL, en Docker: FFmpeg 9.0.2 (commit de la LGPL) + x264 + x265 + libopus y libmp3lame
# (los codificadores de audio de la exportación que no son nativos de FFmpeg) y sus dependencias. Nada más.
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File build\compilar-ffmpeg-gpl.ps1 [-Forzar]
#
# Deja en .entorno\ffmpeg-gpl\: fuente\ (un tarball por pieza, exactamente lo que se compila), salida\ (ffmpeg.exe,
# configure.txt, COPYING.GPLv3.txt, SHA256SUMS), compilacion.json (tiempos y huellas) y rodaje-ffmpeg-gpl-fuente.tar
# (la fuente correspondiente para adjuntar a la release del complemento). Solo hace falta Docker (Desktop
# con WSL 2); no instala nada global. Resistente a apagones: lo bajado se conserva y cada archivo se escribe como .part.

param([switch]$Forzar)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$FfmpegVersion = 'n9.0.2-17-g2a571b6068'
$FfmpegCommit  = '2a571b606854520cf89804d8030c8b328e621689'
$BtbnRelease   = 'autobuild-2026-09-30-13-08'
$BtbnCommit    = '6c9aec5fc9a72ec3abedd1fa84db141fa18cf52b'
# Cadena de herramientas de BtbN (GCC 16.2 de crosstool-NG con mingw-w64), fijada por huella.
$Imagen        = 'ghcr.io/btbn/ffmpeg-builds/base-win64@sha256:f27c68da9e353b5fcef0396fb5d5112b969eb6c9fb02e8df484c0fea130016e7'
$Contenedor    = 'rodaje-ffmpeg-gpl'

function Paso([string]$texto) { Write-Host "[ffmpeg-gpl] $texto" }
function Fallar([string]$texto) { Write-Host ""; Write-Host $texto; exit 1 }
function Huella([string]$ruta) { (Get-FileHash -LiteralPath $ruta -Algorithm SHA256).Hash.ToLowerInvariant() }

$Raiz    = Split-Path -Parent $PSScriptRoot
$Entorno = if ($env:RODAJE_ENTORNO) { $env:RODAJE_ENTORNO } else { Join-Path $Raiz '.entorno' }
$Trabajo = Join-Path $Entorno 'ffmpeg-gpl'
$Fuente  = Join-Path $Trabajo 'fuente'
$Salida  = Join-Path $Trabajo 'salida'
$Btbn    = Join-Path $Trabajo 'btbn'
New-Item -ItemType Directory -Force -Path $Fuente, $Salida | Out-Null

if (-not (Get-Command git.exe -ErrorAction SilentlyContinue)) { Fallar 'Hace falta git para bajar el código fuente.' }
if (-not (Get-Command docker.exe -ErrorAction SilentlyContinue)) {
    Fallar 'Hace falta Docker Desktop (con WSL 2). Sin Docker, los mismos pasos de build\compilar-ffmpeg-gpl.sh corren en una distribución de WSL con la cadena de BtbN, pero no están automatizados.'
}
& docker.exe info --format '{{.ServerVersion}}' *> $null
if ($LASTEXITCODE -ne 0) { Fallar 'Docker no responde: abre Docker Desktop y vuelve a ejecutar el script.' }

# Tarball de un repositorio en un commit, sin conversión de fin de línea; el hash de git es la huella del contenido.
function Tarball([string]$nombre, [string]$repo, [string]$commit) {
    $salidaTar = Join-Path $Fuente "$nombre-$($commit.Substring(0, 12)).tar.gz"
    if (Test-Path -LiteralPath $salidaTar) { return $salidaTar }
    Paso "Fuente de $nombre ($repo @ $($commit.Substring(0, 12)))..."
    $git = Join-Path $Trabajo ('git.tmp-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    try {
        & git.exe init --quiet $git
        & git.exe -C $git fetch --quiet --depth 1 $repo $commit
        if ($LASTEXITCODE -ne 0) { Fallar "No se pudo bajar $nombre de $repo." }
        $real = (& git.exe -C $git rev-parse FETCH_HEAD).Trim()
        if ($real -ne $commit) { Fallar "${nombre}: se esperaba el commit $commit y llegó $real." }
        & git.exe -C $git -c core.autocrlf=false archive --format=tar.gz --prefix="$nombre/" -o "$salidaTar.part" FETCH_HEAD
        if ($LASTEXITCODE -ne 0) { Fallar "No se pudo empaquetar la fuente de $nombre." }
        Move-Item -LiteralPath "$salidaTar.part" -Destination $salidaTar -Force
    } finally {
        Remove-Item -LiteralPath $git -Recurse -Force -ErrorAction SilentlyContinue
    }
    return $salidaTar
}

$inicio = Get-Date
Get-ChildItem -LiteralPath $Trabajo -Directory | Where-Object Name -like 'git.tmp-*' | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
$tarBtbn   = Tarball 'FFmpeg-Builds' 'https://github.com/BtbN/FFmpeg-Builds.git' $BtbnCommit
$tarFfmpeg = Tarball 'FFmpeg' 'https://github.com/FFmpeg/FFmpeg.git' $FfmpegCommit
if (-not (Test-Path -LiteralPath (Join-Path $Btbn 'scripts.d'))) {
    $tmpBtbn = "$Btbn.tmp"
    Remove-Item -LiteralPath $tmpBtbn -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $tmpBtbn | Out-Null
    & tar.exe -xzf $tarBtbn -C $tmpBtbn --strip-components=1
    if ($LASTEXITCODE -ne 0) { Fallar 'No se pudieron extraer los scripts de BtbN.' }
    Move-Item -LiteralPath $tmpBtbn -Destination $Btbn
}

Paso 'Imagen de la cadena de herramientas de BtbN (unos 8 GB la primera vez)...'
& docker.exe pull --quiet $Imagen | Out-Null
if ($LASTEXITCODE -ne 0) { Fallar "No se pudo bajar $Imagen." }

& docker.exe rm -f $Contenedor *> $null
function EnDocker([string]$accion) {
    & docker.exe run --rm --name $Contenedor --memory $(if ($env:RODAJE_MEMORIA) { $env:RODAJE_MEMORIA } else { '6g' }) `
        -v "${Btbn}:/btbn:ro" -v "${Fuente}:/fuente" -v "${Salida}:/salida" -v "${PSScriptRoot}:/rodaje:ro" `
        $Imagen bash /rodaje/compilar-ffmpeg-gpl.sh $accion $FfmpegVersion (Split-Path -Leaf $tarFfmpeg)
    if ($LASTEXITCODE -ne 0) { Fallar "Falló «$accion» dentro de Docker (código $LASTEXITCODE). Vuelve a ejecutar el script: lo bajado se conserva." }
}

Paso 'Bajando la fuente de cada biblioteca en el commit que fijan los scripts de BtbN...'
EnDocker 'descargar'
$finDescarga = Get-Date

$exe = Join-Path $Salida 'ffmpeg.exe'
if ($Forzar -or -not (Test-Path -LiteralPath $exe)) {
    Remove-Item -LiteralPath $exe -Force -ErrorAction SilentlyContinue
    Paso 'Compilando (x265 son tres compilaciones: 8, 10 y 12 bits)...'
    EnDocker 'compilar'
}
$finCompilacion = Get-Date

$conf = & $exe -hide_banner -buildconf
if ($LASTEXITCODE -ne 0) { Fallar 'El ffmpeg.exe compilado no arranca.' }
[System.IO.File]::WriteAllText((Join-Path $Salida 'configure.txt'), (($conf -join "`r`n") + "`r`n"))
$version = ((& $exe -hide_banner -version) | Select-Object -First 1)

$sumas = foreach ($f in @(Get-ChildItem -LiteralPath $Salida -File) + @(Get-ChildItem -LiteralPath $Fuente -File) | Where-Object { $_.Name -ne 'SHA256SUMS' -and $_.Name -notlike '*.part' } | Sort-Object Name) {
    "$(Huella $f.FullName)  $(Split-Path -Leaf $f.DirectoryName)/$($f.Name)"
}
[System.IO.File]::WriteAllLines((Join-Path $Salida 'SHA256SUMS'), [string[]]$sumas, (New-Object System.Text.UTF8Encoding $false))

$registro = [ordered]@{
    version = $version; ffmpegCommit = $FfmpegCommit; btbnCommit = $BtbnCommit; btbnRelease = $BtbnRelease; imagen = $Imagen
    exeBytes = (Get-Item -LiteralPath $exe).Length; exeSha256 = Huella $exe
    fuenteBytes = (Get-ChildItem -LiteralPath $Fuente -File | Measure-Object Length -Sum).Sum
    descargaSegundos = [int]($finDescarga - $inicio).TotalSeconds; compilacionSegundos = [int]($finCompilacion - $finDescarga).TotalSeconds
}
[System.IO.File]::WriteAllText((Join-Path $Trabajo 'compilacion.json'), ($registro | ConvertTo-Json), (New-Object System.Text.UTF8Encoding $false))

# Fuente correspondiente en un solo .tar para adjuntarlo a la release del complemento (GPLv3 §6): los tarballs tal cual
# se compilaron, estos dos scripts, las huellas y cómo repetir la compilación.
Paso 'Empaquetando la fuente correspondiente...'
$tarFuente = Join-Path $Trabajo 'rodaje-ffmpeg-gpl-fuente.tar'
$armado = Join-Path $Trabajo 'fuente.tmp'
Remove-Item -LiteralPath $armado -Recurse -Force -ErrorAction SilentlyContinue
$raizFuente = Join-Path $armado 'rodaje-ffmpeg-gpl-fuente'
New-Item -ItemType Directory -Force -Path (Join-Path $raizFuente 'build') | Out-Null
Copy-Item -LiteralPath (Get-ChildItem -LiteralPath $Fuente -File | Where-Object Name -notlike '*.part').FullName -Destination $raizFuente
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'compilar-ffmpeg-gpl.ps1'), (Join-Path $PSScriptRoot 'compilar-ffmpeg-gpl.sh') -Destination (Join-Path $raizFuente 'build')
Copy-Item -LiteralPath (Join-Path $Salida 'configure.txt'), (Join-Path $Salida 'COPYING.GPLv3.txt') -Destination $raizFuente
$leeme = @(
    "Fuente correspondiente del ffmpeg.exe GPL de rodaje-codificadores-gpl ($version).",
    '',
    "ffmpeg.exe: $($registro.exeBytes) bytes, SHA-256 $($registro.exeSha256).",
    "Licencia del conjunto: GNU GPL versión 3 o posterior (COPYING.GPLv3.txt).",
    '',
    'Contenido (un tarball por pieza, exactamente el árbol que se compiló):',
    "- FFmpeg-*.tar.gz: FFmpeg en el commit $FfmpegCommit (rama release/9.0).",
    "- FFmpeg-Builds-*.tar.gz: scripts de BtbN/FFmpeg-Builds en el commit $BtbnCommit (tag $BtbnRelease).",
    '- 10-mingw, 10-mingw-std-threads, 20-libiconv, 50-x264, 50-x265, 50-libopus, 50-libmp3lame (.tar.xz):',
    '  cada biblioteca en el commit que fija su script de scripts.d, sin .git ni .svn.',
    '- build/compilar-ffmpeg-gpl.ps1 y build/compilar-ffmpeg-gpl.sh: los scripts que controlan la compilación.',
    '',
    'Cadena de herramientas (no se redistribuye; es la imagen pública de BtbN):',
    "  $Imagen",
    "  $((Get-Content -LiteralPath (Join-Path $Salida 'compilador.txt') -Raw).Trim()). libgcc y libstdc++ se enlazan estáticos bajo la",
    '  excepción de bibliotecas de ejecución de GCC; el CRT de mingw-w64 y winpthreads salen del tarball 10-mingw.',
    '',
    'Para repetirla en Windows con Docker Desktop: coloca los tarballs en .entorno\ffmpeg-gpl\fuente\ y ejecuta',
    'build\compilar-ffmpeg-gpl.ps1 (no vuelve a bajar lo que ya está).',
    '',
    'Huellas SHA-256:'
)
$leeme += Get-ChildItem -LiteralPath $raizFuente -Recurse -File | Sort-Object FullName | ForEach-Object {
    "$(Huella $_.FullName)  $($_.FullName.Substring($raizFuente.Length + 1).Replace('\', '/'))"
}
[System.IO.File]::WriteAllLines((Join-Path $raizFuente 'LEEME.txt'), [string[]]$leeme, (New-Object System.Text.UTF8Encoding $false))
& tar.exe -cf "$tarFuente.part" -C $armado 'rodaje-ffmpeg-gpl-fuente'
if ($LASTEXITCODE -ne 0) { Fallar 'No se pudo empaquetar la fuente.' }
Move-Item -LiteralPath "$tarFuente.part" -Destination $tarFuente -Force
Remove-Item -LiteralPath $armado -Recurse -Force
"$(Huella $tarFuente)  $(Split-Path -Leaf $tarFuente)" | Set-Content -LiteralPath "$tarFuente.sha256" -Encoding ascii
Paso "Listo: $exe ($([math]::Round($registro.exeBytes / 1MB, 1)) MB, SHA-256 $($registro.exeSha256))."
Paso "Fuente: $tarFuente ($([math]::Round((Get-Item -LiteralPath $tarFuente).Length / 1MB, 1)) MB). Compilación: $([math]::Round($registro.compilacionSegundos / 60, 1)) min."
