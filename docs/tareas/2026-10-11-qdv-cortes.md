# Entrega: cortar en todas las pistas y borrar a un lado del cursor (tarea 17)

Rama `claude/qdv-cortes`, PR #21. Función 2 de «Funciones de calidad de vida» en `docs/pendientes.md`.

## Hecho

- **Núcleo** (`src/Rodaje.Core/Editing/TimelineEdits.Cuts.cs`):
  - `SplitAllTracks(ctx, at)`: corta en `at` todos los clips de las pistas desbloqueadas que pasan por ese instante (sin contar sus bordes), en una sola edición. Devuelve las piezas derechas en el orden de las pistas (video y luego audio). Si no hay nada que cortar lanza `EditException`.
  - `CanSplitAllTracks(sequence, at)`: si hay algo que cortar (la sesión lo mira antes para no avisar en vano).
  - `DeleteToPoint(ctx, clipIds, at, side, ripple, scope)`: borra de los clips pedidos y sus enlazados lo que hay antes (`TrimEdge.In`, Q) o después (`TrimEdge.Out`, W) de `at`. Solo toca los clips que pasan por `at`; cada uno conserva su identificador, sus efectos y keyframes. Con ripple, el resultado es el de cortar en `at` y hacer `RippleDelete` de la pieza borrada:
    - `RippleScope.Track`: cada pista cierra su propio tramo, y las pistas de los enlazados de lo que se corre cierran el mismo (igual que `RippleDelete`). Un enlazado de otro largo cierra solo su tramo.
    - «Todas las pistas»: se quita de toda la secuencia la unión de los tramos (y se corren los marcadores con `AllTracksAndMarkers`); falla si otro clip atraviesa el tramo, como `RippleDelete`.
    - Con Q y ripple, el clip queda empezando donde empezaba.
- **Comandos** (`src/Rodaje.Core/Commands/Edits.Cuts.cs`): `Edits.SplitAllTracks` («Cortar en todas las pistas») y `Edits.DeleteToPoint` («Borrar a la izquierda/derecha del cursor»). Cada uno es un paso del historial.
- **Sesión** (`src/Rodaje.App/Services/EditorSession.Cuts.cs`, parcial nuevo): `SplitAllTracksAtPlayhead()`, `DeleteLeftOfPlayhead()` y `DeleteRightOfPlayhead()`. Q/W actúan sobre los mismos clips que antes: los seleccionados que pasan por el cursor o, sin selección, el de la pista principal. Un grupo enlazado cierra el hueco si alguno de sus clips está en una pista que no deja huecos (`ClosesGaps`: la principal magnética, o cualquiera con ripple automático), con el `RippleScope` de la sesión. Si hay grupos de los dos tipos, va un `CompositeCommand`: primero los que dejan hueco y luego los que corren. Con Q y ripple el cursor va al inicio del clip, como hacía `TrimStartToPlayhead`.
- **Atajos** (`src/Rodaje.App/Services/Shortcuts/`):
  - Acción nueva `edit.splitAll`, «Cortar en todas las pistas», con Ctrl+Mayús+B en Rodaje y Ágil y Ctrl+Mayús+K en Clásico. Está en la hoja rápida (`CheatSheet.Priority`, después de Q/W).
  - `edit.trimStart` y `edit.trimEnd` se llaman ahora «Borrar a la izquierda/derecha del cursor». Conservan el identificador (los `atajos.json` guardados siguen valiendo) y las teclas: Q/W, y Alt+[ / Alt+] en Clásico. Ahora llaman a los métodos nuevos.
- **Pruebas** (`tests/Rodaje.Core.Tests/Editing/Trimming/CutsAtPointTests.cs`, 17 nuevas): corte en varias pistas, enlace derivado común, pistas bloqueadas, bordes y huecos, deshacer y rehacer con los mismos ids; Q/W con y sin ripple, por pista y en todas las pistas (con marcadores), enlazados de otro largo, unión de tramos, clip que atraviesa, pareja bloqueada.

## Decisiones

- **Pista bloqueada:** al cortar en todas las pistas, un clip enlazado con otro de una pista bloqueada se corta igual. La pareja bloqueada queda entera y enlazada con la pieza izquierda, y las piezas derechas comparten un enlace derivado nuevo. En cambio, `DeleteToPoint` falla («bloqueada») si un enlazado que pasa por el cursor está en una pista bloqueada, igual que `Trim` y `Split`.
- **Atajo de cortar en todas:** no lleva Mayús+S, porque cambiaría la tecla S del teclado dibujado y la prueba `CadaTecla_ListaSusAccionesPrimeroSola` espera exactamente Cortar, Guardar y Guardar como. Ctrl+Mayús+K en Clásico sigue la costumbre de los editores «clásicos».
- **Q/W como edición única:** antes eran un `RippleTrim` por clip dentro de un gesto. Con «todas las pistas» y varios clips, el primer ripple podía correr a los demás. Ahora es una sola edición del núcleo.
- `TrimStartToPlayhead`/`TrimEndToPlayhead` siguen en `EditorSession.Editing.cs`, sin uso desde los atajos. No los toqué porque la tarea pide un parcial nuevo y sus pruebas en `tests/Rodaje.App.Tests` siguen llamándolos. Se pueden redirigir a los nuevos o borrar en una tarea aparte.

