# Guardia de decodificador en las pruebas de Render

Rama `claude/render-guardia-decodificador` (tarea 2 de la cola), unida el 11-oct tras dos revisiones; los textos los corrigió la sesión local con las medidas del revisor.

## Hecho

- `tests/Rodaje.Render.Tests/Support/GpuArray.cs`: `GpuArrayFact` y `GpuArrayTheory` omiten si el adaptador no admite NV12 como salida de decodificador (`CheckFormatSupport` + `D3D11_FORMAT_SUPPORT_DECODER_OUTPUT`). La usan `CompositorTests.Draws_the_requested_slice_of_a_texture_array` y `CompositorPerformanceTests.Reports_gpu_milliseconds_per_frame_for_1_to_8_layers`.
- `EffectPerformanceTests` pasa a la categoría `Rendimiento`, que la CI excluye.
- `EffectFidelityTests.Sharpen_*`: tolerancia ±2 × (1 + cantidad) solo con WARP («Microsoft Basic Render Driver»); en GPU real sigue en ±2.

## Causa de Sharpen en WARP (medida por el revisor forzando WARP en el PC de Daniel)

| Caso | RTX 3050, desvío máx. | WARP, desvío máx. |
|---|---|---|
| Sin efectos | 0,59 | 1,47 |
| Desenfoque r = 3…30 | ≤0,87 | ≤1,43 |
| Sharpen a = 1, r = 1 | 0,76 | 3,03 |
| Sharpen a = 2,5, r = 3 | 1,09 | 3,79 |
| Sharpen a = 0,8, r = 6 | 0,76 | 3,05 |

WARP decodifica el origen con más error; el desenfoque lo promedia y el realce `original + a·(original − desenfoque)` lo multiplica por (1 + a). No es un defecto de borde: solo el 9 % de los píxeles fuera de tolerancia cae en la franja de 3 px (10,6 % de la imagen) y el shader recorta bien por los dos lados.

## Pruebas omitidas en Render (CI de Windows, WARP)

| Grupo | main antes | Con este PR | Causa |
|---|---|---|---|
| `Draws_the_requested_slice…` | falla (E_INVALIDARG) | omitida | sin NV12 `DECODER_OUTPUT` |
| `Reports_gpu_milliseconds…` (2 casos) | fallan | omitida (1 registro) | la misma |
| `DecoderCompositionTests` (2) | omitidas | omitidas | sin D3D11VA (ya existía) |
| `EffectPerformanceTests` (9 casos) | fallan | no corren | categoría `Rendimiento` |
| `Sharpen_*` (3) | fallan | pasan | tolerancia solo en WARP |
| **Total** | 206: 189 pasan, 2 omitidas, 15 fallan | 196: 192 pasan, 4 omitidas, 0 fallan | |

En la RTX 3050: 206/206, ninguna omitida, normal y con `RODAJE_PRUEBAS_LARGAS=1`.

## Pendiente

- El PR #2 excluye por nombre en `ci.yml` todas estas pruebas de Render (bloque `$render`); hay que quitar ese bloque antes de unirlo, para que la CI siga ejecutándolas.
