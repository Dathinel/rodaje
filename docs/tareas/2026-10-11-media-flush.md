# Estado del decodificador tras un flush (Rodaje.Media)

Tarea 4 de la cola, atendiendo la revisión del 11-oct (el bloqueo anterior no era real).

## Causa
Tras paquetes dañados, `avcodec_flush_buffers` no reinicia todo el estado interno de `h264dec`: el siguiente seek devuelve cuadros malos, incluso desde un IDR (reproducido en Linux con el FFmpeg 9.0.2 de BtbN y el H.264 de libopenh264, hilos 0 y 1). Los 7 keyframes del clip son IDR, así que no es un problema de puntos de recuperación ni de cuadros B.

Hallazgo nuevo: parte del daño es **silencioso**: el paquete con el centro lleno de basura decodifica sin error de `send_packet`/`receive_frame`, así que contar solo `_decodeErrors` no basta. Esos cuadros llegan con `decode_error_flags != 0` o `AV_FRAME_FLAG_CORRUPT`.

## Cambio
`src/Rodaje.Media/Video/VideoDecoder.cs`:
- `ResetDecoder` reabre el contexto del códec (`ReopenCodec`: libera, suelta el `GCHandle` propio y llama a `OpenCodec`) si desde el último reset hubo errores de decodificación o un cuadro marcado como dañado; si no, conserva el `avcodec_flush_buffers` de siempre. En el camino de hardware, `get_format` vuelve a crear el pool de superficies al reabrir (igual que en la primera apertura).
- `NextFrame` marca `_damagedFrame` al recibir un cuadro con `decode_error_flags` o `AV_FRAME_FLAG_CORRUPT`.

## Prueba
`VideoDecoderBehaviorTests.A_seek_after_decoding_errors_restores_the_decoder_state` (hilos 0 y 1): decodifica el clip dañado entero, pide el cuadro 150 y luego del 0 al 47 contra la referencia. Falla sin el arreglo y pasa con él. Es independiente del codificador (usa el clip H.264 que haya: libopenh264 sin NVENC).

## Medido en Linux (FFmpeg 9.0.2 BtbN, libopenh264)
- `Corrupted_packets_are_skipped_without_throwing` y la prueba nueva (2 casos): fallaban, ahora pasan.
- Suite `Rodaje.Media.Tests` completa: 39 fallos antes, 36 después; los 36 restantes son idénticos antes y después y son de hardware D3D11 o de Windows (`FrameLifetimeTests`, `HardwarePoolTests`, `Proxies/*`, `Hevc_main10_reports_ten_bits`, `DecodeThroughputTests`).
- `Random_access_*`: no se pudo medir por separado (los casos parametrizados fallan en Linux por descubrimiento, igual que antes). Un clip sin errores ni cuadros dañados no reabre nunca el contexto, así que su camino no cambia; pedir a Daniel que compare `Random_access_*` con `ABRIR.bat`.

## Pruebas omitidas
Sin cambios: 0 antes, 0 después. No se tocó `[FfmpegLibraryFact]` de `Corrupted_packets…`, que en `main` no está limitado a NVENC (la restricción vive en el PR #2).

## Choques
El PR #2 (`claude/ci-windows-verde`) restringe `Corrupted_packets_are_skipped_without_throwing` a `h264_nvenc`: al unir este PR hay que quitarle esa restricción. Es un cambio de una línea en `VideoDecoderBehaviorTests.cs`, que ambos PR tocan.

## Qué debe comprobar Daniel en Windows
1. `ABRIR.bat`: `Corrupted_packets_are_skipped_without_throwing` y la prueba nueva pasan, también con `h264_mf` y NVENC.
2. `Corrupted_packets_are_skipped_in_hardware_too` sigue pasando (la reapertura toca el pool de superficies en hardware).
3. Tiempos de `Random_access_*` iguales a los de antes.
