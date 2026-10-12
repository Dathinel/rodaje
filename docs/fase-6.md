# Fase 6: exportación

Plan del 11 de octubre de 2026. Daniel decidió: LGPL en la v1, 9:16 con barras y botón «Cambiar la secuencia a 9:16», y **dos modos de rendimiento que se eligen al exportar** («Alto rendimiento (menor tiempo)» y «Bajo rendimiento (app más fluida)»). Arquitectura y contrato en D-028.

## Arquitectura

```
Project.Clone() ─► OfflineTimeline (Engine) ─► Compositor (resolución completa)
                                             └► ExportFrameConverter (Render): R'G'B' → Y'CbCr, escala, NV12/P010, anillo de Staging
OfflineAudio (MixRenderer continuo) ─► WAV temporal
ExportRunner (Rodaje.Export) ─► IExportSink ─► FfmpegPipeSink (ffmpeg.exe por tubería, GPU y CPU)
                                            └► NvencDirectSink (textura a NVENC, ronda 6.2 solo si las cifras lo piden)
```

- **Exacto, sin reloj:** `OfflineTimeline.Render(frame)` espera a que todas las capas estén exactas (sin proxies, sin keyframe cercano, sin `SkipNonReference`); lo que en la vista previa se tolera (cuadro no disponible, medio con fallo, LUT o paquete que falta, efecto saltado) aquí es error o se ofrece «continuar sin» de forma explícita. Shaders y LUT se precargan antes del primer cuadro.
- **Dispositivo D3D11 propio** con su hilo y la prioridad del modo de rendimiento (alto o bajo); presupuesto de VRAM aparte (si hay menos de 4 GB, la vista previa pasa a proxies y Mitad mientras exporta).
- **Conversión en la GPU:** BT.709 rango limitado (BT.601 en SD), croma 4:2:0 «left», tramado en 8 bits, P010 para 10 bits SDR; escala con el mip 1 para 2× y Catmull-Rom en lo demás; barras, recorte o estirado para la forma.
- **Audio** mezclado antes a un WAV temporal (sin margen de recarga): evita bloqueos entre tuberías y se prueba muestra a muestra.
- **Salida por `ffmpeg.exe`** como los proxies: proceso aislado, cancelación limpia, licencia separada (el complemento x264 sería otro `ffmpeg.exe` GPL con el mismo protocolo). Escritura atómica (`.rodaje-tmp` y renombrar), espacio en disco comprobado, limpieza tras un apagón.
- **HDR fuera de alcance**; 10 bits SDR dentro.

## Cifras del prototipo (paso 0, 11-oct)

RTX 3050 Laptop / Ryzen 7 7435HS, dos corridas de `tests/Rodaje.Export.Tests/Prototype` (`RODAJE_PRUEBAS_LARGAS=1`), con otros agentes usando la máquina. Cuadros NV12 sintéticos (degradado móvil con ruido) por la entrada estándar de `ffmpeg.exe -f rawvideo`.

| Prueba | fps | MB/s |
|---|---|---|
| Tubería 1080p → `-f null` | 1324–1340 | ~4100 |
| Tubería 4K → `-f null` | 226–303 | 2800–3800 |
| Tubería 1080p → `h264_nvenc` p4 | 339–356 | ~1100 |
| Tubería 4K → `h264_nvenc` / `hevc_nvenc` p4 | 91–92 / 84–88 | ~1100 |
| Tubería 1080p → `libsvtav1` preset 8 (150 cuadros) | 42–80 | — |
| Sin codificar: 3 capas 1080p decodificadas por D3D11VA (H.264, HEVC, H.264; dos en PiP con opacidad), compuestas y bajadas | 247–287 | — |

En el canal sin codificar, por cuadro: espera de decodificación 0,03–0,25 ms, composición 0,2–0,45 ms y copia y bajada (un NV12 1080p a `Staging` con `Map` síncrono, que además espera a la GPU) 3,2–3,3 ms. La bajada es el cuello; el anillo de `ExportFrameConverter` la solapa.

