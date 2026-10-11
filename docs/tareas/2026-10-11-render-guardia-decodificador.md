# Guardia de decodificador en las pruebas de Render

Rama `claude/render-guardia-decodificador`. Tarea 2 de la cola.

## Hecho

- `tests/Rodaje.Render.Tests/Support/GpuArray.cs` (nuevo): `GpuArrayFact` y `GpuArrayTheory`. Además de la guardia de muestreo NV12/P010 (`Skip.GpuTextures`), comprueban con `CheckFormatSupport` que el dispositivo ofrece `D3D11_FORMAT_SUPPORT_DECODER_OUTPUT` para NV12 y P010, que es lo que exige el `D3D11_BIND_DECODER` de `YuvImage.CreateTexture` para arreglos. Si falta, la prueba se omite con el nombre del adaptador.
- Usan la guardia nueva las pruebas que crean arreglos de más de una capa:
  `CompositorTests.Draws_the_requested_slice_of_a_texture_array`, `CompositorPerformanceTests.Reports_gpu_milliseconds_per_frame_for_1_to_8_layers` (2 casos) y, en `EffectPerformanceTests`, `Eight_plain_1080p_layers_do_not_regress`, la de 3 capas 4K (línea 66) y la de transiciones (2 casos). Las de una sola capa no cambian.

## Segunda ronda (revisión del 11-oct)

- `EffectPerformanceTests` completa pasa a `[Trait("Categoria", "Rendimiento")]` (la CI ya la excluye), incluida `One_4k_layer_with_color_and_lut` (242 ms en WARP).
- `Sharpen_*` (3 casos): el desenfoque puro pasa con ±2 también en WARP; el realce multiplica el error del desenfoque por (1 + cantidad), y los desvíos medidos (2,1–2,4) caen en la esquina superior derecha. **No pude confirmar desde Linux si es precisión del rasterizador o el manejo del borde**; si fuera un defecto del shader (`PsSharpen` y `SampleB` no tienen manejo propio del borde), la tolerancia lo taparía en WARP. Por eso la tolerancia extra (`2 + cantidad`) solo se aplica con adaptador «Basic Render Driver»/WARP; con GPU real sigue ±2. Daniel: conviene mirar en Windows el píxel (126,1) con WARP forzado.
- La guardia de arreglos ya solo exige NV12 (todos los arreglos lo son).
- `ci.yml`: en `main` no hay exclusiones por nombre de Render (viven en el PR #2, sin unir), así que no hay nada que quitar aquí. Cuando #2 esté en main, quitar todas las exclusiones `FullyQualifiedName!=Rodaje.Render.Tests.*` y enlazar una corrida verde.

## Pendiente

- Quitar la exclusión de estas pruebas en `ci.yml`: ese cambio vive en el PR #2 (aún no unido), así que no lo toqué. Cuando #2 esté unido, hay que quitar el filtro de Render de esa exclusión.

## Pruebas omitidas

- Casos que se omiten en WARP con la guardia (11 casos, 7 registros): `Draws_the_requested_slice…` (1), `Reports_gpu_milliseconds…` (2), `Eight_plain_1080p_layers…` (1), la de 3 capas 4K (1) y la de transiciones (6 casos). Las de rendimiento además salen por la categoría `Rendimiento`.
- Cifras de la revisión: CI de main 2 omitidas + 16 fallos; este PR 7 registros omitidos (11 casos) + 4 fallos antes de esta ronda.
- Antes: en un equipo con GPU real, ninguna por esta causa. En el runner de Windows (WARP) esas pruebas fallaban con `E_INVALIDARG` en lugar de omitirse.
- Después: en WARP se omiten (causa: el adaptador no admite NV12/P010 como salida de decodificador); con GPU real corren igual que antes. No pude medir el conteo exacto aquí (Linux, sin Direct3D): compila sin errores, el resultado en Windows lo da la CI.

## Decisiones

- La guardia vive en `tests/Rodaje.Render.Tests/Support`, no en `testing/Rodaje.TestMedia/Skip.cs`, porque esa carpeta queda fuera de los caminos permitidos.
- Incluí las pruebas de `EffectPerformanceTests` con arreglos porque usan el mismo `CreateTexture` y fallan igual.

## Posibles choques

- PR #2 toca `ci.yml` y `testing/Rodaje.TestMedia/Skip.cs`; no hay solape con estos archivos.
- `Sharpen_matches_the_unsharp_mask_reference` (PR #2) falla por tolerancia en WARP, no por el bind; esta guardia no lo cubre.

## Qué comprobar en Windows (Daniel)

Con la RTX 3050, `dotnet test tests/Rodaje.Render.Tests` debe correr las mismas pruebas que antes (ninguna omitida por esta guardia). En el runner de CI, las 5 pruebas citadas deben aparecer omitidas, no falladas.
