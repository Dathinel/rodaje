# Licencias de modulo-audio-ia

El código del módulo es propio (GPL-3.0-only con los términos de Rodaje, D-032). Los modelos y las bibliotecas que trae o descarga conservan su licencia; ninguno se versiona en el repositorio: los baja `build\preparar-modelos.ps1` (o el gestor de complementos) con su SHA-256 fijado.

## Modelos

| Modelo | Para qué | Licencia | Fuente fijada | Archivos y SHA-256 |
|---|---|---|---|---|
| **DeepFilterNet3** (Hendrik Schröter et al., Universidad de Erlangen-Núremberg) | Reducción de ruido de voz, 48 kHz | MIT o Apache-2.0, a elección | `github.com/Rikorose/DeepFilterNet`, commit `d375b2d8`, `models/DeepFilterNet3_onnx.tar.gz` (`c94d91f7…3616`) | `deepfilternet3-enc.onnx` `7c5399d3…1916`; `deepfilternet3-erb_dec.onnx` `ab669a1d…d895`; `deepfilternet3-df_dec.onnx` `23114ce3…f78a` |
| **Silero VAD v6.2.3** (Silero Team) | Detección de voz, 16 kHz | MIT | `github.com/snakers4/silero-vad`, commit `5cd79456`, `src/silero_vad/data/silero_vad.onnx` | `silero_vad.onnx` `1a153a22…88e3` |

- **DeepFilterNet:** el repositorio publica el código y los pesos bajo «MIT OR Apache-2.0» (`LICENSE-MIT`, `LICENSE-APACHE`). Se elige MIT; al redistribuir se incluye su aviso de copyright. Los datos de entrenamiento (DNS Challenge, entre otros) no viajan con los pesos.
- **Silero VAD:** `LICENSE` del repositorio, MIT. Se incluye su aviso al redistribuir.
- **Lógica reimplementada:** la STFT, las bandas ERB y la normalización de rasgos siguen a `libDF` (MIT/Apache-2.0) y el paso de probabilidades a tramos sigue a `get_speech_timestamps` de Silero (MIT); el código C# es propio, no una copia.

## Bibliotecas

| Biblioteca | Versión | Licencia | Nota |
|---|---|---|---|
| ONNX Runtime (`Microsoft.ML.OnnxRuntime`, solo CPU) | 1.30.0 | MIT | `onnxruntime.dll` |

## Corpus de prueba (solo en `.entorno`, nunca en el repo ni en la distribución)

- **LibriVox, «El clavo» de Pedro Antonio de Alarcón, capítulo 1** — dominio público (`archive.org/details/elclavo_2305_librivox`). Voz de banda completa para medir la reducción de ruido.
- **FLEURS es_419, partición dev** (Google) — CC-BY-4.0. Doce y veinte frases de distintos hablantes para la reducción de ruido y la detección de voz.