**Conclusiones.**
- La tubería no limita: a 4K mueve 2,8–3,8 GB/s frente a los ~750 MB/s de 4K60. **`NvencDirectSink` no hace falta** en la v1; la ronda 6.2 queda sin plan.
- Con NVENC el cuello será el canal sin codificar, no el codificador. 10 min de 1080p30 (18 000 cuadros) a ~250 fps son ~1,2 min más los efectos. **Objetivo ajustado: NVENC H.264 ≤3 min (meta 2)**, en vez de ≤4 (meta 3).
- Sin codificar ≥150 fps se mantiene: el prototipo da 250–290 con la bajada síncrona.
- SVT-AV1 depende de la carga de la CPU (42–80 fps con otros procesos): ≤10 min se mantiene; el agente F lo mide con el equipo libre.

## Cifras de la aceptación (agente F, 11-oct)

RTX 3050 Laptop 4 GB / Ryzen 7 7435HS (16 hilos), Debug, con otros agentes usando la máquina (carga de CPU del 16–21 % al medir los tiempos, 85–100 % en algunas repeticiones). Pruebas en [`tests/Rodaje.Export.Tests/Acceptance`](../tests/Rodaje.Export.Tests/Acceptance) con `RODAJE_PRUEBAS_LARGAS=1`.

**Escena** ([`AcceptanceScene`](../tests/Rodaje.Export.Tests/Acceptance/AcceptanceScene.cs)): 10 min de 1080p30 (18 000 cuadros), 3 capas y 129 clips. La principal repite un bloque de 60 s: 1× con color, 2×, 0,5×, 1001/1000×, «Bala» y congelado, con fundido, cortinilla, empuje y fundido a negro. Encima van dos PiP con transformación y color: un HEVC a 25 fps que alterna 1× y 2×, y un H.264 a 23,976 con fundidos. En total, 63 transiciones. El audio del medio principal suena a 1× en toda la duración. Cada capa deja su número de cuadro legible en un lugar fijo, y lo esperado sale de `ClipTimeMap`.

