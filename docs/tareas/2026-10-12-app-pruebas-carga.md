# Tarea 47: pruebas que fallan con el runner de Windows cargado

Rama `claude/app-pruebas-carga`, PR #49.

## Causa de cada una

El runner de Windows corre a la vez todos los proyectos de prueba en 4 núcleos. Con la CPU llena, Windows tarda unos 4 s en subir de prioridad a un hilo que se quedó sin turno. Eso afecta sobre todo a los hilos `BelowNormal` y a las continuaciones que esperan al pool.

| Prueba | Dónde falló | Causa | Arreglo |
|---|---|---|---|
| `ClipVisualsTests.Dispose_does_not_wait_for_a_reader_that_ignores_cancellation` (8082 ms) | #32 | **Defecto.** `Dispose` no espera al lector (su `Join` tiene tope de 200 ms), pero tomaba `_gate` con `lock`. Los hilos `BelowNormal` toman ese candado a menudo (`Claim`, `Release`, `MarkChanged`, el despertar de cada 2 s). Uno sin turno con el candado tomado retenía a `Dispose` (inversión de prioridad). | `Dispose` marca `_disposed` (ahora `volatile`) y cancela sin candado. Avisa con `Monitor.TryEnter` dentro del mismo tope y, si no lo consigue, deja el `PulseAll` al pool. La prueba cronometra solo `Dispose`, en un hilo propio de prioridad `Highest`. El tope sigue en 1 s. |
| `ClipVisualsTests.A_second_session_reads_thumbnails_and_peaks_from_disk` («No llegó a tiempo») | #40 | Los hilos `BelowNormal` estuvieron más de 10 s sin turno. | `WaitFor` y las demás esperas de la clase pasan a 60 s (`Patience`). Son topes contra cuelgues y no miden nada. |
| `ClipVisualsTests.Single_requests_snap_...` | Linux (al probar aquí) | Carrera en la propia prueba: el hilo terminaba la miniatura antes de que `GetNearestThumbnail` buscara la más cercana. | Se cierra la compuerta del lector falso alrededor de esas dos llamadas. |
| `ExportEncoderAvailabilityTests.Invalidate_during_a_run_starts_the_next_one_only_after_it_ends` (1 corrida esperada, hubo 2) | #40 | **No es una carrera en `Invalidate`:** el encadenado por `_lastRun` es correcto. La prueba duró 13 s. La sonda falsa esperaba `release.Wait(10 s)`: se soltó sola, la corrida vieja terminó y la nueva empezó, como debe. | La sonda espera sin tope propio y un `finally` la suelta. La espera de `entered` pasa a 60 s. Se agrega `Assert.False(old.IsCompleted)`. |
| `TimelineSpeedTests.Speed_mapping_in_the_plan_reserves_nothing` (3744 bytes) | #38 | Fueron 3744 bytes en 15 000 llamadas, 0,25 bytes por llamada. Es una reserva única en el hilo, no del plan. Hipótesis: el runtime (subida de nivel del JIT u OSR del bucle) con el runner cargado. | Se mide en hasta tres ventanas y cuenta la menor. Una reserva por llamada aparecería en todas. |
| `TimelineReverseTests.A_paused_tick_reserves_the_same_in_reverse_as_forward` (1839 frente a 1815) | #37 | 24 bytes por Tick de diferencia: unos 4,8 KB de una vez en una sola ventana de 200 Ticks. | Igual que la anterior: cada sentido cuenta su ventana menor de tres. |
| `ModuleManagerTests.Una_llamada_larga_que_contesta_el_ping_no_se_toma_por_colgada` | #36, #37, #41, #44 | Exige contestar cada ping en 1,5 s de reloj. Con el runner cargado, el vigilante mataba al módulo sano (`ModuleHungException`). | `Categoria=TiempoReal`: mide tiempo real. La CI la excluye y `probar.ps1` la corre aparte, sola. |
| `ModuleManagerTests.Abre_al_primer_uso_reutiliza_el_proceso_y_lo_cierra_al_quedar_ocioso` (`:38`) | #38 | La prueba no estaba en la tarea, pero la causa es la misma: `HasExited` espera como mucho 3 s a que el módulo ocioso salga. | `await WaitUntil(() => EchoModules.HasExited(pid))`, con un tope de 45 s. |
| `ModuleManagerTests.Un_modulo_pesado_espera_a_que_termine_el_otro` (`:226`, `TimeoutException`) | #49, primera corrida | `second.WaitAsync(5 s)`: el segundo módulo espera a que se cierre el primero y luego arranca su proceso. Con el runner cargado, eso pasó de 5 s. | El tope contra cuelgues pasa a 60 s. Arrancar ya tiene su propio tope de 30 s. |
| `AudioAi ProcessTests.Bad_buffers_fail_the_call_without_closing_the_module` (`:118`) | #41 | Exigía un ping de menos de 1 s. La prueba tardó 14 s. | Ahora comprueba lo que dice su nombre: el ping contesta (tope de 60 s) y el proceso no salió. La latencia de menos de 1 s la sigue midiendo `Cancel_is_confirmed_quickly_and_the_module_keeps_working`. |
| `PackageServiceTests`, `VideoAnalysisTests`, `ModuleMediaTests` | #33 | Topes de 30 s de `UiThread` y del saludo del módulo (10 s). | Ya estaban resueltas en main: `UiThread.DefaultTimeout` es de 120 s (779454b) y `ModuleManagerOptions.StartTimeout` de 30 s. No se tocan. |

