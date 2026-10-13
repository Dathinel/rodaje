# Arma el complemento rodaje-codificadores-gpl: el ffmpeg.exe GPL propio (build\compilar-ffmpeg-gpl.ps1, D-034), la GPLv3,
# su línea de configure, los avisos de cada biblioteca (licencias\, sacados de la fuente) y un LEEME con la fuente y una
# nota de patentes, como .paquete instalable (módulo con protocol 0) con su .sha256, y deja al lado la fuente
# correspondiente para adjuntarla a la misma release. No publica nada.
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File build\armar-codificadores-gpl.ps1 [-Salida <carpeta>]
#
# Antes de empaquetar comprueba que el exe es el compilado (SHA256SUMS), que -buildconf solo activa lo previsto y que
# x264 y x265 codifican en 8 y 10 bits. Resistente a apagones: todo se arma en una carpeta temporal y se renombra.

param([string]$Salida)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$PaqueteVersion = '0.2.0'
# Lo único que puede activar el configure (además de lo que FFmpeg detecta de Windows); si cambia, cambia la fuente.
$Activados = @('--enable-gpl', '--enable-version3', '--enable-pthreads', '--enable-iconv',
    '--enable-libx264', '--enable-libx265', '--enable-libopus', '--enable-libmp3lame')
$Codificadores = @('libx264', 'libx265', 'libopus', 'libmp3lame', 'aac')
$Repositorio = 'https://github.com/Dathinel/rodaje-codificadores-gpl'
# Avisos que van en licencias\ del paquete, sacados de la fuente que se compiló: tarball, ruta dentro, nombre en el paquete.
$Avisos = @(
    @('FFmpeg-2a571b606854.tar.gz', 'FFmpeg/LICENSE.md', 'FFmpeg-LICENSE.md'),
    @('FFmpeg-2a571b606854.tar.gz', 'FFmpeg/CREDITS', 'FFmpeg-CREDITS.txt'),
    @('50-x264.tar.xz', './COPYING', 'x264-COPYING.txt'),
    @('50-x264.tar.xz', './AUTHORS', 'x264-AUTHORS.txt'),
    @('50-x265.tar.xz', './COPYING', 'x265-COPYING.txt'),
    @('50-x265.tar.xz', './source/dynamicHDR10/LICENSE.txt', 'x265-dynamicHDR10-LICENSE.txt'),
    @('50-x265.tar.xz', './source/dynamicHDR10/json11/LICENSE.txt', 'x265-json11-LICENSE.txt'),
    @('50-libopus.tar.xz', './COPYING', 'libopus-COPYING.txt'),
    @('50-libopus.tar.xz', './LICENSE_PLEASE_READ.txt', 'libopus-LICENSE_PLEASE_READ.txt'),
    @('50-libopus.tar.xz', './AUTHORS', 'libopus-AUTHORS.txt'),
    @('50-libmp3lame.tar.xz', './COPYING', 'lame-COPYING.txt'),
    @('50-libmp3lame.tar.xz', './LICENSE', 'lame-LICENSE.txt'),
    @('20-libiconv.tar.xz', './COPYING.LIB', 'libiconv-COPYING.LIB.txt'),
    @('20-libiconv.tar.xz', './AUTHORS', 'libiconv-AUTHORS.txt'),
    @('10-mingw.tar.xz', './COPYING', 'mingw-w64-COPYING.txt'),
    @('10-mingw.tar.xz', './COPYING.MinGW-w64-runtime/COPYING.MinGW-w64-runtime.txt', 'mingw-w64-runtime-COPYING.txt'),
    @('10-mingw.tar.xz', './DISCLAIMER', 'mingw-w64-DISCLAIMER.txt'),
    @('10-mingw.tar.xz', './DISCLAIMER.PD', 'mingw-w64-DISCLAIMER.PD.txt'),
    @('10-mingw.tar.xz', './mingw-w64-libraries/winpthreads/COPYING', 'winpthreads-COPYING.txt'),
    @('10-mingw-std-threads.tar.xz', './LICENSE', 'mingw-std-threads-LICENSE.txt')
)
# GCC 16.2.0 (la de la imagen de BtbN): libgcc y libstdc++ entran estáticas con esta excepción; el texto está en el repositorio.
$ExcepcionGcc = @('codificadores-gpl\gcc-COPYING.RUNTIME.txt', '9d6b43ce4d8de0c878bf16b54d8e7a10d9bd42b75178153e3af6a815bdc90f74')

