# Estado del decodificador tras un flush (Rodaje.Media)

Tarea 4 de la cola. **Bloqueada: sin cambios de código.** No se pudo reproducir el fallo en la nube.

## Bloqueo
- La sesión corre en Linux con FFmpeg 6.1 del sistema (libavcodec 60) y sin `h264_mf` (solo Windows).
- `Rodaje.Media` usa los enlaces de FFmpeg 9 de `.entorno\ffmpeg` (solo Windows), así que `Rodaje.Media.Tests` no compila ni corre aquí.
- Sin reproducir el fallo, cambiar `Seek`/`ResetDecoder` sería adivinar, y el criterio de la tarea (que la prueba corra con `h264_mf` en la CI de Windows) no se puede comprobar desde aquí.

## Lo que se leyó (`src/Rodaje.Media/Video/VideoDecoder.cs`)
- `Seek` (l. 442) llama a `ResetDecoder` (l. 537: `avcodec_flush_buffers`, vacía paquete y cuadros, reinicia contadores) antes de cada `av_seek_frame`, y luego `ReadFirstKeyPacket` (l. 499) acepta el primer paquete con `AV_PKT_FLAG_KEY`.
- En modo software el contexto usa `FF_THREAD_FRAME | FF_THREAD_SLICE` con `thread_count` automático (l. 262-263).

## Hipótesis a comprobar en Windows (de más a menos probable)
1. **Paquetes «key» que no son IDR.** `h264_mf` puede marcar como `KEY` un cuadro I sin punto de recuperación IDR. Tras la zona dañada, el decodificador sigue con referencias rotas aunque `Seek` empiece en ese paquete; con NVENC todos los keyframes son IDR. Diagnóstico: volcar con `ffprobe -show_frames` el `pict_type`/`key_frame` del primer paquete tras `FirstKeyFrameAfterCorruption` y mirar si el NAL es tipo 5 (IDR) o tipo 1.
2. **Hilos por cuadro.** Con `FF_THREAD_FRAME` el flush debe vaciar los hilos de trabajo; si la prueba comparara con `thread_count = 1` y coincidiera, la causa está ahí.
3. **Cuadros B ausentes.** `h264_mf` no produce cuadros B (ver PR #2), y la tolerancia `TestClips.H264.BFrames + 1` de la prueba (l. 118) puede dejar fuera de la zona dañada un cuadro que sí depende de ella.

## Qué debe comprobar Daniel / la sesión local
Correr `Corrupted_packets_are_skipped_without_throwing` con un clip hecho con `h264_mf` y anotar qué índice `n` falla, su `pict_type` y si el paquete de recuperación es IDR. Con eso la tarea se puede retomar con un arreglo concreto.

## Pruebas omitidas
Sin cambios: 0 antes, 0 después.
