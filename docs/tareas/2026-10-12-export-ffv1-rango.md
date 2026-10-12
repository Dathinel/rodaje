# Tarea 32: FFV1 solo intra, rango de color en MOV y pruebas sobre el código real

Rama `claude/export-ffv1-rango`, PR #42. Hallazgos M4, M5 y M7 de la revisión en bloque.

## Comprobado en `origin/main` antes de cambiar

- **FFV1 sin `-g`:** el catálogo marca FFV1 como `IntraOnly`, así que `FfmpegArguments.Video` no pone `-g` y FFmpeg usa su GOP por defecto (12). Por eso `CpuExportTests` tenía `MaxGop = … && video != "ffv1"`. Las pruebas del sink (`FfmpegPipeSinkTests`) ya pasaban `-g 1` a mano.
- **Rango en MOV:** `FfmpegPipeSink` pone `-color_range` en la entrada y en la salida, pero `h264_mf` no lo escribe en el VUI y la caja «colr» de MOV es «nclc» (sin rango). La prueba aceptaba un rango vacío en `h264_mf` + MOV.
- **Pruebas por CPU:** `CpuExportArguments` copiaba a mano las líneas de `FfmpegArguments`, porque `Rodaje.Media.Tests` no referencia `Rodaje.Export`.

## Lo hecho (un commit por punto)

1. **FFV1 con `-g 1`** en `ExportEncoderCatalog` (argumentos fijos, junto a `-level 3 -slicecrc 1`). `CpuExportTests` exige ahora un GOP de 1 también en FFV1.
2. **Pruebas movidas** de `tests/Rodaje.Media.Tests/Export/CpuExport*` a `tests/Rodaje.Export.Tests/Cpu/`, con `git mv` para conservar el historial:
   - `CpuExportArguments` arma `ExportSettings` con los valores por defecto (primer control de tasa del codificador, GOP de 2 s, perfil por formato de píxel; SVT-AV1 con preset 12 para que la prueba sea corta), comprueba que `ExportSettingsValidator` no ve problemas y llama a `FfmpegArguments.Build`. Ya no hay copia.
   - `Rodaje.Export.Tests` referencia `Rodaje.Export`, y a través de él `Rodaje.Media`. Ningún `.csproj` cambia.
   - `Rodaje.Media` solo abre sus internos a `Rodaje.Media.Tests`, y su `.csproj` queda fuera de los caminos de la tarea. Por eso hubo tres cambios:
     - `FfmpegPipeSink.ProcessId` se lee por reflexión (`SinkRuns.ProcessId`).
     - La lista de `ffmpeg -encoders` se lee con el propio `ffmpeg.exe` y un analizador igual al de `FfmpegExportEncoderProbe.ParseEncoderList`.
     - `RunBinary` ya no asigna el proceso al `ChildProcessJob`, que es interno. El tiempo de espera lo sigue matando.
   - `ExportScratch` y `SinkRuns` (sus partes usadas) van copiados en `CpuExportSupport.cs`. Los de `Rodaje.Media.Tests` siguen ahí para las demás pruebas del sink.
   - `CpuExportCancellationTests` conserva el tope de 1 s y los mismos casos.
   - **Nuevo, `CpuExportArgumentsTests`:** comprueba sin `ffmpeg.exe` (también en Linux) que los ajustes de todos los casos son válidos y qué argumentos reciben: sin `-g` en los solo intra, el `-g 1` de FFV1 en el catálogo, y el GOP de 60 con `+cgop` en SVT-AV1 y sin él en VP9.
