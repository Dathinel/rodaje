# Tarea 41: Overwrite cambia el volumen con keyframes del efecto de pista

Rama `claude/overwrite-gain-keyframes`.

## Causa

El fallo se reproduce en Linux sobre main 7d430d4 con `CsCheck_Seed=5ZOfD5KG8C16`. Es un falso positivo de la propiedad 8 (`CheckTrackCurves` en `EditPropertiesTests`), no un defecto de `src/Rodaje.Core/Editing/`:

- Antes del `Overwrite(m4, …, a2, v1, 0)` final, `clip-7` es un **cuadro congelado**. Su origen dura 1 tick (`in=4327680000 out=4327680001`) y en la secuencia ocupa de 799680000 a 3085824000.
- El overwrite tapa de 0 a 846720000 en `v1`. `TrackOps.ClearRange` le recorta el principio, como debe: ahora empieza en 846720000. Su origen no cambia, porque un congelado recortado sigue mostrando el mismo cuadro.
- Los keyframes de `gainDb` del efecto de pista quedan **idénticos**: sobrescribir no corre nada.
- La propiedad salta los clips cuyo origen cambió, porque así detecta un recorte. Con un congelado eso no alcanza: comparaba la curva en `inicio viejo + 0` (799680000, 15,53) con la de `inicio nuevo + 0` (846720000, 15,32). Son dos instantes distintos de la secuencia, y la curva no cambió.

## Arreglo

- `CheckTrackCurves` también salta el clip que **cambia de duración**, es decir, el recortado. La comparación sigue para los clips que el ripple corre enteros. Es un cambio de dos líneas en `tests/Rodaje.Core.Tests/Properties/EditPropertiesTests.cs`.
- `RunScenario` pasa de `private` a `internal` para que la prueba de regresión lo use con la semilla.
- El núcleo no cambia.

## Pruebas

- Archivo nuevo `tests/Rodaje.Core.Tests/Properties/OverwriteFrozenClipTests.cs`:
  - `LaSemillaDeLaTarea41_CumpleTodasLasReglas`: corre el escenario de la semilla `5ZOfD5KG8C16`, una iteración, con todas las propiedades. Sin el arreglo falla con el mismo mensaje de la CI; con el arreglo pasa.
  - `SobrescribirElPrincipioDeUnCongelado_LoRecortaSinTocarLaCurvaDeLaPista`: un caso armado a mano.
    - Congela 3 s de `clip-2` y sobrescribe 1 s que tapa medio segundo de su principio.
    - Comprueba que el congelado empieza en 12,5 s, dura 2,5 s y conserva su origen, que los keyframes de la pista no cambian y que deshacer vuelve al volcado anterior.
- `tests/Rodaje.Core.Tests` con `Categoria!=SoloWindows&Categoria!=TiempoReal&Categoria!=Rendimiento`: 1372 correctas, ninguna falla y ninguna omitida.

## Pruebas omitidas

Ninguna nueva. En Core se omitían 0 antes y se omiten 0 después.

## Decisiones y desvío de los caminos

- Los caminos de la tarea dicen `tests/Rodaje.Core.Tests/` con archivos nuevos, pero la causa estaba en la regla de una prueba existente. Se tocaron solo esas dos líneas y la visibilidad de `RunScenario`, en `EditPropertiesTests.cs`. Cambiar el núcleo para callar la propiedad habría sido un error: recortar un congelado no debe mover su origen ni la curva de la pista.
- La tarea 40 lo describía como un defecto de `Overwrite`. No lo es.

## Pendiente

Nada de esta tarea.

## Posibles choques

- Ninguno conocido. `EditPropertiesTests.cs` solo cambia en `CheckTrackCurves` y en la firma de `RunScenario`.

## Qué comprobar en Windows

- Nada a mano. Solo que el trabajo «Windows» de la CI pase Core.
