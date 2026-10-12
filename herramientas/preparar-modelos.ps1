# Descarga a .entorno\modelos los modelos de los módulos de IA (fase 7), con SHA-256 fijado y reanudación.
# Con -Corpus baja además el corpus de prueba de voz a .entorno\corpus (nunca se versiona).
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File herramientas\preparar-modelos.ps1 [-Corpus] [-Forzar]
#
# Resistente a apagones: descarga a .part con curl -C - (reanuda), verifica la huella y renombra. Un archivo con la
# huella mala se borra. Las URL de Hugging Face y GitHub van fijadas a un commit.

param(
    [switch]$Corpus,
    [switch]$Forzar
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# whisper large-v3-turbo cuantizado q5_0 (MIT, OpenAI; conversión ggml de whisper.cpp, MIT).
# Fuente: https://huggingface.co/ggerganov/whisper.cpp, commit 5359861c739e955e79d9a303bcbc70fb988958b1.
# Silero VAD v6.2.3 (MIT, snakers4/silero-vad, commit 5cd79456): modulo-audio-ia, detección de voz.
# DeepFilterNet3 en ONNX (MIT o Apache-2.0, Rikorose/DeepFilterNet, commit d375b2d8): modulo-audio-ia, reducción de
# ruido. El tar.gz trae los tres grafos (codificador y dos decodificadores); se extraen con su propia huella.
$SileroCommit = '5cd7945676eb32225748052e2e6a0580e4686a08'
$DfnCommit = 'd375b2d8309e0935d165700c91da9de862a99c31'
$Modelos = @(
    @{
        Nombre = 'whisper large-v3-turbo q5_0'; Archivo = 'ggml-large-v3-turbo-q5_0.bin'; Peso = 'unos 550 MB'
        Url = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-large-v3-turbo-q5_0.bin'
        Sha256 = '394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2'; Bytes = 574041195
    },
    @{
        Nombre = 'Silero VAD v6.2.3'; Archivo = 'silero_vad.onnx'; Peso = '2,3 MB'
        Url = "https://github.com/snakers4/silero-vad/raw/$SileroCommit/src/silero_vad/data/silero_vad.onnx"
        Sha256 = '1a153a22f4509e292a94e67d6f9b85e8deb25b4988682b7e174c65279d8788e3'; Bytes = 2327524
    },
    @{
        Nombre = 'DeepFilterNet3 (ONNX)'; Archivo = 'DeepFilterNet3_onnx.tar.gz'; Peso = '8 MB'
        Url = "https://github.com/Rikorose/DeepFilterNet/raw/$DfnCommit/models/DeepFilterNet3_onnx.tar.gz"
        Sha256 = 'c94d91f70911001c946e0fabb4aa9adc37045f45a03b56008cb0c8244cb63616'; Bytes = 7983136
        Extraer = @(
            @{ Dentro = 'tmp/export/enc.onnx'; Archivo = 'deepfilternet3-enc.onnx'; Sha256 = '7c5399d3da8a50ebef1c1a0ae421b33376aa5e45d0e92df16da7e83c9c131916' },
            @{ Dentro = 'tmp/export/erb_dec.onnx'; Archivo = 'deepfilternet3-erb_dec.onnx'; Sha256 = 'ab669a1d10afe20911728b33053a452071042317a90581092b325da7b2f9d895' },
            @{ Dentro = 'tmp/export/df_dec.onnx'; Archivo = 'deepfilternet3-df_dec.onnx'; Sha256 = '23114ce3b0f6464b763ee62f7bb8aab6b2a129a21eabd5bcfe59413db05f278a' }
        )
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

# Voz de banda completa para modulo-audio-ia: «El clavo» de Alarcón, capítulo 1, leído para LibriVox (dominio público;
# https://archive.org/details/elclavo_2305_librivox). Se pasa a WAV mono f32 de 48 kHz con la FFmpeg del entorno.
$VozAudioIa = @{
    Nombre = 'LibriVox «El clavo», cap. 1 (dominio público)'; Archivo = 'elclavo_01_alarcon.mp3'; Peso = '4 MB'
    Url = 'https://archive.org/download/elclavo_2305_librivox/elclavo_01_alarcon.mp3'
    Sha256 = 'a07de83fafc5054e4af573a0a058d75f7a4275221ba6cc5b1d97278c78bdee0c'; Bytes = 4225026
}

function Paso([string]$texto) { Write-Host "[modelos] $texto" }

$Raiz    = Split-Path -Parent $PSScriptRoot
$Entorno = if ($env:RODAJE_ENTORNO) { $env:RODAJE_ENTORNO } else { Join-Path $Raiz '.entorno' }

function Huella([string]$ruta) { (Get-FileHash -LiteralPath $ruta -Algorithm SHA256).Hash.ToLowerInvariant() }

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
    if ((Huella $part) -ne $a.Sha256) {
        Remove-Item -LiteralPath $part -Force
        Write-Host ""
        Write-Host "$($a.Nombre) no coincide con la huella esperada, así que se borró por seguridad."
        Write-Host "Vuelve a ejecutar el script; si se repite, el archivo publicado cambió y hay que actualizar el hash."
        exit 1
    }
    Move-Item -LiteralPath $part -Destination $destino -Force
    Paso "Listo: $($a.Archivo)"
}

# Saca de un tar.gz ya verificado los archivos de $a.Extraer, cada uno con su huella, sin dejar nada a medias.
function Extraer($a, [string]$carpeta) {
    $faltan = @($a.Extraer | Where-Object { $Forzar -or -not (Test-Path -LiteralPath (Join-Path $carpeta $_.Archivo)) })
    if ($faltan.Count -eq 0) { return }
    $temporal = Join-Path $carpeta ('extraer.tmp-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $temporal | Out-Null
    try {
        & tar.exe -xzf (Join-Path $carpeta $a.Archivo) -C $temporal
        if ($LASTEXITCODE -ne 0) { Write-Host "No se pudo descomprimir $($a.Archivo)."; exit 1 }
        foreach ($e in $faltan) {
            $origen = Join-Path $temporal $e.Dentro
            if (-not (Test-Path -LiteralPath $origen) -or (Huella $origen) -ne $e.Sha256) {
                Write-Host "$($e.Dentro) falta o no coincide con su huella dentro de $($a.Archivo)."
                exit 1
            }
            Move-Item -LiteralPath $origen -Destination (Join-Path $carpeta $e.Archivo) -Force
            Paso "Listo: $($e.Archivo)"
        }
    }
    finally {
        Remove-Item -LiteralPath $temporal -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$CarpetaModelos = Join-Path $Entorno 'modelos'
foreach ($m in $Modelos) {
    Bajar $m $CarpetaModelos
    if ($m.Extraer) { Extraer $m $CarpetaModelos }
}

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

    $CarpetaAudioIa = Join-Path $Entorno 'corpus\audio-ia'
    Bajar $VozAudioIa $CarpetaAudioIa
    $wav = Join-Path $CarpetaAudioIa 'voz-48k.wav'
    if (-not (Test-Path -LiteralPath $wav) -or $Forzar) {
        $ffmpeg = Join-Path $Entorno 'ffmpeg\bin\ffmpeg.exe'
        if (-not (Test-Path -LiteralPath $ffmpeg)) { Write-Host "Falta ${ffmpeg}: ejecuta antes herramientas\preparar-entorno.ps1."; exit 1 }
        & $ffmpeg -hide_banner -loglevel error -y -i (Join-Path $CarpetaAudioIa $VozAudioIa.Archivo) -ac 1 -ar 48000 -c:a pcm_f32le "$wav.tmp.wav"
        if ($LASTEXITCODE -ne 0) { Write-Host "No se pudo convertir la voz a WAV."; exit 1 }
        Move-Item -LiteralPath "$wav.tmp.wav" -Destination $wav -Force
    }
    Paso "Voz de 48 kHz lista en $wav (LibriVox, dominio público)."
}

Paso "Modelos listos en $CarpetaModelos."
