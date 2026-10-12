# `VideoDecoder` con GOP abierto (HEVC con CRA)

Tarea 14 de la cola, rama `claude/media-gop-abierto`, desde `origin/main` en `e75e848`.

## Reproducción (Linux, FFmpeg 9.0.2 LGPL compartido de BtbN, el HEVC de libkvazaar de `TestClips.Hevc`)

El clip tiene IDR en 0 y CRA en 64, 128 y 192, con 15 cuadros RASL antes de cada CRA en presentación (`has_b_frames=15`).
Eran dos defectos distintos, y ninguno era el de la hipótesis de la cola:

1. **Modo `Keyframe`: «Duplicate POC».** Con solo keyframes, el decodificador recibe IDR 0, CRA 64, CRA 128... Un CRA calcula su POC desde el último cuadro de capa temporal 0, que fue el IDR porque todo lo de en medio se descartó. Le sale POC 0, choca con el IDR que sigue en el DPB y FFmpeg tira el cuadro. Pasa también con `ffmpeg -skip_frame nokey` (sale un solo cuadro). Por eso pedir el 64 entregaba el 0, con o sin `KeyframeIndex`, y en todo el clip solo se decodificaba 1 cuadro.
2. **Exacto con un solo hilo (el camino de D3D11VA): salida vieja tras el flush.** El HEVC de FFmpeg sin hilos por cuadro pasa varios cuadros de una vez a su cola de salida, por ejemplo al llegar al final del flujo. `avcodec_flush_buffers` no vacía esa cola. Ejemplo: tras pedir el 247, el seek al 216 recibía primero el 248, que pasa del objetivo. Retrocedía, recibía el 249, y así hasta agotar los reintentos: `null`. Con `SoftwareThreads = 1` se reproduce sin GPU; la secuencia de `RandomRequestOrder` daba 8 `null` (216, 85, 122, 183, 240, 97, 135, 75). Con hilos por cuadro (por defecto en software) no aparece. Por eso fallaban `Exact_requests_never_skip_hardware(hevc)` y `Random_access_matches_reference_hardware(hevc)`.

Lo de los RASL tras un seek a un CRA ya estaba bien (`_minValidSlot` y el retroceso cuando el keyframe pasa del objetivo).

## Cambios

- `src/Rodaje.Media/Video/VideoDecoder.cs`:
  - **`DiscardStaleOutput`.** Tras el `avcodec_flush_buffers` de `ResetDecoder`, recoge y tira lo que el decodificador devuelva sin paquetes nuevos, hasta 64 cuadros. En los demás códecs devuelve `EAGAIN` en la primera llamada.
  - **Modo `Keyframe` en HEVC (`IsolatesKeyframes`).** Antes de mandar un keyframe, si ya se mandó otro desde el último reset, `FeedDecoder` deja el paquete esperando y manda el fin de flujo. Cuando `NextFrame` recibe el `EOF` (o `EAGAIN`) de ese vaciado, hace flush y `DiscardStaleOutput` y sigue con el paquete. Cada keyframe empieza así como tras un seek. `ResetDecoder` limpia los dos estados nuevos.
- `testing/Rodaje.TestMedia/OpenGopMediaPresets.cs` (nuevo): `HevcOpenGop`, HEVC 640×360 a 25 fps, 10 s, GOP 64, MP4, `Encoder = "libkvazaar"`, con `FrameCode`. Lo genera el build LGPL en cualquier máquina, también en la RTX 3050.
- `tests/Rodaje.Media.Tests/OpenGopDecodeTests.cs` (nuevo), 9 pruebas:
  - **Estructura del preset.** Tras cada keyframe que no es el primero llegan paquetes con pts menor, es decir, el GOP es abierto. La referencia de ffmpeg.exe trae el número dibujado de cada cuadro.
  - **Cuatro comprobaciones**, cada una por software y por hardware:
    - acceso aleatorio exacto, con `RandomRequestOrder` más el último cuadro y vuelta atrás;
    - modo `Keyframe`, que entrega cada CRA, con índice y sin él, y con el tope de cuadros decodificados;
    - `SkipNonReference`, que dice la verdad sobre el cuadro entregado (libkvazaar no deja cuadros sin referenciar, así que no se exige ahorro);
    - peticiones exactas al CRA y a sus RASL tras las rápidas.
  - **Modos por software:** CPU con hilos, CPU con 1 hilo y subida si hay GPU. El hardware usa `HardwareFact(Encoder = "libkvazaar")`.
- `tests/Rodaje.Media.Tests/HighSpeedDecodeTests.cs`: se quita la omisión del PR #9 que nombraba este defecto. `Keyframe_mode_delivers_only_keyframes_software` vuelve a `SoftwareCases` con el HEVC.
- `docs/arquitectura/medios.md`: GOP abierto, salida vieja tras el flush y el modo `Keyframe` en HEVC.

## Medido en Linux

- **Las pruebas nuevas sin el arreglo** (`VideoDecoder.cs` de `e75e848`): fallan `Keyframe_mode_delivers_each_cra_software` («pedido 64: entregado 0») y `Random_access_matches_reference_software` («[cpu-1-hilo] 8 de 211… 216: null…»). Con el arreglo pasan las 5 de software.
- **`Rodaje.Media.Tests`** (sin `TiempoReal`, `Rendimiento` ni `Export`): antes 42 fallos, 219 pasan y 9 omitidas; después 40 fallos, 222 pasan y 8 omitidas.
  - Los 40 fallos restantes son idénticos antes y después. Son de D3D11 y de Windows, que no hay en Linux: `FrameLifetimeTests`, `HardwarePoolTests`, `Proxies/*`, `VideoColorTests`, las `*_hardware`, que fallan al descubrirse porque no está `d3d11`, y `HighSpeedDecodeSpeedTests`.
  - La única diferencia son las 2 pruebas nuevas arregladas.