function Paso([string]$texto) { Write-Host "[codificadores-gpl] $texto" }
function Fallar([string]$texto) { Write-Host ""; Write-Host $texto; exit 1 }
function Huella([string]$ruta) { (Get-FileHash -LiteralPath $ruta -Algorithm SHA256).Hash.ToLowerInvariant() }
# Con fin de línea LF, para que «sha256sum -c» también lo lea.
function Escribir-Suma([string]$ruta, [string]$huella) {
    [System.IO.File]::WriteAllText("$ruta.sha256", "$huella  $(Split-Path -Leaf $ruta)`n", (New-Object System.Text.UTF8Encoding $false))
}

$Raiz    = Split-Path -Parent $PSScriptRoot
$Entorno = if ($env:RODAJE_ENTORNO) { $env:RODAJE_ENTORNO } else { Join-Path $Raiz '.entorno' }
$Gpl     = Join-Path $Entorno 'ffmpeg-gpl'
$Trabajo = Join-Path $Entorno 'codificadores-gpl'
if (-not $Salida) { $Salida = Join-Path $Raiz 'dist\complementos' }
New-Item -ItemType Directory -Force -Path $Trabajo, $Salida | Out-Null

# 1. El exe propio y su fuente; si faltan, se compilan.
$exe = Join-Path $Gpl 'salida\ffmpeg.exe'
$tarFuente = Join-Path $Gpl 'rodaje-ffmpeg-gpl-fuente.tar'
if (-not ((Test-Path -LiteralPath $exe) -and (Test-Path -LiteralPath $tarFuente))) {
    Paso 'No está compilado: ejecutando build\compilar-ffmpeg-gpl.ps1...'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'compilar-ffmpeg-gpl.ps1')
    if ($LASTEXITCODE -ne 0) { Fallar 'No se pudo compilar el ffmpeg.exe GPL.' }
}
$sumas = Get-Content -LiteralPath (Join-Path $Gpl 'salida\SHA256SUMS')
if (-not ($sumas -contains "$(Huella $exe)  salida/ffmpeg.exe")) { Fallar 'ffmpeg.exe no coincide con el SHA256SUMS de su compilación.' }
$huellaFuente = ((Get-Content -LiteralPath "$tarFuente.sha256" -Raw) -split '\s+')[0]
if ((Huella $tarFuente) -ne $huellaFuente) { Fallar 'La fuente no coincide con su .sha256: vuelve a ejecutar build\compilar-ffmpeg-gpl.ps1.' }

# 2. Solo lo previsto: GPL, sin nonfree, exactamente las bibliotecas externas de la lista.
Paso 'Comprobando -buildconf y los codificadores...'
$conf = (& $exe -hide_banner -buildconf) | ForEach-Object { $_.Trim() } | Where-Object { $_ -like '--*' }
$sobran = @($conf | Where-Object { $_ -like '--enable-*' -and $Activados -notcontains $_ })
$faltan = @($Activados | Where-Object { $conf -notcontains $_ })
if ($sobran.Count -or $faltan.Count) { Fallar "El configure no es el previsto. Sobra: $($sobran -join ' '). Falta: $($faltan -join ' ')." }
$lista = (& $exe -hide_banner -encoders) -join "`n"
foreach ($c in $Codificadores) { if ($lista -notmatch "\s$([regex]::Escape($c))\s") { Fallar "El ffmpeg.exe no trae $c." } }
foreach ($prueba in @(@('libx264', 'yuv420p'), @('libx264', 'yuv420p10le'), @('libx265', 'yuv420p'), @('libx265', 'yuv420p10le'))) {
    & $exe -hide_banner -loglevel error -f lavfi -i 'testsrc2=size=320x180:rate=30:duration=0.5' -pix_fmt $prueba[1] -c:v $prueba[0] -f null NUL
    if ($LASTEXITCODE -ne 0) { Fallar "$($prueba[0]) no codifica en $($prueba[1])." }
}

