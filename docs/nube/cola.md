# Cola de tareas para la nube

La mantiene la sesión local. Reglas en [README.md](README.md). Estados: `pendiente`, `en pausa`, `hecha`.

## 1. CI de Windows en verde (salvo Render)

- **Estado:** pendiente
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
