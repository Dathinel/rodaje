# Instala el SDK de .NET fijado y la caché NuGet en .entorno\, sin tocar nada global.
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File herramientas\preparar-entorno.ps1 [-Forzar]
#
# Resistente a apagones: descarga a .zip.part, verifica SHA-512, descomprime en una carpeta temporal
# que ya trae la marca .sdk-<versión> y la renombra (atómico). Los demás scripts miran esa marca.
#
# Solo para probar este script: FOTOTOMA_SDK_URL (URL o ruta de un .zip) y FOTOTOMA_SDK_SHA512, juntas.

param([switch]$Forzar)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Fuente: https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json
# Se actualiza junto con global.json, que es lo que leen los demás scripts.
$SdkVersion = '10.0.401'
$SdkUrl     = "https://builds.dotnet.microsoft.com/dotnet/Sdk/$SdkVersion/dotnet-sdk-$SdkVersion-win-x64.zip"
$SdkSha512  = '24b670ad3d923bfcf47df6c3b034152398b42f6dbc388e10d783aee1cfb5e5817d399fc0ae2a12cfa822a55e61d34830ccb15c50ef6efee437ab874bb7c79430'

function Paso([string]$texto) { Write-Host "[entorno] $texto" }

$Raiz    = Split-Path -Parent $PSScriptRoot
$Entorno = if ($env:FOTOTOMA_ENTORNO) { $env:FOTOTOMA_ENTORNO } else { Join-Path $Raiz '.entorno' }
$DotnetDir = Join-Path $Entorno 'dotnet'
$NombreMarca = ".sdk-$SdkVersion"
$Marca   = Join-Path $DotnetDir $NombreMarca

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

$Temporal = Join-Path $Entorno ('dotnet.tmp-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
Paso "Descomprimiendo (primero en una carpeta temporal, para que un apagón no deje un SDK a medias)..."
Add-Type -AssemblyName System.IO.Compression.FileSystem
try {
    [System.IO.Compression.ZipFile]::ExtractToDirectory($Zip, $Temporal)
    if (-not (Test-Path -LiteralPath (Join-Path $Temporal 'dotnet.exe'))) {
        throw "el archivo no trae dotnet.exe en su raíz"
    }
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
# El .zip se borra al final: si algo se cortó antes, no hay que volver a bajarlo.
Remove-Item -LiteralPath $Zip -Force

New-Item -ItemType Directory -Force -Path (Join-Path $Entorno 'nuget') | Out-Null
Paso "Listo: SDK de .NET $SdkVersion instalado dentro del proyecto."
