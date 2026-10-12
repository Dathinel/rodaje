# Compila las DLL compartidas LGPL de FFmpeg que usa Rodaje (con ffmpeg.exe y ffprobe.exe a juego): la variante
# win64-lgpl-shared de BtbN/FFmpeg-Builds en el mismo commit de scripts y de FFmpeg que la compilación publicada por BtbN,
# pero sin chromaprint (se enlaza con fftw3, GPL), libzvbi (tiene archivos GPL-2.0-only) ni los plugins de Little CMS
# (fast_float y threaded, GPL-3.0). En Docker.
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File build\compilar-ffmpeg-lgpl.ps1 [-SoloDescargar] [-Forzar]
#
# Deja en .entorno\ffmpeg-lgpl\: el zip que instala build\preparar-entorno.ps1 (con su .sha256), configure.txt,
# compilacion.json y rodaje-ffmpeg-lgpl-fuente.tar (la fuente correspondiente: un tarball por etapa, FFmpeg, los scripts
# de BtbN y estos scripts). Necesita Docker Desktop y unos 30 GB libres en su disco; la primera vez tarda horas.
# Resistente a apagones: lo bajado queda en el volumen rodaje-ffmpeg-lgpl y en la caché de buildx.

param([switch]$SoloDescargar, [switch]$Forzar)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$FfmpegCommit  = '2a571b606854520cf89804d8030c8b328e621689'
$BtbnRelease   = 'autobuild-2026-09-30-13-08'
$BtbnCommit    = '6c9aec5fc9a72ec3abedd1fa84db141fa18cf52b'
# Versión con la que se instala en .entorno\ffmpeg (la misma línea en build\preparar-entorno.ps1).
$Version       = 'n9.0.2-17-g2a571b6068-rodaje1'
$Quitar        = @('chromaprint', 'zvbi')
$Imagen        = 'ghcr.io/btbn/ffmpeg-builds/base-win64@sha256:f27c68da9e353b5fcef0396fb5d5112b969eb6c9fb02e8df484c0fea130016e7'
$Cli           = 'docker:29.1-cli'
$Contenedor    = 'rodaje-ffmpeg-lgpl'
$Volumen       = 'rodaje-ffmpeg-lgpl'

function Paso([string]$texto) { Write-Host "[ffmpeg-lgpl] $texto" }
function Fallar([string]$texto) { Write-Host ""; Write-Host $texto; exit 1 }
function Huella([string]$ruta) { (Get-FileHash -LiteralPath $ruta -Algorithm SHA256).Hash.ToLowerInvariant() }

$Raiz    = Split-Path -Parent $PSScriptRoot
$Entorno = if ($env:RODAJE_ENTORNO) { $env:RODAJE_ENTORNO } else { Join-Path $Raiz '.entorno' }
$Trabajo = Join-Path $Entorno 'ffmpeg-lgpl'
$Entrada = Join-Path $Trabajo 'entrada'
$Salida  = Join-Path $Trabajo 'salida'
New-Item -ItemType Directory -Force -Path $Entrada, $Salida | Out-Null

if (-not (Get-Command git.exe -ErrorAction SilentlyContinue)) { Fallar 'Hace falta git para bajar el código fuente.' }
if (-not (Get-Command docker.exe -ErrorAction SilentlyContinue)) { Fallar 'Hace falta Docker Desktop (con WSL 2).' }
& docker.exe info --format '{{.ServerVersion}}' *> $null
if ($LASTEXITCODE -ne 0) { Fallar 'Docker no responde: abre Docker Desktop y vuelve a ejecutar el script.' }

