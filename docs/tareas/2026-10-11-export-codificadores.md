# Tarea: lista de codificadores para la exportación (11 de octubre de 2026)

Rama `claude/export-codificadores`, creada desde `origin/main` en `56400cf`. Tarea 13 de la cola. Solo modelo y detección; no exporta nada todavía.

## Hecho

Todo en archivos nuevos de `src/Rodaje.Media/Export/` (namespace `Rodaje.Media.Export`):

- **`ExportEncoderInfo.cs`**: enums `ExportCodec` (H264, Hevc, Av1, Vp9), `ExportVendor`, `ExportQuality` y `ExportSpeed`; el registro `ExportEncoderInfo` (id de FFmpeg, nombre, códec, fabricante, licencia, `PatentEncumbered`, `LowQuality`, formato de píxel) y `BaseArguments(calidad, velocidad)`, que traduce cada nivel al parámetro propio de cada codificador (cq/preset de NVENC, qp de AMF, global_quality de QSV, quality de MF, crf/preset de SVT-AV1, crf/cpu-used de libaom y libvpx, crf y preajuste de kvazaar, rc_mode de OpenH264).
- **`ExportEncoderCatalog.cs`**: 16 codificadores en orden de preferencia. Hardware: NVENC, AMF y QSV (h264/hevc/av1) y Media Foundation (h264/hevc). Software: SVT-AV1, libaom, libvpx (VP9), kvazaar (HEVC) y OpenH264 (H.264, `LowQuality`). Sin x264 ni x265; una prueba lo vigila y otra comprueba que cada codificador de software está en `docs/ffmpeg-configure.txt`.
- **`ExportEncoderProbe.cs`**: `IExportEncoderProbe` (doble de prueba) y `FfmpegExportEncoderProbe`, que lee `ffmpeg -encoders` una vez (si no está compilado, lo dice) y codifica 1 s gris de 640x360 con los argumentos Draft/Fast, usando `FfmpegExecutable.Run` (el exe LGPL ya verificado).
- **`ExportEncoderAvailability.cs`**: prueba todo el catálogo en paralelo (límite 4 por defecto), con el orden del catálogo en el informe aunque terminen en otro orden, caché por instancia (peticiones concurrentes comparten una sola ejecución), `Invalidate()`, cancelación que solo afecta a quien espera y una sonda que lanza cuenta como «no disponible» con su motivo.
- **`ExportEncoderChooser.cs`**: «GPU si hay, si no CPU». Con GPU: hardware H.264, luego HEVC, luego AV1 (a igual códec, el primero del catálogo: NVIDIA, AMD, Intel, Windows). Sin GPU: software en el orden del catálogo (AV1, VP9, HEVC; OpenH264 solo si no hay otro, con nota). Si se pide H.264 y no hay GPU: AV1, luego HEVC, luego VP9 por CPU, con `Substituted = true` y una nota; OpenH264 solo si no hay nada más.

Pruebas nuevas en `tests/Rodaje.Media.Tests/Export/` (52 casos): catálogo, argumentos, elección con dobles, disponibilidad (paralelismo con `Barrier`, caché, cancelación, fallos) y la sonda real.

## Verificación en Linux

- `Rodaje.Media` y sus pruebas son `net10.0-windows`, pero compilan y corren en Linux con `-p:EnableWindowsTargeting=true`. Las 51 pruebas con dobles pasan; 1 se omite (la prueba real, que necesita `ffmpeg.exe` LGPL de `.entorno`, solo existe en Windows). `dotnet test tests/Rodaje.Media.Tests -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~Rodaje.Media.Tests.Export"`.
- Los argumentos de SVT-AV1, libaom y libvpx se probaron a mano con el `ffmpeg` de este equipo (salen con código 0). NVENC/QSV/AMF/MF, kvazaar y OpenH264 no se pudieron probar aquí.
- Un hallazgo de las propias pruebas: la leyenda de `ffmpeg -encoders` (` V..... = Video`) se colaba como un codificador llamado `=`; corregido.

