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

# modulo-voz: Kokoro-82M v1.0 cuantizado uint8 (el q8 es 3× más lento en la CPU con ONNX Runtime 1.30) (Apache-2.0, hexgrad; exportación ONNX de onnx-community, commit 1939ad2a),
# sus voces es, pt-BR y en (Apache-2.0) y el léxico inglés de misaki (Apache-2.0, commit fba12365). Unos 190 MB.
$Voz = @(
    @{ Carpeta = 'voz'; Archivo = 'kokoro-v1.0-uint8.onnx'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/onnx/model_uint8.onnx'; Sha256 = '6607a397d77b8514065420b7c1e7320117f7aabfdb45ce15f0050c5b0fe75aea'; Bytes = 177464632 },
    @{ Carpeta = 'voz\misaki'; Archivo = 'us_gold.json'; Url = 'https://raw.githubusercontent.com/hexgrad/misaki/fba1236595f2d2bf21d414ba6e57d25256afada3/misaki/data/us_gold.json'; Sha256 = 'dc414872a49a28ae6c141463d502fd945f3b2fde040484fdc47d00cc4612686f'; Bytes = 3000469 },
    @{ Carpeta = 'voz\misaki'; Archivo = 'us_silver.json'; Url = 'https://raw.githubusercontent.com/hexgrad/misaki/fba1236595f2d2bf21d414ba6e57d25256afada3/misaki/data/us_silver.json'; Sha256 = 'de8f67be911bb6c659187b4a65fd966b6a30e56350e0f790d763210b053ac475'; Bytes = 3099517 },
    @{ Carpeta = 'voz\misaki'; Archivo = 'gb_gold.json'; Url = 'https://raw.githubusercontent.com/hexgrad/misaki/fba1236595f2d2bf21d414ba6e57d25256afada3/misaki/data/gb_gold.json'; Sha256 = '29e62f4b60261c88f7f3c2c7811ca3825978948090b72d2b27d565b729282f71'; Bytes = 2838552 },
    @{ Carpeta = 'voz\misaki'; Archivo = 'gb_silver.json'; Url = 'https://raw.githubusercontent.com/hexgrad/misaki/fba1236595f2d2bf21d414ba6e57d25256afada3/misaki/data/gb_silver.json'; Sha256 = '48131e2d92ccc41655f4543e87e0f938e71463eb5a54be7f0693bb712ebb6bce'; Bytes = 3663898 },
    @{ Carpeta = 'voz\voces'; Archivo = 'ef_dora.bin'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/voices/ef_dora.bin'; Sha256 = 'f66ec66bd295acb18372e37008533a9a3228483ccd294e7538d5d9294ac9a532'; Bytes = 522240 },
    @{ Carpeta = 'voz\voces'; Archivo = 'em_alex.bin'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/voices/em_alex.bin'; Sha256 = '27809e9eafdcbcfff90a3016c697568676531de2a2c39cee29c96c7bd6b83e95'; Bytes = 522240 },
    @{ Carpeta = 'voz\voces'; Archivo = 'em_santa.bin'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/voices/em_santa.bin'; Sha256 = 'ad43b774e1ca24d05c6161297d8aeb770ac3d29bb95daf516727af5f7d543683'; Bytes = 522240 },
    @{ Carpeta = 'voz\voces'; Archivo = 'pf_dora.bin'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/voices/pf_dora.bin'; Sha256 = '3da7b5b2d91847ebf5646f57631af6ececae3c29a89cd300f06edf9aa6cfe9ee'; Bytes = 522240 },
    @{ Carpeta = 'voz\voces'; Archivo = 'pm_alex.bin'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/voices/pm_alex.bin'; Sha256 = '0175c753f59c54e7fd5a995bedef0c5ff2fb67e0043dd3dcb2ae74ec2acbeb2a'; Bytes = 522240 },
    @{ Carpeta = 'voz\voces'; Archivo = 'pm_santa.bin'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/voices/pm_santa.bin'; Sha256 = '8b012db3185778afe2e45a62cbad69db73021774fe68dda634bcc748a982eede'; Bytes = 522240 },
    @{ Carpeta = 'voz\voces'; Archivo = 'af_heart.bin'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/voices/af_heart.bin'; Sha256 = 'd583ccff3cdca2f7fae535cb998ac07e9fcb90f09737b9a41fa2734ec44a8f0b'; Bytes = 522240 },
    @{ Carpeta = 'voz\voces'; Archivo = 'af_bella.bin'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/voices/af_bella.bin'; Sha256 = 'f69d836209b78eb8c66e75e3cda491e26ea838a3674257e9d4e5703cbaf55c8b'; Bytes = 522240 },
    @{ Carpeta = 'voz\voces'; Archivo = 'af_nicole.bin'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/voices/af_nicole.bin'; Sha256 = 'cd2191ab31b914ed7b318416b0e4440fdf392ddad9106a060819aa600a64f59a'; Bytes = 522240 },
    @{ Carpeta = 'voz\voces'; Archivo = 'af_sarah.bin'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/voices/af_sarah.bin'; Sha256 = '4409fbc125afabacc615d94db5398d847006a737b0247d6892b7a9a0007a2f0a'; Bytes = 522240 },
    @{ Carpeta = 'voz\voces'; Archivo = 'af_sky.bin'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/voices/af_sky.bin'; Sha256 = '4435255c9744f3f31659e0d714ab7689bf65d9e77ec1cce060f083912614f0b9'; Bytes = 522240 },
    @{ Carpeta = 'voz\voces'; Archivo = 'af_alloy.bin'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/voices/af_alloy.bin'; Sha256 = 'c4a6b876047fd7fb472edf4ebd63cfac7c3b958a7cae7c106e8f038ca6308c45'; Bytes = 522240 },
    @{ Carpeta = 'voz\voces'; Archivo = 'af_aoede.bin'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/voices/af_aoede.bin'; Sha256 = '4a004c33430762e2461eedb2013fad808ef4ab3121f5300f554476caf58d8361'; Bytes = 522240 },
    @{ Carpeta = 'voz\voces'; Archivo = 'af_jessica.bin'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/voices/af_jessica.bin'; Sha256 = 'a240a5e3c15b43563d6e923bdca8ef5613a23471d9b77653694012435df23bd8'; Bytes = 522240 },
    @{ Carpeta = 'voz\voces'; Archivo = 'af_kore.bin'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/voices/af_kore.bin'; Sha256 = '9be5221b6a941c04b561959b8ff0b06e809444dcc4ab7e75a7b23606f691819e'; Bytes = 522240 },
    @{ Carpeta = 'voz\voces'; Archivo = 'af_nova.bin'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/voices/af_nova.bin'; Sha256 = '18778272caa0d0eebaea251c35fd635f038434f9eee5e691d02a174bd328414f'; Bytes = 522240 },
    @{ Carpeta = 'voz\voces'; Archivo = 'af_river.bin'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/voices/af_river.bin'; Sha256 = '00a2bcf82b1d86e8f19902ede58c65ccf6c0e43b44b7d74fad54e5d8933c9c30'; Bytes = 522240 },
    @{ Carpeta = 'voz\voces'; Archivo = 'am_michael.bin'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/voices/am_michael.bin'; Sha256 = '1d1f21dd8da39c30705cd4c75d039d265e9bc4a2a93ed09bc9e1b1225eb95ba1'; Bytes = 522240 },
    @{ Carpeta = 'voz\voces'; Archivo = 'am_fenrir.bin'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/voices/am_fenrir.bin'; Sha256 = 'c27989f741f7ee34d273a39d8a595cc0837d35f5ced9a29b7cc162614616df43'; Bytes = 522240 },
    @{ Carpeta = 'voz\voces'; Archivo = 'am_puck.bin'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/voices/am_puck.bin'; Sha256 = 'fcf73c989033e9233e0b98713eca600c8c74dcc1614b37009d5450ff4a2274a0'; Bytes = 522240 },
    @{ Carpeta = 'voz\voces'; Archivo = 'am_adam.bin'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/voices/am_adam.bin'; Sha256 = '162b035ed91cfc48b6046982184c645f72edcdd1b82843347f605d7bf7b15716'; Bytes = 522240 },
    @{ Carpeta = 'voz\voces'; Archivo = 'am_echo.bin'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/voices/am_echo.bin'; Sha256 = '3968b92c3c4cd1c4416dbded36c13eaa388a90d5788d02a13e4d781f5f8cf3c3'; Bytes = 522240 },
    @{ Carpeta = 'voz\voces'; Archivo = 'am_eric.bin'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/voices/am_eric.bin'; Sha256 = 'e8b5be17edd1e3636901ce7598baafe2dc8dd8ff707a0c23bf9e461add7e2832'; Bytes = 522240 },
    @{ Carpeta = 'voz\voces'; Archivo = 'am_liam.bin'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/voices/am_liam.bin'; Sha256 = '52403be32fd047c6a44517cb0bcd6b134f2a18baa73e70ef41651e0eab921ade'; Bytes = 522240 },
    @{ Carpeta = 'voz\voces'; Archivo = 'am_onyx.bin'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/voices/am_onyx.bin'; Sha256 = 'da5d135b424164916d75a68ffb4c2abce3d7d5ccc82dd1ee6cf447ce286145e6'; Bytes = 522240 },
    @{ Carpeta = 'voz\voces'; Archivo = 'am_santa.bin'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/voices/am_santa.bin'; Sha256 = '61150cf726ab6c5ed7a99f90a304f91f5a72c00c592e89ec94e5df11c319227a'; Bytes = 522240 },
    @{ Carpeta = 'voz\voces'; Archivo = 'bf_emma.bin'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/voices/bf_emma.bin'; Sha256 = '669fe0647f9dd04fcab92f1439a40eeb4c8b4ab1f82e4996fe3d918ce4a63b73'; Bytes = 522240 },
    @{ Carpeta = 'voz\voces'; Archivo = 'bf_isabella.bin'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/voices/bf_isabella.bin'; Sha256 = '3754352c4aaa46d17f27654ab7518d65b62ad6163a0f55a5f4330c2da2c4e94f'; Bytes = 522240 },
    @{ Carpeta = 'voz\voces'; Archivo = 'bf_alice.bin'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/voices/bf_alice.bin'; Sha256 = '08afa6ba24da61ea5e8efa139e5aadc938d83f0a6da5a900adaf763ac1da5573'; Bytes = 522240 },
    @{ Carpeta = 'voz\voces'; Archivo = 'bf_lily.bin'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/voices/bf_lily.bin'; Sha256 = '5e0ee32ebe64a467124976b14e69590746f1c4ce41a12b587a50c862edfea335'; Bytes = 522240 },
    @{ Carpeta = 'voz\voces'; Archivo = 'bm_george.bin'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/voices/bm_george.bin'; Sha256 = 'c4b235a4c1f2cd3b939fed08b899ce9385638b763f7b73a59616c4fc9bd6c9bc'; Bytes = 522240 },
    @{ Carpeta = 'voz\voces'; Archivo = 'bm_fable.bin'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/voices/bm_fable.bin'; Sha256 = 'f889083196807b4adb15e9204252165f503b8d33d3982e681c52443c49d798f1'; Bytes = 522240 },
    @{ Carpeta = 'voz\voces'; Archivo = 'bm_lewis.bin'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/voices/bm_lewis.bin'; Sha256 = 'b8f671cef828c30e66fdf0b0756a76bba58f6bb3398cbbf27058642acbcedb97'; Bytes = 522240 },
    @{ Carpeta = 'voz\voces'; Archivo = 'bm_daniel.bin'; Url = 'https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/1939ad2a8e416c0acfeecc08a694d14ef25f2231/voices/bm_daniel.bin'; Sha256 = '6b3194bbceffb746733cbc22c8f593dd44e401a71d53895a2dca891bc595a1e8'; Bytes = 522240 }
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
Paso "Voz: modelo Kokoro, $($Voz.Count - 5) voces y léxico inglés..."
foreach ($v in $Voz) {
    $v.Nombre = $v.Archivo
    $v.Peso = if ($v.Bytes -gt 1MB) { '{0:N0} MB' -f ($v.Bytes / 1MB) } else { '{0:N0} KB' -f ($v.Bytes / 1KB) }
    Bajar $v (Join-Path $CarpetaModelos $v.Carpeta)
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
}

Paso "Modelos listos en $CarpetaModelos."
