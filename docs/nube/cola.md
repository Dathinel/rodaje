# Cola de tareas para la nube

La mantiene la sesión local. Reglas en [README.md](README.md). Estados: `pendiente`, `cambios`, `en pausa`, `hecha`.

## 1. CI de Windows en verde (salvo Render)

- **Estado:** cambios (PR #2: atender la revisión del 11-oct en sus comentarios)
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

- **Estado:** cambios (PR #5: atender la revisión del 11-oct en sus comentarios)
- **Rama:** `claude/media-flush`
- **Caminos:** `src/Rodaje.Media/`, `tests/Rodaje.Media.Tests/`, `docs/tareas/`
- **Lee:** `docs/arquitectura/medios.md`, la revisión del PR #2, `tests/Rodaje.Media.Tests/VideoDecoderBehaviorTests.cs`.

`Corrupted_packets_are_skipped_without_throwing` solo corre con `h264_nvenc` porque, con el H.264 de `h264_mf`, el estado del decodificador sobrevive a `avcodec_flush_buffers` y un cuadro tras la zona dañada no coincide. Encuentra la causa en `Rodaje.Media` (seek, flush, cuadros de referencia pendientes), corrígela y haz que la prueba corra con cualquier codificador H.264 disponible. Se verifica con la CI de Windows (sin NVENC) y la revisión local con NVENC.

## 5. Pruebas hostiles del instalador de paquetes

- **Estado:** pendiente
- **Rama:** `claude/fuzz-paquete`
- **Caminos:** `tests/Rodaje.Paquetes.Tests/Fuzz/` (carpeta nueva), `docs/tareas/`. Si encuentras un fallo real en `src/Rodaje.Paquetes/`, NO lo corrijas: prueba que lo reproduce con `Skip = "Defecto: …"` y descripción en la entrega.
- **Lee:** `docs/formato-paquete.md`, `src/Rodaje.Paquetes/` (solo lectura), la revisión del PR #3 (lo que se le pidió a ese fuzz vale aquí).

Un `.paquete` dañado o malicioso nunca debe tumbar ni escribir fuera de su carpeta. Mutaciones deterministas sobre paquetes válidos generados en la prueba: zip truncado o con directorio central falso, rutas con `..`, absolutas, UNC, nombres reservados de Windows y caracteres de control, entradas duplicadas, tamaños declarados falsos, bomba de compresión, manifiesto con tipos cambiados, textos de 1 MB y marcas bidi, archivos de contenido (`.cube`, `rodaje.filter`, `rodaje.effect`, `rodaje.preset`) dañados. Cada caso instala bien o da `PackageInstallException`/`PackageContentException` con mensaje, y la prueba **falla si la excepción interna no es de E/S, zip o JSON** (no aceptes envoltorios genéricos). Comprueba que tras cada fallo no queda nada en la carpeta de destino. Registra la fracción de casos que llegan a cada etapa (zip, manifiesto, contenido, instalación) y exige que al menos un 25 % pase del zip. ≥5000 casos, <30 s en Linux.

## 6. Documentación al día con el código

- **Estado:** pendiente
- **Rama:** `claude/docs-al-dia`
- **Caminos:** `docs/arquitectura/`, `docs/variables-entorno.md`, `docs/formato-paquete.md`, `docs/tareas/`. No toques `README.md`, `CLAUDE.md`, `docs/decisiones.md` ni `docs/fase-*.md`.

Recorre `docs/arquitectura/*.md` y compáralos con el código actual de `src/`: tipos, archivos o flujos que ya no existen o cambiaron de nombre, piezas nuevas de la fase 5 sin documentar, enlaces rotos entre documentos. Corrige solo lo que sea falso u omitido, con el estilo de los documentos (corto, de producto, en español). La entrega lista cada corrección con el archivo de código que la respalda.

## 7. Pistas por defecto en un proyecto nuevo

- **Estado:** pendiente
- **Rama:** `claude/pistas-por-defecto`
- **Caminos:** `src/Rodaje.App/Services/Settings/`, `src/Rodaje.App/Views/Settings/` (o donde viva la página de ajustes), el punto donde la app crea un proyecto nuevo (búscalo; no toques `src/Rodaje.Core`), `tests/Rodaje.App.Tests/` (archivos nuevos), `docs/tareas/`.
- **Lee:** `docs/pendientes.md` (sección Personalización), `docs/arquitectura/app.md`.

Primera mitad de lo pedido por Daniel: en Ajustes, cuántas pistas de video (1–8) y de audio (1–8) tiene un proyecto nuevo, guardado en `ajustes.json` con los valores de hoy por defecto. Al crear un proyecto se usan esos valores con las ediciones que ya existen. La segunda mitad (de qué pistas de video sale el audio) NO: déjala anotada. Se verifica con la CI de Windows; la entrega dice qué debe mirar Daniel.

## 8. Propuesta del formato de estilos de texto (fase 5b)

- **Estado:** pendiente
- **Rama:** `claude/propuesta-texto`
- **Caminos:** `docs/propuestas/texto.md` (nuevo), `docs/tareas/`.
- **Lee:** `docs/fase-5.md` (por qué el texto va en la 5b), `docs/fase-5c.md` (la 5c cambia `Clip` y el formato 4 antes), `docs/formato-paquete.md`, `docs/formato-proyecto.md`, `src/Rodaje.Core/Model/`, `src/Rodaje.Render/` (solo lectura).

Solo documento, sin código: propuesta para la fase 5b con (1) clips generadores sin medio en el modelo (cómo encajan con el formato 4 de la 5c: ¿formato 5?), (2) contenido de `text-style` en `.paquete` v1 (fuente, tamaño, colores, borde, sombra, fondo, animaciones de entrada y salida sobre las primitivas), (3) render con DirectWrite/Direct2D sobre el dispositivo D3D11 existente y su costo por cuadro, (4) fuentes: cuáles se pueden empaquetar (licencias OFL/Apache) y cómo se usan las del sistema, (5) edición sobre el lienzo, (6) cómo se apoyan los subtítulos de la fase 7. Con ventajas y desventajas reales y máximo 3 preguntas para Daniel.

## 9. `PlaybackSchedulerTests` inestable en la CI de Windows

- **Estado:** pendiente
- **Rama:** `claude/playback-seek-tiempo`
- **Caminos:** `tests/Rodaje.Playback.Tests/`, `docs/tareas/`

`PlaybackSchedulerTests.SeekLejosReproduciendo_CuestaLoMismoQueUnSaltoCorto(jump: 10000000000)` falla a veces en el runner de 2 núcleos («tardó 00:00:03.22», límite 2 s en `PlaybackSchedulerTests.cs:358`); en el PC de Daniel tarda <1 ms. Mide con un reloj inyectado o cuenta operaciones en lugar de reloj de pared, de modo que siga detectando un seek que crece con la distancia; si no se puede, categoría `TiempoReal`. Que nunca dependa de la carga del pool.