function Tarball([string]$nombre, [string]$repo, [string]$commit) {
    $salidaTar = Join-Path $Entrada "$nombre-$($commit.Substring(0, 12)).tar.gz"
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
$null = Tarball 'FFmpeg-Builds' 'https://github.com/BtbN/FFmpeg-Builds.git' $BtbnCommit
# FFmpeg lo clona build.sh dentro de Docker; este tarball del mismo commit es el que va en la fuente.
$tarFfmpeg = Tarball 'FFmpeg' 'https://github.com/FFmpeg/FFmpeg.git' $FfmpegCommit

& docker.exe volume create $Volumen | Out-Null
& docker.exe rm -f $Contenedor *> $null
function EnDocker([string]$accion) {
    & docker.exe run --rm --name $Contenedor --network host `
        -v '/var/run/docker.sock:/var/run/docker.sock' -v "${Volumen}:/var/lib/docker/volumes/$Volumen/_data" `
        -v "${Entrada}:/entrada:ro" -v "${Salida}:/salida" -v "${PSScriptRoot}:/rodaje:ro" `
        $Cli sh -c 'apk add --no-cache bash coreutils findutils git grep sed tar xz curl >/dev/null && bash /rodaje/compilar-ffmpeg-lgpl.sh $@' `
        sh $accion $Imagen $FfmpegCommit @Quitar
    if ($LASTEXITCODE -ne 0) { Fallar "Falló «$accion» dentro de Docker (código $LASTEXITCODE). Vuelve a ejecutar el script: lo bajado se conserva." }
}

Paso "Scripts de BtbN sin $($Quitar -join ' ni '), con la imagen y FFmpeg fijados..."
EnDocker 'preparar'
Paso 'Bajando la fuente de cada etapa (varios GB la primera vez)...'
EnDocker 'descargar'
$finDescarga = Get-Date
if ($SoloDescargar) { Paso 'Fuente bajada en el volumen; falta compilar.'; exit 0 }

$zipBtbn = Get-ChildItem -LiteralPath $Salida -Filter 'ffmpeg-*-win64-lgpl-shared-9.0.zip' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($Forzar -or -not $zipBtbn) {
    Get-ChildItem -LiteralPath $Salida -Filter '*.zip' | Remove-Item -Force
    Paso 'Compilando la imagen de BtbN y FFmpeg (horas la primera vez)...'
    EnDocker 'compilar'
    $zipBtbn = Get-ChildItem -LiteralPath $Salida -Filter 'ffmpeg-*-win64-lgpl-shared-9.0.zip' | Select-Object -First 1
}
$finCompilacion = Get-Date

# El zip con el nombre de la versión de Rodaje (carpeta interior incluida), que es lo que instala preparar-entorno.
$nombre = "ffmpeg-$Version-win64-lgpl-shared-9.0"
$zip = Join-Path $Trabajo "$nombre.zip"
$tmp = Join-Path $Trabajo 'zip.tmp'
Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::ExtractToDirectory($zipBtbn.FullName, $tmp)
$interior = Get-ChildItem -LiteralPath $tmp -Directory | Select-Object -First 1
Rename-Item -LiteralPath $interior.FullName -NewName $nombre
$bin = Join-Path $tmp "$nombre\bin"

$conf = (& (Join-Path $bin 'ffmpeg.exe') -hide_banner -buildconf) | ForEach-Object { $_.Trim() } | Where-Object { $_ -like '--*' }
$prohibidos = @('--enable-gpl', '--enable-nonfree', '--enable-chromaprint', '--enable-libzvbi', '--enable-libx264', '--enable-libx265',
    '--enable-libxvid', '--enable-frei0r', '--enable-librubberband', '--enable-libvidstab', '--enable-libdavs2', '--enable-libxavs2', '--enable-libfdk-aac')
$malos = @($conf | Where-Object { $prohibidos -contains $_ })
if ($malos.Count -or $conf -notcontains '--enable-version3') { Fallar "La configuración no es LGPL v3 sin GPL: $($malos -join ' ')" }
[System.IO.File]::WriteAllText((Join-Path $Trabajo 'configure.txt'), (($conf -join ' ') + "`r`n"))

Remove-Item -LiteralPath "$zip.part" -Force -ErrorAction SilentlyContinue
[System.IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $tmp $nombre), "$zip.part", [System.IO.Compression.CompressionLevel]::Optimal, $true)
Move-Item -LiteralPath "$zip.part" -Destination $zip -Force
Remove-Item -LiteralPath $tmp -Recurse -Force
$huellaZip = Huella $zip
"$huellaZip  $(Split-Path -Leaf $zip)" | Set-Content -LiteralPath "$zip.sha256" -Encoding ascii