## Pruebas omitidas

Antes: 0 omitidas añadidas por esta tarea. Después: 1 (`FfmpegExportEncoderProbeTests.The_real_probe_lists_what_this_machine_can_encode`), con `[ProxyFact]`: se omite sin `ffmpeg.exe` LGPL (Linux y equipos sin `preparar-entorno`); en la CI de Windows corre. No se omitió ni debilitó ninguna prueba existente.

## Pendiente

- Nada de exportar: falta la interfaz de salida (fase 6) que use `ExportEncoderInfo.BaseArguments` y el contenedor, GOP, tamaño y audio.
- Probar los argumentos de NVENC, AMF, QSV, MF, kvazaar y OpenH264 en máquinas reales (la sonda ya usa los de Draft/Fast, así que un argumento inválido haría que el codificador figure como «no funciona»).
- Mostrar el motivo de cada codificador no disponible en la interfaz (`ExportEncoderStatus.Reason`).
- Estados de color y 10 bits: la sonda y los argumentos son de 8 bits (`yuv420p`/`nv12`).

## Decisiones tomadas (revisables)

1. **Sin GPU, el H.264 pedido cae a AV1 antes que a HEVC.** La tarea decía «AV1 o HEVC»; AV1 va primero porque es libre de regalías (`docs/legal.md`, patentes) y SVT-AV1 es rápido. Es la decisión pendiente de Daniel en `CLAUDE.md`; cambiarla es una línea (`H264Substitutes` en `ExportEncoderChooser`).
2. **El valor por defecto de CPU también es AV1.** Compatibilidad: AV1 no se reproduce en equipos viejos. Daniel puede preferir HEVC o VP9.
3. **Media Foundation cuenta como hardware** (`IsHardware`), aunque puede ser un codificador de software de Windows en equipos sin GPU. La sonda no distingue; si hace falta, se mide.
4. **Sonda con timeout de 60 s** por codificador y de 4 a la vez, para no saturar los motores de la GPU ni la CPU con libaom.
5. **Licencias del catálogo** (BSD-3-Clause-Clear, BSD-2, BSD-3) están escritas de memoria sobre los proyectos; hay que confirmarlas contra `THIRD-PARTY-NOTICES.txt` cuando se complete ese archivo.

## Posibles choques

- `docs/legal.md` dice que H.264 y HEVC se delegan a hardware y que las DLL de OpenH264 y kvazaar por software no traen licencia de patentes de Cisco/consorcios. Este catálogo incluye kvazaar y OpenH264 como último recurso (`PatentEncumbered = true`) y nunca los elige por defecto si hay otra cosa, pero la revisión de patentes del punto pendiente de `legal.md` debe decidir si se exponen al usuario.
- No toca `Proxies/` ni `ProxyEncoders.cs`. Reutiliza `FfmpegExecutable.Run`, `Tail` y la excepción `FfmpegNotFoundException` (internos de Media); si la reorganización del repo (`docs/reorganizacion.md`) los mueve, hay que ajustar los `using`.

## Qué comprobar en Windows

1. Que `FfmpegExportEncoderProbeTests.The_real_probe_lists_what_this_machine_can_encode` corra y pase en la CI de Windows (software y Media Foundation).
2. En la máquina de Daniel (NVIDIA): que NVENC h264/hevc/av1 figuren como disponibles según la generación de la gráfica, y que `ChooseDefault()` dé `h264_nvenc`. Imprimir `report.All` (id, `Works`, `Reason`) para ver los motivos de los que fallan.
3. Que kvazaar acepte `-crf` y `-kvazaar-params preset=...` en FFmpeg 9.0.2 y que OpenH264 acepte `-rc_mode quality`; si no, ajustar los argumentos.
4. Cuánto tarda la sonda completa (esperado unos segundos; AV1 por libaom es el más lento).
