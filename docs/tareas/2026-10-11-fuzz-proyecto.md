# Pruebas hostiles del lector de proyectos `.rodaje`

Rama: `claude/fuzz-proyecto`.

## Hecho

`tests/Rodaje.Core.Tests/Fuzz/ProjectReaderFuzzTests.cs` (tras la revisión de Daniel, 11-oct):
- Semillas v1, v2, v3 y v4. La v4 es `tests/Rodaje.Core.Tests/Fuzz/proyecto-v4.rodaje`, copia del ejemplo de `origin/main`; esta rama aún lee el formato 3, así que la semilla v4 solo se usa cuando `Project.CurrentSchemaVersion >= 4` (probado en un worktree de `main` con las pruebas añadidas: 26 pasan, 1 omitida). La v2 usa `rodaje.volume` con `gain` lineal y keyframes, así que ejercita `MigrateEffectToV3`/`ToDecibels` (hay una prueba que lo comprueba). v1 y v2 se obtienen quitando del ejemplo v3 lo que esos formatos no tenían.
- 6000 mutaciones de bytes (UTF-8 inválido, truncados), pocas destructivas: 1 de cada 20 por `ProjectSerializer.Load` con archivo temporal y el resto con el mismo decodificador estricto en memoria (con 6000 archivos tardaba 47–59 s en Windows); más archivos raros: vacío, BOM UTF-8/UTF-16, UTF-8 inválido a mitad.
- 6000 mutaciones estructurales: un tercio genéricas (tipos cambiados, racionales como objeto `{num, den}` con `den: 0`, negativos o `long.MaxValue`, listas desordenadas) y dos tercios dirigidas que conservan el tipo (`mediaId` y `from`/`to` hacia ids existentes o inexistentes, transiciones a sí mismas o no contiguas, ids repetidos entre entidades distintas, tiempos enteros alterados, tamaños de secuencia).
- Cadenas de 1 MB, anidamiento de 5000 niveles y entradas escritas a mano.
- Cada caso abre o lanza `ProjectFormatException` con mensaje en español. Si lo lanza, la causa debe ser del lector (JSON, tipos, UTF-8) o del validador con problemas concretos: **un fallo interno del validador envuelto como «no se pudieron comprobar» hace fallar la prueba**. Si abre, pasa `ModelValidator` y guardar → abrir → guardar es estable.
- Cada corrida exige al menos 5000 casos y que ≥ 25 % de las mutaciones estructurales lleguen al validador (medido: unas 74 %: 1628 abren, 2819 rechazadas por el validador, 1530 por tipos, 12 por JSON mal formado, 11 por el defecto de abajo).
- El límite de 30 s se comprueba con un `Stopwatch` por prueba; en Linux tarda unos 8 s las cuatro pruebas. Con menos archivos temporales debería bajar de 10 s en Windows, pero no lo he medido allí.
- El desbordamiento conocido solo se tolera si la pila pasa por `TransitionOps.MaxDuration` y como mucho en el 1 % de los casos (hoy 11 de 6000).
- Las mutaciones de tiempos incluyen keyframes y los ids repetidos son siempre de entidades distintas; la versión «futura» es 5 y el máximo de `int`.

## Defecto encontrado (no corregido, fuera de los caminos de la tarea)

Un clip que es extremo de una transición con `sourceIn`/`sourceOut` extremos (p. ej. `clip-2` con `sourceOut: -9223372036854775808`) hace que `ModelValidator.ValidateTransitions` → `TransitionOps.MaxDuration` → `Clip.Duration` → `Time.op_Subtraction` lance `OverflowException`. El lector la envuelve en «El proyecto tiene datos que no se pudieron comprobar…», sin decir qué falla. No tumba la app, pero el mensaje no sirve. Reproducción: `Validator_DoesNotOverflowOnExtremeClipTimes`, marcada con `Skip = "Defecto: …"`. Mientras tanto el fuzz cuenta esos casos aparte (`desbordamiento conocido`) y no los deja pasar en silencio. Cuando se corrija en `src/`, se quita el `Skip` y el caso especial de `Run`.

## Pendiente / decisiones

- Las semillas v1 y v2 son derivadas del v3, no archivos de verdad de esas versiones; si Daniel conserva proyectos antiguos reales, conviene añadirlos como ejemplos.
- La comparación es sobre el texto guardado (`Serialize`), que es el formato canónico del lector.

## Choques

Ninguno: solo archivos nuevos en `tests/Rodaje.Core.Tests/Fuzz/` y esta entrega.

## En Windows

Nada específico; `dotnet test tests/Rodaje.Core.Tests --filter FullyQualifiedName~Fuzz`.
