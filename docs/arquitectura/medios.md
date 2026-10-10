# Medios: `Rodaje.Media`

Abre, analiza y decodifica los archivos con FFmpeg 9 LGPL; crea el dispositivo Direct3D 11 que comparten el decodificador (D3D11VA) y el compositor; y genera proxies. Es `net10.0-windows` e `IsAotCompatible`, y referencia [Núcleo](nucleo.md) (por `Time` y `Rational`) y [Reproducción](reproduccion.md) (por `DecodedFrame` e `IVideoFrameSource`). Usa FFmpeg.AutoGen (enlace dinámico) y TerraFX.Interop.Windows. La usan el [Motor](motor.md) y el [Render](render.md).

## Cargar FFmpeg sin arriesgar la licencia

Rodaje es MIT, así que solo carga un FFmpeg LGPL con la versión verificada y por ruta absoluta.

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

Tolera 64 errores seguidos antes de lanzar `MediaException`.

**Hardware.**
- `get_format` es un puntero a función `[UnmanagedCallersOnly]`, compatible con AOT y sin delegados.
- El pool D3D11VA se crea con `SHADER_RESOURCE` y su tamaño es referencias + extra.
- Con `CompactReferencePool`, las 16 referencias del peor caso se cambian por las que permite el nivel del códec (`LevelReferenceFrames`): en 4K, 6 como mucho, de unos 12 MB cada una.
- `HardwarePoolSizer` deja que el motor decida el extra.

**Entrega.** Cada cuadro sale en uno de tres formatos:
- con D3D11VA, una capa del pool;
- por software con dispositivo, una textura NV12/P010 propia ([`FrameUploader`](../../src/Rodaje.Media/Video/FrameUploader.cs));
- sin dispositivo, [`CpuFramePayload`](../../src/Rodaje.Media/Video/CpuFramePayload.cs).

El color se resuelve en [`VideoColor`](../../src/Rodaje.Media/Video/VideoColor.cs). Sin declaración se usa BT.601 hasta 576 líneas y BT.709 por encima, y los formatos `yuvj` son de rango completo. [`FrameReadback.ReadLuma`](../../src/Rodaje.Media/Video/FrameReadback.cs) lee la luma, para pruebas y miniaturas.

**Propiedad.** Un decodificador no es seguro entre hilos (detecta llamadas solapadas y lanza). Quien pide un cuadro recibe su única referencia, y el cuadro puede liberarse en cualquier hilo, incluso después de cerrar el decodificador. Retener un cuadro D3D11VA ocupa una superficie del pool.

## Audio y análisis

- **[`AudioDecoder`](../../src/Rodaje.Media/Audio/AudioDecoder.cs)** entrega float32 estéreo a 48 kHz (swresample y un FIFO).
  - La posición 0 es el primer cuadro de video si lo hay (`AlignToVideo`).
  - Los huecos de las marcas de tiempo suenan como silencio y los solapes se descartan.
  - `Seek` es exacto a la muestra, con un pre-roll de 100 ms más el que pide el códec.
- **[`MediaProbe`](../../src/Rodaje.Media/Probe/MediaProbe.cs)** lee los flujos (ignora carátulas) y calcula aspecto de píxel, rotación, tamaño visible, profundidad, color, número de cuadros y VFR (midiendo 240 paquetes). La tasa se ajusta a una estándar si difiere menos de 0,05 %. `MediaInfo.ApplyTo(MediaRef)` copia los datos al modelo.

## Proxies (`Proxies/`)

Un proxy es una copia ligera (540p o 720p, H.264, GOP 15 sin cuadros B) **con los mismos cuadros y marcas que el original**, así que el cuadro n del proxy es el cuadro n del original. Solo la vista previa usa proxies; la exportación usa siempre el original.

| Pieza | Qué hace |
|---|---|
| [`ProxyStore`](../../src/Rodaje.Media/Proxies/ProxyStore.cs) | Carpeta `RODAJE_PROXIES` o `%LOCALAPPDATA%\Rodaje\proxies`. Archivos `<huella>-<ajustes>.mp4` con su `.json`; la huella ([`SourceFingerprint`](../../src/Rodaje.Media/Proxies/SourceFingerprint.cs)) es SHA-256 de ruta, tamaño, fecha y primer y último MiB. Un proxy solo cuenta si el JSON coincide. Todo se escribe como `.tmp` y se renombra al final. `Cleanup` borra temporales viejos, huérfanos y proxies de orígenes que cambiaron, y desaloja por LRU hasta un tope |
| [`FfmpegExecutable`](../../src/Rodaje.Media/Proxies/FfmpegExecutable.cs) | Solo acepta el `ffmpeg.exe` de la carpeta de las DLL; verifica que sea LGPL y de la misma compilación, y prueba de verdad cada codificador |
| [`ProxyEncoders`](../../src/Rodaje.Media/Proxies/ProxyEncoders.cs) | Preferencia `h264_nvenc`, `h264_mf`, `libopenh264` (ninguno GPL). Argumentos con `-copyts`, `-fps_mode passthrough`, sin audio y con `-progress` |
| [`ProxyGenerator`](../../src/Rodaje.Media/Proxies/ProxyGenerator.cs) | Hasta 2 trabajos a la vez. Pedidos iguales comparten un trabajo, que solo se cancela si todos cancelan. `ffmpeg.exe` corre en un Job de Windows que lo mata si Rodaje cae. Si un codificador falla prueba el siguiente; al terminar verifica número de cuadros, inicio y tasa |
| [`ProxyPolicy`](../../src/Rodaje.Media/Proxies/ProxyPolicy.cs) | Recomienda proxy por encima de 1920×1088, con HEVC de 10 bits en varias capas, con GOP de más de 60, por encima de 80 Mbps o si la decodificación medida no llega a 1,5× por capa |
| [`MediaSourceResolver`](../../src/Rodaje.Media/Proxies/MediaSourceResolver.cs) | Decide qué archivo usar según el propósito (vista previa o exportación) y publica el estado del proxy (`Recommended`, `Generating`, `Ready`...) |

## Pruebas

- **[`tests/Rodaje.Media.Tests`](../../tests/Rodaje.Media.Tests)** cubre:
  - la búsqueda y la licencia de FFmpeg;
  - exactitud por hash de luma frente a `ffmpeg.exe` en software;
  - vida de los cuadros y fugas;
  - el pool compacto en 4K;
  - color, análisis y audio con saltos de marcas;
  - rendimiento;
  - todos los proxies (almacén, política, argumentos, generación que lee el número dibujado en cada cuadro).
  Cada prueba corre en tres modos: D3D11VA, software con subida a GPU y CPU.
- **[`testing/Rodaje.TestMedia`](../../testing/Rodaje.TestMedia)** genera los medios deterministas de todas las suites:
  - [`FrameCode`](../../testing/Rodaje.TestMedia/FrameCode.cs) dibuja el número de cuadro en la luma (20 bits con CRC-8) y resiste la compresión;
  - [`AudioCode`](../../testing/Rodaje.TestMedia/AudioCode.cs) marca cada segundo de audio;
  - `TestMediaGenerator` codifica con un `ffmpeg.exe` de desarrollo y guarda los archivos en `TestResults\media-cache`;
  - [`Skip.cs`](../../testing/Rodaje.TestMedia/Skip.cs) reúne los atributos que omiten una prueba, con su motivo, cuando falta FFmpeg, D3D11VA, NVENC o un dispositivo de audio.