| Criterio | Cifra | |
|---|---|---|
| NVENC H.264 10 min ≤3 min (meta 2) | **93,5 s** (192 fps, 12 Mbps VBR + AAC, alto rendimiento) | CUMPLE |
| SVT-AV1 10 min ≤10 min | **205 s** (88 fps, preset 8, CRF 35) | CUMPLE |
| Sin codificar ≥150 fps | **258 fps** en los 18 000 cuadros (motor, composición, NV12 y bajada); 30 de los 70 s, esperando a la GPU | CUMPLE |
| 100 % de cuadros por su número, NVENC | **53 055/53 055** lecturas en 18 000 cuadros (945 tapadas por transiciones, no se cuentan) | CUMPLE |
| 100 % de cuadros por su número, FFV1 | **5310/5310** en el bloque de 60 s (1800 cuadros, 90 tapadas) | CUMPLE |
| Frente a la vista previa (luma ≤1, croma ≤2) | FFV1 contra `TimelineSession` + `Compositor` leídos en RGBA16F y convertidos en la CPU, en 14 cuadros (cada velocidad, congelado y las 4 transiciones): **luma máx. 1, croma máx. 1** | CUMPLE |
| A/V tras 10 min (medio ≤1 ms, máx. ≤½ cuadro, pts 0 ±1) | 600 marcas de audio, **error medio 0,000 ms, máximo 0,000 ms**; `start_time` 0 en video y en audio | CUMPLE |
| `moov` al principio, `bt709/tv/left` | Lo comprueba el verificador en cada exportación de la prueba | CUMPLE |
| Recorte por región | Cuadrante de abajo a la izquierda a 960×540: **90/90** cuadros con el número del PiP | CUMPLE (arreglado) |
| VMAF YouTube 1080p ≥93 (≥85 en el 1 % peor) | **99,96** (1 % peor 98,9) a 8,7 Mbps | CUMPLE |
| VMAF Archivo pequeño ≤3 Mbps con ≥85 | Con CRF 35 sin techo daba 3,36 Mbps. Recalibrado a CRF 38 con techo de 3 Mbps: **2,06 Mbps, VMAF 96,5** (1 % peor 88,6) | CUMPLE (recalibrado) |
| VMAF Máxima calidad ≥97 | **99,96** (HEVC 10 bits CQ 18, 6,3 Mbps) | CUMPLE |
| AV1 ≤60 % del tamaño de NVENC a VMAF ±1 | NVENC CQ 23: 99,96 a 4,95 Mbps; AV1 CRF 41: 99,82 a 2,57 Mbps = **52 %** | CUMPLE |
| 20 cancelaciones ≤1 s, sin archivo ni proceso, VRAM ±50 MB | 4 corridas. Con la máquina al 16–91 %: mediana 14–139 ms y máximo 159–609 ms. Con la máquina al 100 % por otros agentes, una de 2,5 s. Ningún archivo ni `ffmpeg.exe` vivo; VRAM ±0,0 MB (+203 MB mientras exporta) | CUMPLE, salvo un caso con la máquina saturada |
| Tras matar Rodaje, limpieza al reabrir | El proceso hijo de prueba se mata con `Kill`: su `ffmpeg.exe` muere solo en 40–540 ms y `CleanupStale` borra el temporal y la carpeta. Una vez el temporal seguía abierto y la entrada se perdía; ahora se queda para el próximo arranque | CUMPLE (arreglado) |
| App usable: ≤1 % refrescos tarde | 3 corridas de 25 s a 60 Hz con 3 capas 1080p a resolución completa. Exportando: 0–0,93 % tarde, salvo la corrida con la máquina al 91 %, que dio 3,7–5,1 % en modo bajo | CUMPLE con la máquina normal |
| App usable: edición → pantalla mediana ≤20 ms, p95 ≤40 ms | Exportando: mediana 1,3–4,7 ms, p95 1,8–41,5 ms; con la máquina al 91 %, p95 de hasta 368 ms | CUMPLE con la máquina normal |
| App usable: ningún bloqueo >50 ms | Bloqueos de 51–115 ms con NVENC y de 52–326 ms con AV1. **También sin exportar** (59–370 ms), siempre en los cortes y transiciones de la vista previa (entrada al clip a 2× en 10 s, PiP en 20 y 24,5 s) | **NO CUMPLE** (la causa es la vista previa, no la exportación) |
| Rodaje.Export ≥90 % de cobertura | 94,6 % de líneas | CUMPLE |
| AOT sin advertencias | `app.ps1 -SoloPublicar` sobre esta rama: la app publicada con Native AOT sin advertencias de recorte ni de AOT | CUMPLE |

**Modo bajo.** Antes del ajuste los dos modos dejaban la vista previa igual, y el bajo solo bajaba la prioridad de la CPU (NVENC alto 234 fps, bajo 186 fps). Ahora el dispositivo de la exportación baja su prioridad en el planificador de la GPU (`ExportGpuPriority`, −7). La exportación cede de verdad: NVENC pasa de 167–229 fps en alto a 118–121 fps en bajo, y AV1 de 37–75 a 25–32 fps. La vista previa mejora en algunas corridas (p95 de edición 21 frente a 41,5 ms; con AV1, ningún bloqueo frente a 52 ms) y queda igual en otras: la diferencia está dentro del ruido de la máquina compartida.

**Pendiente:**
- Los bloqueos de la vista previa en cortes con velocidad y transiciones, que salen también sin exportar (pre-roll del motor).
- Repetir las cifras de fluidez y cancelación con el equipo libre.
- Lo que solo puede juzgar Daniel: reproducción en VLC, Películas y TV y Edge, y la comparación con CapCut.
- La escena es sintética (ruido en movimiento y números): los VMAF reales serán distintos, pero los preajustes tienen margen.

## Formatos y preajustes

MP4 (por defecto, `+faststart`), MOV, MKV, WebM y solo audio. H.264/HEVC por GPU (NVENC, AMF, QSV) o por Media Foundation; AV1 por CPU con SVT-AV1 (y por GPU solo en RTX 40); VP9 por CPU; FFV1 y DNxHR como maestros; audio AAC (`aac_mf`), Opus, FLAC, PCM, MP3. Preajustes como datos: YouTube 1080p, YouTube 4K, Reels/TikTok 9:16, Máxima calidad, Archivo pequeño, Solo audio, Personalizado; sus números se calibran con VMAF.

## Alcance profesional (pedido por Daniel el 11-oct)