# 3. Carpeta temporal del paquete.
Get-ChildItem -LiteralPath $Trabajo -Directory | Where-Object Name -like 'armado.tmp-*' | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
$tmp = Join-Path $Trabajo ('armado.tmp-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$paquete = Join-Path $tmp 'paquete'
New-Item -ItemType Directory -Force -Path (Join-Path $paquete 'bin') | Out-Null
Copy-Item -LiteralPath $exe -Destination (Join-Path $paquete 'bin\ffmpeg.exe')
Copy-Item -LiteralPath (Join-Path $Gpl 'salida\COPYING.GPLv3.txt') -Destination $paquete
Copy-Item -LiteralPath (Join-Path $Gpl 'salida\configure.txt') -Destination $paquete
$version = (& $exe -hide_banner -version | Select-Object -First 1)
$nombreFuente = "rodaje-codificadores-gpl-$PaqueteVersion-fuente.tar"
$urlFuente = "$Repositorio/releases/download/v$PaqueteVersion/$nombreFuente"

$licencias = Join-Path $paquete 'licencias'
$extraido = Join-Path $tmp 'extraido'
New-Item -ItemType Directory -Force -Path $licencias, $extraido | Out-Null
$tar = Join-Path $env:SystemRoot 'System32\tar.exe'
foreach ($a in $Avisos) {
    $origen = Join-Path $Gpl "fuente\$($a[0])"
    if (-not (Test-Path -LiteralPath $origen)) { $origen = Join-Path $Gpl $a[0] }
    if (-not (Test-Path -LiteralPath $origen)) { Fallar "Falta $($a[0]) en $Gpl (vuelve a ejecutar build\compilar-ffmpeg-gpl.ps1)." }
    & $tar -xf $origen -C $extraido $a[1]
    $texto = Join-Path $extraido ($a[1] -replace '^\./', '' -replace '/', '\')
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $texto)) { Fallar "No encuentro $($a[1]) en $($a[0])." }
    Copy-Item -LiteralPath $texto -Destination (Join-Path $licencias $a[2])
}
$gcc = Join-Path $PSScriptRoot $ExcepcionGcc[0]
if ((Huella $gcc) -ne $ExcepcionGcc[1]) { Fallar "$gcc no es el texto fijado de la excepción de GCC." }
Copy-Item -LiteralPath $gcc -Destination (Join-Path $licencias 'gcc-COPYING.RUNTIME.txt')
Remove-Item -LiteralPath $extraido -Recurse -Force

$manifiesto = [ordered]@{
    formatVersion = 1; id = 'rodaje-codificadores-gpl'; version = $PaqueteVersion; kind = 'module'
    name = 'Codificadores GPL (x264 y x265)'
    description = 'Añade x264 y x265 (H.264 y HEVC, 8 y 10 bits) en Exportar → Avanzado con un ffmpeg.exe GPL aparte. Trae sus licencias (licencias/) y el LEEME con su código fuente y una nota sobre patentes.'
    author = 'Dathinel; FFmpeg compilado con los scripts de BtbN/FFmpeg-Builds'; license = 'GPL-3.0-or-later'
    repository = $Repositorio; apps = @('rodaje')
    module = [ordered]@{ entry = 'bin/ffmpeg.exe'; protocol = 0; functions = @('libx264', 'libx265'); runtime = 'cpu'; vramMb = 0 }
}
[System.IO.File]::WriteAllText((Join-Path $paquete 'paquete.json'), ($manifiesto | ConvertTo-Json -Depth 4), (New-Object System.Text.UTF8Encoding $false))

$leeme = @'
# Codificadores GPL para Rodaje

{VERSION}, compilado para Rodaje con `build\compilar-ffmpeg-gpl.ps1` (D-034). Añade x264 y x265 en Exportar → Avanzado.
Rodaje lo ejecuta como un proceso aparte por una tubería: no se enlaza con el editor.

## Licencias

El conjunto es **GNU GPL versión 3 o posterior** (`COPYING.GPLv3.txt`): FFmpeg se configuró con `--enable-gpl
--enable-version3` (línea completa en `configure.txt`). Dentro del exe, enlazado de forma estática:

| Pieza | Licencia | Aviso en `licencias/` |
|---|---|---|
| FFmpeg (commit `2a571b6`) | GPL-3.0-or-later por la configuración; LGPL/GPL según el archivo | `FFmpeg-LICENSE.md`, `FFmpeg-CREDITS.txt` |
| x264 | GPL-2.0-or-later | `x264-COPYING.txt`, `x264-AUTHORS.txt` |
| x265 (8, 10 y 12 bits, HDR10+) | GPL-2.0-or-later; json11, MIT | `x265-COPYING.txt`, `x265-dynamicHDR10-LICENSE.txt`, `x265-json11-LICENSE.txt` |
| libopus | BSD-3-Clause | `libopus-COPYING.txt`, `libopus-LICENSE_PLEASE_READ.txt`, `libopus-AUTHORS.txt` |
| LAME (libmp3lame), sin modificar | LGPL-2.0-or-later | `lame-COPYING.txt`, `lame-LICENSE.txt` |
| libiconv (con gnulib) | LGPL-2.1-or-later | `libiconv-COPYING.LIB.txt`, `libiconv-AUTHORS.txt` |
| CRT de mingw-w64 y winpthreads | Aviso de tipo BSD del runtime, con las piezas que lista (getopt, gdtoa, matemáticas…); winpthreads, MIT; el proyecto, ZPL-2.1 | `mingw-w64-COPYING.txt`, `mingw-w64-runtime-COPYING.txt`, `mingw-w64-DISCLAIMER*.txt`, `winpthreads-COPYING.txt` |
| mingw-std-threads | BSD-2-Clause | `mingw-std-threads-LICENSE.txt` |
| libgcc y libstdc++ (GCC 16.2.0) | GPL-3.0 con la GCC Runtime Library Exception 3.1 | `gcc-COPYING.RUNTIME.txt` y `COPYING.GPLv3.txt` |

