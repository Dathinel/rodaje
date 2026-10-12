# Descarga a .entorno\modelos los modelos de los módulos de IA (fase 7), con SHA-256 fijado y reanudación.
# Con -Corpus baja además el corpus de prueba de voz a .entorno\corpus (nunca se versiona).
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File herramientas\preparar-modelos.ps1 [-Corpus] [-Forzar]
#
# Resistente a apagones: descarga a .part con curl -C - (reanuda), verifica la huella y renombra. Un archivo con la
# huella mala se borra. Las URL de Hugging Face van fijadas a un commit.

param(
    [switch]$Corpus,
    [switch]$Forzar
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# whisper large-v3-turbo cuantizado q5_0 (MIT, OpenAI; conversión ggml de whisper.cpp, MIT).
# Fuente: https://huggingface.co/ggerganov/whisper.cpp, commit 5359861c739e955e79d9a303bcbc70fb988958b1.
$Modelos = @(
    @{
        Nombre = 'whisper large-v3-turbo q5_0'; Archivo = 'ggml-large-v3-turbo-q5_0.bin'; Peso = 'unos 550 MB'
        Url = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-large-v3-turbo-q5_0.bin'
        Sha256 = '394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2'; Bytes = 574041195
    }
)

# FLEURS es_419, partición dev (CC-BY-4.0, Google; https://huggingface.co/datasets/google/fleurs, commit 70bb2e84).
# Voz leída real con su transcripción: WER, desvío de tiempos y velocidad de modulo-subtitulos.
$FleursCommit = '70bb2e84b976b7e960aa89f1c648e09c59f894dd'
$CorpusArchivos = @(
    @{
        Nombre = 'FLEURS es_419 dev (transcripciones)'; Archivo = 'dev.tsv'; Peso = '256 KB'
        Url = "https://huggingface.co/datasets/google/fleurs/resolve/$FleursCommit/data/es_419/dev.tsv"
        Sha256 = '352b2a5f03f02fe752254fab737f76519963650f4630d2fb63957177aeec927f'; Bytes = 261638
    },
    @{
        Nombre = 'FLEURS es_419 dev (audio)'; Archivo = 'dev.tar.gz'; Peso = 'unos 255 MB'
        Url = "https://huggingface.co/datasets/google/fleurs/resolve/$FleursCommit/data/es_419/audio/dev.tar.gz"
        Sha256 = '94f0f3d9fd89c0496385bf0991f2565410cc489a73dd18cafa0ee0177dc5c815'; Bytes = 255285841
    }
)

function Paso([string]$texto) { Write-Host "[modelos] $texto" }

$Raiz    = Split-Path -Parent $PSScriptRoot
$Entorno = if ($env:RODAJE_ENTORNO) { $env:RODAJE_ENTORNO } else { Join-Path $Raiz '.entorno' }

# Deja $a.Archivo en $carpeta con la huella correcta, o termina el script con un mensaje.
function Bajar($a, [string]$carpeta) {
    New-Item -ItemType Directory -Force -Path $carpeta | Out-Null
    $destino = Join-Path $carpeta $a.Archivo
    if ((Test-Path -LiteralPath $destino) -and -not $Forzar) {
        if ((Get-Item -LiteralPath $destino).Length -eq $a.Bytes) {
            Paso "Ya está $($a.Nombre)"
            return
        }
        Remove-Item -LiteralPath $destino -Force
    }
    $part = "$destino.part"
    if ($Forzar -and (Test-Path -LiteralPath $part)) { Remove-Item -LiteralPath $part -Force }
    Paso "Descargando $($a.Nombre) ($($a.Peso); si se corta, la próxima vez sigue donde quedó)..."
    & curl.exe --location --fail --silent --show-error --retry 3 --continue-at - --output $part $a.Url
    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "No se pudo descargar $($a.Nombre). Revisa la conexión y vuelve a ejecutar el script: sigue donde quedó."
        exit 1
    }
    Paso "Comprobando la huella SHA-256..."
    $hash = (Get-FileHash -LiteralPath $part -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $a.Sha256) {
        Remove-Item -LiteralPath $part -Force
        Write-Host ""
        Write-Host "$($a.Nombre) no coincide con la huella esperada, así que se borró por seguridad."
        Write-Host "Vuelve a ejecutar el script; si se repite, el archivo publicado cambió y hay que actualizar el hash."
        exit 1
    }
    Move-Item -LiteralPath $part -Destination $destino -Force
    Paso "Listo: $($a.Archivo)"
}

$CarpetaModelos = Join-Path $Entorno 'modelos'
foreach ($m in $Modelos) { Bajar $m $CarpetaModelos }

if ($Corpus) {
    $CarpetaFleurs = Join-Path $Entorno 'corpus\fleurs-es'
    foreach ($a in $CorpusArchivos) { Bajar $a $CarpetaFleurs }
    $marca = Join-Path $CarpetaFleurs '.audio-dev'
    if (-not (Test-Path -LiteralPath $marca) -or $Forzar) {
        Paso "Descomprimiendo el audio (en una carpeta temporal, para que un apagón no deje nada a medias)..."
        $temporal = Join-Path $CarpetaFleurs ('dev.tmp-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
        New-Item -ItemType Directory -Force -Path $temporal | Out-Null
        & tar.exe -xzf (Join-Path $CarpetaFleurs 'dev.tar.gz') -C $temporal
        if ($LASTEXITCODE -ne 0) {
            Remove-Item -LiteralPath $temporal -Recurse -Force -ErrorAction SilentlyContinue
            Write-Host "No se pudo descomprimir el audio de FLEURS. Puede faltar espacio en el disco."
            exit 1
        }
        $wavs = Get-ChildItem -LiteralPath $temporal -Recurse -Filter '*.wav' | Select-Object -First 1
        if (-not $wavs) { Remove-Item -LiteralPath $temporal -Recurse -Force; Write-Host "El archivo no trae audio .wav."; exit 1 }
        $final = Join-Path $CarpetaFleurs 'dev'
        if (Test-Path -LiteralPath $final) { Remove-Item -LiteralPath $final -Recurse -Force }
        [System.IO.Directory]::Move($wavs.Directory.FullName, $final)
        Remove-Item -LiteralPath $temporal -Recurse -Force -ErrorAction SilentlyContinue
        [System.IO.File]::WriteAllText($marca, $FleursCommit)
    }
    Paso "Corpus listo en $CarpetaFleurs (FLEURS, CC-BY-4.0: se cita a Google y no se redistribuye desde este repo)."
}

Paso "Modelos listos en $CarpetaModelos."
