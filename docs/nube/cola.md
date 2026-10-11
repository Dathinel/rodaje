# Cola de tareas para la nube

La mantiene la sesión local. Reglas en [README.md](README.md). Estados: `pendiente`, `cambios`, `en pausa`, `hecha`.

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

- **Estado:** cambios (PR #11: atender la revisión del 11-oct en sus comentarios)
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

- **Estado:** cambios (PR #12: atender la revisión del 11-oct en sus comentarios)
- **Rama:** `claude/probe-rebobinar`
- **Caminos:** `src/Rodaje.Media/Probe/MediaProbe.cs`, `tests/Rodaje.Media.Tests/` (archivos nuevos), `docs/tareas/`

Tras el análisis, `ScanTimestamps` rebobina con `av_seek_frame(long.MinValue, AVSEEK_FLAG_BACKWARD)`, y en MP4 eso no mueve la posición: quien lea paquetes después del análisis sin hacer un seek propio empieza a mitad del archivo (hoy lo esquivan `KeyframeIndex` reabriendo el archivo y `VideoDecoder` buscando siempre). Reprodúcelo con una prueba (MP4 y MKV, leer el primer paquete tras `ScanTimestamps` y comprobar que es el primero del archivo), corrígelo con un rebobinado que sí funcione en MP4 (`avformat_seek_file` al inicio del flujo, o reabrir) y comprueba que no cambia el resultado del análisis. Verificable en Linux con el FFmpeg 9.0.2 de BtbN (instrucciones en `docs/tareas/2026-10-11-ci-windows.md`, apartado «Pendiente») y con la CI de Windows.

## 11. Defectos del lector de paquetes hallados por el fuzz

- **Estado:** pendiente
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

- **Estado:** pendiente
- **Rama:** `claude/export-codificadores`
- **Caminos:** archivos NUEVOS en `src/Rodaje.Media/Export/` (carpeta nueva), `tests/Rodaje.Media.Tests/Export/`, `docs/tareas/`.
- **Lee:** `CLAUDE.md` (sección «Hacia la versión pública»: exportar por GPU o CPU sin separar el programa, licencia LGPL en la v1), `docs/legal.md`, `docs/ffmpeg-configure.txt`, `src/Rodaje.Media/Proxies/ProxyEncoders.cs` (solo lectura).

Solo el modelo y la detección, sin exportar todavía: un catálogo de codificadores de salida en orden de preferencia — hardware: NVENC (h264/hevc/av1), AMF (AMD), QSV (Intel), Media Foundation; software y LGPL: SVT-AV1, libaom, libvpx (VP9), kvazaar (HEVC), OpenH264 (H.264, calidad baja) — cada uno con su códec, si es hardware, licencia, y los argumentos base de calidad y velocidad; y una sonda que diga cuáles funcionan en esta máquina (compilado en el FFmpeg y que codifica 1 s de prueba sin error), en paralelo y cacheada. Elección por defecto: «GPU si hay, si no CPU», con la regla de que H.264 sin GPU cae a AV1 o HEVC por CPU (decisión pendiente de Daniel en `CLAUDE.md`). Pruebas con dobles para la elección y una prueba real que liste lo disponible (en la CI de Windows solo habrá software y Media Foundation). Nada de x264/x265 (GPL).

## 14. `VideoDecoder` con GOP abierto (HEVC con CRA)

- **Estado:** pendiente
- **Rama:** `claude/media-gop-abierto`
- **Caminos:** `src/Rodaje.Media/Video/`, `src/Rodaje.Media/Probe/`, `testing/Rodaje.TestMedia/` (preset nuevo), `tests/Rodaje.Media.Tests/`, `docs/arquitectura/medios.md`, `docs/tareas/`
- **Lee:** la revisión del PR #9, `docs/arquitectura/medios.md` (seek exacto y GOP abierto), `VideoDecoder.cs`, `KeyframeIndex.cs`.

Con un HEVC de GOP abierto (p. ej. el que genera libkvazaar del build LGPL: IDR en 0 y CRA en 64, 128, 192 con cuadros RASL) el decodificador falla: en modo `Keyframe` pedir el 64 entrega el 0 (software y hardware), una petición exacta devuelve null (`Exact_requests_never_skip_hardware(hevc)`) y el acceso aleatorio por hardware no entrega el 216. Hipótesis a comprobar: con `AVDISCARD_NONKEY` un CRA no vacía el DPB como un IDR y el cuadro queda retenido en el reordenamiento; en exacto, los RASL tras un seek a un CRA se descartan mal. Corrígelo y añade un preset de prueba de **GOP abierto** que se genere en cualquier máquina (libkvazaar u otro LGPL), para que también la RTX de Daniel cubra el caso: seek exacto, acceso aleatorio, modo `Keyframe` y `SkipNonReference`, en software (y en hardware donde haya GPU). Quita las omitidas del PR #9 que nombren este defecto.

## 15. Personalización: de qué pistas de video sale el audio

- **Estado:** pendiente
- **Rama:** `claude/audio-por-pista`
- **Caminos:** `src/Rodaje.App/Services/Settings/`, la página de ajustes, el punto donde la app importa o suelta un medio en la línea de tiempo (búscalo; no toques `src/Rodaje.Core`), `tests/Rodaje.App.Tests/` (archivos nuevos), `docs/tareas/`.
- **Lee:** `docs/pendientes.md` («Personalización»), la entrega de la tarea 7 (pistas por defecto) si ya existe, `docs/arquitectura/app.md`.

Segunda mitad de lo pedido por Daniel: en Ajustes, elegir de qué pistas de video sale el audio de un medio a su propia pista de audio al importarlo o soltarlo (todas, o una lista: por ejemplo Video 1 y 2 sí, Video 3 no). Si el video cae en una pista sin audio, el medio entra sin su clip de audio (o con el audio desactivado, decide y justifica). Guardado en `ajustes.json`. Pruebas de la regla con la sesión real y una nota en `docs/pendientes.md` marcando lo hecho.
