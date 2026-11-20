# Pruebas intermitentes: causa probable y tarea

Resultado de una lectura del código (Gemini, 20-nov-2026), no de corridas: son hipótesis hasta que alguien las reproduzca. Las que miden reloj van con `[Trait("Categoria", "TiempoReal")]` y la CI de PR las excluye (`Categoria!=TiempoReal&Categoria!=Rendimiento`); las nocturnas las corren. Regla: una prueba funcional espera una **condición** (evento, `TaskCompletionSource`, progreso de la llamada) con tope `Support.CiBound` (10 s); solo la que mide un tiempo es `TiempoReal`.

## Ya hecho

- `VulkanGpuTests.Acquired_surface_outlives_its_owner_until_the_frame_completes` (falló una vez en la CI de Linux con lavapipe): `VulkanGpu.Poll` publicaba `_completed` dentro del candado y soltaba las superficies fuera, de modo que un lector de otro hilo veía el envío terminado con la superficie sin soltar. Ahora publica después de soltarlas.
- `CoreAudioOutputTests`: las cinco que miden el reloj contra el dispositivo ya son `TiempoReal` y `AudioMac` (manuales con BlackHole); la sexta (la fábrica) es determinista. Nada que reclasificar.
- `ExportOutputGuard.DeleteTemporary` ya no tumba el hilo de exportación con una carpeta sin permiso de lectura.

## Protocolo de módulos (`tests/Rodaje.Modules.Protocol.Tests`, hoy en Sala)

| Prueba | Causa probable | Cambio |
|---|---|---|
| `ProcessTests.A_killed_module_fails_the_running_call` | `Task.Delay(100)` para esperar el arranque de la llamada y `WaitAsync(2 s)`: con el runner cargado el proceso tarda (falló tras 1 min 19 s en la CI de Windows) | Esperar a que la llamada haya empezado (callback de progreso) y usar `Support.CiBound` |
| `ProcessTests.Process_killed_mid_call…` (misma familia) | Idem | Idem |
| `ConnectionTests.Calls_beyond_the_limit_are_refused_as_busy` | `Task.Delay(30)` supone que la primera llamada ya ocupa la ranura del host | Función del host que señala su inicio con un `TaskCompletionSource` y espera una señal de salida |
| `ConnectionTests.Closing_the_editor_cancels_running_calls…` | `Task.Delay(50)` antes de cerrar | Esperar al progreso de la llamada |
| `CloseWaitTests` (línea ~96) | Bucle de `Task.Delay(10)` hasta que `s_closeKills` registra la muerte | Señalar con un evento tras el registro |
| `UnixTransportTests` (líneas ~164 y ~171) | Esperas fijas de 500 ms y sondeo de `IsAlive` | `WaitForExitAsync().WaitAsync(CiBound)` |
| `Fuzz/ProtocolDefectTests` (líneas ~244-255) | CTS de 300 ms y `Task.Delay(50)` con el pool saturado; `WhenAny` con 3 s | Cancelar a mano tras confirmar el envío; tope `CiBound` |
| `Fuzz/ReplyRaceTests` (línea ~44) | `Task.WhenAny(run, Task.Delay(500))` | Señal de que el host entró en el drenaje del cierre |
| `Latency_and_shared_memory_throughput`, `A_frozen_module_is_dead_within_the_default_budget_plus_margin`, `A_module_that_ignores_cancel_is_killed_soon_after_the_grace_period`, `Cancellation_in_a_real_process_is_fast` | Miden tiempos finos | Ya son `TiempoReal`: la CI de Sala debe filtrarlas igual que la de Rodaje |
| `ProcessKillTests` | Funcional, cota de 10 s: solo falla por carga extrema | Nada |

Los fuzz largos (`ClientFuzzTests`, `HostFuzzTests`) ya son `TiempoReal`; la versión corta sufre con plazos de 300-500 ms y `CancelGrace` de 150 ms bajo carga: calibrar con un factor en CI o con `ManualTime`.

## Caché de render (`tests/Rodaje.Export.Tests/RenderCache/RenderCacheJobTests.cs`)

- `La_pausa_detiene_la_entrega_en_cien_ms…` y `Mide_tres_fragmentos_de_60_cuadros_1080p30_por_fase`: miden tiempo real; ya son `TiempoReal`. En la primera, `Task.Delay(250)` ciego se puede cambiar por `Assert.True(job.Pause.IsWaiting)` más la comparación de cuadros escritos.
- `Una_pausa_larga_o_cancelada_borra_el_fragmento_incompleto`: usa un `ExportPauseToken` con 200 ms de reloj real; tarea: que acepte un `TimeProvider` (como `ManualTime`) para disparar la expiración con un reloj virtual.
- `Dos_trabajos_reutilizan_el_dispositivo_incluso_tras_cancelar`: sondeo con `WaitUntilAsync` (`Task.Delay(1)`, ~15 ms reales en Windows); tarea: un `TaskCompletionSource` en `ExportPauseToken` que señale la entrada en pausa.

Aún sin estabilizar: son cambios en código de producción (`ExportPauseToken`) y no hay corrida que los haya visto fallar.
