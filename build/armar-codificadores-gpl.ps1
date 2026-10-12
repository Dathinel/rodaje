# Arma el complemento rodaje-codificadores-gpl: el ffmpeg.exe GPL propio (build\compilar-ffmpeg-gpl.ps1, D-034), el texto
# de la GPLv3 y su línea de configure, como .paquete instalable (módulo con protocol 0), y deja al lado la fuente
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

function Paso([string]$texto) { Write-Host "[codificadores-gpl] $texto" }
function Fallar([string]$texto) { Write-Host ""; Write-Host $texto; exit 1 }
function Huella([string]$ruta) { (Get-FileHash -LiteralPath $ruta -Algorithm SHA256).Hash.ToLowerInvariant() }

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

$manifiesto = [ordered]@{
    formatVersion = 1; id = 'rodaje-codificadores-gpl'; version = $PaqueteVersion; kind = 'module'
    name = 'Codificadores GPL (x264 y x265)'
    description = 'Añade x264 y x265 en Exportar → Avanzado con un ffmpeg.exe GPL aparte. Incluye la licencia GPLv3; su código fuente se publica junto al paquete.'
    author = 'Dathinel; FFmpeg compilado con los scripts de BtbN/FFmpeg-Builds'; license = 'GPL-3.0-or-later'; apps = @('rodaje')
    module = [ordered]@{ entry = 'bin/ffmpeg.exe'; protocol = 0; functions = @('libx264', 'libx265'); runtime = 'cpu'; vramMb = 0 }
}
[System.IO.File]::WriteAllText((Join-Path $paquete 'paquete.json'), ($manifiesto | ConvertTo-Json -Depth 4), (New-Object System.Text.UTF8Encoding $false))

$lineas = @(
    '# Codificadores GPL para Rodaje',
    '',
    "$version, compilado para Rodaje con build\compilar-ffmpeg-gpl.ps1: FFmpeg 9.0.2 con x264, x265 (GPL-2.0-or-later),",
    'libopus, libmp3lame y libiconv, nada más. Licencia del conjunto: GNU GPL versión 3 o posterior (COPYING.GPLv3.txt);',
    'su línea de configure está en configure.txt.',
    '',
    'Rodaje lo ejecuta como un proceso aparte por una tubería: no se enlaza con el editor.',
    '',
    '## Código fuente correspondiente',
    '',
    "``$nombreFuente``, publicado en la misma release que este paquete (SHA-256 $huellaFuente):",
    'FFmpeg, los scripts de BtbN/FFmpeg-Builds y cada biblioteca en el árbol exacto que se compiló, con los scripts',
    'de compilación y las instrucciones para repetirla (LEEME.txt).',
    '',
    '## Huellas SHA-256',
    ''
)
foreach ($f in Get-ChildItem -LiteralPath $paquete -Recurse -File | Sort-Object FullName) {
    $lineas += "- ``$($f.FullName.Substring($paquete.Length + 1).Replace('\', '/'))`` $(Huella $f.FullName)"
}
[System.IO.File]::WriteAllLines((Join-Path $paquete 'LEEME.md'), $lineas, (New-Object System.Text.UTF8Encoding $false))

# 4. El .paquete (zip) y la fuente al lado, enteros o nada.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$destino = Join-Path $Salida "rodaje-codificadores-gpl-$PaqueteVersion.paquete"
Paso 'Comprimiendo el paquete...'
[System.IO.Compression.ZipFile]::CreateFromDirectory($paquete, "$destino.part", [System.IO.Compression.CompressionLevel]::Optimal, $false)
Move-Item -LiteralPath "$destino.part" -Destination $destino -Force
$destinoFuente = Join-Path $Salida $nombreFuente
Copy-Item -LiteralPath $tarFuente -Destination "$destinoFuente.part" -Force
Move-Item -LiteralPath "$destinoFuente.part" -Destination $destinoFuente -Force
"$huellaFuente  $nombreFuente" | Set-Content -LiteralPath "$destinoFuente.sha256" -Encoding ascii
Remove-Item -LiteralPath $tmp -Recurse -Force
Paso "Listo: $destino ($([math]::Round((Get-Item -LiteralPath $destino).Length / 1MB, 1)) MB, SHA-256 $(Huella $destino))."
Paso "Fuente para la misma release: $destinoFuente ($([math]::Round((Get-Item -LiteralPath $destinoFuente).Length / 1MB, 1)) MB)."
