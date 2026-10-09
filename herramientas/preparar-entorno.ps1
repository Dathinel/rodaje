# Prepara el entorno propio de FotoToma dentro de la carpeta .entorno\ del proyecto.
# No toca nada global: el SDK de .NET y la caché de NuGet quedan dentro del repositorio
# (ignorados por git) y se reconstruyen con este script cuando faltan.
#
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File herramientas\preparar-entorno.ps1 [-Forzar]

param([switch]$Forzar)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Versión y hash fijados. Salen de
# https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json
# Para actualizar: cambiar los tres valores y la versión de global.json a la vez.
$SdkVersion = '10.0.401'
$SdkUrl     = "https://builds.dotnet.microsoft.com/dotnet/Sdk/$SdkVersion/dotnet-sdk-$SdkVersion-win-x64.zip"
$SdkSha512  = '24b670ad3d923bfcf47df6c3b034152398b42f6dbc388e10d783aee1cfb5e5817d399fc0ae2a12cfa822a55e61d34830ccb15c50ef6efee437ab874bb7c79430'

$Raiz    = Split-Path -Parent $PSScriptRoot
$Entorno = Join-Path $Raiz '.entorno'
$DotnetDir = Join-Path $Entorno 'dotnet'
$Marca   = Join-Path $DotnetDir ".sdk-$SdkVersion"

function Paso([string]$texto) { Write-Host "[entorno] $texto" }

if ((Test-Path -LiteralPath $Marca) -and -not $Forzar) {
    Paso "SDK de .NET $SdkVersion ya está en .entorno\dotnet"
    exit 0
}

New-Item -ItemType Directory -Force -Path $Entorno | Out-Null
$Zip = Join-Path $Entorno "dotnet-sdk-$SdkVersion-win-x64.zip"

if (-not (Test-Path -LiteralPath $Zip)) {
    Paso "Descargando el SDK de .NET $SdkVersion (unos 280 MB, solo la primera vez)..."
    $tmp = "$Zip.part"
    try {
        Invoke-WebRequest -Uri $SdkUrl -OutFile $tmp -UseBasicParsing
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

Paso "Descomprimiendo en .entorno\dotnet..."
if (Test-Path -LiteralPath $DotnetDir) { Remove-Item -LiteralPath $DotnetDir -Recurse -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::ExtractToDirectory($Zip, $DotnetDir)
Set-Content -LiteralPath $Marca -Value $SdkVersion -Encoding ascii
Remove-Item -LiteralPath $Zip -Force

New-Item -ItemType Directory -Force -Path (Join-Path $Entorno 'nuget') | Out-Null
Paso "Listo: SDK de .NET $SdkVersion instalado dentro del proyecto."