## Decisiones

- El único cambio de código es `ClipVisuals.Dispose`. Los hilos de trabajo siguen en `BelowNormal`, como en la app: bajarles la prioridad es a propósito.
- No subí ningún tope que mida algo: `Dispose` sigue en 1 s y las reservas, en 0 bytes o iguales en los dos sentidos. Lo que pasó de 10 a 60 s son esperas contra cuelgues (hay precedente en `PlaybackSchedulerTests` y en `UiThread`).
- Las pruebas de reservas no se marcan `TiempoReal`, porque no miden el reloj. Contar la ventana mínima conserva el contrato.

## Pruebas omitidas

Ninguna nueva. Una prueba pasa a `TiempoReal` (`Una_llamada_larga_...`): la CI excluye esa categoría y `herramientas\probar.ps1` la corre en su pasada propia.

## Verificación

- En Linux compilan con `-p:EnableWindowsTargeting=true` los proyectos de prueba de App, Media, Engine y AudioAi.
- Pasan en Linux, también con la CPU saturada (12 procesos `yes` en 4 núcleos):
  - `ClipVisualsTests`: 14 de 14, en 8 corridas;
  - `ExportEncoderAvailabilityTests`: 13 de 13;
  - las 2 pruebas de reservas del motor.
- Las de módulos necesitan los exe de Windows: se verifican con la CI.
- CI de Windows, primera corrida (8604632, job 114751244126): pasan todos los proyectos menos App, con 2 fallos.
  - `CurrentCultureNumbersTests.LaApp_NoFormateaNumerosConLaCulturaDelHilo` (`TranslateDialog.cs:141`): defecto real de main, ajeno a esta tarea; lo arregla el PR #50.
  - `ModuleManagerTests.Un_modulo_pesado_espera_a_que_termine_el_otro`: corregida en el segundo push (ver la tabla).
  - Engine, Media y AudioAi, con las pruebas cambiadas, en verde. Linux en verde.
- CI de Windows, segunda corrida: el resultado está en el comentario final del PR #49.

## Pendiente y posibles choques

- Otras pruebas del mismo tipo, fuera de los caminos de esta tarea:
  - `Rodaje.Modules.Protocol.Tests.ProcessTests.A_module_that_ignores_cancel_is_killed_after_the_grace_period` falló con «tardó 4746 ms» y «4608 ms» en #41 y #44.
  - `CpuExportCancellationTests` (1571 ms) es de la tarea 44.
  - `ModuleManagerTests` mide además menos de 1 s y menos de 2 s en `Un_modulo_que_muere_...` y `Un_modulo_colgado_...`, y `Cancel_is_confirmed_quickly_...` mide menos de 1 s. Estas aún no fallaron; si fallan, van con `TiempoReal`.
  - Propongo una tarea para `Rodaje.Modules.Protocol.Tests`.
- No toqué los archivos de las tareas 44 (`FfmpegPipeSink.cs`) ni 37 (subtítulos en la App).

## Qué comprobar en Windows

- `ABRIR.bat` (todas las pruebas) en verde.
- Con `RODAJE_PRUEBAS_LARGAS=1`, la pasada `TiempoReal` incluye `Una_llamada_larga_que_contesta_el_ping_no_se_toma_por_colgada` y pasa.
- En la app, cerrar un proyecto con la tira de miniaturas a medio llenar no se nota más lento.
