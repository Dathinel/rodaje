# Prepara el entorno propio de FotoToma dentro de la carpeta .entorno\ del proyecto.
# No toca nada global: el SDK de .NET y la caché de NuGet quedan dentro del repositorio
# (ignorados por git) y se reconstruyen con este script cuando faltan.
#
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File herramientas\preparar-entorno.ps1 [-Forzar]
#
# Resistencia a apagones: el portátil se ha apagado de golpe más de una vez, así que la
# instalación nunca deja un SDK a medias que parezca bueno.
#   1. La descarga se guarda como .zip.part y solo pasa a .zip cuando terminó.
#   2. El .zip se comprueba con su huella SHA-512 antes de usarlo.
#   3. Se descomprime en una carpeta temporal hermana (.entorno\dotnet.tmp-xxxxxxxx), se escribe
#      la marca .sdk-<versión> dentro de ella y solo entonces se renombra a .entorno\dotnet.
#      Renombrar una carpeta en el mismo disco es atómico: o queda la carpeta completa o no queda.
#   4. "Listo" significa que existe .entorno\dotnet\.sdk-<versión>. ABRIR.bat, dotnet-local.cmd,
#      probar.ps1 y publicar-aot-prueba.ps1 miran esa marca, no dotnet.exe.
# Si algo se corta a medias, la siguiente ejecución borra los restos y vuelve a empezar.
#
# Variables solo para pruebas del propio script (no usarlas en el día a día):
#   FOTOTOMA_SDK_URL     URL o ruta local de un .zip que reemplaza la descarga del SDK.
#   FOTOTOMA_SDK_SHA512  huella SHA-512 esperada de ese .zip. Hay que dar las dos juntas.

param([switch]$Forzar)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Versión y hash fijados. Salen de
# https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json
# Para actualizar: cambiar la versión y el hash aquí y la versión de global.json a la vez.
# global.json es la fuente de verdad que leen los demás scripts; si no coincide con este
# script, se avisa en vez de instalar algo distinto.
$SdkVersion = '10.0.401'
$SdkUrl     = "https://builds.dotnet.microsoft.com/dotnet/Sdk/$SdkVersion/dotnet-sdk-$SdkVersion-win-x64.zip"
$SdkSha512  = '24b670ad3d923bfcf47df6c3b034152398b42f6dbc388e10d783aee1cfb5e5817d399fc0ae2a12cfa822a55e61d34830ccb15c50ef6efee437ab874bb7c79430'

function Paso([string]$texto) { Write-Host "[entorno] $texto" }

$Raiz    = Split-Path -Parent $PSScriptRoot
# FOTOTOMA_ENTORNO permite usar un entorno de otra carpeta (por ejemplo desde un worktree).
$Entorno = if ($env:FOTOTOMA_ENTORNO) { $env:FOTOTOMA_ENTORNO } else { Join-Path $Raiz '.entorno' }
$DotnetDir = Join-Path $Entorno 'dotnet'
$NombreMarca = ".sdk-$SdkVersion"
$Marca   = Join-Path $DotnetDir $NombreMarca

# --- La versión de global.json tiene que ser la que este script sabe instalar ----------------
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

# --- Sustitutos solo para probar este script --------------------------------------------------
if ($env:FOTOTOMA_SDK_URL -or $env:FOTOTOMA_SDK_SHA512) {
    if (-not ($env:FOTOTOMA_SDK_URL -and $env:FOTOTOMA_SDK_SHA512)) {
        Write-Host "FOTOTOMA_SDK_URL y FOTOTOMA_SDK_SHA512 son solo para pruebas y van juntas; falta una de las dos."
        exit 1
    }
    $SdkUrl = $env:FOTOTOMA_SDK_URL
    $SdkSha512 = $env:FOTOTOMA_SDK_SHA512.ToLowerInvariant()
    Paso "AVISO: modo de prueba, el SDK sale de $SdkUrl en vez de la descarga oficial."
}

# --- Restos de una instalación cortada (apagón, ventana cerrada) ------------------------------
if (Test-Path -LiteralPath $Entorno) {
    Get-ChildItem -LiteralPath $Entorno -Directory -Filter 'dotnet.tmp-*' | ForEach-Object {
        Paso "Borrando restos de una instalación anterior que no terminó: $($_.Name)"
        Remove-Item -LiteralPath $_.FullName -Recurse -Force
    }
}

