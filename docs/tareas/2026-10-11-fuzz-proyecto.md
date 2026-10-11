# Pruebas hostiles del lector de proyectos `.rodaje`

Rama: `claude/fuzz-proyecto`.

## Hecho

`tests/Rodaje.Core.Tests/Fuzz/ProjectReaderFuzzTests.cs` (tras la revisión de Daniel, 11-oct):
- Semillas v1, v2 y v3. La v2 usa `rodaje.volume` con `gain` lineal y keyframes, así que ejercita `MigrateEffectToV3`/`ToDecibels` (hay una prueba que lo comprueba). v1 y v2 se obtienen quitando del ejemplo v3 lo que esos formatos no tenían.
- 6000 mutaciones de bytes por `ProjectSerializer.Load` con archivo temporal (UTF-8 inválido, BOM, truncados), pocas destructivas; más archivos raros: vacío, BOM UTF-8/UTF-16, UTF-8 inválido a mitad.
- 6000 mutaciones estructurales: un tercio genéricas (tipos cambiados, racionales como objeto `{num, den}` con `den: 0`, negativos o `long.MaxValue`, listas desordenadas) y dos tercios dirigidas que conservan el tipo (`mediaId` y `from`/`to` hacia ids existentes o inexistentes, transiciones a sí mismas o no contiguas, ids repetidos entre entidades distintas, tiempos enteros alterados, tamaños de secuencia).
- Cadenas de 1 MB, anidamiento de 5000 niveles y entradas escritas a mano.
- Cada caso abre o lanza `ProjectFormatException` con mensaje en español. Si lo lanza, la causa debe ser del lector (JSON, tipos, UTF-8) o del validador con problemas concretos: **un fallo interno del validador envuelto como «no se pudieron comprobar» hace fallar la prueba**. Si abre, pasa `ModelValidator` y guardar → abrir → guardar es estable.
- Cada corrida exige al menos 5000 casos y que ≥ 25 % de las mutaciones estructurales lleguen al validador (medido: unas 74 %: 1628 abren, 2819 rechazadas por el validador, 1530 por tipos, 12 por JSON mal formado, 11 por el defecto de abajo).
- Unos 8 s en Linux (límite 30 s).

## Defecto encontrado (no corregido, fuera de los caminos de la tarea)

Un clip que es extremo de una transición con `sourceIn`/`sourceOut` extremos (p. ej. `clip-2` con `sourceOut: -9223372036854775808`) hace que `ModelValidator.ValidateTransitions` → `TransitionOps.MaxDuration` → `Clip.Duration` → `Time.op_Subtraction` lance `OverflowException`. El lector la envuelve en «El proyecto tiene datos que no se pudieron comprobar…», sin decir qué falla. No tumba la app, pero el mensaje no sirve. Reproducción: `Validator_DoesNotOverflowOnExtremeClipTimes`, marcada con `Skip = "Defecto: …"`. Mientras tanto el fuzz cuenta esos casos aparte (`desbordamiento conocido`) y no los deja pasar en silencio. Cuando se corrija en `src/`, se quita el `Skip` y el caso especial de `Run`.

## Pendiente / decisiones

- Las semillas v1 y v2 son derivadas del v3, no archivos de verdad de esas versiones; si Daniel conserva proyectos antiguos reales, conviene añadirlos como ejemplos.
- La comparación es sobre el texto guardado (`Serialize`), que es el formato canónico del lector.

## Choques

Ninguno: solo archivos nuevos en `tests/Rodaje.Core.Tests/Fuzz/` y esta entrega.

## En Windows

Nada específico; `dotnet test tests/Rodaje.Core.Tests --filter FullyQualifiedName~Fuzz`.
