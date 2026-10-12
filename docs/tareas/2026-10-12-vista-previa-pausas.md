# Pausas de la vista previa en cortes con velocidad y transiciones

Rama `claude/vista-previa-pausas` (tarea 36 de la cola), PR #41. Hecho en la nube, sin GPU: el análisis sale de leer el código y de reproducir el reparto de decodificadores con dobles; la medida real queda para la RTX 3050.

## Causa

`docs/fase-6.md` anotó refrescos de 59–370 ms también sin exportar, siempre en cortes y transiciones (entrada al 2× en 10 s, PiP en 20 y 24,5 s). Lo que hace el hilo de la vista previa en cada refresco es `Tick` (no espera a ningún decodificador; ni al que se cierra) y la composición, que toma `GpuDevice.LockContext`. Ese candado es el `lock` del `AVD3D11VADeviceContext` de FFmpeg, y FFmpeg crea el decodificador de D3D11VA (`CreateVideoDecoder` y sus vistas, en `ff_dxva2_decode_init`) con él tomado. Así que **abrir un decodificador mientras suena detiene la composición** lo que tarde el controlador; es la causa probable. No la pude medir: lo confirma la prueba con hardware de abajo.

Con dobles, reproduciendo la escena de aceptación se abrían **18 decodificadores en dos minutos**, y las aperturas caen justo en los cortes medidos:

- **Entrada al 2× (10 s) y al 0,5× (20 s) de la principal:** el material sigue de un clip al otro, pero el pre-roll del clip siguiente (y del carril compañero de la transición) formaba un tramo aparte del que ya avanzaba y abría otro decodificador del mismo medio. Con un arranque lento de los decodificadores el pre-roll empieza hasta 2 s antes y abría todavía más (11 en la principal sola).
- **Fundido de 1001/1000× a «Bala» (40 s):** el que sale queda detrás del que entra y sus ventanas se separan al final.
- **PiP (cada corte):** vuelven al principio de su material, así que el pre-roll sí necesita un segundo decodificador; pero el primero quedaba ocioso, se cerraba a los 5 s y en el corte siguiente se abría otro.

## Hecho

En `src/Rodaje.Engine/Timeline/TimelineScheduler.Demand.cs`:

- **`Continuing`:** el pre-roll de un corte del mismo medio donde el material sigue no abre decodificador. Espera a quedar en el mismo tramo que el clip que sale, o se asigna al decodificador que ya avanza cuando pedirlo no lo hace volver atrás. Si el que entra va por delante y el que sale lo alcanza antes de salir y va al menos tan rápido (`CatchesUp`, de 2× a 0,5×), el decodificador se adelanta una vez y vuelve con un seek.
- **`Leaving`:** un tramo visible al que le quedan menos de `PreRollFrames` cuadros lo sirve, de paso, el decodificador que está a menos de 64 cuadros.
- **Reparto:** en `Assign`, lo que se ve elige decodificador antes que un pre-roll.
- **`KeptForCut`:** reproduciendo, un decodificador ocioso se guarda si su medio es el siguiente clip de algún carril en los próximos 30 s, y el pre-roll de ese corte lo reutiliza con un seek. En pausa, con muchos abiertos o con otro archivo (proxy) se cierra como siempre; `MakeRoom` lo cierra primero si falta VRAM.
- **Estadística:** `TimelineStats.DecodersOpenedWhilePlaying` cuenta las aperturas con el reloj corriendo.

La documentación está en `docs/arquitectura/motor.md`, en la sección «Pausas en los cortes (tarea 36)».

## Pruebas

- **`tests/Rodaje.Engine.Tests/Timeline/TimelineCutPauseTests.cs`** (con dobles; corre en la CI de Windows):
  - La principal de la escena, dos bloques, con pre-roll de 0,5 s y de 2 s: **ninguna apertura reproduciendo**. Antes eran 4 y 11. Ningún refresco tarde ni cuadro perdido, todos los pre-roll a tiempo y los mismos seeks.
  - Un PiP que vuelve atrás en cada corte: se abre un solo decodificador en todo el recorrido, no uno por corte.
  - Un ocioso se guarda para el corte que viene y se cierra en pausa.
  - La escena de aceptación entera en dos minutos: **2 aperturas**, la del primer corte de cada PiP (antes, 18). Ningún refresco tarde, 35 de 35 pre-roll a tiempo y 52 seeks (antes, 54).