if ((Test-Path -LiteralPath $Marca) -and (Test-Path -LiteralPath (Join-Path $DotnetDir 'dotnet.exe')) -and -not $Forzar) {
    Paso "SDK de .NET $SdkVersion ya está en .entorno\dotnet"
    exit 0
}
if ((Test-Path -LiteralPath $DotnetDir) -and -not (Test-Path -LiteralPath $Marca)) {
    Paso "La carpeta .entorno\dotnet está incompleta (sin la marca $NombreMarca); se va a reinstalar."
}

New-Item -ItemType Directory -Force -Path $Entorno | Out-Null
$Zip = Join-Path $Entorno "dotnet-sdk-$SdkVersion-win-x64.zip"

if (-not (Test-Path -LiteralPath $Zip)) {
    Paso "Descargando el SDK de .NET $SdkVersion (unos 280 MB, solo la primera vez)..."
    $tmp = "$Zip.part"
    try {
        if ($SdkUrl -notmatch '^https?://') {
            # Solo en modo de prueba: una ruta local en vez de una URL.
            Copy-Item -LiteralPath $SdkUrl -Destination $tmp -Force
        } else {
            Invoke-WebRequest -Uri $SdkUrl -OutFile $tmp -UseBasicParsing
        }
    } catch {
        Write-Host ""
        Write-Host "No se pudo descargar el SDK de .NET. Revisa la conexión a internet y vuelve a intentarlo."
        Write-Host "Detalle técnico: $($_.Exception.Message)"
        exit 1
    }
    # Solo un archivo descargado entero deja de llamarse .part.
    Move-Item -LiteralPath $tmp -Destination $Zip -Force
}

Paso "Comprobando la huella SHA-512 del archivo descargado..."
$hash = (Get-FileHash -LiteralPath $Zip -Algorithm SHA512).Hash.ToLowerInvariant()
if ($hash -ne $SdkSha512) {
    Remove-Item -LiteralPath $Zip -Force
    Write-Host ""
    Write-Host "El archivo descargado no coincide con la huella esperada, así que se borró por seguridad."
    Write-Host "Vuelve a ejecutar el script; si se repite, la versión publicada cambió y hay que actualizar el hash."
    exit 1
}

# --- Descomprimir aparte, marcar y renombrar ---------------------------------------------------
$Temporal = Join-Path $Entorno ('dotnet.tmp-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
Paso "Descomprimiendo (primero en una carpeta temporal, para que un apagón no deje un SDK a medias)..."
Add-Type -AssemblyName System.IO.Compression.FileSystem
try {
    [System.IO.Compression.ZipFile]::ExtractToDirectory($Zip, $Temporal)
    if (-not (Test-Path -LiteralPath (Join-Path $Temporal 'dotnet.exe'))) {
        throw "el archivo no trae dotnet.exe en su raíz"
    }
    # La marca se escribe dentro de la carpeta temporal: cuando aparezca .entorno\dotnet, ya la trae.
    [System.IO.File]::WriteAllText((Join-Path $Temporal $NombreMarca), $SdkVersion)
} catch {
    if (Test-Path -LiteralPath $Temporal) { Remove-Item -LiteralPath $Temporal -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Host ""
    Write-Host "No se pudo descomprimir el SDK de .NET. Puede faltar espacio en el disco."
    Write-Host "Detalle técnico: $($_.Exception.Message)"
    exit 1
}

if (Test-Path -LiteralPath $DotnetDir) {
    Paso "Quitando el SDK anterior de .entorno\dotnet..."
    try {
        Remove-Item -LiteralPath $DotnetDir -Recurse -Force
    } catch {
        Remove-Item -LiteralPath $Temporal -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host ""
        Write-Host "No se pudo borrar la carpeta .entorno\dotnet anterior. Suele pasar cuando algún programa"
        Write-Host "la está usando (una compilación abierta o un proceso dotnet colgado). Cierra Visual Studio,"
        Write-Host "las consolas que estén compilando, o reinicia, y vuelve a intentarlo."
        Write-Host "Detalle técnico: $($_.Exception.Message)"
        exit 1
    }
}
[System.IO.Directory]::Move($Temporal, $DotnetDir)
# El .zip se borra al final: si algo se cortó antes, la siguiente vez no hay que volver a bajarlo.
Remove-Item -LiteralPath $Zip -Force

New-Item -ItemType Directory -Force -Path (Join-Path $Entorno 'nuget') | Out-Null
Paso "Listo: SDK de .NET $SdkVersion instalado dentro del proyecto."
