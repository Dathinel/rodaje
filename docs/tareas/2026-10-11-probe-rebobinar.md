# Tarea 10: `MediaProbe.ScanTimestamps` no rebobina en MP4

## Qué cambió

- `src/Rodaje.Media/Probe/MediaProbe.cs`: el rebobinado tras `ScanTimestamps` pasa a `RewindToStart`, que busca la marca de inicio del flujo con `avformat_seek_file(min = long.MinValue, ts = max = inicio)` y, si falla, `av_seek_frame(inicio, BACKWARD)`. Antes buscaba `long.MinValue`, que en MP4 no mueve la posición.
- `tests/Rodaje.Media.Tests/ProbeRewindTests.cs` (nuevo): tras el análisis, el primer paquete (flujo, dts, posición en el archivo) es el mismo que el de un archivo recién abierto, para H.264 MP4, HEVC MP4 y H.264 MKV; y el análisis no cambia su resultado.
- `Rodaje.Media.Tests.csproj`: `AllowUnsafeBlocks` para poder leer paquetes en la prueba.

## Estado de las pruebas (honesto)

- **No pude ejecutar las pruebas nuevas aquí.** No pude bajar el FFmpeg 9.0.2 de BtbN (404 desde el entorno de la nube) y el del sistema es la 6.1, incompatible con los bindings. Compila sin advertencias; en Linux las dos pruebas se omiten (`FfmpegLibraryTheory`).
- Por eso **no reproduje el fallo antes del arreglo**; la causa viene del enunciado de la tarea y del comentario de `KeyframeIndex.cs:122`. Debe confirmarlo la CI de Windows: ver que las pruebas corren y que fallarían con el código anterior (cambiar `RewindToStart` por la línea vieja).
- Pruebas omitidas: no se omite ni excluye ninguna existente (antes 0 cambios, después 0 cambios); las 2 teorías nuevas solo se omiten sin FFmpeg.

## Pendiente / decisiones

- `KeyframeIndex` y `VideoDecoder` siguen reabriendo/buscando; no se tocaron.
- Sin choques conocidos con `origin/main` (PR #5, flush, ya unido).

## Qué comprobar en Windows

`ABRIR.bat`: `ProbeRewindTests` (6 casos) deben correr y pasar; el resto de Media, igual que antes.
