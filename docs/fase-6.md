# Fase 6: exportación

Plan del 11 de octubre de 2026; Daniel respondió las preguntas del final el mismo día (D-028).

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

## Formatos y preajustes

MP4 (por defecto, `+faststart`), MOV, MKV, WebM y solo audio. H.264/HEVC por GPU (NVENC, AMF, QSV) o por Media Foundation; AV1 por CPU con SVT-AV1 (y por GPU solo en RTX 40); VP9 por CPU; FFV1 y DNxHR como maestros; audio AAC (`aac_mf`), Opus, FLAC, PCM, MP3. Preajustes como datos: YouTube 1080p, YouTube 4K, Reels/TikTok 9:16, Máxima calidad, Archivo pequeño, Solo audio, Personalizado; sus números se calibran con VMAF.

## Interfaz

Diálogo de exportar (Ctrl+E y EXPORTAR): preajuste en tarjetas, «Avanzado» plegado, rango, estimación de tamaño y tiempo, y GPU/CPU explicados con honestidad (solo los codificadores que pasan la sonda; AMF y QSV «sin probar»). Progreso en Trabajos → Exportaciones con fase, fps, tiempo restante, miniatura, cancelar, abrir carpeta. Cola de una exportación a la vez con su copia del proyecto; se puede seguir editando.

## Reparto

Paso 0 en serie: `docs/fase-6.md` → D-028, proyecto nuevo `src/Rodaje.Export` y sus pruebas, contrato (`IExportSink`, formatos, `ExportVideoFrame`, `OfflineTimeline`, `OfflineAudio`, `ExportFrameConverter`, `ExportSettings`/`ExportPreset`/`ExportProgress`, `EncoderSpec` alineado con la tarea 13 de la nube) y un prototipo que fija las cifras (caudal de la tubería y fps sin codificar). Luego: A motor fuera de línea, B render de salida, C salida con FFmpeg, D orquestación, E interfaz, F medición y aceptación. Unión 0 → C → B → A → D → E → F, integración y verificación por un agente de otro modelo (Sonnet). Para la nube: codificadores (tarea 13), matriz de contenedores y validación, conformidad del conversor en WARP, laboratorio del sink en la CI, calibración VMAF y licencias.

## Criterios de terminado

- 0 fallos, AOT sin advertencias, `Rodaje.Export` ≥90 % de cobertura.
- **Exactitud:** 100 % de los cuadros con el número esperado por `ClipTimeMap` (3 capas con 2×, 0,5×, 1001/1000×, «Bala», congelado y 4 transiciones; FFV1 y también NVENC); frente a la vista previa, luma ≤1 código y croma ≤2.
- **Color:** barras BT.709 ±1 código; ffprobe `bt709/tv/left`; sin cambio de gamma en VLC, Películas y TV y Edge.
- **A/V tras 10 min:** error medio ≤1 ms, máximo ≤½ cuadro; primer sample en pts 0 ±1.
- **Velocidad (RTX 3050 / Ryzen 7 7435HS), 1080p30, 10 min, 3 capas con efectos y transiciones:** NVENC H.264 ≤4 min (meta 3); SVT-AV1 ≤10 min; sin codificar ≥150 fps.
- **Calidad (VMAF):** YouTube 1080p ≥93 (≥85 en el 1 % peor); Archivo pequeño ≤3 Mbps con ≥85; Máxima calidad ≥97; AV1 CPU ≤60 % del tamaño de NVENC H.264 a VMAF ±1.
- **Cancelación:** 20 al azar, cada una ≤1 s, sin archivo ni proceso vivo, VRAM ±50 MB; tras matar Rodaje, limpieza al reabrir.
- **Archivo** sin errores en ffmpeg, `moov` al principio, se reproduce en Películas y TV, VLC y Edge.
- **App usable al exportar:** ≤1 % de refrescos tarde, edición → pantalla mediana ≤20 ms y p95 ≤40 ms, ningún bloqueo de >50 ms.

**Daniel, a mano:** el mismo proyecto en Rodaje y en CapCut a 1080p (colores, nitidez, bandas, tamaño, tiempo), claridad del diálogo y de GPU/CPU, 9:16 con secuencia 16:9, editar mientras exporta, cancelar.

## Riesgos

AMF, QSV y AV1 por GPU no se pueden probar en el equipo de Daniel; VRAM en gráficas de 2–4 GB; el modo exportación toca el programador (la vista previa debe quedar idéntica con él apagado); la reversa 4K es lenta hasta que se una el lector hacia atrás; la 5b (texto) debe entrar en `OfflineTimeline` sin decodificador; la tubería a 4K60 (~750 MB/s) puede pedir adelantar `NvencDirectSink`; patentes de AAC (se prefiere `aac_mf`); Windows N no trae Media Foundation.

## Preguntas (decididas por Daniel el 11 de octubre; D-028)

1. **Licencia de la v1:** LGPL — H.264 por GPU o Media Foundation (patentes cubiertas por Microsoft), CPU con AV1 (SVT-AV1) y VP9, kvazaar y OpenH264 fuera de los valores por defecto (contradicen la mitigación de patentes de `legal.md`), x264 como complemento GPL aparte más adelante.
2. **Reels/TikTok con secuencia 16:9:** barras por defecto y un botón visible «Cambiar la secuencia a 9:16» (deshacible).
3. **Mientras exporta:** dos modos que se eligen en el diálogo cada vez que se exporta (`ExportSettings.Performance`): «Alto rendimiento (menor tiempo)», con prioridad normal/alta y la vista previa en pausa o al mínimo, y «Bajo rendimiento (app más fluida)», con prioridad baja para seguir editando.
