# Tarea 44: carrera entre `Abort` y `Complete`, y `Kill` fuera del cerrojo

Rama `claude/export-abort-carrera`, PR #48. Solo cambia `src/Rodaje.Media/Export/FfmpegPipeSink.cs`, más dos archivos de pruebas nuevos.

## 1. `Complete` y `Failure` usaban un `Process` liberado

**Causa.** `Complete` guardaba `_process` en una variable local y esperaba con `while (!process.WaitForExit(100))`. Si en ese momento se cancelaba, `Abort` corría en el hilo que cancela, mataba a ffmpeg.exe y liberaba el `Process` (`ReleaseProcess`). La siguiente llamada de `Complete` a `WaitForExit` lanzaba `InvalidOperationException` en vez de `OperationCanceledException`. Además, `Failure` leía `_process` sin tomar el cerrojo, y `Flush` dentro de `Complete` podía encontrarse la tubería ya cerrada (`ObjectDisposedException`), una excepción que nadie capturaba.

**Arreglo.**
- **Préstamo del proceso.** El proceso va en un `RunningProcess` con su contador de usuarios. `Complete` y `Failure` lo piden prestado bajo `_gate` (`Lease`/`Return`). `ReleaseProcess` saca el proceso del sink, pero solo llama a `Dispose` cuando nadie lo tiene prestado. Si hay alguien usándolo, el `Dispose` lo hace el último que lo devuelve.
- **Tubería cerrada.** Si `Abort` ya cerró la tubería, `Complete` recibe `ObjectDisposedException` y la trata como una tubería rota: pasa por `Failure`, que la convierte en cancelación.
- **Cancelación con todo limpio.** `ThrowIfStopped` y `Failure` llaman a `Abort` antes de lanzar la excepción. Si otro hilo está abortando, esa llamada espera a que termine. Así la cancelación sale siempre como `OperationCanceledException` (con el token, si lo hay) y llega después de matar al proceso y borrar los temporales.
- **Punto sin vuelta atrás.** Antes de renombrar el temporal al archivo final, `Complete` marca `_committing` bajo el cerrojo, salvo que ya se haya cancelado. Desde ese momento `Abort` no hace nada, así que ya no puede borrar el archivo mientras `Complete` lo entrega. Si la cancelación llega durante el renombrado, llega tarde y la exportación termina bien. Si falla el renombrado, se desmarca, se aborta y se relanza la excepción.

## 2. `Kill` con `_gate` tomado y temporales abiertos

**Antes.** `Abort` esperaba dos veces `KillWait` (900 ms) con `_gate` tomado, así que en el peor caso tardaba unos 1,8 s. Si ffmpeg.exe seguía vivo, igual liberaba el proceso e intentaba borrar archivos que ffmpeg.exe tenía abiertos.

**Ahora.**
- **Datos bajo el cerrojo.** `Abort` toma bajo `_gate` lo que necesita: el proceso prestado, la tubería, una copia de los temporales y el id del registro. Ahí mismo marca `_aborted` y saca el proceso del sink.
- **Matar sin el cerrojo.** La muerte del proceso y la espera ocurren fuera de `_gate`. Las esperas bajan a 600 ms (`KillWait`) y 300 ms (`TreeKillWait`), así que el peor caso queda en unos 0,9 s.
- **Un solo `Abort` a la vez.** Quien llama a `Abort` mientras otro hilo está abortando espera a que ese termine (`_abortFinished`). Sin esto, el escritor sacaría la cancelación antes de que se borren los temporales, y las pruebas que exigen que no quede nada fallarían.
- **Si ffmpeg.exe no salió:**
  - `Abort` no libera el proceso, no cierra la tubería y no borra nada.
  - Lo anota en `Diagnostics`, una lista interna que también se escribe con `Trace.WriteLine`.
  - Lanza `PendingCleanup`, un hilo de fondo que reintenta matarlo cada 2 s. Cuando por fin sale, cierra la tubería, borra los temporales y devuelve el proceso prestado, que entonces se libera.
  - Si ffmpeg.exe no sale nunca, el `ChildProcessJob` lo mata al cerrarse Rodaje y `ExportJournal.CleanupStale` borra los temporales al volver a abrirlo.
