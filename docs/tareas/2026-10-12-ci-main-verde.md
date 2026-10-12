# Tarea 40: main en rojo en la CI de Windows

Rama `claude/ci-main-verde`, PR #31. Desde f89d896 (run 38194016251) el trabajo «Windows (solución completa)» fallaba con 13 pruebas. En el run 38203723264 (PR #29, solo cola) fallaron además dos de `OfflineTimelineTests` en Engine. Abajo va la causa de cada una y lo que se cambió.

## Causa y arreglo de cada prueba

| Prueba | Causa | Arreglo |
|---|---|---|
| `TitleEditorTests.BordesYConvertirEnKeyframes` («0.5» frente a «0,5») | **Defecto del código.** `TitleEditor.DescribeAnimation` formateaba con `CultureInfo.CurrentCulture`, pero la app corre con `InvariantGlobalization` y la pestaña Texto mostraba «Entrada de 0.5 s». Falla siempre, no depende de la carga. | Coma decimal fija, la misma que usa `SpeedMath` (`src/Rodaje.App/Services/Inspector/TitleEditor.cs`). |
| `TitleRasterizerTests.Composing_a_ready_title_costs_little_gpu_time` (2,8 y 3,8 ms) | El runner no tiene GPU: Direct3D 11 «por hardware» da el Microsoft Basic Render Driver (WARP), que rasteriza en la CPU que comparten todas las pruebas. Ese tiempo no mide el costo en una GPU. | Se omite **solo con un adaptador WARP**, con el atributo nuevo `MeasuredGpuFact` (`tests/Rodaje.Render.Tests/Text/MeasuredGpuFactAttribute.cs`). Combina `Skip.GpuTextures` con el motivo «WARP, sin GPU». Los umbrales no cambian (2 ms, y 0,15 ms con `RODAJE_PRUEBAS_LARGAS`). |
| `ExportEncoderAvailabilityTests.Probes_run_in_parallel_up_to_the_limit` (2 frente a 3) | Cada sonda falsa bloquea un hilo del pool hasta que llegan las tres. Con el pool ocupado por las demás pruebas, el tercer hilo tardaba más de los 10 s de la barrera. Fallaba el pool, no el límite de paralelismo. | Mientras corre la prueba se sube el mínimo de hilos del pool y la barrera espera hasta 60 s. Se siguen exigiendo 3 sondas a la vez. |
| `ModuleManagerTests.Un_modulo_de_GPU_baja_la_vista_previa…` («no se conectó en 10 s») | **Defecto del código, por los tiempos de arranque en Windows.** El primer arranque de un exe recién copiado pasa por el antivirus y por el arranque de .NET. Con el equipo cargado, 10 s no alcanzan, y en la app pasaría lo mismo (por ejemplo, al abrir un módulo durante una exportación). | `ModuleManagerOptions.StartTimeout` (30 s), que `ModuleConnection` usa como `HandshakeTimeout` del cliente. Un módulo que muere al abrir se sigue notando enseguida, porque `ModuleProcess` también espera su salida. |
| `ModuleManagerTests.Matar_el_modulo_a_mitad…` («No se cumplió a tiempo») | `WaitUntil` esperaba 5 s a que el proceso arrancara y avanzara, menos que el propio tope de conexión. | `WaitUntil` espera hasta 45 s (más que `StartTimeout`) y, si se agota, informa el estado del pool. |
| `ModuleManagerTests.Una_llamada_larga_que_contesta_el_ping…` (`ModuleHungException`) | El tope del ping era de 500 ms. Con el runner cargado, la respuesta llegaba tarde porque el pool la atendía tarde, y un módulo sano se daba por colgado. | El tope del ping pasa a 1,5 s y la llamada a unos 3 s (300 pasos). La llamada sigue durando más que el tope, así que solo sigue viva si el módulo contesta cada ping. Antes duraba 0,6 s, apenas más que el tope de 0,5 s. |
| `ModuleManagerTests.Un_modulo_colgado…` (`TimeoutException` en lugar de `ModuleHungException`) | Es la misma carga: el ping, el `Kill` y el aviso de salida dependen de temporizadores y del pool. | La espera de la llamada sube a 30 s, solo como tope contra cuelgues. Lo exigido no cambia: matar y avisar en menos de 2 s. |
| Las tres anteriores, en conjunto | Las clases de App.Tests corren en paralelo y estas miden décimas de segundo. | `ModuleManagerTests` va en la colección `ModuleProcessGroup`, sin paralelismo: corre sola, al final del proyecto. |
| `TransitionAndEnvelopeTests` (2), `TitleHandlesTests` (1), `SpeedEditorTests` (2) (`TimeoutException` a los 30 s) | Son pruebas de milisegundos. `UiThread.Run` tenía un tope de 30 s y las continuaciones (por ejemplo, el `Task.Run` de `OpenFilesAsync`) esperaban al pool, con los 15 proyectos de prueba corriendo a la vez en 4 núcleos. En esa corrida App tardó 3 min 37 s; en 30b0a73, 48 s. | El tope de `UiThread` sube a 120 s: protege de cuelgues, no mide tiempo. El mensaje de tiempo agotado incluye ahora el estado del pool (`UiThread.PoolState()`), para que la próxima vez se vea si faltaban hilos. |
| `TitleClipTests.ElTituloSeVeEnElVisor` (en otros PR: «no llegó en 10 s») | Es la misma carga. El título se rasteriza en otro hilo y se compone con WARP. | El tope contra cuelgues sube a 60 s, igual que el de los shaders. Lo que cuesta rasterizar ya lo exige `TitleRasterizerTests`. |
| `OfflineTimelineTests.Every_frame_is_the_composition_the_settled_preview_shows(60/1)` («no llegó a la composición completa») | La vista previa usaba el reloj del sistema. Con el runner cargado, entre dos refrescos se pasaban el tope de la composición a medias o el cierre de decodificadores ociosos (1,25 s con muchos abiertos), y `Settle` se quedaba sin sus 20 refrescos. | La vista previa de esta prueba usa un `ManualTimeProvider` quieto. La prueba compara composiciones, no tiempos. |
| `OfflineTimelineTests.Cancelling_while_a_decoder_is_busy…` (`TimeoutException`) | El render iba en `Task.Run`, que con el pool ocupado tardaba más de 1 s en empezar. Se medía la espera del pool, no la cancelación. | El render va en un hilo propio (`LongRunning`) y el tiempo se cuenta desde la cancelación. Se sigue exigiendo menos de 1 s; antes eran 1,2 s contando 100 ms de espera. |
| `EditPropertiesTests.OperacionesAleatorias_CumplenTodasLasReglas` (en otro PR) | **Defecto real de `src/Rodaje.Core/Editing/`, sin corregir.** Se reproduce en Linux con `CsCheck_Seed=5ZOfD5KG8C16`: «`Overwrite(m4, 4064256000-4910976000, a2, v1, 0)`: «gainDb» de la pista «v1» vale 15.3235… en el clip «clip-7» (+0) y valía 15.5277…». Es una propiedad al azar, por eso solo falla con algunas semillas. | **No se tocó:** `src/Rodaje.Core/Editing/` está fuera de los caminos, porque otro agente trabaja ahí. Hace falta una tarea para ese agente con la semilla. |

