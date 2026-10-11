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

- **Estado:** en pausa (el agente local de render acaba de terminar; se libera al cerrar la fase 5)
- **Rama:** `claude/render-guardia-decodificador`
- **Caminos:** `tests/Rodaje.Render.Tests/`, `.github/workflows/ci.yml`, `docs/tareas/`

La guardia de GPU de `CompositorTests.Draws_the_requested_slice_of_a_texture_array` y de `CompositorPerformanceTests` mira el muestreo NV12/P010 pero no el bind de decodificador (`YuvImage.CreateTexture`, `tests/Rodaje.Render.Tests/Support/YuvImage.cs`). Que comprueben `D3D11_BIND_DECODER` con `CheckFormatSupport` y se omitan si falta. Luego quita la exclusión de esas pruebas en `ci.yml`.

## 3. Pruebas hostiles del lector de proyectos `.rodaje`

- **Estado:** cambios (PR #3: atender la revisión del 11-oct en sus comentarios)
- **Rama:** `claude/fuzz-proyecto`
- **Caminos:** `tests/Rodaje.Core.Tests/Fuzz/` (carpeta nueva), `docs/tareas/`. Si encuentras un fallo real en `src/Rodaje.Core/Serialization/`, NO lo corrijas: deja la prueba que lo reproduce marcada con `Skip = "Defecto: …"` y descríbelo en la entrega.
- **Lee:** `docs/formato-proyecto.md`, `src/Rodaje.Core/Serialization/` (solo lectura), `tests/Rodaje.Core.Tests/Serialization/ejemplos/proyecto-v3.rodaje`.

Un `.rodaje` dañado o malicioso nunca debe tumbar la app: abrirlo da un proyecto válido o un error controlado en español (la excepción propia del lector), nunca otra excepción ni un cuelgue.
1. Pruebas con mutaciones deterministas (semilla fija) sobre los ejemplos v1, v2 y v3: bytes cambiados, truncados, JSON con tipos cambiados, números enormes, negativos, NaN, `schemaVersion` futuro o ausente, ids duplicados, referencias a clips o medios inexistentes, transiciones entre clips no contiguos, keyframes desordenados, cadenas de 1 MB, anidamiento profundo.
2. Al menos 5000 casos por corrida, en menos de 30 s en Linux.
3. Comprueba además que todo proyecto que abre sin error pasa el validador y se vuelve a guardar y abrir igual byte a byte.

## 4. Estado del decodificador tras un flush (Rodaje.Media)

- **Estado:** pendiente
- **Rama:** `claude/media-flush`
- **Caminos:** `src/Rodaje.Media/`, `tests/Rodaje.Media.Tests/`, `docs/tareas/`
- **Lee:** `docs/arquitectura/medios.md`, la revisión del PR #2, `tests/Rodaje.Media.Tests/VideoDecoderBehaviorTests.cs`.

`Corrupted_packets_are_skipped_without_throwing` solo corre con `h264_nvenc` porque, con el H.264 de `h264_mf`, el estado del decodificador sobrevive a `avcodec_flush_buffers` y un cuadro tras la zona dañada no coincide. Encuentra la causa en `Rodaje.Media` (seek, flush, cuadros de referencia pendientes), corrígela y haz que la prueba corra con cualquier codificador H.264 disponible. Se verifica con la CI de Windows (sin NVENC) y la revisión local con NVENC.
