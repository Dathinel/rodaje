# modulo-traducir

Módulo de IA de Rodaje (fase 7, agente F): traduce los textos de una pista de subtítulos a otro idioma. La app crea
una pista nueva con los mismos tiempos; el módulo solo devuelve los textos con los mismos ids. Proceso aparte que
habla el protocolo v1 (`src/Rodaje.Modules.Protocol`).

- **Motor:** llama.cpp vía LLamaSharp 0.27.0 (MIT). La biblioteca nativa se elige al cargar el primer modelo: Vulkan
  si el controlador trae `vulkan-1.dll`, si no la de CPU (AVX512, AVX2, AVX o sin AVX).
- **Modelo:** Qwen3-4B-Instruct-2507 Q4_K_M (Apache-2.0), unos 2,5 GB. `herramientas\preparar-modelos.ps1 -Traductor`
  lo baja a `.entorno\modelos` con SHA-256 fijado (ver la nota de `LICENCIAS.md`).
- **Reparto de capas:** con `device: auto` se suben a la GPU todas las capas; si no caben (4 GB de VRAM con el editor
  abierto) se repite con 18 y luego en la CPU. `vramMb` 3000 en `paquete.json`.
- **Sin AOT:** LLamaSharp no declara soporte; el módulo se publica autocontenido con JIT
  (`dotnet publish -c Release --self-contained`), como prevé D-031.

## Función `traducir`

Parámetros:

| Campo | Qué es |
|---|---|
| `target` | Idioma de destino (obligatorio): `es`, `en`, `pt-BR`, `pt-PT`, `fr`, `it`, `de`, `nl`, `ca`, `gl`, `eu`, `pl`, `ru`, `uk`, `tr`, `ar`, `hi`, `ja`, `ko`, `zh-Hans` (`pt` vale como `pt-BR`, `zh` como `zh-Hans`) |
| `source` | Idioma de origen o `auto` (por defecto) |
| `segments` | `[ { "id", "text" } ]`, hasta 5000, ids únicos de hasta 64 caracteres, textos de hasta 500 |
| `maxLineChars` | Caracteres por línea del resultado, 42 por defecto |
| `device` | `auto`, `gpu` o `cpu` |
| `model`, `threads`, `gpuLayers`, `batch` | Ruta del `.gguf`, hilos de CPU, capas en la GPU (−1 = todas), subtítulos por lote (8) |

Búferes opcionales, de bytes: si los subtítulos no caben en un mensaje (1 MiB), van como `{"segments":[…]}` en el
primero (sin `segments` en los parámetros); un segundo búfer (o el único, si los subtítulos van en los parámetros)
recibe el resultado, y `value` es entonces `{ "bytes": n }`.

Resultado:

```json
{ "source": "es", "target": "en", "device": "vulkan", "untranslated": [],
  "segments": [ { "id": "clip-12", "text": "Good morning,\neveryone." } ] }
```

Mismos ids y en el mismo orden; cada texto viene partido en líneas de hasta `maxLineChars` con `\n`, en el mínimo de
líneas y con el corte más parejo. Un subtítulo vacío sale vacío. Si el modelo no devuelve nada para uno, queda el
texto original y su id va en `untranslated`.

**Cómo traduce:** lotes de 8 subtítulos seguidos (hasta 1000 caracteres) con los 2 anteriores como contexto, numerados
`[1] …` en el prompt ChatML de Qwen; la respuesta se lee por número. Lo que el modelo junta, salta o repite se pide
otra vez suelto. Muestreo sin azar (temperatura 0): la misma entrada da la misma traducción.