3. **Rango en MOV:** `FfmpegArguments.RangeInBitstream` devuelve `-bsf:v:0 h264_metadata=video_full_range_flag=0|1` (o `hevc_metadata`) para `h264_mf`/`hevc_mf`. `Build` lo añade solo en MOV, con el rango de `ExportColor`. Si el color es `null`, el rango es limitado, que es lo que da `ExportColor.For` para Y'CbCr, y Media Foundation solo recibe NV12.
   - El filtro va con `:v:0` para que no toque la miniatura adjunta, que es otro flujo de video.
   - La prueba de `h264_mf` en MOV deja de aceptar el rango vacío: comprueba todo el color con `ExportVerifier` (matriz, curva, primarios, rango y croma «left»).
   - Hay pruebas unitarias del filtro: MOV sí y MP4 no, H.264 y HEVC, rango completo y limitado, y ningún filtro en NVENC, ProRes y SVT-AV1.

## Decisiones

- Solo se escribe el rango (`video_full_range_flag`). Matriz, primarios y curva ya llegan por «nclc» y la prueba lo comprobaba. Escribirlos también en el VUI obligaría a pasar `ExportColor` completo a `Build`, que hoy no recibe el tamaño, y `ExportPlan` está fuera de los caminos.
- Solo en MOV: en MP4 la caja «nclx» lleva el rango y en MKV va en el elemento `Colour`.
- La GOP de las pruebas por CPU pasa de 30 a la de por defecto (2 s = 60 cuadros), y `h264_mf` y SVT-AV1 llevan ahora `-flags +cgop`, como en una exportación real. Antes la copia no lo ponía.
- Audio: el primer control de tasa de cada codificador, a 160 kbps cuando hace falta tasa. Antes AAC iba a 160 kbps con `-profile:a aac_low`, `aac_mf` a 192 y Opus a 128.

## Pruebas

- **Linux:** `Rodaje.Media.Tests` y `Rodaje.Export.Tests` compilan con `-p:EnableWindowsTargeting=true`, sin avisos.
  - Pasan todas las pruebas de `Rodaje.Export.Tests` que corren aquí: argumentos, plan, contrato y `CpuExportArgumentsTests`, que son 22 nuevas.
  - Las demás fallan al descubrirse por `d3d11` igual que en `main`, porque no hay GPU en Linux.
- **Pruebas omitidas:**
  - **Antes:** las pruebas de `CpuExport*` (5 métodos) se omitían sin `ffmpeg.exe` LGPL (Linux) o si faltaba un codificador compilado. En la CI de Windows corrían.
  - **Después:** las mismas, con las mismas causas, ahora en `Rodaje.Export.Tests`. No hay omisiones nuevas. Las 22 pruebas nuevas no se omiten nunca.
- **CI de Windows:** es la que corre de verdad las exportaciones por CPU (resultado en el PR).

## Posibles choques

- La tarea 33 (ya unida en #33) tocaba `CpuExportCancellationTests` en `tests/Rodaje.Media.Tests/Export/`. Esta rama mueve ese archivo. Cualquier rama abierta que lo edite en la ruta vieja chocará.
- La tarea 44 tocará `FfmpegPipeSink.cs`, que esta rama no toca. Pero si cambia `ProcessId` (nombre o tipo), `SinkRuns.ProcessId` lo dirá al arrancar: lanza «FfmpegPipeSink ya no tiene la propiedad ProcessId».
- Docs fuera de los caminos que conviene poner al día: `docs/arquitectura/exportacion.md` (pruebas en `Cpu/`) y `docs/fase-6.md` si nombra la ubicación de estas pruebas.

## Qué comprobar en Windows

- **Con NVENC:**
  - Exportar en MOV con H.264 NVENC y HEVC NVENC: no cambia nada (no llevan filtro) y `ffprobe` da `color_range=tv`.
  - Exportar en MOV con `h264_mf` (y `hevc_mf` si están las «Extensiones de video HEVC»): `ffprobe -show_streams` debe dar `color_range=tv`.
  - Si el proyecto tiene miniatura, comprobar que se adjunta igual.
- **FFV1 en MKV:** `ffprobe -show_frames -select_streams v -show_entries frame=key_frame` debe dar `key_frame=1` en todos los cuadros.
- **La aceptación de FFV1** (`ABRIR.bat aceptacion`) debe seguir dando todos los cuadros exactos. El archivo crece un poco con `-g 1`.