LAME pide reconocer su uso: este paquete usa LAME, https://lame.sourceforge.io/.

## Código fuente correspondiente

`{FUENTE}` (SHA-256 `{HUELLA}`), en la misma release que este paquete:

{URL}

Trae FFmpeg, los scripts de BtbN/FFmpeg-Builds y cada biblioteca en el árbol exacto que se compiló (mingw-w64 con su
CRT y winpthreads, mingw-std-threads, libiconv con gnulib, x264, x265, libopus con sus modelos y LAME), los scripts
de compilación y las instrucciones para repetirla (`LEEME.txt`). El compilador no se redistribuye: es la imagen pública
`base-win64` de BtbN, citada por huella. El código de Rodaje está en https://github.com/Dathinel/rodaje.

## Patentes (nota informativa, no es consejo legal)

H.264/AVC y H.265/HEVC son formatos con patentes que se licencian en pools (Via LA para AVC; Access Advance y Via LA
para HEVC). Las licencias de copyright de este paquete no conceden licencias de patentes, y codificar con x264 o x265
puede requerir una según el país y el uso, en especial si es comercial. Por eso Rodaje no trae este complemento por
defecto y nunca lo elige solo: cada quien decide si lo instala y responde por su uso. Opus tiene licencias de patentes sin
regalías declaradas ante la IETF, y el programa de licencias de MP3 terminó en 2017; tampoco esto es una garantía.

## Garantía

Ninguna, en la medida que lo permita la ley (GPLv3, secciones 15 y 16).

## Huellas SHA-256

'@
$lineas = @(($leeme -replace '\{VERSION\}', $version -replace '\{FUENTE\}', $nombreFuente -replace '\{HUELLA\}', $huellaFuente -replace '\{URL\}', $urlFuente) -split "`r?`n")
foreach ($f in Get-ChildItem -LiteralPath $paquete -Recurse -File | Sort-Object FullName) {
    $lineas += "- ``$($f.FullName.Substring($paquete.Length + 1).Replace('\', '/'))`` $(Huella $f.FullName)"
}
[System.IO.File]::WriteAllLines((Join-Path $paquete 'LEEME.md'), $lineas, (New-Object System.Text.UTF8Encoding $false))

# 4. El .paquete (zip) y la fuente al lado, enteros o nada.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$destino = Join-Path $Salida "rodaje-codificadores-gpl-$PaqueteVersion.paquete"
Paso 'Comprimiendo el paquete...'
# Como empaquetar-modulo.ps1: rutas con «/», orden y fecha fijos (CreateFromDirectory de PowerShell 5.1 escribe «\»).
if (Test-Path -LiteralPath "$destino.part") { Remove-Item -LiteralPath "$destino.part" -Force }
$zip = [System.IO.Compression.ZipFile]::Open("$destino.part", [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($f in Get-ChildItem -LiteralPath $paquete -Recurse -File | Sort-Object { $_.FullName.Substring($paquete.Length + 1).Replace('\', '/') } -CaseSensitive) {
        $entrada = $zip.CreateEntry($f.FullName.Substring($paquete.Length + 1).Replace('\', '/'), [System.IO.Compression.CompressionLevel]::Optimal)
        $entrada.LastWriteTime = [DateTimeOffset]::new(2026, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        $salidaZip = $entrada.Open()
        try { $origen = [System.IO.File]::OpenRead($f.FullName); try { $origen.CopyTo($salidaZip) } finally { $origen.Dispose() } } finally { $salidaZip.Dispose() }
    }
} finally { $zip.Dispose() }
Move-Item -LiteralPath "$destino.part" -Destination $destino -Force
Escribir-Suma $destino (Huella $destino)
$destinoFuente = Join-Path $Salida $nombreFuente
Copy-Item -LiteralPath $tarFuente -Destination "$destinoFuente.part" -Force
Move-Item -LiteralPath "$destinoFuente.part" -Destination $destinoFuente -Force
Escribir-Suma $destinoFuente $huellaFuente
Remove-Item -LiteralPath $tmp -Recurse -Force
Paso "Listo: $destino ($([math]::Round((Get-Item -LiteralPath $destino).Length / 1MB, 1)) MB, SHA-256 $(Huella $destino))."
Paso "Fuente para la misma release: $destinoFuente ($([math]::Round((Get-Item -LiteralPath $destinoFuente).Length / 1MB, 1)) MB)."
