# Licencias de modulo-voz

| Componente | Versión | Licencia | Fuente |
|---|---|---|---|
| Código del módulo (fonemizadores de español y portugués, normalizador, síntesis) | 0.1.0 | GPL-3.0-only con los términos de Rodaje (D-032) | este repositorio |
| ONNX Runtime (`Microsoft.ML.OnnxRuntime`, DLL nativa incluida) | 1.30.0 | MIT | https://github.com/microsoft/onnxruntime |
| Kokoro-82M v1.0, exportación ONNX cuantizada uint8 (`model_uint8.onnx`) | commit `1939ad2a` de `onnx-community/Kokoro-82M-v1.0-ONNX` | Apache-2.0 | https://huggingface.co/hexgrad/Kokoro-82M |
| Vocabulario de fonemas de Kokoro (copiado en `KokoroEngine.cs`) | `config.json` de `hexgrad/Kokoro-82M`, commit `f3ff3571` | Apache-2.0 | https://huggingface.co/hexgrad/Kokoro-82M |
| Léxico inglés de misaki (`us_gold`, `us_silver`, `gb_gold`, `gb_silver`) | commit `fba12365` de `hexgrad/misaki` | Apache-2.0 | https://github.com/hexgrad/misaki |

## Voces

Todas son vectores de estilo de Kokoro-82M v1.0 (`voices/<id>.bin` de la misma exportación), Apache-2.0, con el SHA-256 de cada archivo en `paquete.json` y en `build\preparar-modelos.ps1`.

| Idioma | Voces |
|---|---|
| Español (sirven para España y Latinoamérica) | `ef_dora` (Dora), `em_alex` (Alex), `em_santa` (Santa) |
| Portugués de Brasil | `pf_dora` (Dora), `pm_alex` (Alex), `pm_santa` (Santa) |
| Inglés de EE. UU. | `af_heart`, `af_bella`, `af_nicole`, `af_sarah`, `af_sky`, `af_alloy`, `af_aoede`, `af_jessica`, `af_kore`, `af_nova`, `af_river`, `am_michael`, `am_fenrir`, `am_puck`, `am_adam`, `am_echo`, `am_eric`, `am_liam`, `am_onyx`, `am_santa` |
| Inglés británico | `bf_emma`, `bf_isabella`, `bf_alice`, `bf_lily`, `bm_george`, `bm_fable`, `bm_lewis`, `bm_daniel` |

**Datos de entrenamiento de Kokoro** (ficha del modelo, `README.md` de `hexgrad/Kokoro-82M`, commit `f3ff3571`, anotada el 12 de octubre de 2026): «Kokoro was trained exclusively on permissive/non-copyrighted audio data and IPA phoneme labels»: audio de dominio público, audio con licencia Apache, MIT y similares, y audio sintético de modelos de TTS cerrados de grandes proveedores (sin audio sintético de modelos abiertos ni de «custom voice clones»). Unas pocas cientos de horas en total; el conjunto no es público. La ficha atribuye dos fuentes CC BY que entraron desde la v0.19: Koniwa `tnc` (<1 h, CC BY 3.0) y SIWIS (<11 h, CC BY 4.0). Arquitectura StyleTTS 2 e ISTFTNet; modelo base `yl4579/StyleTTS2-LJSpeech`. Las secciones literales están en `THIRD-PARTY-NOTICES.txt` de Rodaje, sección 10, junto a los textos de las licencias.

## Lo que no se usa y por qué

- **espeak-ng** (GPL-3.0): Kokoro se entrenó con sus fonemas, pero el módulo no lo enlaza ni lo distribuye. El español y el portugués van con fonemizadores propios por reglas, y el inglés con el léxico de misaki. Para medir el parecido con espeak-ng se usó solo en una carpeta temporal, sin copiar su código ni sus datos.
- **Voces de Piper** (`es_ES-davefx`, `pt_BR-faber`, `pt_BR-cadu`, `pt_BR-jeff`, `en_US-libritts_r`, `es_MX-ald`): sus fichas dicen «Finetuned from U.S. English lessac voice» (o de davefx, que sale de lessac). Las derivadas de lessac quedan fuera por la licencia de sus datos (Blizzard 2011). `es_MX-claude` (Apache-2.0) no dice de qué voz parte, así que tampoco entra hasta confirmarlo.
- **Portugués de Portugal:** no hay voz con licencia limpia. En la v1 solo hay pt-BR.

## Corpus de medición (no se distribuye ni se versiona)

FLEURS `es_419` y `pt_br`, partición dev, de Google, CC-BY-4.0 (https://huggingface.co/datasets/google/fleurs, commit `70bb2e84`). Se usaron sus frases para comparar los fonemas con los de espeak-ng.
