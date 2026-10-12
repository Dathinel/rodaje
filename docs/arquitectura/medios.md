# Medios: `Rodaje.Media`

Abre, analiza y decodifica los archivos con FFmpeg 9 LGPL; crea el dispositivo Direct3D 11 que comparten el decodificador (D3D11VA) y el compositor; y genera proxies. Es `net10.0-windows` e `IsAotCompatible`, y referencia [Núcleo](nucleo.md) (por `Time` y `Rational`) y [Reproducción](reproduccion.md) (por `DecodedFrame` e `IVideoFrameSource`). Usa FFmpeg.AutoGen (enlace dinámico) y TerraFX.Interop.Windows. La usan el [Motor](motor.md) y el [Render](render.md).

## Cargar FFmpeg sin arriesgar la licencia

Rodaje no es GPL, así que solo carga un FFmpeg LGPL con la versión verificada y por ruta absoluta.

- **Dónde busca.** [`FfmpegLocator.Candidates`](../../src/Rodaje.Media/FfmpegLocator.cs) arma la lista de carpetas en este orden:
  1. la carpeta explícita;
  2. `RODAJE_FFMPEG`, que se ignora si junto al ejecutable hay una carpeta `ffmpeg\`;
  3. `ffmpeg\` junto al ejecutable (la distribución);
  4. `RODAJE_ENTORNO\ffmpeg\bin`;
  5. `.entorno\ffmpeg\bin` subiendo desde el ejecutable, y desde un worktree de git también el `.entorno` del checkout principal.
  Gana la primera carpeta con avutil-61, swresample-7, swscale-10, avcodec-63 y avformat-63. Los nombres de las variables están en [`EnvironmentVariables`](../../src/Rodaje.Media/EnvironmentVariables.cs) ([tabla](../variables-entorno.md)).
- **Cómo carga.** [`FfmpegLibrary.EnsureInitialized`](../../src/Rodaje.Media/FfmpegLibrary.cs) es idempotente y recuerda un fallo para repetirlo en las llamadas siguientes.
  - Instala un resolvedor de DLL que nunca deja a Windows buscar en el PATH, donde podría haber un FFmpeg GPL.
  - Comprueba las versiones mayores y rechaza cualquier biblioteca configurada con `--enable-gpl` o `--enable-nonfree`.
  - `TryInitialize(out reason)` da el motivo en español; `CompanionLibraryProblem` aplica la misma verificación a avfilter y avdevice, que usa el `ffmpeg.exe` de los proxies.

## GPU compartida (`Gpu/`)

- **[`GpuDevice`](../../src/Rodaje.Media/Gpu/GpuDevice.cs).** `TryCreate` crea un dispositivo D3D11 (nivel 11_1 u 11_0, con soporte de video si existe) y activa la protección multihilo. Sin hardware devuelve null y todo se decodifica en CPU.
- **D3D11VA sin copias.** Si hay D3D11VA, el contexto de FFmpeg se construye sobre **este** dispositivo, así que los cuadros decodificados son texturas que el compositor muestrea directamente. `LockContext`/`UnlockContext` usan el mismo candado que FFmpeg y son obligatorios en secuencias de varias llamadas.
- **Lo que expone.** `SupportsHardwareDecoding`, `SupportsNv12Textures`, `SupportsP010Textures`, `DedicatedVideoMemory` y `QueryLocalVideoMemoryUsage`; el motor usa la VRAM para su presupuesto.
- **[`D3D11TexturePayload`](../../src/Rodaje.Media/Gpu/D3D11TexturePayload.cs)** es una capa de un arreglo de texturas con conteo de referencias. **[`DecoderSurfacePool`](../../src/Rodaje.Media/Gpu/DecoderSurfacePool.cs)** describe el arreglo de un decodificador (superficies, bytes, si ya se cerró).

## Decodificar video (`Video/`)

[`VideoDecoder`](../../src/Rodaje.Media/Video/VideoDecoder.cs) implementa `IVideoFrameSource`. El cuadro n es el que tiene pts igual a `start_time + n / fps`.

**Seek exacto.** `GetFrame(n)` funciona así:

1. Si n está hasta 64 cuadros por delante, sigue decodificando sin seek.
2. Si no, busca hacia atrás y comprueba que el pts del keyframe no pase del objetivo, porque el índice del contenedor puede ir por DTS. Si se pasa, retrocede más, hasta 24 intentos.
3. Descarta los cuadros iniciales de un GOP abierto y los anteriores al objetivo.
4. En VFR, un hueco lo cubre el cuadro anterior.

Antes de cada seek, `ResetDecoder` vacía los hilos por cuadro (`DrainFrameThreads`, hasta 64 cuadros) para ver el daño que quedaba dentro del decodificador; si hubo errores o cuadros dañados desde el último reset, reabre el códec (`ReopenCodec`, conservando `skip_frame`) en lugar del flush, porque `avcodec_flush_buffers` no limpia todo el estado de `h264dec`. Con clips limpios no reabre nunca y el costo es el del flush. Tras el flush recoge y tira lo que el decodificador todavía devuelva sin paquetes nuevos (`DiscardStaleOutput`): el HEVC de FFmpeg sin hilos por cuadro (el camino de D3D11VA) pasa varios cuadros de una vez a su cola de salida, al final del flujo o en un IRAP, y `avcodec_flush_buffers` no la vacía; sin esto, tras un seek salían cuadros del sitio anterior y, con GOP abierto, la petición exacta acababa en null.

**GOP abierto (HEVC con CRA).** Al buscar un CRA, sus cuadros RASL (anteriores en presentación, posteriores en decodificación) no se pueden decodificar y FFmpeg los tira; `_minValidSlot` descarta además lo que salga antes del keyframe. Si se pide un RASL, el keyframe del CRA queda por encima del objetivo y el seek retrocede al keyframe anterior. Lo cubre [`OpenGopDecodeTests`](../../tests/Rodaje.Media.Tests/OpenGopDecodeTests.cs) con el preset `OpenGopMediaPresets.HevcOpenGop` (libkvazaar, del build LGPL: IDR en 0 y CRA en 64, 128 y 192), por software con hilos, con un hilo y por hardware.

Tolera 64 errores seguidos antes de lanzar `MediaException`.

**Hardware.**
- `get_format` es un puntero a función `[UnmanagedCallersOnly]`, compatible con AOT y sin delegados.
- El pool D3D11VA se crea con `SHADER_RESOURCE` y su tamaño es referencias + extra, con un tope de 64 superficies (`VideoDecoder.MaxHardwarePoolSurfaces`): medido con NVDEC en la RTX 3050, FFmpeg no pasa de 64 y, si se retienen más cuadros de los que sobran, la decodificación falla o pisa superficies en uso. `DecoderSurfacePool.SpareSurfaces` dice cuántos cuadros se pueden retener.
- Con `CompactReferencePool`, las 16 referencias del peor caso se cambian por las que permite el nivel del códec (`LevelReferenceFrames`): en 4K, 6 como mucho, de unos 12 MB cada una.
- `HardwarePoolSizer` deja que el motor decida el extra.

**Entrega.** Cada cuadro sale en uno de tres formatos:
- con D3D11VA, una capa del pool;
- por software con dispositivo, una textura NV12/P010 propia ([`FrameUploader`](../../src/Rodaje.Media/Video/FrameUploader.cs));
- sin dispositivo, [`CpuFramePayload`](../../src/Rodaje.Media/Video/CpuFramePayload.cs).

**Alta velocidad (fase 5c).** `GetFrame(n, DecodeAccuracy)` admite dos modos aproximados, que solo actúan si el decodificador se abrió con `VideoDecoderOptions.SkipNonReference` (al exportar no se activa):
- `SkipNonReference` pone `AVDISCARD_NONREF` y entrega el último cuadro decodificado que no pasa del pedido;
- `Keyframe` deja en el demuxer los paquetes que no son clave y entrega el último keyframe que no pasa del pedido. Con `VideoDecoderOptions.Keyframes` va directo al keyframe sin leer el siguiente. En HEVC cada keyframe se decodifica aparte: antes del siguiente se vacía el decodificador (fin de flujo) y se hace flush, como tras un seek. Sin eso, un CRA calcula su POC desde cuadros descartados, choca con el keyframe anterior («Duplicate POC») y FFmpeg lo tira, así que con GOP abierto solo salía el primer IDR. Con un HEVC 1080p de GOP cerrado no cuesta más (medido en Linux, 4 núcleos: 356–371 ms frente a 492–527 ms sin aislar, 47 peticiones a 16×).

Un cuadro aproximado lleva su índice real (menor o igual al pedido), así la caché nunca lo confunde con el pedido. `GetFrame(n)` e `IVideoFrameSource` son siempre exactos, y tras descartar, la primera petición exacta vuelve a buscar el keyframe.

**Índice de keyframes.** [`KeyframeIndex.Read`](../../src/Rodaje.Media/Probe/KeyframeIndex.cs) da los keyframes con la numeración del decodificador, sin decodificar. En MP4/MOV usa el índice del contenedor: busca cada keyframe y lee su paquete, porque el índice va en DTS y con cuadros B no coincide con la presentación; solo lo acepta si el DTS del paquete es el del índice. En los demás contenedores recorre los paquetes. `AtOrBefore`, `After` y `GopOf` sirven a la reversa (5c.3) y al keyframe cercano de 16× a 100×. En 20 s de 4K tarda 35–170 ms.

**Velocidad medida** (RTX 3050 Laptop, 30 fps, GOP 30 con 2 B, contenido sintético, con otros procesos usando la máquina; [`HighSpeedDecodeSpeedTests`](../../tests/Rodaje.Media.Tests/HighSpeedDecodeSpeedTests.cs), larga). Veces el tiempo real que avanza el origen:

| Medio | Exacto | Sin no-referencia, 2× | Solo keyframes, 16× |
|---|---|---|---|
| 1080p H.264, D3D11VA | 600 fps (20×) | 58–65× | 550–600× |
| 1080p H.264, software | 175–220 fps (6–7×) | 6–10× | 87× |
| 1080p HEVC, D3D11VA | 720–1200 fps (24–40×) | 18–35× | 200–900× |
| 1080p HEVC, software | 150–170 fps (5–6×) | 5–6× | 114× |
| 4K H.264, D3D11VA | 170–190 fps (6×) | 16× | 100–140× |
| 4K H.264, software | 50 fps (1,7×) | 2–2,7× | 29× |
| 4K HEVC, D3D11VA | 210–330 fps (7–11×) | 12–13× | 110–250× |
| 4K HEVC, software | 51–55 fps (1,7–1,8×) | 1,7–2× | 30× |

Consecuencias para los criterios de cuadros perdidos: por hardware, 1080p y 4K pasan holgados a 2× y 4× con una capa. Por software, 4K no llega a 2× y necesita proxy. `AVDISCARD_NONREF` rinde según el codificador: con H.264 de NVENC (B que no son referencia) decodifica un tercio de los cuadros, y con HEVC de NVENC (B de referencia) no ahorra nada. Con material de cámara a más tasa de bits, las cifras serán menores.

El color se resuelve en [`VideoColor`](../../src/Rodaje.Media/Video/VideoColor.cs). Sin declaración se usa BT.601 hasta 576 líneas y BT.709 por encima, y los formatos `yuvj` son de rango completo. [`FrameReadback.ReadLuma`](../../src/Rodaje.Media/Video/FrameReadback.cs) lee la luma, para pruebas y miniaturas.

**Miniaturas.** [`ThumbnailDecoder`](../../src/Rodaje.Media/Video/ThumbnailDecoder.cs) no es exacto al cuadro a propósito: decodifica en la CPU sin filtro de desbloqueo (`skip_loop_filter`) y saltándose los cuadros que no son referencia (`skip_frame`), devuelve el último cuadro que no pasa del instante pedido y lo reduce con swscale (área, con la matriz y el rango del cuadro; BT.709 desde 720 líneas si no lo declara) a BGRA del alto pedido, con la rotación de pantalla aplicada. Si el siguiente pedido queda hasta 4 s más adelante sigue decodificando sin buscar y guarda el primer cuadro que se pasó para la próxima llamada. En 1080p una miniatura con búsqueda tarda unos 47 ms en H.264 y 66 ms en HEVC (mediana, GOP 50), y en una tira en orden unos 26 y 38 ms cada una. Lo usa `ClipVisuals` de la [app](app.md).

**Reversa (fase 5c.3).** [`ReverseFrameReader`](../../src/Rodaje.Media/Video/ReverseFrameReader.cs) entrega un tramo de mayor a menor índice, exacto: cada tramo se decodifica hacia adelante desde su keyframe (pidiendo a `VideoDecoder` solo los cuadros que se verán, `Wanted`) y se entrega al revés. El plan (`Plan`) depende solo de los keyframes, lo pedido y `M` (`MaxRetained`), así que es determinista: junta GOP enteros mientras quepan en `C` cuadros (`M`/2 con la ventana de dos GOP, `M` sin ella) y parte desde arriba el GOP que no cabe (dos pasadas o más desde el mismo keyframe). Con la ventana, el tramo siguiente se decodifica en otro hilo mientras se entrega el actual. Con D3D11VA, `M` sale del pool (como mucho 64 menos las referencias). Lo usa la exportación; el motor hace lo mismo con su caché ([Motor](motor.md#reversa-fase-5c3)).

Cifras ([`ReverseFrameReaderTests`](../../tests/Rodaje.Media.Tests/ReverseFrameReaderTests.cs), clip entero hacia atrás, cada cuadro comparado con la luma de `ffmpeg.exe` o con su número dibujado; software, subida y D3D11VA):

| Caso | Decodificaciones por cuadro |
|---|---|
| GOP 6, 30 o 48 que caben en `C` | 1,00 |
| GOP 48 con `M` = 40 (D3D11VA) | 1,72 |
| GOP 48 con `M` = 16 | 3,41 |
| GOP 250 con `M` = 128 / 40 / 16 | 2,22 / 5,93 / 14,05 |
| 1080p H.264 GOP 60, D3D11VA, `M` = 48 | 1,80, a 322 fps (10,7× el tiempo real, leyendo la luma de cada cuadro) |

El GOP abierto (CRA con RASL) no tiene todavía medio de prueba (tarea 14 de la nube); los RASL tienen índice menor que su CRA y caen en el GOP anterior, que es donde el lector los decodifica.

**Propiedad.** Un decodificador no es seguro entre hilos (detecta llamadas solapadas y lanza). Quien pide un cuadro recibe su única referencia, y el cuadro puede liberarse en cualquier hilo, incluso después de cerrar el decodificador. Retener un cuadro D3D11VA ocupa una superficie del pool.

## Audio y análisis

- **[`AudioDecoder`](../../src/Rodaje.Media/Audio/AudioDecoder.cs)** entrega float32 estéreo a 48 kHz (swresample y un FIFO).
  - La posición 0 es el primer cuadro de video si lo hay (`AlignToVideo`).
  - Los huecos de las marcas de tiempo suenan como silencio y los solapes se descartan.
  - `Seek` es exacto a la muestra, con un pre-roll de 100 ms más el que pide el códec.
- **[`MediaProbe`](../../src/Rodaje.Media/Probe/MediaProbe.cs)** lee los flujos (ignora carátulas) y calcula aspecto de píxel, rotación, tamaño visible, profundidad, color, número de cuadros y VFR (midiendo 240 paquetes). La tasa se ajusta a una estándar si difiere menos de 0,05 %. `MediaInfo.ApplyTo(MediaRef)` copia los datos al modelo.
- **[`MediaSupport`](../../src/Rodaje.Media/Probe/MediaSupport.cs)** (`Classify`) dice si la línea de tiempo puede usar un archivo y, si no, el motivo en español (no se pudo leer, imagen suelta...). Lo usa la importación de la [app](app.md).

## Proxies (`Proxies/`)

Un proxy es una copia ligera (540p o 720p, H.264, GOP 15 sin cuadros B) **con los mismos cuadros y marcas que el original**, así que el cuadro n del proxy es el cuadro n del original. Solo la vista previa usa proxies; la exportación usa siempre el original.

| Pieza | Qué hace |
|---|---|
| [`ProxyStore`](../../src/Rodaje.Media/Proxies/ProxyStore.cs) | Carpeta `RODAJE_PROXIES` o `%LOCALAPPDATA%\Rodaje\proxies`. Archivos `<huella>-<ajustes>.mp4` con su `.json`; la huella ([`SourceFingerprint`](../../src/Rodaje.Media/Proxies/SourceFingerprint.cs)) es SHA-256 de ruta, tamaño, fecha y primer y último MiB. Un proxy solo cuenta si el JSON coincide. Todo se escribe como `.tmp` y se renombra al final. `Cleanup` borra temporales viejos, huérfanos y proxies de orígenes que cambiaron, y desaloja por LRU hasta un tope |
| [`FfmpegExecutable`](../../src/Rodaje.Media/Proxies/FfmpegExecutable.cs) | Solo acepta el `ffmpeg.exe` de la carpeta de las DLL; verifica que sea LGPL y de la misma compilación, y prueba de verdad cada codificador |
| [`ProxyEncoders`](../../src/Rodaje.Media/Proxies/ProxyEncoders.cs) | Preferencia `h264_nvenc`, `h264_mf`, `libopenh264` (ninguno GPL). Argumentos con `-copyts`, `-fps_mode passthrough`, sin audio y con `-progress` |
| [`ProxyGenerator`](../../src/Rodaje.Media/Proxies/ProxyGenerator.cs) | Hasta 2 trabajos a la vez. Pedidos iguales comparten un trabajo, que solo se cancela si todos cancelan. `ffmpeg.exe` corre en un Job de Windows que lo mata si Rodaje cae. Si un codificador falla prueba el siguiente; al terminar verifica número de cuadros, inicio y tasa |
| [`ProxyPolicy`](../../src/Rodaje.Media/Proxies/ProxyPolicy.cs) | Recomienda proxy por encima de 1920×1088, con HEVC de 10 bits en varias capas, con GOP de más de 60, por encima de 80 Mbps o si la decodificación medida no llega a 1,5× por capa. `HighSpeed`: entre 1× y 16×, si la velocidad del clip × capas × 1,25 supera lo medido por `DecodeSpeedProbe`. `Reverse`: obligatoria (`IsRequired`) en reversa por encima de 1080p |
| [`MediaSourceResolver`](../../src/Rodaje.Media/Proxies/MediaSourceResolver.cs) | Decide qué archivo usar según el propósito (vista previa o exportación) y publica el estado del proxy (`Recommended`, `Generating`, `Ready`...) |

## Salida de la exportación (`Export/`)

El contrato (`IExportSink`, `ExportVideoFrame`, formatos, `EncoderSpec`, `ExportCompatibility`) está en D-028. La v1 lo implementa con el mismo `ffmpeg.exe` LGPL verificado de los proxies; los argumentos de cada codificador los arma [`FfmpegArguments`](../../src/Rodaje.Export/FfmpegArguments.cs) en `Rodaje.Export` a partir de `ExportSettings`.

| Pieza | Qué hace |
|---|---|
| [`FfmpegPipeSink`](../../src/Rodaje.Media/Export/FfmpegPipeSink.cs) | `-f rawvideo -i pipe:0` más el WAV como segunda entrada. Un único búfer de 4 MB se llena fila a fila (quita el pitch) y se vacía a la tubería: sin reservas por cuadro. Etiqueta el color en la entrada y en la salida (`bt709`/`tv`/`left`, BT.601 en SD). `ChildProcessJob`, prioridad Normal o BelowNormal según el modo (`PriorityFor`), `-progress` para `FramesEncoded`. Capítulos por `ffmetadata`, portada en MP4/MOV, secuencias de imágenes en carpeta, dos pasadas con `-pass`. `Abort` (o el token) mata el proceso y borra los temporales antes de volver |
| [`FfmpegMessages`](../../src/Rodaje.Media/Export/FfmpegMessages.cs) | La salida de error de ffmpeg a `ExportErrorCode` y un mensaje en español, con el detalle técnico al final entre paréntesis |
| [`ExportOutputGuard`](../../src/Rodaje.Media/Export/ExportOutputGuard.cs) | Temporal `<salida>.rodaje-tmp` y renombrado; la salida no puede ser un medio de la secuencia ni estar abierta en otro programa; espacio libre antes de empezar y cada segundo mientras se escribe (reserva de 64 MB) |
| [`ExportJournal`](../../src/Rodaje.Media/Export/ExportJournal.cs) | `%LOCALAPPDATA%\Rodaje\exportando.json`: temporales de cada exportación con el proceso dueño. `CleanupStale` (al arrancar) borra los de procesos muertos, y solo rutas con `.rodaje-tmp` |
| [`WaveFileWriter`](../../src/Rodaje.Media/Export/WaveFileWriter.cs) | WAV de la mezcla: float32 (o PCM 16/24/32 con saturación); pasa a RF64 al cerrar si supera 4 GB, sin mover datos (reserva `JUNK` para `ds64`) |
| [`FfmpegLoudness`](../../src/Rodaje.Media/Export/FfmpegLoudness.cs) | `loudnorm` en dos pasadas: mide el WAV y da el filtro lineal con lo medido |
| [`ExportVerifier`](../../src/Rodaje.Media/Export/ExportVerifier.cs) | Con `ffprobe.exe`: cuadros, tasa, píxel, etiquetas de color, GOP máximo, inicio de audio y video, capítulos; lee las cajas para saber si `moov` va antes de `mdat` |

**Cifras (RTX 3050 Laptop, 12-oct).** Tubería del sink a `h264_nvenc` p4: 1080p NV12 a 295–322 fps (0,9–1,0 GB/s), 4K a 85–86 fps (~1,05 GB/s), igual que el prototipo. Cancelación: mediana 3 ms y máxima 7 ms en 10 rondas, sin proceso ni archivo. Inicio de audio y video en 0 ±1 ms en MP4, MOV, MKV, WebM, MXF y MPEG-TS (en MPEG-TS con `-muxdelay 0`); MP3 en MPEG-TS adelanta el video 23 ms (retardo de LAME sin lista de edición), por eso se prueba MP3 en MP4.

**Codificadores probados aquí** con exportaciones cortas verificadas con ffprobe: `h264_nvenc`, `hevc_nvenc` (8 y 10 bits), `h264_mf`, `libsvtav1` (8 y 10 bits), `libvpx-vp9` (también dos pasadas), `prores_ks` (422 HQ y 4444 con alfa), `dnxhd` (DNxHR SQ en MOV y HQX en MXF), `ffv1`, `png` (secuencia); audio `aac_mf`, `aac`, `libopus`, `flac`, `alac`, `libmp3lame`, `pcm_s16le`, `pcm_s24le`. AMF y QSV tienen argumentos pero no se pueden probar en este equipo. MXF etiqueta BT.601 como `bt470bg` (la misma matriz); el verificador los da por iguales.

## Pruebas

- **[`tests/Rodaje.Media.Tests`](../../tests/Rodaje.Media.Tests)** cubre:
  - la búsqueda y la licencia de FFmpeg;
  - exactitud por hash de luma frente a `ffmpeg.exe` en software;
  - vida de los cuadros y fugas;
  - el pool compacto en 4K;
  - los modos de alta velocidad (cada cuadro aproximado coincide con la referencia de su índice real y ninguna petición exacta sale de un descarte) y el índice de keyframes frente a ffprobe;
  - color, análisis y audio con saltos de marcas;
  - rendimiento;
  - todos los proxies (almacén, política, argumentos, generación que lee el número dibujado en cada cuadro);
  - la salida de la exportación (`Export/`): una exportación corta por codificador y contenedor, cancelación ×10, disco lleno antes y durante, codificador ausente o que rechaza, salida que pisa un medio, RF64, `exportando.json` y sonoridad.
  Cada prueba corre en tres modos: D3D11VA, software con subida a GPU y CPU.
- **[`tests/Rodaje.TestMedia`](../../tests/Rodaje.TestMedia)** genera los medios deterministas de todas las suites:
  - [`FrameCode`](../../tests/Rodaje.TestMedia/FrameCode.cs) dibuja el número de cuadro en la luma (20 bits con CRC-8) y resiste la compresión;
  - [`AudioCode`](../../tests/Rodaje.TestMedia/AudioCode.cs) marca cada segundo de audio;
  - `TestMediaGenerator` codifica con un `ffmpeg.exe` de desarrollo y guarda los archivos en `TestResults\media-cache`;
  - [`Skip.cs`](../../tests/Rodaje.TestMedia/Skip.cs) reúne los atributos que omiten una prueba, con su motivo, cuando falta FFmpeg, D3D11VA, NVENC o un dispositivo de audio.
