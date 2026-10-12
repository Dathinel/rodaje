# Licencias de modulo-subtitulos

| Componente | Versión | Licencia | Fuente |
|---|---|---|---|
| Código del módulo | 0.1.0 | GPL-3.0-only con los términos de Rodaje (D-032) | este repositorio |
| Whisper.net, Whisper.net.Runtime, Whisper.net.Runtime.Vulkan | 1.9.1 | MIT | https://github.com/sandrohanea/whisper.net |
| whisper.cpp y ggml (DLL nativas dentro de los paquetes anteriores) | la de Whisper.net 1.9.1 | MIT | https://github.com/ggml-org/whisper.cpp |
| Microsoft.Extensions.AI.Abstractions (dependencia de Whisper.net) | 10.2.0 | MIT | https://github.com/dotnet/extensions |
| Modelo whisper large-v3-turbo, conversión ggml q5_0 | commit `5359861c` de `ggerganov/whisper.cpp` en Hugging Face | MIT (pesos de OpenAI) | https://github.com/openai/whisper/blob/main/LICENSE |
| Modelo whisper small (CPU), conversión ggml q5_1 | commit `5359861c` de `ggerganov/whisper.cpp` en Hugging Face | MIT (pesos de OpenAI) | https://github.com/openai/whisper/blob/main/LICENSE |
| Vulkan | `vulkan-1.dll` del controlador de la GPU | no se redistribuye | — |

**Corpus de prueba (no se distribuye ni se versiona):** FLEURS `es_419`, partición dev, de Google, CC-BY-4.0 (https://huggingface.co/datasets/google/fleurs, commit `70bb2e84`). Conneau et al., «FLEURS: Few-shot Learning Evaluation of Universal Representations of Speech», 2022. Se baja con `build\preparar-modelos.ps1 -Corpus`.
