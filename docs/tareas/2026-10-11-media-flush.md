# Estado del decodificador tras un flush (Rodaje.Media)

Tarea 4 de la cola, atendiendo la revisión del 11-oct (el bloqueo anterior no era real).

## Causa
Tras paquetes dañados, `avcodec_flush_buffers` no reinicia todo el estado interno de `h264dec`: el siguiente seek devuelve cuadros malos, incluso desde un IDR (reproducido en Linux con el FFmpeg 9.0.2 de BtbN y el H.264 de libopenh264, hilos 0 y 1). Los 7 keyframes del clip son IDR, así que no es un problema de puntos de recuperación ni de cuadros B.

Hallazgo nuevo: parte del daño es **silencioso**: el paquete con el centro lleno de basura decodifica sin error de `send_packet`/`receive_frame`, así que contar solo `_decodeErrors` no basta. Esos cuadros llegan con `decode_error_flags != 0` o `AV_FRAME_FLAG_CORRUPT`.

## Cambio
`src/Rodaje.Media/Video/VideoDecoder.cs`:
- `ResetDecoder` reabre el contexto del códec (`ReopenCodec`: libera, suelta el `GCHandle` propio y llama a `OpenCodec`) si desde el último reset hubo errores de decodificación o un cuadro marcado como dañado; si no, conserva el `avcodec_flush_buffers` de siempre. En el camino de hardware, `get_format` vuelve a crear el pool de superficies al reabrir (igual que en la primera apertura).
- `NextFrame` marca `_damagedFrame` al recibir un cuadro con `decode_error_flags` o `AV_FRAME_FLAG_CORRUPT`.

## Segunda revisión (11-oct)
- **Hilos por cuadro (`h264_mf`, `SoftwareThreads=0`):** el decodificador retiene hasta ~15 paquetes y `avcodec_flush_buffers` se traga sus errores. Nuevo `DrainFrameThreads`: antes del flush, con `FF_THREAD_FRAME` activo, envía el fin de flujo y recoge los cuadros pendientes; si alguno trae error o marca de daño, o hay errores, el reset reabre el contexto. Sin daño sigue el flush (el drenaje solo espera lo que el flush esperaría de todos modos).
- **`skip_frame` (5c):** `ReopenCodec` conserva el `skip_frame` del contexto viejo y lo aplica al nuevo. `GetFrame` reabre además si `_codec` es `null` antes de fijarlo.
- **Fallo de apertura:** `ReopenCodec` libera el contexto si `avcodec_open2` falla y deja `_codec = null`; los contadores solo se ponen a cero si la reapertura sale bien, y el siguiente seek reintenta.
- **Rama:** traje `origin/main` con un merge (sin reescribir historia) para trabajar sobre el código actual (5c); no hubo conflictos. Se quitó `Encoder = "h264_nvenc"` de `Corrupted_packets…` (ya no hace falta).
- **Prueba nueva:** `Damage_still_inside_the_decoder_at_a_seek_does_not_leak_into_a_later_gop` (hilos 0 y 1): parar justo antes de la zona dañada y pedir el último GOP hasta el final. **Límite:** con libopenh264 (lo único que hay en Linux y en la CI sin NVENC) pasa también sin el drenaje, así que no reproduce el caso de `h264_mf`; solo se puede comprobar con `h264_mf` en Windows.
- **Costo en hardware (aviso):** al reabrir en hardware, `HardwarePoolSizer` se vuelve a llamar y pisa `decoder.Reserved` (la VRAM del pool viejo que retiene la caché deja de contarse), y la creación del pool puede esperar hasta 300 ms (`PoolWait`) dentro de un seek. Solo ocurre tras daños.
- **Medido (Linux, libopenh264):** Media.Tests completa 46 fallos en `main` → 45 con el cambio; los 45 son idénticos (hardware/Windows) y `Corrupted_packets_are_skipped_without_throwing` pasa.

## Prueba
`VideoDecoderBehaviorTests.A_seek_after_decoding_errors_restores_the_decoder_state` (hilos 0 y 1): decodifica el clip dañado entero, pide el cuadro 150 y luego del 0 al 47 contra la referencia. Falla sin el arreglo y pasa con él. Es independiente del codificador (usa el clip H.264 que haya: libopenh264 sin NVENC).

## Medido en Linux (FFmpeg 9.0.2 BtbN, libopenh264)
- `Corrupted_packets_are_skipped_without_throwing` y la prueba nueva (2 casos): fallaban, ahora pasan.
- Suite `Rodaje.Media.Tests` completa: 39 fallos antes, 36 después; los 36 restantes son idénticos antes y después y son de hardware D3D11 o de Windows (`FrameLifetimeTests`, `HardwarePoolTests`, `Proxies/*`, `Hevc_main10_reports_ten_bits`, `DecodeThroughputTests`).
- `Random_access_*`: no se pudo medir por separado (los casos parametrizados fallan en Linux por descubrimiento, igual que antes). Un clip sin errores ni cuadros dañados no reabre nunca el contexto, así que su camino no cambia; pedir a Daniel que compare `Random_access_*` con `ABRIR.bat`.

## Pruebas omitidas
Sin cambios: 0 antes, 0 después. No se tocó `[FfmpegLibraryFact]` de `Corrupted_packets…`, que en `main` no está limitado a NVENC (la restricción vive en el PR #2).

## Choques
Ninguno pendiente: la restricción a NVENC de `Corrupted_packets…` ya se quitó aquí.

## Qué debe comprobar Daniel en Windows
1. `ABRIR.bat`: `Corrupted_packets_are_skipped_without_throwing` y las dos pruebas nuevas pasan, también con `h264_mf` y NVENC; en especial el caso `SoftwareThreads=0` de `Damage_still_inside…`.
2. `Corrupted_packets_are_skipped_in_hardware_too` sigue pasando (la reapertura toca el pool de superficies en hardware).
3. Tiempos de `Random_access_*` iguales a los de antes.
