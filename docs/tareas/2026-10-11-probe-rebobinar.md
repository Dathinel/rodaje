# Tarea 10: `MediaProbe.ScanTimestamps` no rebobina en MP4

## Qué cambió

- `src/Rodaje.Media/Probe/MediaProbe.cs`: tras `ScanTimestamps`, `RewindToStart` busca `min(stream->start_time, primer dts de video leído)` con `avformat_seek_file(min = long.MinValue, ts = max = inicio)` y, si falla, `av_seek_frame(inicio, BACKWARD)`. Antes buscaba `long.MinValue`, que en MP4, MOV y AVI no mueve la posición. El dts mínimo evita perder el primer keyframe en MPEG-TS con cuadros B (allí `start_time` es pts y la búsqueda va por dts).
- `tests/Rodaje.Media.Tests/ProbeRewindTests.cs` (nuevo): 5 casos (H.264 MP4, HEVC MP4, MKV, y remux a MOV y a MPEG-TS con cuadros B). Tras el análisis, el primer paquete de video (dts y posición) y el número de paquetes de video coinciden con una apertura limpia.
- `tests/Rodaje.Media.Tests/Support/TestClips.cs`: `Remuxed` para los casos MOV y TS. `Rodaje.Media.Tests.csproj`: `AllowUnsafeBlocks` para leer paquetes en la prueba.

## Evidencia

- Con el código anterior fallan 3 de 5 casos; sin el dts mínimo falla el caso TS (298 de 300 paquetes); con el arreglo pasan los 5.
- Revisión independiente con 16 medios (MP4 con B, desfase y audio por delante, fragmentado, MOV, MKV, WebM, 4 MPEG-TS, 2 AVI, VFR): los 16 coinciden en primer dts, posición y número de paquetes, y `MediaInfo` es idéntico al de antes. El código anterior fallaba en 7 (MPEG-4 en AVI quedaba en 0 paquetes).
- CI de Windows: los 5 casos corren y pasan; ninguna omisión nueva.

## Límite conocido

`Remuxed` copia solo el video (`-map 0:v`): MPEG-TS con el audio por delante se comprobó a mano, no en la prueba.