## Pruebas omitidas en Windows

- **Antes** (main f89d896, run 38194016251): 145 omitidas. Por proyecto: Acceptance 18, Audio 5, App 2, Export 32, Engine 12, Subtitles 3, Render 4, TestMedia 5, Media 64.
- **Después:** +1 por esta tarea, `Composing_a_ready_title_costs_little_gpu_time`, solo con un adaptador WARP (causa concreta de hardware: no hay GPU). Render pasa de 4 a 5.
- **Otra omisión, ajena a esta tarea:** la corrida del PR se hace sobre el main actual, y ahí Paquetes trae 2 omitidas nuevas que vienen de main (3f8d920, «pruebas contra GitHub solo con RODAJE_PRUEBAS_RED=1»).
- Ninguna prueba se quitó ni se desactivó.

## Estado de la CI

- **Linux:** en verde. En local también pasan Core (1321), Paquetes (374) y Playback (65) con el filtro de la CI.
- **Windows**, run 38204631440 sobre 779454b, en tres intentos (el segundo y el tercero relanzan solo el trabajo de Windows):
  - En los dos primeros intentos, App (765 sin fallos, antes con 10), Render (5 omitidas) y Core pasaron. Engine pasó en el primero.
  - **Intento 1:** falló `CpuExportCancellationTests.Canceling_mid_export_leaves_no_process_and_no_file` (Media), 3 casos con «La cancelación tardó 3924, 4861 y 5781 ms» frente a un tope de 3 s. `Abort` volvía a los ~930 ms y ffmpeg.exe no se veía muerto («NaN ms»): con el equipo saturado, Windows tarda segundos en terminar un ffmpeg grande. En el intento 2 Media pasó entero.
  - **Intento 2:** falló `TimelineSpeedTests.Speed_adds_no_bytes_to_what_each_tick_already_reserves` (Engine): 3175 frente a 3176 bytes por tick en pausa. Es 1 B de media, unos 200 B en 200 refrescos, medidos con `GC.GetAllocatedBytesForCurrentThread`. En el intento 1 pasó.