La exportación debe ser completa, como la de una app profesional, con todo lo que permite el FFmpeg LGPL; lo que necesite otra licencia va como complemento aparte en un repo de Dathinel.

- **Contenedores:** MP4, MOV, MKV, WebM, AVI, MXF (OP1a), MPEG-TS, GIF animado y WebP animado, secuencia de imágenes (PNG, TIFF, JPEG, EXR si el build lo trae) y solo audio (WAV, FLAC, MP3, M4A/AAC, Opus, AIFF).
- **Códecs de video:** H.264 y HEVC (GPU y Media Foundation), AV1 (SVT-AV1, libaom; GPU en RTX 40), VP9, ProRes (422 Proxy/LT/Standard/HQ y 4444 con alfa, codificador nativo de FFmpeg), DNxHR (LB/SQ/HQ/HQX/444), CineForm si el build lo trae, FFV1, MJPEG, PNG/QuickTime Animation para alfa, GIF/WebP.
- **Control de tasa:** CRF/CQ (calidad constante), VBR (media y máxima), CBR, ABR y **dos pasadas** donde el codificador lo admita; tamaño objetivo que calcula la tasa; tasa en Mbps o calidad.
- **Parámetros avanzados:** perfil y nivel (H.264 Baseline/Main/High/High 10/High 4:2:2; HEVC Main/Main10/Main 4:2:2), preset de velocidad del codificador, GOP (longitud, cerrado/abierto, keyframe cada N s), cuadros B y referencias, ajuste psicovisual/tune donde exista, entrelazado no (solo progresivo), 8/10 bits, submuestreo 4:2:0/4:2:2/4:4:4 si el códec lo admite, canal alfa (ProRes 4444, PNG, WebM VP9 con alfa), rango y espacio de color etiquetados (BT.709, BT.601, sRGB para imágenes), velocidad de cuadro (la de la secuencia o conversión a 23,976/24/25/29,97/30/50/59,94/60 con mezcla o repetición), resolución con escala de calidad y forma (barras, recorte, estirar), recorte por rango o región.
- **Audio:** AAC (LC, HE), Opus, MP3, FLAC, ALAC, PCM 16/24/32 float; 44,1/48/96 kHz; mono, estéreo y 5.1 si el proyecto lo tiene; tasa en kbps o calidad; normalización de sonoridad opcional a LUFS (−14 para plataformas, −23 EBU R128) con medición previa.
- **Metadatos y extras:** título, autor, fecha, capítulos desde los marcadores, subtítulos como pista (SRT/WebVTT) cuando llegue la fase 7, miniatura incrustada, `faststart`.
- **Preajustes propios y del usuario:** los de fábrica (YouTube, Vimeo, Instagram/Reels, TikTok, X, maestro ProRes/DNxHR, archivo pequeño, GIF) y guardar/importar/exportar preajustes propios como archivo; los preajustes también pueden venir en un `.paquete`.
- **Validación honesta:** el diálogo solo ofrece combinaciones válidas (contenedor × códec × perfil × píxel × audio) y explica por qué otra no lo es.
- **Complementos de exportación en GitHub de Dathinel** (se instalan aparte, como los demás complementos): `rodaje-codificadores-gpl` con un `ffmpeg.exe` GPL que trae x264, x265 y fdk-aac no (no es libre), usado por el mismo protocolo de tubería (frontera de proceso; consultar licencias antes de publicar); preajustes de plataformas como `.paquete`.

## Interfaz

Diálogo de exportar (Ctrl+E y EXPORTAR): preajuste en tarjetas, «Avanzado» plegado, rango, estimación de tamaño y tiempo, y GPU/CPU explicados con honestidad (solo los codificadores que pasan la sonda; AMF y QSV «sin probar»). Progreso en Trabajos → Exportaciones con fase, fps, tiempo restante, miniatura, cancelar, abrir carpeta. Cola de una exportación a la vez con su copia del proyecto; se puede seguir editando.

## Reparto

