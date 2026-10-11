# CI de Windows: pruebas de velocidad de Medios

Rama `claude/ci-media-velocidad`, desde `origin/main` en `e9c865c`.

## Causa (log de la corrida 38126616239, runner sin NVENC)

`TestClips.EncodeVideo` prueba NVENC y, si falta, cae en **libopenh264** (H.264) o **libkvazaar** (HEVC):

- **H.264 con libopenh264:** no produce cuadros B ni no referenciados. `Skipping_non_reference_…_software` (h264, h264 mkv y hevc, en `SoftwareCpu` y `SoftwareUpload`: 6 casos) falla con «ningún cuadro pedido fue de los descartados».
- **HEVC con libkvazaar:** no respeta `-g` (intervalo de keyframes 64 en lugar de 48); `KeyframeIndexTests.Both_sources_find_the_decoded_keyframes(hevc)` fallaba solo en `Assert.Equal(spec.Gop, index.MaxInterval)`. Además el clip es de **GOP abierto** (IDR en 0 y CRA en 64, 128, 192, con cuadros RASL): `Keyframe_mode_delivers_only_keyframes_software(hevc)` pide el 64 (keyframe real, CRA) y `VideoDecoder` en modo Keyframe entrega el 0. Es un **defecto del decodificador con GOP abierto** (según la revisión del PR, también en hardware), que arregla la tarea 14; no es un problema del GOP pedido.
- **`Skipping_non_reference…(hevc)`:** libkvazaar sí produce cuadros B (jerárquicos), pero todos referenciados (TRAIL_R/RASL_R): no hay cuadros no referenciados que descartar.
- **HEVC de 10 bits:** `KeyframeIndexTests` seguía pidiendo `Hevc10` (solo se genera con `hevc_nvenc`) y fallaba al generarlo.

No pude reproducir los fallos en Linux: esta máquina no tiene libopenh264 ni libkvazaar, solo compila; la causa sale del log y de `TestClips.EncodeVideo`.

## Cambios

- `tests/Rodaje.Media.Tests/Support/ExactEncoderClipDataAttribute.cs` (nuevo): filas de clips que se omiten, con un motivo propio de cada prueba (no el de `Skip.Encoder`), si no funciona el codificador exacto (`TestClips.ExactEncoder`: `h264_nvenc` / `hevc_nvenc`, `hevc_nvenc` también para el de 8 bits).
- `TestClips.ExactEncoder(ClipCodec)`.
- `HighSpeedDecodeTests`: `Skipping_non_reference…_software` usa el atributo con una fila de H.264 (h264 y mkv) y otra de HEVC, para que cada códec se omita por su codificador; `Keyframe_mode…_software` sigue con H.264 y mkv y pasa el HEVC por el atributo.
- `KeyframeIndexTests`: `Hevc10` pasa a `TenBitClipData`; `spec.Gop` se exige siempre en H.264 (libopenh264 lo respeta) y en HEVC solo con NVENC; sin él los keyframes del HEVC se siguen comparando con los decodificados.

## Omitidas, antes y después (runner de Windows sin NVENC)

| | Antes | Después |
|---|---|---|
| `Skipping_non_reference…_software` | 0 (6 fallos) | 6 omitidas: h264/mkv sin cuadros B (libopenh264); hevc sin cuadros no referenciados (libkvazaar) |
| `Keyframe_mode…_software` hevc | 0 (2 fallos) | 2 omitidas: defecto del decodificador con GOP abierto (CRA/RASL de libkvazaar), tarea 14 |
| `KeyframeIndex` hevc10 | 0 (1 fallo) | 1 omitida: HEVC 10 bits solo con `hevc_nvenc` |
| `KeyframeIndex` hevc | 0 (1 fallo) | 0: corre sin exigir el GOP pedido |

Total: 9 casos pasan de fallo a omitida. Con NVENC (RTX 3050) no se omite ninguno y se mantienen todas las comprobaciones.

## Posibles choques

Ninguno conocido con `origin/main`; el PR #5 (`media-flush`) toca `VideoDecoder` y `VideoDecoderBehaviorTests`, no estos archivos.

## Qué comprobar

- **Linux:** compila (`dotnet build tests/Rodaje.Media.Tests -p:EnableWindowsTargeting=true`); las pruebas de Medios no corren aquí.
- **CI de Windows:** debe quedar en verde salvo `PlaybackSchedulerTests.SeekLejos…` (tarea 9).
- **PC de Daniel (RTX 3050):** esas pruebas deben **correr**, no aparecer omitidas.

Nota: el mensaje de `Skip.Encoder` («solo se genera con …») no sirve para estos clips, así que el atributo compone su propio motivo.

## Revisión interna
Un agente nuevo sin contexto halló: relajación del GOP demasiado amplia en H.264 y omisión de H.264 por falta de `hevc_nvenc` (ambas corregidas), y el texto de `Skip.Encoder` (anotado). Sin GPU en el runner serían 3 + 1 + 1 omitidas en vez de 6 + 2 + 1.