- **Registro de exportaciones.** `Abort` y la limpieza pendiente solo quitan la entrada del registro si se borró todo. Si algo queda, se anota en el diagnóstico y lo intenta de nuevo el registro al reabrir la app.
- **`Kill` devuelve si el proceso salió.** `KillOverride` es un gancho interno solo para las pruebas.

## Pruebas nuevas (Windows, `ProxyFact`)

Ninguna depende de los tiempos del runner.

- **`FfmpegPipeSinkAbortRaceTests`.** El gancho `BeforeCompleteWaits` detiene a `Complete` justo antes de la espera. Otro hilo cancela (con el token o con `Abort`), y `Complete` no sigue hasta que ese hilo terminó. Tiene que salir `OperationCanceledException`, sin ffmpeg.exe vivo ni temporales y con el registro vacío. Con el código anterior, esta prueba saca `InvalidOperationException`. Incluye también un caso de `Abort` después de un `Complete` correcto, que no debe tocar el archivo entregado.
- **`FfmpegPipeSinkStuckKillTests`.** `KillOverride` no mata en el intento de `Abort`, y en el de la limpieza pendiente espera a que la prueba lo suelte. La prueba comprueba, en este orden:
  1. `Abort` vuelve en menos de 1 s, y un segundo `Abort` no se cuelga.
  2. ffmpeg.exe sigue vivo, la entrada del registro sigue ahí, el diagnóstico dice «no salió» y `WriteVideo` lanza la cancelación.
  3. Al soltarlo, la limpieza pendiente termina sin dejar proceso, temporales ni registro.

## Pruebas omitidas

No se omitió ni se desactivó ninguna prueba. En Linux, las de `Rodaje.Media.Tests` que necesitan ffmpeg.exe (`ProxyFact`) se saltan igual que antes. `Rodaje.Media.Tests` compila en Linux con `-p:EnableWindowsTargeting=true`, sin advertencias.

## Decisiones

- **Cancelación durante el renombrado.** Si la cancelación llega mientras se renombra el temporal, gana el archivo terminado: no se borra algo que ya se entregó.
- **Limpieza pendiente con tiempo indefinido.** Reintenta cada 2 s hasta que ffmpeg.exe salga. Un proceso que ni así muere se lo lleva el `ChildProcessJob`.
- **Diagnóstico interno.** No había un registro de diagnóstico en `Rodaje.Media`. Se usa `Trace.WriteLine`, como hace `ModuleConnection`, más la propiedad interna `Diagnostics` para las pruebas. Si la interfaz quiere mostrar estos avisos, hay que hacerla pública o exponer un evento.
- **El escritor puede seguir bloqueado.** Si ffmpeg.exe no muere, el hilo que escribe puede quedarse bloqueado en `WriteFile` hasta que salga. Esto no cambia respecto de antes; arreglarlo pediría `CancelSynchronousIo`, que queda fuera de esta tarea.

## Posibles choques

Ninguna otra rama `claude/*` sin unir toca `FfmpegPipeSink.cs`. El PR #22 (tarea 20) toca `CpuExportCancellationTests.cs`, que esta rama no cambia.

## Qué comprobar en Windows

1. **`ABRIR.bat`.** Que pasen `FfmpegPipeSinkAbortRaceTests`, `FfmpegPipeSinkStuckKillTests`, `FfmpegPipeSinkFailureTests` (las diez cancelaciones) y `CpuExportCancellationTests` (tope de 1 s).
2. **En la app, cancelar al cerrar.** Cancelar una exportación justo al final, cuando la barra llega al 100 % y ffmpeg.exe está cerrando el contenedor. Tiene que decir «Exportación cancelada», sin un error raro y sin dejar archivo.