- **Costo del modo `Keyframe` aislado** con un HEVC 1080p de GOP cerrado (libkvazaar `open-gop=0`, 750 cuadros, 47 peticiones a 16×, mejor de 3, 4 núcleos):

  | Hilos | Con aislar | Sin aislar |
  |---|---|---|
  | Por defecto | 356 ms | 527 ms |
  | Uno | 371 ms | 492 ms |

  No es más lento: sin aislar, cada keyframe esperaba a que se leyera el siguiente para salir.

Para correr Medios en Linux:
- FFmpeg compartido de BtbN (`ffmpeg-n9.0-latest-linux64-lgpl-shared-9.0`), con enlaces `lib*.so.N` → `<nombre>-N.dll` y `ffmpeg`/`ffprobe` → `.exe` en `<entorno>/ffmpeg/bin`, más `RODAJE_ENTORNO` y `LD_LIBRARY_PATH`.
- Hicieron falta **dos parches locales que no se suben**:
  - `FfmpegTool.RunExecutable`: el `stdout.CanRead` tras cerrar la tubería lanza «Cannot access a closed pipe» en Linux (ya anotado en `2026-10-11-ci-windows.md`).
  - `MediaTestEnvironment.Gpu` llama a `GpuDevice.TryCreate` y en Linux lanza `DllNotFoundException` (`d3d11`), lo que tumba el descubrimiento de todas las teorías con `DecodeModes.Software`.

  Arreglarlos (un `try`/`ObjectDisposedException` y `OperatingSystem.IsWindows()`) daría Medios en Linux a la próxima tarea; quedan fuera de esta.

## Pruebas omitidas, antes y después

| Equipo | Antes | Después | Causa |
|---|---|---|---|
| CI de Windows (sin NVENC ni D3D11VA) | `Keyframe_mode…_software(hevc)`: 2 omitidas por este defecto (CPU y subida, o 1 sin GPU) | 0 | Defecto corregido |
| | — | 4 omitidas: las `OpenGopDecodeTests.*_hardware` | Hardware: sin D3D11VA. Las 5 de software corren si el ffmpeg trae libkvazaar, que el build LGPL trae; si no, se omiten con ese motivo |
| PC de Daniel (RTX 3050) | 0 | 0 | Las 9 nuevas corren, las 4 de hardware con D3D11VA |
| Linux local | 9 | 8 | |

Siguen omitidas, con su causa propia y fuera de esta tarea:
- `Skipping_non_reference…_software`: libopenh264 sin cuadros B, y libkvazaar sin cuadros no referenciados;
- `KeyframeIndex` de `hevc10`: solo con `hevc_nvenc`.

## Decisiones

- **Aislar los keyframes solo en HEVC.** El choque de POC es propio de los CRA de HEVC. No comprobé H.264 con keyframes sin IDR (I con punto de recuperación) porque libopenh264 solo genera IDR, así que ahí no cambia nada. No se lee el tipo de NAL de cada paquete (MP4/MKV con prefijo de longitud o TS con Annex B): aislar también los IDR no cuesta (ver la medida).
- **Vaciar la salida vieja siempre después del flush, no solo en HEVC.** Es una llamada que en los demás códecs devuelve `EAGAIN` enseguida, y protege de lo mismo en cualquier decodificador con cola de salida.
- **La referencia de las pruebas nuevas es ffmpeg.exe** (luma `extractplanes=y`, como `TestClips`), con el número dibujado comprobado cuadro a cuadro, no el propio `VideoDecoder`.
- **El preset fija `Encoder = "libkvazaar"`, sin parámetros propios.** Su valor por omisión ya es GOP abierto, y la prueba de estructura avisa si deja de serlo.

## Posibles choques

- `origin/main` cambió `VideoDecoder.cs` mientras tanto (`MaxHardwarePoolSurfaces` y `DecoderSurfacePool` con referencias, ronda 5c.3 de la reversa). `git merge-tree` con `origin/main` sale limpio; no lo traje a la rama.
- `ReverseFrameReader` (nuevo en main) usa `VideoDecoder`: el vaciado tras el flush le aplica igual. Conviene que la CI corra `ReverseFrameReaderTests` sobre el merge.
- El otro agente trabaja en `src/Rodaje.Media/Export/` y `tests/Rodaje.Media.Tests/Export/`; aquí no se tocan.

## Qué comprobar en Windows

1. **CI de Windows:** en verde. Las 5 `OpenGopDecodeTests.*_software` y `Keyframe_mode_delivers_only_keyframes_software(hevc…)` corren y pasan. Las 4 `*_hardware` se omiten por hardware.
2. **`ABRIR.bat` en la RTX 3050:**
   - Las 9 `OpenGopDecodeTests` corren y pasan, en especial las 4 de hardware: `Random_access…_hardware` era el «216 no se entrega».
   - `HighSpeedDecodeTests` y `VideoDecoderAccuracyTests` con hardware siguen en verde.
3. **Opcional:** generar `TestClips.Hevc` con libkvazaar en la RTX (borrar `hevc-25-g48-bf2.mp4` de la caché y hacer que falle `hevc_nvenc`). Así `Exact_requests_never_skip_hardware(hevc)` y `Random_access_matches_reference_hardware(hevc)` pasan con el clip abierto, como en la revisión del PR #9.
4. **Tiempos de alta velocidad** (`HighSpeedDecodeSpeedTests`, larga): «Solo keyframes, 16×» de HEVC no baja.
