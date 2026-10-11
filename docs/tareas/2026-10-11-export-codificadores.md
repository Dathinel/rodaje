# Tarea: lista de codificadores para la exportación (11 de octubre de 2026)

Rama `claude/export-codificadores` (PR #14), tarea 13 de la cola. Primera entrega el 11-oct por la mañana; esta es la segunda, que atiende la revisión del 11-oct y rehace el catálogo sobre el contrato del paso 0 de la fase 6 (`EncoderSpec`, D-028). Solo modelo y detección; no exporta nada.

## Hecho

Todo en `src/Rodaje.Media/Export/` (namespace `Rodaje.Media.Export`), sin tocar los archivos del contrato:

- **Fuera `ExportEncoderInfo.cs`** y con él el `ExportCodec` duplicado (CS0101), `ExportVendor`, `ExportQuality`, `ExportSpeed` y la tabla de argumentos por calidad × velocidad. El catálogo solo describe y `FfmpegArguments` (`src/Rodaje.Export`) construye: no se duplica ningún argumento.
- **`ExportEncoderCatalog.cs`**: `Video`, `Audio` y `All` como `IReadOnlyList<EncoderSpec>`, en orden de preferencia, más `Find`, `IndexOf` y `DisplayName`. 36 codificadores:
  - GPU: NVENC, AMF y QSV (H.264, HEVC, AV1) con `Nv12` primero y `P010` en HEVC/AV1; escalas CQ/QVBR/ICQ, presets, tunes, niveles y perfiles con sus píxeles. `av1_nvenc` con «Solo RTX 40 (Ada) o posterior.»; AMF y QSV con «Sin probar en un equipo AMD/Intel; la sonda decide si se ofrece.».
  - Media Foundation: `h264_mf` y `hevc_mf` con `-hw_encoding 0` (el MFT de software de Microsoft; `IsHardware = false`, `Vendor = Microsoft`): el primer recurso sin GPU.
  - Software sin patentes de consorcio: SVT-AV1, libaom y libvpx (`Yuv420P`/`Yuv420P10`…). libaom y libvpx no tienen `-preset`: su velocidad va fija en `BaseArguments` (`-cpu-used`).
  - kvazaar y OpenH264 con `InDefaults = false` y `Note = "Patentes de H.264/HEVC sin cubrir por software: no por defecto."` (OpenH264, además, «calidad baja»). Ninguno tiene escala de calidad ni presets: `-crf` no existe en libkvazaar (va en `-kvazaar-params`) y OpenH264 ignora calidad y velocidad; los dos solo admiten tasa media.
  - Maestros: `prores_ks` (HQ, Proxy, LT, Standard, 4444 y 4444 XQ, con alfa), `dnxhd` (DNxHR HQ, LB, SQ, HQX y 444), `cfhd` («Solo si este FFmpeg lo trae.») y `ffv1` (versión 3 con CRC).
  - Intermedios e imágenes: `mjpeg` (con `-strict unofficial`, porque FFmpeg rechaza JPEG en rango limitado), `png`, `qtrle`, `gif`, `libwebp`, `tiff` y `exr`.
  - Audio: `aac_mf` (antes que `aac`; 44,1/48 kHz, 6 canales), `aac`, `libopus`, `libmp3lame`, `flac`, `alac`, `pcm_s16le`, `pcm_s24le` y `pcm_f32le` (el `Id` decide las muestras), con `SampleRates` y `MaxChannels`.
- **`ExportEncoderChooser.cs`**: solo entran los `InDefaults`. `ChooseDefault`: GPU (H.264, HEVC, AV1; VP9 fuera porque no tiene hardware), si no Media Foundation, si no AV1 (SVT-AV1, libaom) o VP9 por CPU con una nota. `Choose(códec)` sirve para video y audio; H.264 o HEVC sin GPU ni Media Foundation caen a AV1 o VP9 con `Substituted` y nota. `ChooseDefaultAudio` elige AAC (`aac_mf` primero). `ChooseById` es la elección manual, la única que da kvazaar u OpenH264, con su `Note` como aviso.
- **`ExportEncoderProbe.cs`**: `ExportProbeStatus` (`Works`, `NotCompiled`, `Failed`). La lista de `-encoders` (ahora con el audio) solo se guarda si salió con código 0 y trae algo; si no, la prueba es `Failed` y la siguiente la vuelve a leer. Cada codificador se prueba con su primer formato de píxel, el primer perfil que lo admite y su preset más rápido (DNxHR con `dnxhr_hq` y `yuv422p`); el audio, con un tono de 1 s a su primera frecuencia y `-b:a 192k` en los que tienen pérdida (sin eso FFmpeg pide una tasa que `aac_mf` no acepta). Un proceso que no responde se reintenta al momento. El motivo lleva las líneas del propio codificador (`[h264_nvenc @ …]`) sin la dirección, o las que no son eco genérico de FFmpeg; como mucho tres. Cada ejecución pasa por `FfmpegExecutable.EnsureUnchanged()`.
- **`ExportEncoderAvailability.cs`**: cachea el informe. Los `Failed` (no los `NotCompiled`) se vuelven a probar en la primera petición pasado `failureRetryInterval` (2 min por defecto, con `TimeProvider`) y lo demás se conserva. `Invalidate` llama a `Reset` de la sonda; con una prueba en curso, quien esperaba recibe esa y la nueva empieza cuando acaba (nunca dos pruebas a la vez sobre la GPU). Un informe que falló o se canceló se vuelve a pedir. `ExportEncoderReport` trae `Find`, `HasFailures`, `ProbedAt` y las cuatro elecciones.

Pruebas en `tests/Rodaje.Media.Tests/Export/`: 175 casos, de los que 174 corren en Linux.

## Verificación

- **Compila contra `origin/main`** (`9c72d2e`): en un worktree temporal de main con estos archivos copiados encima, `Rodaje.Media` y `Rodaje.Media.Tests` compilan sin advertencias (`-p:EnableWindowsTargeting=true`), y `Rodaje.Export.Tests` también. La rama no tiene el contrato (se creó antes y las reglas prohíben unir main), así que **la rama sola no compila**; el PR sí, porque `git merge-tree` con `origin/main` sale limpio y la CI corre sobre la unión (`pull_request` hace checkout de `refs/pull/14/merge`). Ver «Choques».
- **Pruebas en Linux:** las 174 nuevas pasan; las de disponibilidad, 5 corridas seguidas sin fallos intermitentes. La suite entera de `Rodaje.Media.Tests` da 4 fallos que ya da main sin estos cambios (`VideoColorTests.Conversion_through_swscale_keeps_what_the_samples_mean` y tres de `ProxyStoreTests`, que dependen de Windows); esta tarea no los toca.
- **Sonda real con un FFmpeg de verdad** (el 6.1 de Ubuntu, con una prueba temporal que no se sube): funcionan SVT-AV1, libaom, libvpx, ProRes, DNxHR, CineForm, FFV1, MJPEG, PNG, qtrle, GIF, WebP, TIFF, EXR y todo el audio; AMF, Media Foundation, kvazaar y OpenH264 salen «no compilado»; NVENC y QSV, `Failed` con su motivo («Cannot load libcuda.so.1», «Error creating a MFX session: -9»). Así apareció que `av1_nvenc` rechaza `-profile:v main` y que su `-cq` va de 0 a 51; está corregido en un commit propio.
- **Catálogo × `FfmpegArguments`** (otra prueba temporal, en `Rodaje.Export.Tests`, que no se sube): cada codificador de software disponible, con cada modo de `RateControls` y cada formato de `PixelFormats`, arma su línea con `FfmpegArguments.Video`/`Audio` y FFmpeg la acepta. Pasan 85 de 86 combinaciones; la que falla es un error de la propia prueba (pedía 2000 kbps a Opus).

## Pruebas omitidas

- **Antes** (primera entrega): 1, `FfmpegExportEncoderProbeTests.The_real_probe_lists_what_this_machine_can_encode`, con `[ProxyFact]`: necesita el `ffmpeg.exe` LGPL de `.entorno`, que solo existe en Windows.
- **Después:** la misma, por la misma causa. En la CI de Windows corre. No se omitió ni se debilitó ninguna prueba existente.
- En la suite de `Rodaje.Media.Tests` en Linux: main da 99 omitidas de 270; con esta rama, 100 de 445.

## Decisiones tomadas (revisables)

1. **Media Foundation solo por software.** `EncoderSpec.Id` es el nombre de `-c:v`, así que no puede haber dos `h264_mf` (por hardware y por software). Con GPU ya van antes NVENC, AMF y QSV; el MFT de hardware solo aportaría algo si el codificador del fabricante fallara y el de Windows no. Si Daniel lo quiere, hace falta una clave aparte del `Id` en el contrato.
2. **HEVC pedido sin GPU ni `hevc_mf` cae a AV1 o VP9**, igual que H.264 (la cola solo hablaba de H.264). Antes devolvía `null`.
3. **Sin GPU, el H.264 por defecto es el de Windows** (`h264_mf`); AV1 solo si tampoco hay Media Foundation, y la elección lo explica en `Note`.
4. **Velocidad fija en libaom (`-cpu-used 6`) y libvpx (`-deadline good -cpu-used 2`)**: no tienen `-preset` y `FfmpegArguments` solo sabe poner `-preset`. Para elegirla haría falta otra capacidad en el contrato.
5. **kvazaar y OpenH264 solo con tasa media** (`Abr`): su calidad no cabe en `QualityScale` (un solo argumento) y `FfmpegArguments` pondría `-crf`.
6. **AV1 por GPU sin perfiles**: solo existe Main y `av1_nvenc` no acepta `main` como valor.
7. **Reintentos**: un proceso colgado se reintenta una vez al momento; los demás fallos, pasados 2 minutos y solo en la siguiente petición. Un `av1_nvenc` sin RTX 40 cuesta una sonda cada vez que vence el plazo.
8. **No se unifica con `FfmpegExecutable.ProbeEncoder`/`ProxyEncoders`**: están en `src/Rodaje.Media/Proxies/`, fuera de los caminos de la tarea, y su prueba (H.264 con `yuv420p` o `nv12`, cacheada para siempre) no vale para DNxHR, ProRes o el audio. Esta sonda reutiliza `FfmpegExecutable.Run`, `EnsureUnchanged` y `FfmpegNotFoundException`. Unificarlas (que los proxies pregunten a esta sonda) sería otra tarea.

## Choques

- **La rama no tiene el contrato.** Se creó en `56400cf`, antes del paso 0 de la fase 6, y las reglas prohíben unir o rebasar main. El código está escrito contra la API de `origin/main` y comprobado allí (ver «Verificación»); la unión es limpia porque todos los archivos son nuevos y ninguno coincide con los de main. Si la sesión local prefiere una rama que compile sola, basta con unir main al aceptar el PR.
- `tests/Rodaje.Export.Tests/Support/TestEncoders.cs` y `tests/Rodaje.Media.Tests/Export/ExportSinkTestSupport.cs` (main) tienen dobles de `EncoderSpec` que dicen que «el catálogo de verdad es de la tarea 13». No los toco (fuera de caminos); más adelante se pueden cambiar por `ExportEncoderCatalog.Find`. Una diferencia: `TestEncoders.Kvazaar` admite `ConstantQuality` y el catálogo no, por el `-crf`.
- `FfmpegArguments.Family` reconoce las familias por el `Id` (`_nvenc`, `_amf`, `_qsv`, `_mf`, `libsvtav1`…): los `Id` del catálogo encajan con esa tabla.

## Pendiente

- Que la interfaz de exportación (fase 6, agente E) muestre `ExportEncoderStatus.Reason` y las `Note`.
- Probar AMF y QSV en equipos reales (no hay probadores).
- Si la sesión local quiere elegir la velocidad de libaom/libvpx o la calidad de kvazaar, ampliar el contrato.

## Qué comprobar en Windows

1. Que `FfmpegExportEncoderProbeTests.The_real_probe_lists_what_this_machine_can_encode` pase en la CI de Windows: afirma que `h264_mf` (MFT de software) y `aac_mf` funcionan, que la elección por defecto es H.264 y que el audio es `aac_mf`. Si algo falla, el mensaje trae la tabla entera con los motivos.
2. En la RTX 3050 de Daniel: `h264_nvenc` y `hevc_nvenc` funcionan, `av1_nvenc` sale `Failed` con un motivo legible (sin procesos colgados), `ChooseDefault()` da `h264_nvenc` y `Choose(Hevc)` da `hevc_nvenc` (nunca `libkvazaar`).
3. Que `hevc_mf` diga algo razonable sin las «Extensiones de video HEVC» (debería salir `Failed`).
4. Que el tune `uhq` de HEVC/AV1 exista en FFmpeg 9 (llegó en FFmpeg 7).
5. Cuánto tarda la sonda completa (36 codificadores, 4 a la vez).