- **`TimelineCutPauseHardwareTests`** (`TiempoReal`, `[HardwareFact]`): FFmpeg, D3D11VA y el compositor reales. La principal hace 1×, 2×, 0,5× y 1001/1000× con fundido, cortinilla y empuje, con un PiP que vuelve atrás con un fundido. Reproduce a 60 Hz y mide `Tick` más `Compositor.Render`. Exige que ningún refresco pase de 50 ms fuera de la apertura del PiP y que haya una sola apertura. La pausa de esa apertura la informa como `MEDIDA pausa_apertura_pip`.
- **En Linux:**
  - `tests/Rodaje.Playback.Tests`: 66 de 66.
  - `tests/Rodaje.Engine.Tests`, compilado con `-p:EnableWindowsTargeting=true` y las rutas `C:\…` de los dobles cambiadas a rutas de Linux, solo en local y sin subir: 347 pasan y 5 fallan, igual que en `main`. Las 5 son `AudioReverseMediaTests` (×4), que necesitan FFmpeg, y `Relative_media_paths…`, por las rutas de Windows. No cambió el resultado de ninguna prueba que ya existía.
- **CI de Windows:** `Rodaje.Engine.Tests` 355 de 355 con las nuevas (12 omitidas por hardware, como en `main`). El trabajo falla por dos pruebas ajenas a esta rama:
  - `ModuleManagerTests.Una_llamada_larga_que_contesta_el_ping_no_se_toma_por_colgada` (App, módulos);
  - `AudioAi ProcessTests.Bad_buffers_fail_the_call_without_closing_the_module`.

  En el primer push falló en cambio `CpuExportCancellationTests` (1571 ms). Son las fallas conocidas de tiempos en el runner que arregla el PR de integración #35.
- **Pruebas omitidas:** ninguna nueva. La de hardware es `TiempoReal`, como las demás de su clase, y la CI filtra esa categoría.

## Pendiente y bloqueo parcial

- **Sin GPU no se puede medir la pausa.** La causa (abrir un decodificador con el candado del contexto) es la probable, no la medida. Lo que queda es la primera apertura de cada PiP: el pre-roll de un medio que vuelve atrás necesita un segundo decodificador y abrirlo sigue costando la pausa. Si la medida la confirma, lo siguiente es una de estas dos cosas, y las dos tocan `Rodaje.Media`:
  - abrir esos decodificadores antes de reproducir (al asentarse un seek en pausa);
  - crear el decodificador de D3D11VA fuera del candado.
- **Otras causas posibles que no son de este camino:** la primera vez que el controlador usa un shader de transición (la cortinilla y el empuje se ven por primera vez en 20 y 30 s). `EffectPipeline` crea los shaders en segundo plano, pero el controlador puede compilar más al primer dibujo. La prueba de hardware asienta antes con `RenderStrict` para no medir esto. La aceptación no lo hace, y si allí queda una pausa en el primer uso de cada transición, la causa es esta.

## Qué comprobar en Windows (RTX 3050)

1. **La prueba nueva con hardware:** `herramientas\dotnet-local.cmd test tests\Rodaje.Engine.Tests --filter "FullyQualifiedName~TimelineCutPause"`. Las de dobles deben pasar.
   - La de hardware debe pasar, con `MEDIDA refresco_max_ms` por debajo de 50 fuera de la apertura del PiP.
   - Anotar `MEDIDA pausa_apertura_pip`: si sale de 50–300 ms, confirma la causa.
2. **La aceptación de fluidez:** `RODAJE_PRUEBAS_LARGAS=1`, `ExportResponsivenessAcceptanceTests`, caso «sin exportar». Los «Bloqueos >50 ms» deberían bajar a los primeros cortes de los PiP (alrededor de 17,5 s y 24,3 s, o antes con el pre-roll largo) y desaparecer en 10 s y 20 s. Si quedan bloqueos en 20 s o 30 s, mirar la compilación de shaders del controlador.
3. **`TimelineLoadTests` (4K con cortes, `RODAJE_PRUEBAS_LARGAS=1`):** el pico de VRAM debe seguir dentro del presupuesto con los ociosos guardados. `MakeRoom` los cierra al hacer falta, pero el pool que se libera tarda y `SizePool` puede esperar hasta 300 ms por él; mirar `PoolWaits` y los pre-roll a tiempo.
4. **`app.ps1 -Medir N`** con un proyecto con PiP y velocidades: comparar las pausas antes y después.

## Choques posibles

- `origin/main` sumó, después de la punta de la que salió esta rama, `a1d6ae1a` (pruebas de `OfflineTimeline` que no dependen de la carga del runner), en `tests/Rodaje.Engine.Tests/Offline/`. No toca los mismos archivos.
- `TimelineScheduler.Demand.cs` es donde se reparte la demanda; otra tarea sobre proxies o VRAM chocaría ahí.