Paso 0 en serie: `docs/fase-6.md` → D-028, proyecto nuevo `src/Rodaje.Export` y sus pruebas, contrato (`IExportSink`, formatos, `ExportVideoFrame`, `OfflineTimeline`, `OfflineAudio`, `ExportFrameConverter`, `ExportSettings`/`ExportPreset`/`ExportProgress`, `EncoderSpec` alineado con la tarea 13 de la nube) y un prototipo que fija las cifras (caudal de la tubería y fps sin codificar). Luego: A motor fuera de línea, B render de salida, C salida con FFmpeg, D orquestación, E interfaz, F medición y aceptación. Unión 0 → C → B → A → D → E → F, integración y verificación por un agente de otro modelo (Sonnet). Para la nube: codificadores (tarea 13), matriz de contenedores y validación, conformidad del conversor en WARP, laboratorio del sink en la CI, calibración VMAF y licencias.

## Criterios de terminado

- 0 fallos, AOT sin advertencias, `Rodaje.Export` ≥90 % de cobertura.
- **Exactitud:** 100 % de los cuadros con el número esperado por `ClipTimeMap` (3 capas con 2×, 0,5×, 1001/1000×, «Bala», congelado y 4 transiciones; FFV1 y también NVENC); frente a la vista previa, luma ≤1 código y croma ≤2.
- **Color:** barras BT.709 ±1 código; ffprobe `bt709/tv/left`; sin cambio de gamma en VLC, Películas y TV y Edge.
- **A/V tras 10 min:** error medio ≤1 ms, máximo ≤½ cuadro; primer sample en pts 0 ±1.
- **Velocidad (RTX 3050 / Ryzen 7 7435HS), 1080p30, 10 min, 3 capas con efectos y transiciones:** NVENC H.264 ≤3 min (meta 2; ajustado con el prototipo); SVT-AV1 ≤10 min; sin codificar ≥150 fps.
- **Calidad (VMAF):** YouTube 1080p ≥93 (≥85 en el 1 % peor); Archivo pequeño ≤3 Mbps con ≥85; Máxima calidad ≥97; AV1 CPU ≤60 % del tamaño de NVENC H.264 a VMAF ±1.
- **Cancelación:** 20 al azar, cada una ≤1 s, sin archivo ni proceso vivo, VRAM ±50 MB; tras matar Rodaje, limpieza al reabrir.
- **Archivo** sin errores en ffmpeg, `moov` al principio, se reproduce en Películas y TV, VLC y Edge.
- **App usable al exportar:** ≤1 % de refrescos tarde, edición → pantalla mediana ≤20 ms y p95 ≤40 ms, ningún bloqueo de >50 ms.

**Daniel, a mano:** el mismo proyecto en Rodaje y en CapCut a 1080p (colores, nitidez, bandas, tamaño, tiempo), claridad del diálogo y de GPU/CPU, 9:16 con secuencia 16:9, editar mientras exporta, cancelar.

## Riesgos

AMF, QSV y AV1 por GPU no se pueden probar en el equipo de Daniel; VRAM en gráficas de 2–4 GB; el modo exportación toca el programador (la vista previa debe quedar idéntica con él apagado); la reversa 4K es lenta hasta que se una el lector hacia atrás; la 5b (texto) debe entrar en `OfflineTimeline` sin decodificador; la tubería a 4K60 (~750 MB/s) ya no es riesgo (el prototipo mueve 2,8 GB/s o más); patentes de AAC (se prefiere `aac_mf`); Windows N no trae Media Foundation.

## Preguntas (decididas por Daniel el 11 de octubre; D-028)

1. **Licencia de la v1:** LGPL — H.264 por GPU o Media Foundation (patentes cubiertas por Microsoft), CPU con AV1 (SVT-AV1) y VP9, kvazaar y OpenH264 fuera de los valores por defecto (contradicen la mitigación de patentes de `legal.md`), x264 como complemento GPL aparte más adelante.
2. **Reels/TikTok con secuencia 16:9:** barras por defecto y un botón visible «Cambiar la secuencia a 9:16» (deshacible).
3. **Mientras exporta:** dos modos que se eligen en el diálogo cada vez que se exporta (`ExportSettings.Performance`): «Alto rendimiento (menor tiempo)», con prioridad normal/alta y la vista previa en pausa o al mínimo, y «Bajo rendimiento (app más fluida)», con prioridad baja para seguir editando.