## Pendiente

- **Pruebas de la sesión y de los atajos en `tests/Rodaje.App.Tests`:** esa carpeta no está entre los caminos de la tarea, así que no hay pruebas nuevas de la sesión ni de los atajos. Las que ya existen cubren el catálogo (combinaciones reservadas, hoja rápida de 15, búsqueda, teclado dibujado) y deberían seguir en verde. Propuesta para otra tarea: probar `SplitAllTracksAtPlayhead` con un clip en V2, `DeleteLeftOfPlayhead` con los mismos casos que `RecortarInicio_*`, y que Ctrl+Mayús+B llega a la sesión.
- **Botón o menú:** no se añadió ninguno para «Cortar en todas las pistas» en la barra de la línea de tiempo (`Views/` no está entre los caminos).

## Choques posibles

- La tarea 18 (`claude/qdv-enlaces-atributos`) también añade a `Edits`/`TimelineEdits` parciales y quizá acciones a `EditorShortcuts.cs`. Si las dos tocan el catálogo puede haber un choque de texto en `EditorShortcuts.cs`, fácil de resolver.
- Nada en `origin/main` tocaba estos archivos al crear la rama (88de39c).

## Qué comprobar en Windows

1. La CI de Windows compila `Rodaje.App` y pasan `Rodaje.App.Tests`. En Linux también compilan, con `-p:EnableWindowsTargeting=true`; ver «Pruebas en Linux».
2. Con `Rodaje.bat`: dos videos en la principal y un clip en V2 que pase por el cursor. Ctrl+Mayús+B debe cortar los tres (video, su audio y V2) y Ctrl+Z deshacerlo en un paso.
3. Q y W en la principal magnética: el clip se recorta sin hueco y, con Q, el cursor va a su inicio. En V2 queda el hueco. Con «todas las pistas» en el modo de ripple, V2 se corre también.
4. La página de atajos muestra «Cortar en todas las pistas» en los tres estilos y en la hoja rápida, y Q/W con su nombre nuevo.

## Pruebas en Linux

- `dotnet test tests/Rodaje.Core.Tests`: 1232 pruebas, 1231 pasan, 0 omitidas y 1 falla; en una segunda corrida fallaron 2. Ninguno de los dos fallos viene de esta rama:
  - `ProjectFileTests.Save_TempLockedByAnotherProgram_ThrowsAndKeepsExistingFile` también falla en `origin/main` en Linux, porque el bloqueo de archivos es de Windows.
  - `EditPropertiesTests.ElSorteo_EjercitaLasVelocidades` falló una vez en la suite completa («FreezeFrame solo se aplicó 96 veces», el umbral es 100). Pasa sola en esta rama y en main: el sorteo varía.
- No se omitió ninguna prueba: 0 omitidas antes y después.
- `dotnet test tests/Rodaje.App.Tests -p:EnableWindowsTargeting=true`: 650 pruebas, 645 pasan, 1 omitida (ya lo estaba en main) y 4 fallan. Las 194 de atajos pasan todas, y también las de sesión (`EditingAtPlayheadTests`). Los 4 fallos son de rutas de Windows y no tocan nada de esta rama: `ThumbnailCacheTests.Disk_names_carry_fingerprint_height_and_time`, `ProxyJobListTests.CicloCompleto_EnColaGenerandoListo` y dos de `ExportPreferencesTests`.

## Corrección tras la CI de Windows

- La primera corrida (commit 4c195ae) falló en `ShortcutSearchTests.CadaAccionDelCatalogo_TieneUnaExplicacionCortaEnEspañol`: las explicaciones de Q/W pasaban del límite de 80 caracteres. Ahora miden 74 y 76; la de `edit.splitAll`, 72. Las etiquetas cortas miden 16 como mucho.
- Fallos en la CI de Windows que vienen de main y no son de esta rama: `TitleEditorTests.BordesYConvertirEnKeyframes` (cultura, 0,5 frente a 0.5), `TitleRasterizerTests.Composing_a_ready_title_costs_little_gpu_time` (WARP) y `TimelineSpeedTests.Speed_mapping_in_the_plan_reserves_nothing` (760 B).