- **Run 38205735276** (1e2d2f6, sobre e890f54 de main): pasaron App (793), Engine (350), Render (5 omitidas), Core, Paquetes y los demás. Entre ellas, todas las que el coordinador vio fallar en e890f54: `ClipPropertyEditorTests`, `TitleEditorTests`, `PackageServiceTests` (2), `PlanningTests`, `TitleHandlesTests`, `ExportEncoderAvailabilityTests`, `ProcessTests` y `TitleRasterizerTests`.
  - Solo volvió a fallar `CpuExportCancellationTests`, con 3 casos: 3040, 4893 y 6887 ms.
- **Causa probable de `CpuExportCancellationTests`**, por las 8 líneas de diagnóstico de las dos corridas:
  - `Abort` vuelve siempre a los 917–945 ms, es decir, cuando se agota `KillWait` (900 ms) sin que ffmpeg.exe haya salido. Además, `ExitTime` no se puede leer («NaN»).
  - El sink lanza ffmpeg con prioridad `BelowNormal` (`FfmpegPipeSinkOptions.Priority`). Con la CPU saturada por los demás procesos de prueba, Windows tarda segundos en dar tiempo a los hilos de un proceso de baja prioridad para terminar. Mientras tanto, la escritura en la tubería sigue bloqueada.
  - Un arreglo posible, en `src/Rodaje.Media/Export/FfmpegPipeSink.cs` (fuera de los caminos): subir el proceso a `Normal` antes de matarlo en `Abort`. En la app también se notaría al cancelar con el equipo ocupado.

- **Las fallas de Windows que quedan están fuera de los caminos de esta tarea.** Ni esas pruebas ni `src/Rodaje.Media/` cambiaron desde f89d896, y en main pasaron. Hacen falta tareas con esos archivos en sus caminos (ver «Pendiente»).

## Decisiones

- **El tope de arranque se cambia en la app, no en el protocolo.** El de 30 s va en `ModuleManagerOptions.StartTimeout` y no en `ModuleClientOptions`: el protocolo (`src/Rodaje.Modules.Protocol/`) no estaba en los caminos y `MainWindow` crea su propio `ClientOptions`, que habría vuelto a los 10 s.
- **Se subieron los topes que protegen de cuelgues, no los que miden.** Las cotas de rendimiento siguen igual: matar a un módulo colgado en menos de 2 s, cancelar un render en menos de 1 s, el costo de GPU en una GPU real y tres sondas a la vez.
- **La única omisión nueva tiene causa de hardware:** WARP no es una GPU.

## Pendiente

- El defecto de `Overwrite` con keyframes de `gainDb` en `src/Rodaje.Core/Editing/`, con la semilla de arriba.
- `CpuExportCancellationTests` (tope de 3 s) falló en 2 de 3 corridas del PR, y no falló en las de main que vi. Hace falta una tarea con `src/Rodaje.Media/Export/FfmpegPipeSink.cs` y esa prueba en sus caminos: subir la prioridad antes de matar (ver «Estado de la CI»).
- `TimelineSpeedTests.Speed_adds_no_bytes_to_what_each_tick_already_reserves`: la igualdad exacta en pausa falló una vez por 1 B por tick. Hace falta mirar qué reserva de más (algo del runtime en el mismo hilo o un camino raro del Tick) antes de darle margen. Camino: `tests/Rodaje.Engine.Tests/Timeline/`.
- Otros textos de la app formatean decimales con `CurrentCulture` y salen con punto por `InvariantGlobalization`: `Views/Analysis/AnalysisUi.Sec`, el tempo de `RhythmDialog` y el estado de `StabilizeDialog`. Están fuera de los caminos.

## Posibles choques

- El PR #27 (`claude/modulos-fuzz`) cambia `src/Rodaje.Modules.Protocol/MessageChannel.cs`. Aquí no se toca el protocolo: el cambio de módulos está en `src/Rodaje.App/Services/Modules/`.

## Qué comprobar en Windows

1. `ABRIR.bat`: que pasen las pruebas de `ModuleManagerTests`, `TitleEditorTests`, `OfflineTimelineTests` y `ExportEncoderAvailabilityTests`.
2. Con una GPU real, que `Composing_a_ready_title_costs_little_gpu_time` **corra** (no se omite) y pase.
3. En la app, que la pestaña Texto de un título con animación diga «Entrada de 0,5 s» con coma.
