# Instala en .entorno\ el SDK de .NET fijado, la caché NuGet y las DLL de FFmpeg (LGPL), sin tocar nada global.
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File herramientas\preparar-entorno.ps1 [-Forzar] [-SoloFFmpeg]
#
# Resistente a apagones: descarga a .zip.part, verifica la huella, descomprime en una carpeta temporal
# que ya trae la marca (.sdk-<versión> o .ffmpeg-<versión>) y la renombra (atómico). Los demás scripts miran esas marcas.
#
# Solo para probar este script: FOTOTOMA_SDK_URL (URL o ruta de un .zip) y FOTOTOMA_SDK_SHA512, juntas.

param(
    [switch]$Forzar,
    [switch]$SoloFFmpeg
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Fuente: https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json
# Se actualiza junto con global.json, que es lo que leen los demás scripts.
$SdkVersion = '10.0.401'
$SdkUrl     = "https://builds.dotnet.microsoft.com/dotnet/Sdk/$SdkVersion/dotnet-sdk-$SdkVersion-win-x64.zip"
$SdkSha512  = '24b670ad3d923bfcf47df6c3b034152398b42f6dbc388e10d783aee1cfb5e5817d399fc0ae2a12cfa822a55e61d34830ccb15c50ef6efee437ab874bb7c79430'

# Compilación lgpl-shared de BtbN con fecha (las "latest" cambian cada día). Hash de checksums.sha256 de esa release.
# La serie mayor debe coincidir con FFmpeg.AutoGen (9.x: avcodec-63, avformat-63, avutil-61, swresample-7, swscale-10).
# dotnet-local.cmd, ABRIR.bat y probar.ps1 leen la versión de la línea siguiente: debe empezar por "$FfmpegVersion = '".
$FfmpegVersion = 'n9.0.2-23-g27b46f0fbc'
$FfmpegRelease = 'autobuild-2026-10-08-13-05'
$FfmpegUrl     = "https://github.com/BtbN/FFmpeg-Builds/releases/download/$FfmpegRelease/ffmpeg-$FfmpegVersion-win64-lgpl-shared-9.0.zip"
$FfmpegSha256  = 'a168ed7b315366354df532a755a93341b6e7e66b65834f56ce36b3ee8be0d6f0'

function Paso([string]$texto) { Write-Host "[entorno] $texto" }

$Raiz    = Split-Path -Parent $PSScriptRoot
$Entorno = if ($env:FOTOTOMA_ENTORNO) { $env:FOTOTOMA_ENTORNO } else { Join-Path $Raiz '.entorno' }

$GlobalJson = Join-Path $Raiz 'global.json'
try {
    $VersionGlobal = (Get-Content -LiteralPath $GlobalJson -Raw -Encoding UTF8 | ConvertFrom-Json).sdk.version
} catch {
    $VersionGlobal = $null
}
if ($VersionGlobal -ne $SdkVersion) {
    Write-Host ""
    Write-Host "global.json pide el SDK de .NET '$VersionGlobal', pero este script instala el $SdkVersion."
    Write-Host "Hay que cambiar a la vez la versión de global.json y la versión y el hash de"
    Write-Host "herramientas\preparar-entorno.ps1 para que digan lo mismo."
    exit 1
}

if ($env:FOTOTOMA_SDK_URL -or $env:FOTOTOMA_SDK_SHA512) {
    if (-not ($env:FOTOTOMA_SDK_URL -and $env:FOTOTOMA_SDK_SHA512)) {
        Write-Host "FOTOTOMA_SDK_URL y FOTOTOMA_SDK_SHA512 son solo para pruebas y van juntas; falta una de las dos."
        exit 1
    }
    $SdkUrl = $env:FOTOTOMA_SDK_URL
    $SdkSha512 = $env:FOTOTOMA_SDK_SHA512.ToLowerInvariant()
    Paso "AVISO: modo de prueba, el SDK sale de $SdkUrl en vez de la descarga oficial."
}

# Restos de una instalación cortada.
if (Test-Path -LiteralPath $Entorno) {
    Get-ChildItem -LiteralPath $Entorno -Directory | Where-Object { $_.Name -like 'dotnet.tmp-*' -or $_.Name -like 'ffmpeg.tmp-*' } | ForEach-Object {
        Paso "Borrando restos de una instalación anterior que no terminó: $($_.Name)"
        Remove-Item -LiteralPath $_.FullName -Recurse -Force
    }
}

# Descarga, verifica y deja $Componente.Carpeta en .entorno\ con su marca, o termina el script con un mensaje.
# Interior: subcarpeta del zip que pasa a ser la carpeta final ('' = la raíz del zip).
function Instalar($c) {
    $destino = Join-Path $Entorno $c.Carpeta
    $marca = Join-Path $destino $c.Marca
    $completo = (Test-Path -LiteralPath $marca)
    foreach ($a in $c.Archivos) { if (-not (Test-Path -LiteralPath (Join-Path $destino $a))) { $completo = $false } }
    if ($completo -and -not $Forzar) {
        Paso "Ya está $($c.Nombre) en .entorno\$($c.Carpeta)"
        return
    }
    if ((Test-Path -LiteralPath $destino) -and -not $completo) {
        Paso "La carpeta .entorno\$($c.Carpeta) está incompleta (sin la marca $($c.Marca)); se va a reinstalar."
    }

    New-Item -ItemType Directory -Force -Path $Entorno | Out-Null
    $zip = Join-Path $Entorno $c.Zip

    if (-not (Test-Path -LiteralPath $zip)) {
        Paso "Descargando $($c.Nombre) ($($c.Peso), solo la primera vez)..."
        $tmp = "$zip.part"
        try {
            if ($c.Url -notmatch '^https?://') {
                Copy-Item -LiteralPath $c.Url -Destination $tmp -Force
            } else {
                Invoke-WebRequest -Uri $c.Url -OutFile $tmp -UseBasicParsing
            }
        } catch {
            Write-Host ""
            Write-Host "No se pudo descargar $($c.Nombre). Revisa la conexión a internet y vuelve a intentarlo."
            Write-Host "Detalle técnico: $($_.Exception.Message)"
            exit 1
        }
        Move-Item -LiteralPath $tmp -Destination $zip -Force
    }

    Paso "Comprobando la huella $($c.Algoritmo) del archivo descargado..."
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm $c.Algoritmo).Hash.ToLowerInvariant()
    if ($hash -ne $c.Hash) {
        Remove-Item -LiteralPath $zip -Force
        Write-Host ""
        Write-Host "El archivo descargado no coincide con la huella esperada, así que se borró por seguridad."
        Write-Host "Vuelve a ejecutar el script; si se repite, el archivo publicado cambió y hay que actualizar el hash."
        exit 1
    }

    $temporal = Join-Path $Entorno ($c.Carpeta + '.tmp-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    Paso "Descomprimiendo (primero en una carpeta temporal, para que un apagón no deje nada a medias)..."
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    try {
        [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $temporal)
        $origen = $temporal
        if ($c.Interior) { $origen = Join-Path $temporal $c.Interior }
        foreach ($a in $c.Archivos) {
            if (-not (Test-Path -LiteralPath (Join-Path $origen $a))) { throw "el archivo no trae $a donde se esperaba" }
        }
        [System.IO.File]::WriteAllText((Join-Path $origen $c.Marca), $c.Version)
    } catch {
        if (Test-Path -LiteralPath $temporal) { Remove-Item -LiteralPath $temporal -Recurse -Force -ErrorAction SilentlyContinue }
        Write-Host ""
        Write-Host "No se pudo descomprimir $($c.Nombre). Puede faltar espacio en el disco."
        Write-Host "Detalle técnico: $($_.Exception.Message)"
        exit 1
    }

    if (Test-Path -LiteralPath $destino) {
        Paso "Quitando la versión anterior de .entorno\$($c.Carpeta)..."
        try {
            Remove-Item -LiteralPath $destino -Recurse -Force
        } catch {
            Remove-Item -LiteralPath $temporal -Recurse -Force -ErrorAction SilentlyContinue
            Write-Host ""
            Write-Host "No se pudo borrar la carpeta .entorno\$($c.Carpeta) anterior. Suele pasar cuando algún programa"
            Write-Host "la está usando (una compilación abierta, un proceso dotnet colgado o FotoToma abierto)."
            Write-Host "Cierra Visual Studio, las consolas que estén compilando, o reinicia, y vuelve a intentarlo."
            Write-Host "Detalle técnico: $($_.Exception.Message)"
            exit 1
        }
    }
    [System.IO.Directory]::Move($origen, $destino)
    # Si el zip traía una carpeta interior, queda la cáscara vacía; si un apagón la deja, se borra al empezar.
    if ($origen -ne $temporal) { Remove-Item -LiteralPath $temporal -Recurse -Force -ErrorAction SilentlyContinue }
    # El .zip se borra al final: si algo se cortó antes, no hay que volver a bajarlo.
    Remove-Item -LiteralPath $zip -Force
    Paso "Listo: $($c.Nombre) instalado en .entorno\$($c.Carpeta)."
}

if (-not $SoloFFmpeg) {
    Instalar @{
        Nombre = "el SDK de .NET $SdkVersion"; Carpeta = 'dotnet'; Version = $SdkVersion
        Marca = ".sdk-$SdkVersion"; Archivos = @('dotnet.exe'); Interior = ''
        Url = $SdkUrl; Hash = $SdkSha512; Algoritmo = 'SHA512'
        Zip = "dotnet-sdk-$SdkVersion-win-x64.zip"; Peso = 'unos 280 MB'
    }
    New-Item -ItemType Directory -Force -Path (Join-Path $Entorno 'nuget') | Out-Null
}

$FfmpegCarpeta = "ffmpeg-$FfmpegVersion-win64-lgpl-shared-9.0"
Instalar @{
    Nombre = "FFmpeg $FfmpegVersion (LGPL)"; Carpeta = 'ffmpeg'; Version = $FfmpegVersion
    Marca = ".ffmpeg-$FfmpegVersion"; Interior = $FfmpegCarpeta
    Archivos = @('bin\ffmpeg.exe', 'bin\ffprobe.exe', 'bin\avcodec-63.dll', 'bin\avformat-63.dll',
        'bin\avutil-61.dll', 'bin\swresample-7.dll', 'bin\swscale-10.dll')
    Url = $FfmpegUrl; Hash = $FfmpegSha256; Algoritmo = 'SHA256'
    Zip = "$FfmpegCarpeta.zip"; Peso = 'unos 75 MB'
}

Paso "Entorno listo dentro del proyecto."
