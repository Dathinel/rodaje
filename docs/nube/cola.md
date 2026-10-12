# Cola de tareas para la nube

La mantiene la sesión local. Reglas en [README.md](README.md). Estados: `pendiente`, `cambios`, `en pausa`, `hecha`.


**PR sin revisar** (unidos en verde; revisión por bloques al llegar a 4): #38, #37. Los bloques #15, #31, #33, #34 (tareas 42 ampliada y 44–47) y #30, #32, #18, #36 (tareas 48–50) los revisó Sonnet el 12-oct.

## 0. URGENTE: main en rojo en la CI de Windows (pruebas de velocidad de Medios)

- **Estado:** hecha (PR #9 unido el 11-oct; comentario y aserción ajustados al unir)
- **Rama:** `claude/ci-media-velocidad`
- **Caminos:** `tests/Rodaje.Media.Tests/`, `docs/tareas/` (y, solo si hace falta, archivos nuevos en `testing/Rodaje.TestMedia/`)
- **Lee:** el log de la última corrida de main en rojo (`gh run list --branch main`, `gh run view <id> --log-failed`), `docs/arquitectura/medios.md` (sección de velocidad), `testing/Rodaje.TestMedia/Skip.cs`.

Desde la ronda 5c.1 fallan en el runner sin NVENC `HighSpeedDecodeTests.Skipping_non_reference_frames_decodes_less_and_labels_truthfully_software` (6 casos), `HighSpeedDecodeTests.Keyframe_mode_delivers_only_keyframes_software` (hevc) y `KeyframeIndexTests.Both_sources_find_the_decoded_keyframes` (hevc, hevc10). Averigua la causa en cada una (¿el clip sin NVENC no tiene cuadros B, no es HEVC o tiene otro GOP?) y que corran en el runner comprobando lo mismo con el clip que haya, o se omitan solo con la causa concreta de hardware o codificador (el mecanismo de `Skip.cs`), con el conteo de omitidas antes y después en la entrega. En la RTX 3050 de Daniel deben seguir corriendo todas. Objetivo: CI de Windows de main en verde (salvo `PlaybackSchedulerTests.SeekLejos…`, que es la tarea 9).

## 1. CI de Windows en verde (salvo Render)

- **Estado:** hecha (PR #2 unido el 11-oct; Windows bloquea en la CI)
- **Rama:** `claude/ci-windows-verde`
- **Caminos:** `tests/Rodaje.Media.Tests/`, `testing/Rodaje.TestMedia/`, `tests/Rodaje.TestMedia.Tests/`, `tests/Rodaje.Audio.Tests/`, `.github/workflows/ci.yml`, `docs/tareas/`
- **Lee:** `docs/tareas/2026-10-10-ci.md`

Los 13 fallos del trabajo de Windows vienen del runner sin GPU ni NVENC y con 2 núcleos:
1. **Media, 6 casos de HEVC 10 bits:** `TestClips` genera HEVC solo con `hevc_nvenc`. Sin NVENC deben omitirse con el mecanismo de `testing/Rodaje.TestMedia/Skip.cs`, no fallar.
2. **`VideoDecoderBehaviorTests.Corrupted_packets_are_skipped_without_throwing`:** confirma la causa (H.264 de `h264_mf` sin cuadros B) y haz que la zona dañada se calcule con el clip real o que se omita sin el codificador esperado, sin debilitarla en una máquina con NVENC.
3. **`PresetTests.H264Ntsc24Aac` y `H264Ntsc30BFrames`:** si el preset exige cuadros B y el codificador no los da, omitir con mensaje claro.
4. **`LevelMathTests.LeerMientrasSeEscribeNuncaDaBloquesMezclados`:** que el escritor ceda o que la prueba garantice lecturas sin depender del planificador; si no, categoría `TiempoReal`.
5. **Render (3 fallos por `D3D11_BIND_DECODER` en WARP):** no lo toques; en `ci.yml` exclúyelos en Windows con un filtro explícito.

Con eso en verde, quita `continue-on-error` del trabajo de Windows y deja el enlace a la corrida verde en la entrega.

## 2. Guardia de decodificador en las pruebas de Render

- **Estado:** hecha (PR #4 unido el 11-oct; Sharpen en WARP es precisión del rasterizador, medido)
- **Rama:** `claude/render-guardia-decodificador`
- **Caminos:** `tests/Rodaje.Render.Tests/`, `.github/workflows/ci.yml`, `docs/tareas/`

La guardia de GPU de `CompositorTests.Draws_the_requested_slice_of_a_texture_array` y de `CompositorPerformanceTests` mira el muestreo NV12/P010 pero no el bind de decodificador (`YuvImage.CreateTexture`, `tests/Rodaje.Render.Tests/Support/YuvImage.cs`). Que comprueben `D3D11_BIND_DECODER` con `CheckFormatSupport` y se omitan si falta. Además: las 4 de `EffectPerformanceTests` (E_INVALIDARG y `One_4k_layer_with_color_and_lut` de 238 ms en WARP) pasan a la categoría `Rendimiento` que la CI ya excluye, y `EffectFidelityTests.Sharpen_*` (3 casos, 9 frente a 6,9 ±2 en WARP): averigua por qué WARP da otro resultado y, si es una diferencia de precisión del rasterizador software, que la prueba lo detecte (adaptador WARP) y aplique una tolerancia justificada solo ahí, nunca en GPU real. Luego quita del filtro de `ci.yml` todas las exclusiones por nombre de Render.

## 3. Pruebas hostiles del lector de proyectos `.rodaje`

- **Estado:** hecha (PR #3 unido el 11-oct; halló un desbordamiento real en `ModelValidator.cs:293`, se corrige en la sesión local)
- **Rama:** `claude/fuzz-proyecto`
- **Caminos:** `tests/Rodaje.Core.Tests/Fuzz/` (carpeta nueva), `docs/tareas/`. Si encuentras un fallo real en `src/Rodaje.Core/Serialization/`, NO lo corrijas: deja la prueba que lo reproduce marcada con `Skip = "Defecto: …"` y descríbelo en la entrega.
- **Lee:** `docs/formato-proyecto.md`, `src/Rodaje.Core/Serialization/` (solo lectura), `tests/Rodaje.Core.Tests/Serialization/ejemplos/proyecto-v3.rodaje`.

Un `.rodaje` dañado o malicioso nunca debe tumbar la app: abrirlo da un proyecto válido o un error controlado en español (la excepción propia del lector), nunca otra excepción ni un cuelgue.
1. Pruebas con mutaciones deterministas (semilla fija) sobre los ejemplos v1, v2 y v3: bytes cambiados, truncados, JSON con tipos cambiados, números enormes, negativos, NaN, `schemaVersion` futuro o ausente, ids duplicados, referencias a clips o medios inexistentes, transiciones entre clips no contiguos, keyframes desordenados, cadenas de 1 MB, anidamiento profundo.
2. Al menos 5000 casos por corrida, en menos de 30 s en Linux.
3. Comprueba además que todo proyecto que abre sin error pasa el validador y se vuelve a guardar y abrir igual byte a byte.

## 4. Estado del decodificador tras un flush (Rodaje.Media)

- **Estado:** hecha (PR #5 unido el 11-oct tras tres revisiones)
- **Rama:** `claude/media-flush`
- **Caminos:** `src/Rodaje.Media/`, `tests/Rodaje.Media.Tests/`, `docs/tareas/`
- **Lee:** `docs/arquitectura/medios.md`, la revisión del PR #2, `tests/Rodaje.Media.Tests/VideoDecoderBehaviorTests.cs`.

`Corrupted_packets_are_skipped_without_throwing` solo corre con `h264_nvenc` porque, con el H.264 de `h264_mf`, el estado del decodificador sobrevive a `avcodec_flush_buffers` y un cuadro tras la zona dañada no coincide. Encuentra la causa en `Rodaje.Media` (seek, flush, cuadros de referencia pendientes), corrígela y haz que la prueba corra con cualquier codificador H.264 disponible. Se verifica con la CI de Windows (sin NVENC) y la revisión local con NVENC.

## 5. Pruebas hostiles del instalador de paquetes

- **Estado:** hecha (PR #6 unido el 11-oct; halló 2 defectos reales en `Rodaje.Paquetes`, tarea 11)
- **Rama:** `claude/fuzz-paquete`
- **Caminos:** `tests/Rodaje.Paquetes.Tests/Fuzz/` (carpeta nueva), `docs/tareas/`. Si encuentras un fallo real en `src/Rodaje.Paquetes/`, NO lo corrijas: prueba que lo reproduce con `Skip = "Defecto: …"` y descripción en la entrega.
- **Lee:** `docs/formato-paquete.md`, `src/Rodaje.Paquetes/` (solo lectura), la revisión del PR #3 (lo que se le pidió a ese fuzz vale aquí).

Un `.paquete` dañado o malicioso nunca debe tumbar ni escribir fuera de su carpeta. Mutaciones deterministas sobre paquetes válidos generados en la prueba: zip truncado o con directorio central falso, rutas con `..`, absolutas, UNC, nombres reservados de Windows y caracteres de control, entradas duplicadas, tamaños declarados falsos, bomba de compresión, manifiesto con tipos cambiados, textos de 1 MB y marcas bidi, archivos de contenido (`.cube`, `rodaje.filter`, `rodaje.effect`, `rodaje.preset`) dañados. Cada caso instala bien o da `PackageInstallException`/`PackageContentException` con mensaje, y la prueba **falla si la excepción interna no es de E/S, zip o JSON** (no aceptes envoltorios genéricos). Comprueba que tras cada fallo no queda nada en la carpeta de destino. Registra la fracción de casos que llegan a cada etapa (zip, manifiesto, contenido, instalación) y exige que al menos un 25 % pase del zip. ≥5000 casos, <30 s en Linux.

## 6. Documentación al día con el código

- **Estado:** hecha (PR #7 unido el 11-oct)
- **Rama:** `claude/docs-al-dia`
- **Caminos:** `docs/arquitectura/`, `docs/variables-entorno.md`, `docs/formato-paquete.md`, `docs/tareas/`. No toques `README.md`, `CLAUDE.md`, `docs/decisiones.md` ni `docs/fase-*.md`.

Recorre `docs/arquitectura/*.md` y compáralos con el código actual de `src/`: tipos, archivos o flujos que ya no existen o cambiaron de nombre, piezas nuevas de la fase 5 sin documentar, enlaces rotos entre documentos. Corrige solo lo que sea falso u omitido, con el estilo de los documentos (corto, de producto, en español). La entrega lista cada corrección con el archivo de código que la respalda.

## 7. Pistas por defecto en un proyecto nuevo

- **Estado:** hecha (PR #10 unido el 11-oct)
- **Rama:** `claude/pistas-por-defecto`
- **Caminos:** `src/Rodaje.App/Services/Settings/`, `src/Rodaje.App/Views/Settings/` (o donde viva la página de ajustes), el punto donde la app crea un proyecto nuevo (búscalo; no toques `src/Rodaje.Core`), `tests/Rodaje.App.Tests/` (archivos nuevos), `docs/tareas/`.
- **Lee:** `docs/pendientes.md` (sección Personalización), `docs/arquitectura/app.md`.

Primera mitad de lo pedido por Daniel: en Ajustes, cuántas pistas de video (1–8) y de audio (1–8) tiene un proyecto nuevo, guardado en `ajustes.json` con los valores de hoy por defecto. Al crear un proyecto se usan esos valores con las ediciones que ya existen. La segunda mitad (de qué pistas de video sale el audio) NO: déjala anotada. Se verifica con la CI de Windows; la entrega dice qué debe mirar Daniel.

## 8. Propuesta del formato de estilos de texto (fase 5b)

- **Estado:** hecha (11-oct; correcciones finales en la última sección de la propuesta)
- **Rama:** `claude/propuesta-texto`
- **Caminos:** `docs/propuestas/texto.md` (nuevo), `docs/tareas/`.
- **Lee:** `docs/fase-5.md` (por qué el texto va en la 5b), `docs/fase-5c.md` (la 5c cambia `Clip` y el formato 4 antes), `docs/formato-paquete.md`, `docs/formato-proyecto.md`, `src/Rodaje.Core/Model/`, `src/Rodaje.Render/` (solo lectura).

Solo documento, sin código: propuesta para la fase 5b con (1) clips generadores sin medio en el modelo (cómo encajan con el formato 4 de la 5c: ¿formato 5?), (2) contenido de `text-style` en `.paquete` v1 (fuente, tamaño, colores, borde, sombra, fondo, animaciones de entrada y salida sobre las primitivas), (3) render con DirectWrite/Direct2D sobre el dispositivo D3D11 existente y su costo por cuadro, (4) fuentes: cuáles se pueden empaquetar (licencias OFL/Apache) y cómo se usan las del sistema, (5) edición sobre el lienzo, (6) cómo se apoyan los subtítulos de la fase 7. Con ventajas y desventajas reales y máximo 3 preguntas para Daniel.

## 9. `PlaybackSchedulerTests` inestable en la CI de Windows

- **Estado:** hecha (PR #8 unido el 11-oct; salto de 3e11 añadido al unir)
- **Rama:** `claude/playback-seek-tiempo`
- **Caminos:** `tests/Rodaje.Playback.Tests/`, `docs/tareas/`

`PlaybackSchedulerTests.SeekLejosReproduciendo_CuestaLoMismoQueUnSaltoCorto(jump: 10000000000)` falla a veces en el runner de 2 núcleos («tardó 00:00:03.22», límite 2 s en `PlaybackSchedulerTests.cs:358`); en el PC de Daniel tarda <1 ms. Mide con un reloj inyectado o cuenta operaciones en lugar de reloj de pared, de modo que siga detectando un seek que crece con la distancia; si no se puede, categoría `TiempoReal`. Que nunca dependa de la carga del pool.

## 10. `MediaProbe.ScanTimestamps` no rebobina en MP4

- **Estado:** hecha (11-oct)
- **Rama:** `claude/probe-rebobinar`
- **Caminos:** `src/Rodaje.Media/Probe/MediaProbe.cs`, `tests/Rodaje.Media.Tests/` (archivos nuevos), `docs/tareas/`

Tras el análisis, `ScanTimestamps` rebobina con `av_seek_frame(long.MinValue, AVSEEK_FLAG_BACKWARD)`, y en MP4 eso no mueve la posición: quien lea paquetes después del análisis sin hacer un seek propio empieza a mitad del archivo (hoy lo esquivan `KeyframeIndex` reabriendo el archivo y `VideoDecoder` buscando siempre). Reprodúcelo con una prueba (MP4 y MKV, leer el primer paquete tras `ScanTimestamps` y comprobar que es el primero del archivo), corrígelo con un rebobinado que sí funcione en MP4 (`avformat_seek_file` al inicio del flujo, o reabrir) y comprueba que no cambia el resultado del análisis. Verificable en Linux con el FFmpeg 9.0.2 de BtbN (instrucciones en `docs/tareas/2026-10-11-ci-windows.md`, apartado «Pendiente») y con la CI de Windows.

## 11. Defectos del lector de paquetes hallados por el fuzz

- **Estado:** hecha (11-oct)
- **Rama:** `claude/paquetes-textos`
- **Caminos:** `src/Rodaje.Paquetes/`, `tests/Rodaje.Paquetes.Tests/`, `docs/formato-paquete.md`, `docs/tareas/`
- **Lee:** la entrega `docs/tareas/2026-10-11-fuzz-paquete.md` y las pruebas con `Skip = "Defecto: …"` de `tests/Rodaje.Paquetes.Tests/Fuzz/PackageInstallerFuzzTests.cs`.

Corrige y quita los 5 `Skip`: (1) un sustituto suelto escrito como escape JSON (barra invertida + `ud800`) hace que `Install`, `PackageLibrary.Scan` (`ScanRoot:124`, tumba el escaneo al arrancar) y `PresetReader` (`String:533`) lancen `InvalidOperationException` («missing low surrogate») desde `GetString`: rechaza el texto con `PackageInstallException`/`PackageContentException` y mensaje en español; (2) UTF-8 inválido en el contenido lanza `InvalidOperationException` con `DecoderFallbackException`: conviértelo en `PackageContentException`; (3) un `repository` con U+FFFD lanza `UriFormatException` desde `SafeText.TryParseHttpsUri:70`: devuelve inválido en lugar de lanzar. Además, en el fuzz: exige con una aserción que el subconjunto de Windows cubra todos los nombres peligrosos; corrige o quita la comprobación de los centinelas relativos (miran el directorio actual); añade centinelas UNC y `C:x`. Baja a 0 los topes de «defecto conocido» al quitar los `Skip`.

## 12. Lector de video hacia atrás por GOP (base de la reversa, fase 5c.3)

- **Estado:** en pausa (lo hace la sesión local con la ronda 5c.3, 11-oct)
- **Rama:** `claude/media-reversa`
- **Caminos:** archivos NUEVOS en `src/Rodaje.Media/Video/` (no modifiques `VideoDecoder.cs`: el PR #5 lo está cambiando; usa su API pública), `tests/Rodaje.Media.Tests/` (archivos nuevos), `docs/arquitectura/medios.md` (sección nueva), `docs/tareas/`.
- **Lee:** `docs/fase-5c.md` (sección «Reversa»), `docs/arquitectura/medios.md`, `src/Rodaje.Media/Probe/KeyframeIndex.cs`, `src/Rodaje.Media/Video/VideoDecoder.cs` (solo lectura).

Un `ReverseFrameReader` que entregue los cuadros de un tramo en orden descendente y exacto: usa `KeyframeIndex` para buscar el keyframe ≤ el extremo bajo de cada GOP, decodifica hacia adelante guardando solo los cuadros que se mostrarán y los entrega al revés; ventana de dos GOP (uno mostrándose, otro decodificándose); tope de cuadros retenidos (`M`) con **decodificación en dos pasadas** cuando el GOP no cabe (guarda los últimos M, los entrega, redecodifica el GOP para los anteriores), determinista. Pruebas con decodificación por software (sirven en Linux con el FFmpeg de BtbN y en la CI de Windows): recorrer un clip entero hacia atrás da exactamente los índices N-1…0 (número de cuadro dibujado en los medios de prueba), con GOP largos y cortos, con cuadros B, y con M pequeño (dos pasadas); y medir cuántas decodificaciones por cuadro cuesta.

## 13. Lista de codificadores para la exportación (base de la fase 6)

- **Estado:** hecha (PR #14 unido el 11-oct)
- **Rama:** `claude/export-codificadores`
- **Caminos:** archivos NUEVOS en `src/Rodaje.Media/Export/` (carpeta nueva), `tests/Rodaje.Media.Tests/Export/`, `docs/tareas/`.
- **Lee:** `CLAUDE.md` (sección «Hacia la versión pública»: exportar por GPU o CPU sin separar el programa, licencia LGPL en la v1), `docs/legal.md`, `docs/ffmpeg-configure.txt`, `src/Rodaje.Media/Proxies/ProxyEncoders.cs` (solo lectura).

Solo el modelo y la detección, sin exportar todavía: un catálogo de codificadores de salida en orden de preferencia — hardware: NVENC (h264/hevc/av1), AMF (AMD), QSV (Intel), Media Foundation; software y LGPL: SVT-AV1, libaom, libvpx (VP9), kvazaar (HEVC), OpenH264 (H.264, calidad baja) — cada uno con su códec, si es hardware, licencia, y los argumentos base de calidad y velocidad; y una sonda que diga cuáles funcionan en esta máquina (compilado en el FFmpeg y que codifica 1 s de prueba sin error), en paralelo y cacheada. Elección por defecto: «GPU si hay, si no CPU», con la regla de que H.264 sin GPU cae a AV1 o HEVC por CPU (decisión pendiente de Daniel en `CLAUDE.md`). Pruebas con dobles para la elección y una prueba real que liste lo disponible (en la CI de Windows solo habrá software y Media Foundation). Nada de x264/x265 (GPL).

**Diferencias con el contrato del paso 0 de la fase 6 (11-oct, para la revisión del PR #14).** El paso 0 fijó en `src/Rodaje.Media/Export/` el tipo del codificador (`EncoderSpec.cs`, D-028) y el PR #14 no compilará al unirse: los dos declaran `Rodaje.Media.Export.ExportCodec`. Para unirlo hay que alinearlo así:
- `ExportEncoderInfo` → **`EncoderSpec`** (la del contrato; el catálogo pasa a ser `IReadOnlyList<EncoderSpec>`). Quitar su `ExportCodec` (el del contrato es un superconjunto con audio, maestros e imágenes) y `ExportVendor` → `EncoderVendor` (`None` → `Software`). `License` de texto → `EncoderLicense` (el texto de la licencia puede ir en `Note`).
- `PatentEncumbered`/`LowQuality` → **`InDefaults = false`** con `Note = "Patentes de H.264/HEVC sin cubrir por software: no por defecto."` en kvazaar y OpenH264 (y «calidad baja» en OpenH264). La elección automática nunca los usa: H.264 sin GPU ni Media Foundation cae a **AV1 (SVT-AV1) o VP9**, no a kvazaar (decisión de Daniel, D-028).
- `PixelFormat` de texto → **`PixelFormats` (`ExportPixelFormat`)**, del preferido al último: NVENC/AMF/QSV/MF `Nv12` (la GPU baja NV12; NVENC no necesita `yuv420p`), y `P010` en HEVC/AV1 de 10 bits; SVT-AV1, libaom y libvpx `Yuv420P`/`Yuv420P10`.
- `BaseArguments(ExportQuality, ExportSpeed)` → `BaseArguments` fijos más las capacidades: `RateControls`, `Quality` (`QualityScale`: CQ 0–51 en NVENC, CRF 1–63 en SVT-AV1…), `SpeedPresets`/`DefaultSpeedPreset`, `Profiles` (con sus píxeles), `Levels`, `SupportsTwoPass`, `MaxBFrames`, `Tunes`. La tabla calidad × velocidad puede quedarse como ayuda interna para los preajustes de `Rodaje.Export`.
- **Faltan en el catálogo:** audio `aac_mf` (antes que `aac`), `aac`, `libopus`, `libmp3lame`, `flac`, `alac`, `pcm_s16le`, `pcm_s24le`, `pcm_f32le` (con `SampleRates` y `MaxChannels`); video `ffv1`, `dnxhd` (perfiles DNxHR LB/SQ/HQ/HQX/444), `prores_ks` (Proxy/LT/Standard/HQ/4444 con alfa), `mjpeg`, `png`, `qtrle`, `gif`, `libwebp`, `tiff`; `cfhd` si el FFmpeg lo trae. La sonda prueba también los de audio.
- `av1_nvenc`: `Note = "Solo RTX 40 (Ada) o posterior"`; en la RTX 3050 la sonda debe darlo por no disponible sin error.
- La matriz contenedor × códec ya está en `ExportCompatibility.cs` (paso 0); la tarea «matriz de contenedores y validación» la completa con archivos reales.

## 14. `VideoDecoder` con GOP abierto (HEVC con CRA)

- **Estado:** hecha (PR #17 unido el 11-oct en la integración local; CI sin minutos, verificado en la RTX 3050)
- **Rama:** `claude/media-gop-abierto`
- **Caminos:** `src/Rodaje.Media/Video/`, `src/Rodaje.Media/Probe/`, `testing/Rodaje.TestMedia/` (preset nuevo), `tests/Rodaje.Media.Tests/`, `docs/arquitectura/medios.md`, `docs/tareas/`
- **Lee:** la revisión del PR #9, `docs/arquitectura/medios.md` (seek exacto y GOP abierto), `VideoDecoder.cs`, `KeyframeIndex.cs`.

Con un HEVC de GOP abierto (p. ej. el que genera libkvazaar del build LGPL: IDR en 0 y CRA en 64, 128, 192 con cuadros RASL) el decodificador falla: en modo `Keyframe` pedir el 64 entrega el 0 (software y hardware), una petición exacta devuelve null (`Exact_requests_never_skip_hardware(hevc)`) y el acceso aleatorio por hardware no entrega el 216. Hipótesis a comprobar: con `AVDISCARD_NONKEY` un CRA no vacía el DPB como un IDR y el cuadro queda retenido en el reordenamiento; en exacto, los RASL tras un seek a un CRA se descartan mal. Corrígelo y añade un preset de prueba de **GOP abierto** que se genere en cualquier máquina (libkvazaar u otro LGPL), para que también la RTX de Daniel cubra el caso: seek exacto, acceso aleatorio, modo `Keyframe` y `SkipNonReference`, en software (y en hardware donde haya GPU). Quita las omitidas del PR #9 que nombren este defecto.

## 15. Personalización: de qué pistas de video sale el audio

- **Estado:** hecha (PR #16 unido el 11-oct)
- **Rama:** `claude/audio-por-pista`
- **Caminos:** `src/Rodaje.App/Services/Settings/`, la página de ajustes, el punto donde la app importa o suelta un medio en la línea de tiempo (búscalo; no toques `src/Rodaje.Core`), `tests/Rodaje.App.Tests/` (archivos nuevos), `docs/tareas/`.
- **Lee:** `docs/pendientes.md` («Personalización»), la entrega de la tarea 7 (pistas por defecto) si ya existe, `docs/arquitectura/app.md`.

Segunda mitad de lo pedido por Daniel: en Ajustes, elegir de qué pistas de video sale el audio de un medio a su propia pista de audio al importarlo o soltarlo (todas, o una lista: por ejemplo Video 1 y 2 sí, Video 3 no). Si el video cae en una pista sin audio, el medio entra sin su clip de audio (o con el audio desactivado, decide y justifica). Guardado en `ajustes.json`. Pruebas de la regla con la sesión real y una nota en `docs/pendientes.md` marcando lo hecho.

## 16. Material perdido al encadenar curvas de velocidad

- **Estado:** hecha (PR #18 unido el 12-oct en modo autónomo)
- **Rama:** `claude/curvas-material`
- **Caminos:** `src/Rodaje.Core/Editing/`, `src/Rodaje.Core/Timing/` (solo si hace falta), `tests/Rodaje.Core.Tests/` (archivos nuevos), `docs/tareas/`
- **Lee:** `docs/fase-5c.md`, `docs/arquitectura/nucleo.md` (sección Velocidad), `src/Rodaje.Core/Editing/TimelineEdits.Speed.cs` (`AlignEnd`), `src/Rodaje.Core/Model/SpeedPresets.cs`.

Aplicar «Bala», luego «Héroe» y luego «Ninguna» a un clip de 10 s lo deja en 9,942 s: el redondeo al cuadro de `AlignEnd` con curvas va comiendo material (~58 ms). Reprodúcelo con una prueba (cadenas aleatorias de preajustes, `SetCurve`, `SetSpeed` y vuelta a 1×: el material al volver a 1× debe ser el original, o como mucho un cuadro de origen distinto y nunca acumulativo) y corrígelo (calcular cada cambio desde el material original del clip, no desde el ya redondeado; o redondear al cuadro más cercano conservando el material). Incluye la propiedad en las pruebas de 10 000 proyectos si es barato.

## 17. Cortar en todas las pistas y borrar a un lado del cursor

- **Estado:** hecha (PR #21 unido el 11-oct en la integración local)
- **Rama:** `claude/qdv-cortes`
- **Caminos:** `src/Rodaje.Core/Editing/`, `src/Rodaje.Core/Commands/`, `tests/Rodaje.Core.Tests/` (archivos nuevos), `src/Rodaje.App/Services/Shortcuts/`, `src/Rodaje.App/Services/EditorSession*.cs` (parcial nuevo), `docs/tareas/`.
- **Lee:** `docs/pendientes.md` (funciones de calidad de vida, aprobadas por Daniel el 11-oct: 2), `docs/arquitectura/nucleo.md`, D-026 (magnetismo).

Cortar en el cursor en todas las pistas desbloqueadas a la vez (un paso del historial) y «borrar a la izquierda/derecha del cursor» del clip seleccionado (Q/W, con ripple según el modo de la sesión). Atajos en los 3 estilos y en la hoja rápida. Pruebas del núcleo y de los atajos; la UI se verifica con la CI.

## 18. Enlazar y desenlazar, separar audio, y copiar/pegar atributos

- **Estado:** hecha (PR #28 unido el 11-oct en la integración local)
- **Rama:** `claude/qdv-enlaces-atributos`
- **Caminos:** `src/Rodaje.Core/Editing/`, `src/Rodaje.Core/Commands/`, `tests/Rodaje.Core.Tests/` (archivos nuevos), `src/Rodaje.App/Services/` (parciales nuevos), menús contextuales de clip en `src/Rodaje.App/Views/Timeline/` (lo mínimo), `docs/tareas/`.
- **Lee:** `docs/pendientes.md` (funciones 3 y 7), `docs/arquitectura/nucleo.md`, `app-vistas.md`.

Enlazar/desenlazar clips con un clic y «separar el audio» a su propia pista (crea el clip de audio enlazado si el medio lo tiene); «copiar atributos» de un clip y «pegar atributos» (efectos, transformación, velocidad, volumen; con selección de cuáles) en uno o varios clips, un paso del historial. Pruebas del núcleo con propiedades si es barato.

## 19. Reemplazar medio y lista de marcadores

- **Estado:** hecha (PR #23 unido el 11-oct en la integración local; el color de los marcadores queda pendiente, pide cambiar el modelo)
- **Rama:** `claude/qdv-reemplazar-marcadores`
- **Caminos:** `src/Rodaje.Core/Editing/`, `src/Rodaje.Core/Commands/`, `tests/Rodaje.Core.Tests/` (archivos nuevos), `src/Rodaje.App/Views/Library/` y un panel nuevo de marcadores en `src/Rodaje.App/Views/Markers/`, `docs/tareas/`.
- **Lee:** `docs/pendientes.md` (funciones 5 y 8), D-020 (marcadores y regiones).

Reemplazar el medio de un clip por otro conservando duración, efectos y keyframes (con aviso si el nuevo es más corto). Marcadores con nombre y color, y una lista para saltar entre ellos (doble clic va al marcador; edición de nombre y color), con el estilo de la app (D-027).

## 20. Exportación en la CI de Windows (sin GPU)

- **Estado:** hecha (PR #22 unido el 11-oct; al unir se corrigió la excepción del rango de h264_mf en MOV, que no llegaba al verificador)
- **Rama:** `claude/export-ci`
- **Caminos:** `tests/Rodaje.Media.Tests/Export/` (archivos nuevos), `.github/workflows/ci.yml` (solo si hace falta `ffmpeg.exe` en el runner), `docs/tareas/`.
- **Lee:** `docs/fase-6.md`, `docs/arquitectura/medios.md` (sección «Salida de la exportación»), `src/Rodaje.Media/Export/FfmpegPipeSink.cs`, `src/Rodaje.Export/FfmpegArguments.cs`.

Exportaciones cortas reales con fuentes `lavfi` por `FfmpegPipeSink` y `FfmpegArguments`, verificadas con `ExportVerifier`, con los codificadores que hay en un runner sin GPU: `h264_mf` (software), SVT-AV1, VP9, ProRes, DNxHR, FFV1, PNG; audio AAC/Opus/FLAC/PCM. Cancelación sin procesos vivos. Que corran en la CI de Windows (no se omiten por falta de GPU) y se omitan con causa concreta si falta `ffmpeg.exe`.

## 21. Licencias de la exportación

- **Estado:** hecha (PR #15 unido en local el 12-oct)
- **Rama:** `claude/export-licencias`
- **Caminos:** `LICENCIAS.md`, `docs/legal.md`, `docs/tareas/`.
- **Lee:** `docs/fase-6.md` (LGPL en la v1), `docs/ffmpeg-configure.txt`, `src/Rodaje.Media/Export/` (catálogo y codificadores).

Por cada codificador y biblioteca que usa la exportación (SVT-AV1, libaom, libvpx, libopus, LAME, kvazaar, OpenH264, Media Foundation, NVENC/AMF/QSV, ProRes/DNxHR de FFmpeg): licencia, si se redistribuye, obligaciones (aviso, código fuente de FFmpeg LGPL, patentes de H.264/HEVC/AAC con el códec del sistema o de la GPU) y la lista de comprobación de publicación en `docs/legal.md`. Solo hechos verificables, con enlace a la fuente.

## 22. Autoguardado y recuperación

- **Estado:** hecha (PR #20 unido el 11-oct en la integración local)
- **Rama:** `claude/qdv-autoguardado`
- **Caminos:** `src/Rodaje.App/Services/Autosave/` (nuevo), `src/Rodaje.Core/Serialization/` (archivos nuevos si hace falta), `tests/Rodaje.Core.Tests/` (archivos nuevos), el arranque de la App donde se ofrece recuperar (lo mínimo), `docs/tareas/`.
- **Lee:** `docs/pendientes.md` (función 30), `docs/formato-proyecto.md`, `docs/arquitectura/app.md`.

Guardar una copia del proyecto cada N minutos y tras cambios (en segundo plano, escritura atómica, sin bloquear la edición) en `%LOCALAPPDATA%\Rodaje\Autoguardado`, con un historial de las últimas versiones; al abrir tras un cierre inesperado, ofrecer recuperar. Pruebas: escritura atómica, rotación del historial, detección de cierre inesperado.

## 23. Zoom a la selección y ajustar a la ventana

- **Estado:** hecha (PR #24 unido el 11-oct en la integración local)
- **Rama:** `claude/qdv-zoom`
- **Caminos:** `src/Rodaje.App/Views/Timeline/Logic/` (archivos nuevos), `src/Rodaje.App/Services/Shortcuts/`, la barra de la línea de tiempo (lo mínimo), `tests/` de la lógica, `docs/tareas/`.
- **Lee:** `docs/pendientes.md` (función 9), `docs/arquitectura/app-vistas.md`.

«Ajustar a la ventana» (todo el proyecto visible) y «zoom a la selección» (los clips elegidos llenan la vista), con atajos en los 3 estilos y animación corta. La lógica (cálculo de escala y desplazamiento) es pura y con pruebas.

## 24. Plantillas de proyecto

- **Estado:** hecha (PR #19 unido el 11-oct en la integración local)
- **Rama:** `claude/qdv-plantillas`
- **Caminos:** `src/Rodaje.App/Services/Templates/` (nuevo), el diálogo de proyecto nuevo (lo mínimo), `tests/` de la lógica, `docs/tareas/`.
- **Lee:** `docs/pendientes.md` (función 33), PR #10 (pistas por defecto), `docs/formato-proyecto.md`.

Al crear un proyecto: plantillas 16:9 1080p, 9:16 1080×1920 (TikTok/Reels/Shorts), 1:1, 4K, con fps y pistas; y «guardar como plantilla» desde el proyecto actual (formato, pistas, nombres de pista). Pruebas de la lógica.

## 25. Fuzz del protocolo de módulos

- **Estado:** cambios (PR #27: `ClientFuzzTests` se cuelga en Windows; ver el PR)
- **Rama:** `claude/modulos-fuzz`
- **Caminos:** `tests/Rodaje.Modules.Protocol.Tests/` (archivos nuevos), `docs/tareas/`.
- **Lee:** `docs/fase-7.md`, D-031, `src/Rodaje.Modules.Protocol/`.

Mensajes y marcos al azar en los dos sentidos (largos, JSON hostil, ids repetidos, regiones de memoria fuera de rango, versiones raras): `ModuleClient` nunca se cuelga ni lanza excepciones no controladas, y `ModuleHost` se cierra limpio. Semillas reproducibles y un tope de tiempo por caso; lo largo con `Categoria=TiempoReal`.

## 26. Documentación del protocolo para terceros

- **Estado:** hecha (PR #25 unido el 11-oct en la integración local)
- **Rama:** `claude/modulos-doc`
- **Caminos:** `docs/protocolo-modulos.md` (nuevo), `docs/tareas/`.
- **Lee:** `docs/fase-7.md`, D-031, D-032 (excepción para complementos de `LICENSE`), `src/Rodaje.Modules.Protocol/`, `modules/Rodaje.Module.Echo/`.

Guía para escribir un módulo de terceros: transporte, saludo y versión, mensajes con ejemplos JSON reales, progreso, cancelación, errores, memoria compartida, límites, y un módulo mínimo de ejemplo. Cada afirmación verificada contra el código. Explica que los complementos que hablan por este protocolo pueden tener su propia licencia (término 4 de `LICENSE`).

## 27. Servidor HTTP local para probar descargas

- **Estado:** cambios (PR #26: un corte con `Abort` en Windows pierde los bytes ya enviados; ver el PR)
- **Rama:** `claude/descarga-lab`
- **Caminos:** `testing/Rodaje.TestServer/` (nuevo), `docs/tareas/`.
- **Lee:** `docs/fase-7.md` (sección Descarga).

Servidor de pruebas en proceso (Kestrel o `HttpListener`) que sirve archivos con `Range`, cortes a mitad, redirecciones, hashes y firmas malas, lentitud y 404/500, configurable por prueba, para el agente local que hace `GitHubReleaseDownloader`. Con sus propias pruebas.

## 28. Volver a enlazar medios movidos o renombrados

- **Estado:** pendiente
- **Rama:** `claude/qdv-reenlazar`
- **Caminos:** `src/Rodaje.Core/Editing/` y `src/Rodaje.Core/Commands/` (archivos nuevos), `src/Rodaje.App/Services/Relink/` (nuevo), un diálogo nuevo en `src/Rodaje.App/Views/Relink/`, pruebas, `docs/tareas/`.
- **Lee:** `docs/pendientes.md` (función 32), `docs/formato-proyecto.md`, `docs/arquitectura/app.md`.

Al abrir un proyecto con medios que faltan: diálogo con la lista, «Buscar en carpeta» que encuentra todos por nombre, tamaño y duración (y huella rápida de los primeros MB si hay dudas), y reenlazar uno a mano. Un paso del historial; los clips, efectos y keyframes no cambian. Pruebas de la búsqueda y de la edición.

## 29. Exportar el fotograma actual como imagen

- **Estado:** pendiente
- **Rama:** `claude/qdv-fotograma`
- **Caminos:** `src/Rodaje.Export/Still/` (nuevo), una entrada en el menú Archivo y un atajo (lo mínimo), pruebas, `docs/tareas/`.
- **Lee:** `docs/pendientes.md` (función 34), `docs/arquitectura/exportacion.md`, D-028.

Exportar el cuadro del cursor a PNG (con alfa si la secuencia lo tiene) o JPEG con calidad, a la resolución de la secuencia o la del preajuste, usando el mismo motor fuera de línea y el compositor estricto que la exportación (títulos incluidos). Pruebas contra la vista previa.

## 30. Biblioteca con carpetas, búsqueda y favoritos

- **Estado:** hecha (PR #38 unido el 12-oct en modo autónomo; carpetas y favoritos en `%LOCALAPPDATA%`, fuera del `.rodaje`)
- **Rama:** `claude/qdv-biblioteca`
- **Caminos:** `src/Rodaje.App/Views/Library/`, `src/Rodaje.Core/Model/` (solo si hace falta guardar carpetas en el proyecto, con migración de formato documentada), pruebas, `docs/tareas/`.
- **Lee:** `docs/pendientes.md` (función 38), `docs/formato-proyecto.md`, D-027 (estilo).

Carpetas en la biblioteca del proyecto (crear, renombrar, arrastrar medios), búsqueda por nombre, filtro por tipo, favoritos, y columna de datos (resolución, duración, códec, fps). Sin tocar los paneles de títulos ni análisis.

## 31. Un solo catálogo de codificadores

- **Estado:** pendiente
- **Rama:** `claude/export-catalogo-unico`
- **Caminos:** `src/Rodaje.Media/Export/`, `src/Rodaje.Export/Encoders/`, `src/Rodaje.Media/Proxies/ProxyEncoders.cs`, sus pruebas, `docs/arquitectura/exportacion.md`, `docs/tareas/`.
- **Lee:** revisión en bloque del 11-oct (M3): hay tres listas (`ExportEncoderCatalog` de #14 sin usar, `ExportEncoderCandidates`/`ProbedEncoderSource` en la app, `ProxyEncoders`).

Conecta `ExportEncoderAvailability` a `IExportEncoderSource` (punto previsto en `ExportEncoderSource.cs:9`), borra `ExportEncoderCandidates` y `ProbedEncoderSource`, y que `ProxyEncoders` use el mismo catálogo. Un codificador de hardware sin dispositivo no debe reintentarse cada 2 min. Pruebas sin dobles duplicados.

## 32. FFV1 solo intra, rango de color en MOV y pruebas sobre el código real

- **Estado:** pendiente
- **Rama:** `claude/export-ffv1-rango`
- **Caminos:** `src/Rodaje.Media/Export/ExportEncoderCatalog.cs`, `src/Rodaje.Export/FfmpegArguments.cs`, `tests/Rodaje.Media.Tests/Export/CpuExport*` (moverlas a `tests/Rodaje.Export.Tests/`), `docs/tareas/`.
- **Lee:** revisión en bloque (M4, M5, M7).

FFV1 con `-g 1` (hoy sale un cuadro clave cada 12); `h264_mf`/`hevc_mf` en MOV escriben el rango con `h264_metadata`/`hevc_metadata` según `ExportColor` (y la prueba deja de aceptar «vacío»); las pruebas de exportación por CPU usan `FfmpegArguments.Build` real en vez de una copia.

## 33. Cancelar la exportación en ≤1 s también con VP9

- **Estado:** hecha (PR #33 unido el 12-oct junto con #31 y #34 por el PR de integración, que es el primero con Windows en verde)
- **Rama:** `claude/export-abort`
- **Caminos:** `src/Rodaje.Media/Export/FfmpegPipeSink.cs`, `tests/Rodaje.Media.Tests/Export/`, `docs/tareas/`.
- **Lee:** revisión en bloque (M6), `docs/fase-6.md` (criterio de cancelación).

`Abort` espera 900 ms y el escritor queda bloqueado en `WriteFile` (1,6–6,3 s en la CI con VP9). Esperar sin el cerrojo o terminar el Job Object al vencer; bajar el límite de `CpuExportCancellationTests` a 1 s y que sea estable.

## 34. Autoguardado: pruebas del servicio y orden de escritura

- **Estado:** hecha (PR #37 unido el 12-oct en modo autónomo)
- **Rama:** `claude/autoguardado-pruebas`
- **Caminos:** `src/Rodaje.App/Services/Autosave/`, `src/Rodaje.Core/` (solo `AutosaveSession`), `tests/`, `docs/tareas/`.
- **Lee:** revisión en bloque (M8, M9).

Pruebas de `AutosaveService` con reloj falso y un doble de `IEditorDialogs`: `Tick`, cola de escrituras, `MarkSaved` al guardar o deshacer hasta lo guardado, `Clear` al abrir, `RecoverAsync` con copia dañada y con `sesion.json` corrupto, `Discard` que falla al borrar el bloqueo (`AutosaveRecovery.cs:69`), doble clic en «Recuperar». Escribe `sesion.json` (`unsavedChanges: true`) antes de la copia en `AutosaveSession.Write` (`:125`).

## 35. Limpiezas de la revisión en bloque

- **Estado:** hecha (PR #36 unido el 12-oct en modo autónomo; falta confirmar al borrar una plantilla, que vive en `Shell/TitleBar.xaml.cs`, fuera de los caminos)
- **Rama:** `claude/limpiezas-bloque-1`
- **Caminos:** `src/Rodaje.App/Services/EditorSession*.cs`, `src/Rodaje.Core/Editing/ClipAttributes.cs`, `src/Rodaje.App/Views/Timeline/` (zoom), `src/Rodaje.App/Services/Templates/`, `docs/protocolo-modulos.md`, sus pruebas, `docs/tareas/`.
- **Lee:** revisión en bloque (menores y M10).

Quitar `TrimStartToPlayhead`/`TrimEndToPlayhead`/`TrimToPlayhead` (`EditorSession.Editing.cs:36,39`) y migrar `EditingAtPlayheadTests` a `DeleteLeft/RightOfPlayhead`; pegar velocidad 1× restablece tono y muestreo (`ClipAttributes.cs:257`) y los keyframes se recortan a la duración del destino; el zoom desengancha `CompositionTarget.Rendering` al descargar la vista y usa `TimelineViewport.FitTo`; plantillas: quitar bidi en `Clean`, confirmar al borrar, no permitir el nombre de una de fábrica; `docs/protocolo-modulos.md` al día con el `ModuleManager` ya en main.

## 36. Pausas de la vista previa en cortes con velocidad y transiciones

- **Estado:** pendiente
- **Rama:** `claude/vista-previa-pausas`
- **Caminos:** `src/Rodaje.Engine/Timeline/`, `src/Rodaje.Playback/`, sus pruebas, `docs/arquitectura/motor.md`, `docs/tareas/`.
- **Lee:** `docs/fase-6.md` («Cifras de la aceptación»: bloqueos de 59–370 ms sin exportar en cortes y transiciones), `tests/Rodaje.Export.Tests/Acceptance/AcceptanceScene.cs`.

Reproduce con la escena de aceptación sin exportar, mide qué bloquea (pre-roll, apertura de decodificadores, compilación de shaders) y corrígelo para que ningún refresco tarde >50 ms en una máquina normal. Prueba que lo exija (con `Categoria=TiempoReal` si mide tiempo).

## 37. Subtítulos automáticos en la app

- **Estado:** pendiente
- **Rama:** `claude/subtitulos-ui`
- **Caminos:** archivos nuevos en `src/Rodaje.App/Views/Analysis/` o `Views/Subtitles/`, `src/Rodaje.Core/Model/` (campo `generator.words` con migración documentada si hace falta), pruebas, `docs/tareas/`.
- **Lee:** `docs/fase-7.md` (sección del agente E: lo que queda para la UI), `docs/arquitectura/app.md` («Módulos de IA»), `src/Rodaje.Core/Editing/SubtitleEdits.cs`.

«Subtítulos automáticos» en el menú del clip: baja el audio a f32 16 kHz con `ModuleMedia`, llama a `modulo-subtitulos` por el `ModuleManager`, pasa los tiempos con `ModuleTime`/`ClipTimeMap` y muestra una propuesta con vista previa que aplica `Edits.AddSubtitles` en un paso. Guarda los tiempos por palabra (`generator.words`) para editar por palabra después.

## 38. Traducir subtítulos (módulo)

- **Estado:** pendiente
- **Rama:** `claude/modulo-traducir`
- **Caminos:** `modules/Rodaje.Module.Translate/` (nuevo), `tests/Rodaje.Module.Translate.Tests/` (nuevo), `herramientas/preparar-modelos.ps1` (añadir entradas), archivos nuevos en la App para la acción, `docs/tareas/`.
- **Lee:** `docs/fase-7.md` (agente F: Qwen3-4B-Instruct-2507 Q4 Apache-2.0 con LLamaSharp; Opus-MT como opción ligera; NLLB descartado por no comercial), D-031, D-032, `modules/Rodaje.Module.Subtitles/` como patrón.

Traduce los clips de una pista de subtítulos a otro idioma creando una pista nueva, conservando tiempos. Licencias en `LICENCIAS.md` del módulo; modelos con SHA-256 fijado. Pruebas con frases conocidas y medida de velocidad.

## 39. Quitar fondo y reencuadre automático (módulo de visión)

- **Estado:** pendiente
- **Rama:** `claude/modulo-vision`
- **Caminos:** `modules/Rodaje.Module.Vision/` (nuevo), sus pruebas, `herramientas/preparar-modelos.ps1`, la primitiva de máscara en Render y archivos nuevos en la App, `docs/tareas/`.
- **Lee:** `docs/fase-7.md` (agente H: MODNet Apache-2.0 por defecto, BiRefNet_lite MIT como calidad alta; YuNet MIT y RT-DETRv2 Apache-2.0 para reencuadre; RMBG y RVM y YOLO descartados), D-031, D-032.

Quitar fondo como máscara (FFV1 gris en la caché del proyecto) aplicada por una primitiva de máscara del render; reencuadre a 9:16/1:1 siguiendo rostros o personas con keyframes de transformación suavizados. Pruebas con imágenes sintéticas y medidas de velocidad/VRAM.

## 40. URGENTE: main en rojo en la CI de Windows (módulos, títulos, codificadores)

- **Estado:** hecha (PR #31 unido el 12-oct por el PR de integración con #33 y #34)
- **Rama:** `claude/ci-main-verde`
- **Caminos:** `tests/Rodaje.App.Tests/Modules/`, `tests/Rodaje.App.Tests/Inspector/`, `tests/Rodaje.App.Tests/` (otras pruebas que fallen en main), `src/Rodaje.App/Services/Modules/` y el formateo de números del editor de títulos (solo si el defecto es del código), `tests/Rodaje.Render.Tests/Text/`, `tests/Rodaje.Media.Tests/Export/ExportEncoderAvailabilityTests.cs`, `tests/Rodaje.Core.Tests/` (solo si falla en main), `docs/tareas/`
- **Lee:** el log de la corrida de main en f89d896 (run 38194016251, job 114632689518), `testing/Rodaje.TestMedia/Skip.cs`.

Desde f89d896 falla el trabajo de Windows: `ModuleManagerTests` (varias: «El módulo no se conectó en 10 s», `TimeoutException` en lugar de `ModuleHungException`, «No se cumplió a tiempo»), `TitleEditorTests.BordesYConvertirEnKeyframes` («0.5» frente a «0,5»: cultura del runner), `TitleRasterizerTests.Composing_a_ready_title_costs_little_gpu_time` (3,8 ms en WARP), `ExportEncoderAvailabilityTests.Probes_run_in_parallel_up_to_the_limit` (2 frente a 3 con el runner cargado); en otros PR además `TitleClipTests.ElTituloSeVeEnElVisor` y `EditPropertiesTests.OperacionesAleatorias_CumplenTodasLasReglas`. Causa de cada una; corrige el código si es un defecto (p. ej. formatear con la cultura de la app), haz robustas las que dependen de la carga, y omite solo con causa concreta (WARP sin GPU), con conteo de omitidas antes y después. Objetivo: Windows de main en verde para que el modo autónomo pueda volver a unir.

## 41. Overwrite cambia el volumen con keyframes del efecto de pista

- **Estado:** pendiente
- **Rama:** `claude/overwrite-gain-keyframes`
- **Caminos:** `src/Rodaje.Core/Editing/`, `tests/Rodaje.Core.Tests/` (archivos nuevos), `docs/tareas/`
- **Lee:** entrega de la tarea 40 (`docs/tareas/2026-10-12-ci-main-verde.md`), `docs/arquitectura/nucleo.md`.

`EditPropertiesTests.OperacionesAleatorias_CumplenTodasLasReglas` falla con `CsCheck_Seed=5ZOfD5KG8C16`: Overwrite cambia el `gainDb` con keyframes del efecto de pista v1. Prueba de regresión con esa semilla y corrección en el núcleo.

## 42. Números con coma decimal en toda la App

- **Estado:** pendiente
- **Rama:** `claude/analisis-cultura`
- **Caminos:** `src/Rodaje.App/Views/Analysis/`, `src/Rodaje.App/Services/Titles/SubtitleStyles.cs`, `src/Rodaje.App/Views/Inspector/Text/TextPanel.xaml.cs`, `src/Rodaje.App/Views/Voice/VoiceDialog.cs`, `src/Rodaje.App/Services/Format/` (un archivo nuevo para el formato común), `src/Rodaje.App/Services/Inspector/TitleEditor.cs` y el `SpeedMath` de la App (solo para usar el formato común), `tests/Rodaje.App.Tests/` (archivos nuevos), `docs/tareas/`
- **Lee:** entrega de la tarea 40 (la app corre con InvariantGlobalization; `TitleEditor.DescribeAnimation` ya usa coma fija como `SpeedMath`), revisión en bloque 2 (hallazgo 4).

La app corre con `InvariantGlobalization` (`Rodaje.App.csproj:38`), así que todo formato con `CurrentCulture` sale con punto decimal. Siguen así: `AnalysisUi.Db/Ms/Sec` (`AnalysisUi.cs:43-47`), el BPM de `RhythmDialog` (`:95`), el contraste de `SubtitleStyles` (`:54,83`), `StabilizeDialog` (`:50,57`), `SilenceDialog` (`:58`), `DuckingDialog` (`:68`), `NoiseReductionDialog` (`:71`), `AutoColorAction` (`:36-38`), `TextPanel.xaml.cs:106` y `VoiceDialog.cs:274`. Un solo `NumberFormatInfo` en español compartido (hoy `TitleEditor` y `SpeedMath` duplican el suyo), usado en todos esos sitios, con pruebas y una prueba que falle si vuelve a aparecer `CurrentCulture` en un formato numérico de `src/Rodaje.App`.

## 43. `NU1004` intermitente al restaurar en la CI de Windows

- **Estado:** hecha (PR #34 unido el 12-oct por el PR de integración con #31 y #33)
- **Rama:** `claude/ci-restore-rid`
- **Caminos:** `.github/workflows/ci.yml`, los `.csproj` y `packages.lock.json` de `src/Rodaje.Modules.Protocol/` y `modules/*/` (solo lo mínimo), `docs/tareas/`
- **Lee:** jobs 114670188931 (PR #31, falla en `Rodaje.Modules.Protocol`: «runtime identifiers have changed from. Project's runtime identifiers: , lock file's runtime identifiers win-x64») y 114665862040 (PR #30, falla en `Rodaje.Module.Echo`: «win-x64» frente a «linux-x64»).

`dotnet restore Rodaje.slnx --locked-mode` falla a veces antes de compilar, cada vez en un proyecto distinto, con los identificadores de runtime cruzados entre los proyectos que publican con AOT (`win-x64`) y `Rodaje.Modules.Protocol`, que no declara ninguno. Averigua la carrera (¿un `RuntimeIdentifier` heredado por referencia de proyecto o un lockfile regenerado en Linux?), corrígela para que la restauración sea determinista en Windows y en Linux, y que la CI lo compruebe.

## 44. Carrera entre `Abort` y `Complete` en la exportación, y `Kill` fuera del cerrojo

- **Estado:** pendiente
- **Rama:** `claude/export-abort-carrera`
- **Caminos:** `src/Rodaje.Media/Export/FfmpegPipeSink.cs`, `tests/Rodaje.Media.Tests/Export/` (archivos nuevos o `FfmpegPipeSinkFailureTests.cs`), `docs/tareas/`
- **Lee:** revisión en bloque 2 (hallazgos 1 y 5), entrega de la tarea 33 (`docs/tareas/2026-10-12-export-abort.md`).

1. Si se cancela mientras `Complete` espera a que ffmpeg cierre el contenedor (`while (!process.WaitForExit(100))`, ~`:234`), `Abort` corre en el hilo que cancela, mata y libera el `Process` (`ReleaseProcess`, ~`:610`), y `Complete` sigue esperando sobre un objeto liberado: sale `InvalidOperationException`/`ObjectDisposedException` en vez de `OperationCanceledException`. `Failure` (~`:530`) lee `_process` fuera del cerrojo. Que `Complete` y `Failure` no usen un `Process` liberado (leerlo bajo `_gate`, o no liberarlo mientras `Complete` está dentro) y que una cancelación siempre llegue como cancelación. Prueba que cancele durante `Complete`.
2. `Kill` (`:622-653`) espera `KillWait` dos veces con `_gate` tomado (peor caso ~1,8 s, por encima del criterio de 1 s). Si ffmpeg sigue vivo tras el segundo intento, hoy se libera igual y se intenta borrar temporales que ffmpeg tiene abiertos. Esperar sin el cerrojo, no liberar ni borrar si no salió (reintentar o encolar el borrado) y registrarlo en el diagnóstico.

## 45. `docs/legal.md` al día con `LICENSE` y el repo público

- **Estado:** pendiente
- **Rama:** `claude/legal-al-dia`
- **Caminos:** `docs/legal.md`, `LICENCIAS.md`, `docs/pendientes-legales.md`, `docs/tareas/`
- **Lee:** `LICENSE`, `Directory.Build.props`, D-032, `docs/ffmpeg-configure.txt`, revisión en bloque 2 (hallazgos 2 y 3).

1. `docs/legal.md:59` («Versión de la GPL») dice que `LICENSE` no fija la versión; `LICENSE:4-5` dice «version 3 only (SPDX: GPL-3.0-only)» y `Directory.Build.props:14` también. Marcarlo resuelto con la cita.
2. `docs/legal.md:55` (sección e) dice que el repositorio es privado y el enlace del crédito da 404; es público desde el 11-oct. Marcarlo cumplido.
3. `docs/legal.md:129` concluye «Ninguna es GPL-only» para las bibliotecas de la FFmpeg que no son de exportación (dav1d, freetype, harfbuzz, fribidi, libass, zimg, libvmaf, libzmq, libssh, libsrt, librist, libplacebo, libbluray, gmp, librsvg, libxml2, libopenjpeg, libjxl…) sin filas ni fuente. Añadir las filas con licencia y enlace en `LICENCIAS.md` y su compatibilidad con la GPLv3 (ojo con FreeType FTL/GPLv2, gmp LGPLv3/GPLv2, libzmq), o reescribir la frase como «sin revisar» y llevarlo a `docs/pendientes-legales.md`. La fila de IJG (`docs/legal.md:70`) necesita su fuente. Solo hechos verificables con enlace.

## 46. Limpiezas de la revisión en bloque 2

- **Estado:** pendiente
- **Rama:** `claude/limpiezas-bloque-2`
- **Caminos:** `herramientas/app.ps1` (solo la línea del eco), `.github/workflows/ci.yml` (paso «Lockfiles deterministas»), `tests/Rodaje.Engine.Tests/Offline/` (archivos nuevos), `tests/Rodaje.Engine.Tests/Timeline/TimelineSpeedTests.cs`, `tests/Rodaje.App.Tests/Modules/` (archivos nuevos), `docs/tareas/`
- **Lee:** revisión en bloque 2 (hallazgos 6 y 7), entregas de las tareas 40 y 43.

1. Quitar el `-r win-x64` que sobra al publicar el eco en `herramientas/app.ps1:169` y que el paso «Lockfiles deterministas» restaure en modo bloqueado, con `-r win-x64`, todos los proyectos que se publican con AOT (Echo, Voice, Subtitles y AudioAi), no solo el eco.
2. `OfflineTimelineTests` usa ahora un reloj quieto: añadir una prueba aparte con reloj real que cubra el cierre de decodificadores ociosos y el tope de composición a medias (con `Categoria=TiempoReal` si mide tiempo).
3. `TimelineSpeedTests.Speed_adds_no_bytes_to_what_each_tick_already_reserves` falló una vez por 1 B en la CI de Windows: averiguar qué reserva de más y corregir la causa, o justificar el margen.
4. Una prueba que fije `ModuleManagerOptions.StartTimeout` (30 s) y el camino «no se conectó a tiempo» con un tope corto inyectado.


## 47. Pruebas de la App que fallan con el runner de Windows cargado

- **Estado:** pendiente
- **Rama:** `claude/app-pruebas-carga`
- **Caminos:** `tests/Rodaje.App.Tests/Media/ClipVisualsTests.cs`, `tests/Rodaje.App.Tests/` (las otras pruebas citadas), `tests/Rodaje.Media.Tests/Export/ExportEncoderAvailabilityTests.cs`, `tests/Rodaje.Engine.Tests/Timeline/` y `tests/Rodaje.Module.AudioAi.Tests/` (las pruebas citadas), `src/Rodaje.App/Services/` y `src/Rodaje.Media/Export/ExportEncoderAvailability*.cs` (solo si el defecto es del código), `docs/tareas/`
- **Lee:** el job 114679035551 (PR #32), el comentario del 12-oct en el PR #33 y `testing/Rodaje.TestMedia/Skip.cs`.

`ClipVisualsTests.Dispose_does_not_wait_for_a_reader_that_ignores_cancellation` exige que `Dispose` vuelva en menos de 1 s y en la CI tardó 8082 ms (`ClipVisualsTests.cs:402`), en un PR que no toca esa parte. En el job 114683663348 (PR #40) falló `ClipVisualsTests.A_second_session_reads_thumbnails_and_peaks_from_disk` con «No llegó a tiempo» (`:150`, `:272`). En la corrida de #33 fallaron además `PackageServiceTests`, `ModuleMediaTests` y `VideoAnalysisTests`. En el job 114681867969 (PR #40, que solo cambia la cola) falló `ExportEncoderAvailabilityTests.Invalidate_during_a_run_starts_the_next_one_only_after_it_ends` (esperaba 1 corrida y hubo 2): puede ser una carrera real en `Invalidate`. También fallaron una vez, en PR que no las tocan: `ModuleManagerTests.Una_llamada_larga_que_contesta_el_ping_no_se_toma_por_colgada` (#36, #37, #41), `AudioAi ProcessTests.Bad_buffers_fail_the_call_without_closing_the_module` (#41), `TimelineReverseTests.A_paused_tick_reserves_the_same_in_reverse_as_forward` (#37) y `TimelineSpeedTests.Speed_mapping_in_the_plan_reserves_nothing` (#38). Que la CI de Windows de main salga en verde varias veces seguidas. Averigua la causa de cada una. Si `Dispose` de verdad espera al lector (un `Wait` o un `Join` en el camino), es un defecto: corrígelo. Si no, haz que la medición no dependa del planificador del runner (cronometrar solo `Dispose`, hilos dedicados) sin subir el tope que mide. Si alguna prueba mide tiempo real, va con `Categoria=TiempoReal`.

## 48. Reenlazar: un archivo asignado a dos medios

- **Estado:** pendiente
- **Rama:** `claude/reenlazar-duplicados`
- **Caminos:** `src/Rodaje.Core/Editing/MediaRelinkSearch.cs`, `src/Rodaje.Core/Editing/MediaRelink.cs`, `src/Rodaje.App/Services/Relink/`, `src/Rodaje.App/Views/Relink/`, `tests/Rodaje.Core.Tests/Editing/Relink/` (archivos nuevos), `docs/tareas/`
- **Lee:** entrega de la tarea 28 (`docs/tareas/2026-10-12-qdv-reenlazar.md`), revisión en bloque 3.

1. `MediaRelinkSearch.cs:142` (`taken`) solo cuenta las rutas de los medios que no faltan. Lo ya resuelto en el diálogo (`pending`) no cuenta. Si faltan A y B, ambos 1080p de 10 s, y el usuario elige a mano `\nuevo\b.mp4` para B, la búsqueda que lanza `PickAsync` (`RelinkDialog.cs:225`) asigna ese mismo archivo a A por la pasada «Renamed». Arreglo: pasar las rutas ya resueltas como ocupadas y rechazar en `TargetFor` o en el servicio un archivo que ya use otro medio del proyecto o de `pending`, sin distinguir mayúsculas. Prueba de la búsqueda por tandas, como la hace el diálogo.
2. La pasada «Renamed» (`:253`) enlaza un único candidato con la misma extensión, pistas, tamaño y duración (±50 ms) sin comparar contenido: que en el diálogo salga como «confirmar».
3. `AttributesToSkip` (`:28`) no excluye `Offline` ni `RecallOnDataAccess`, así que abrir con FFmpeg puede descargar medios de OneDrive que solo están en la nube. Excluirlos en esa pasada.
4. `RelinkDialog.cs:262`: «Volver a enlazar» en el aviso no hace nada si ya no falta ningún medio. Avisar «Ya están todos los medios» y, si el motor guarda el medio como fallido, pedir un refresco.

## 49. Exportar fotograma: extensión, cancelación y reentrada

- **Estado:** pendiente
- **Rama:** `claude/fotograma-robusto`
- **Caminos:** `src/Rodaje.Export/Still/`, `src/Rodaje.App/Views/Export/StillExportCommand.cs`, `tests/Rodaje.Export.Tests/Still/` (archivos nuevos), `docs/tareas/`
- **Lee:** entrega de la tarea 29 (`docs/tareas/2026-10-12-qdv-fotograma.md`), revisión en bloque 3.

1. `StillSettings.FormatFor` saca PNG para cualquier extensión que no sea `.jpg` o `.jpeg`, y `StillEncoder` escribe en la ruta tal cual: `foto.bmp` termina siendo un PNG con nombre BMP. Aceptar solo `.png`, `.jpg` y `.jpeg`, y con otra extensión añadir la del formato o rechazarla con `InvalidSettings`. Prueba en `StillPlanTests`.
2. `StillExportCommand.cs:76` llama a `ExportAsync` sin `CancellationToken` y sin bloquear la reentrada. Si se repite Ctrl+Mayús+E con el mismo destino, dos exportaciones comparten la carpeta temporal. Añadir cancelación, un indicador de «exportando» y que el atajo se ignore mientras haya una en marcha.

## 50. Confirmar al borrar una plantilla propia

- **Estado:** pendiente
- **Rama:** `claude/plantillas-confirmar`
- **Caminos:** `src/Rodaje.App/Shell/TitleBar.xaml.cs` (solo el borrado de plantillas), `src/Rodaje.App/Services/` (la confirmación en `IEditorDialogs`, si hace falta), `tests/Rodaje.App.Tests/Templates/` (archivos nuevos), `docs/tareas/`
- **Lee:** entrega de la tarea 35 (`docs/tareas/2026-10-12-limpiezas-bloque-1.md`).

Lo que quedó pendiente de la tarea 35: borrar una plantilla propia (`TitleBar.xaml.cs:330`) pide confirmación con el estilo de la app (D-027). Si `IEditorDialogs` no tiene una confirmación genérica, añadirla con su doble de pruebas.