# Fuente correspondiente: los tarballs de cada etapa tal como los montó el Dockerfile, FFmpeg, BtbN y estos scripts.
Paso 'Empaquetando la fuente correspondiente...'
$tarFuente = Join-Path $Trabajo 'rodaje-ffmpeg-lgpl-fuente.tar'
$armado = Join-Path $Trabajo 'fuente.tmp'
Remove-Item -LiteralPath $armado -Recurse -Force -ErrorAction SilentlyContinue
$raizFuente = Join-Path $armado 'rodaje-ffmpeg-lgpl-fuente'
New-Item -ItemType Directory -Force -Path (Join-Path $raizFuente 'etapas'), (Join-Path $raizFuente 'build') | Out-Null
Copy-Item -LiteralPath (Get-ChildItem -LiteralPath (Join-Path $Salida 'fuente') -File).FullName -Destination (Join-Path $raizFuente 'etapas')
Copy-Item -LiteralPath (Get-ChildItem -LiteralPath $Entrada -File -Filter '*.tar.gz').FullName -Destination $raizFuente
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'compilar-ffmpeg-lgpl.ps1'), (Join-Path $PSScriptRoot 'compilar-ffmpeg-lgpl.sh') -Destination (Join-Path $raizFuente 'build')
Copy-Item -LiteralPath (Join-Path $Salida 'Dockerfile.generado') -Destination $raizFuente
Copy-Item -LiteralPath (Join-Path $Trabajo 'configure.txt') -Destination $raizFuente
$leeme = @(
    "Fuente correspondiente de las DLL LGPL de FFmpeg de Rodaje ($nombre.zip, SHA-256 $huellaZip).",
    '',
    "- FFmpeg-*.tar.gz: FFmpeg en el commit $FfmpegCommit (rama release/9.0).",
    "- FFmpeg-Builds-*.tar.gz: scripts de BtbN/FFmpeg-Builds en el commit $BtbnCommit (tag $BtbnRelease), sin modificar.",
    '- etapas/: el árbol de cada biblioteca tal como lo bajó download.sh de BtbN y lo montó el Dockerfile generado',
    '  (Dockerfile.generado); el nombre lleva la huella del comando de descarga.',
    "- build/: los scripts que aplican los cambios sobre BtbN (sin $($Quitar -join ' ni '), imagen y FFmpeg fijados) y compilan.",
    '',
    'Cadena de herramientas (imagen pública de BtbN, no se redistribuye):',
    "  $Imagen",
    '',
    'Para repetirla en Windows con Docker Desktop: build\compilar-ffmpeg-lgpl.ps1.',
    '',
    'Huellas SHA-256:'
)
$leeme += Get-ChildItem -LiteralPath $raizFuente -Recurse -File | Sort-Object FullName | ForEach-Object {
    "$(Huella $_.FullName)  $($_.FullName.Substring($raizFuente.Length + 1).Replace('\', '/'))"
}
[System.IO.File]::WriteAllLines((Join-Path $raizFuente 'LEEME.txt'), [string[]]$leeme, (New-Object System.Text.UTF8Encoding $false))
& tar.exe -cf "$tarFuente.part" -C $armado 'rodaje-ffmpeg-lgpl-fuente'
if ($LASTEXITCODE -ne 0) { Fallar 'No se pudo empaquetar la fuente.' }
Move-Item -LiteralPath "$tarFuente.part" -Destination $tarFuente -Force
Remove-Item -LiteralPath $armado -Recurse -Force
"$(Huella $tarFuente)  $(Split-Path -Leaf $tarFuente)" | Set-Content -LiteralPath "$tarFuente.sha256" -Encoding ascii

$registro = [ordered]@{
    version = $Version; zip = Split-Path -Leaf $zip; zipBytes = (Get-Item -LiteralPath $zip).Length; zipSha256 = $huellaZip
    fuenteBytes = (Get-Item -LiteralPath $tarFuente).Length; quitadas = $Quitar; imagen = $Imagen
    ffmpegCommit = $FfmpegCommit; btbnCommit = $BtbnCommit
    descargaSegundos = [int]($finDescarga - $inicio).TotalSeconds; compilacionSegundos = [int]($finCompilacion - $finDescarga).TotalSeconds
}
[System.IO.File]::WriteAllText((Join-Path $Trabajo 'compilacion.json'), ($registro | ConvertTo-Json), (New-Object System.Text.UTF8Encoding $false))
Paso "Listo: $zip ($([math]::Round($registro.zipBytes / 1MB, 1)) MB, SHA-256 $huellaZip)."
Paso "Fuente: $tarFuente ($([math]::Round($registro.fuenteBytes / 1MB)) MB). Compilación: $([math]::Round($registro.compilacionSegundos / 60)) min."
