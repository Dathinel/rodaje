# Tarea 62: limpieza pendiente del sink, dueña de sus rutas, y `Return` a prueba de excepciones

Rama `claude/export-limpieza-pendiente`, PR #65. Cambia solo `src/Rodaje.Media/Export/FfmpegPipeSink.cs`. Añade un archivo de pruebas nuevo, `tests/Rodaje.Media.Tests/Export/FfmpegPipeSinkPendingCleanupTests.cs`. `ExportOutputGuard.cs` no hizo falta tocarlo.

## Lo hecho

### 1. Registro de rutas con limpieza pendiente

**Problema.** `ExportOutputGuard.TemporaryPath` es determinista, y `Start` no sabía si había una limpieza pendiente. Si el usuario reexportaba enseguida al mismo archivo después de un `Abort` cuyo ffmpeg.exe no salió, pasaban dos cosas:
- La exportación nueva chocaba con el ffmpeg.exe viejo, que seguía escribiendo el mismo `.rodaje-tmp`.
- Cuando el proceso viejo salía, la limpieza vieja borraba los archivos de la nueva: el temporal, el `.ffmeta` y los hermanos `name-*`.

**Arreglo.**
- **Registro.** `PendingTemporaries` es un registro estático y privado de `FfmpegPipeSink`, válido para todos los sinks del proceso. Guarda cada ruta, normalizada con `GetFullPath` y sin distinguir mayúsculas, junto a su dueña, un `PendingOwner`.
- **Cuándo se reclaman.** `DeferCleanup` reclama los temporales antes de lanzar el hilo de fondo. Al terminar, pase lo que pase, los suelta y completa `owner.Finished`.
- **`Start` lo consulta** con `WaitForPendingCleanup` antes de `CheckOutput`. Calcula las rutas que escribiría la pasada (`PlannedTemporaries`: temporal, `.ffmeta` y registro de dos pasadas). Hay choque si una ruta es igual a otra o la extiende con `-` o `.`, en cualquiera de los dos sentidos. Es lo mismo que borra `DeleteTemporary`, más el `.ffmeta`.
- **Si choca**, `Start` espera hasta 1 s (`PendingCleanupWait`) y respeta el token de cancelación. Si la limpieza sigue pendiente, lanza `ExportException(OutputNotWritable)` con `Culprit` igual a la salida y este mensaje: «Una exportación cancelada a … todavía se está cerrando (ffmpeg.exe N no salió). Espera unos segundos o elige otro nombre.» No lanza ningún proceso ni escribe nada.
- **La limpieza solo borra lo suyo.** `DeleteFiles` con dueña borra con el cerrojo del registro tomado, y solo las rutas que siguen a su nombre. Un `Start` que pregunte mientras tanto espera, y luego ya no encuentra nada. Si alguna ruta ya no es suya, no la toca, lo anota en `Diagnostics` y suelta la entrada del registro de exportaciones, para que tampoco la borre `CleanupStale` al reabrir. Hoy esto no puede pasar, porque nadie quita la propiedad; es una defensa.

### 2. `Abort` a prueba de excepciones

**Problema.** Si `Stop`/`Kill` (o `KillOverride`) lanzaban algo que no se capturaba, `Return(running)` no corría y el `Process` no se liberaba. Además, la excepción subía por el callback de `Cancel()` hasta el hilo que cancelaba.

**Arreglo.**
- **`TryStop`.** Envuelve a `Stop`. Ante cualquier excepción la anota en `Diagnostics` («Matar a ffmpeg.exe (N) lanzó Tipo: mensaje») y devuelve si el proceso ya salió (`WaitQuietly` con 0).
- **`Abort`.** Usa `TryStop`, y el `Return(running)` va en el `finally` exterior, salvo que el proceso se haya entregado a la limpieza pendiente (`handedOff`). Cualquier otra excepción (al borrar o al lanzar el hilo) se captura y se anota («Cancelar ffmpeg.exe (N) falló…»). `finished` se completa siempre.
- **Limpieza pendiente.** Usa `TryStop` en su bucle y anota solo el primer fallo, para no llenar el diagnóstico cada 2 s. Su `catch` ahora cubre cualquier excepción.

## Pruebas

`FfmpegPipeSinkPendingCleanupTests` tiene 3 pruebas `[Fact]` con el ffmpeg falso de la tarea 63, así que corren en Linux y en Windows sin ffmpeg.exe. No dependen de los tiempos del runner: `KillOverride` decide cuándo muere el proceso, y `-falso-colgar` lo mantiene vivo.

1. **`Start_to_the_same_output_fails_while_a_canceled_export_is_still_closing`.** Un `Abort` deja la limpieza pendiente y la prueba comprueba lo siguiente:
   - Un sink nuevo a la misma salida falla con `OutputNotWritable` tras la espera de 1 s, y el mismo sink tampoco puede reusarla.
   - Otra salida de la misma carpeta funciona.
   - Al soltar la limpieza, la exportación nueva arranca, termina con su archivo íntegro y no queda ningún temporal.
2. **`Kill_that_throws_while_canceling_is_noted_and_the_process_is_still_released`.** `KillOverride` mata y luego lanza. La prueba comprueba que `CancellationTokenSource.Cancel()` no lanza, que el `Process` queda liberado y que el diagnóstico nombra la excepción. Tampoco quedan temporales ni registro.
3. **`Kill_that_throws_with_ffmpeg_alive_leaves_the_cleanup_pending_until_it_exits`.** `KillOverride` lanza sin matar. La limpieza queda pendiente y el `Process` sigue sin liberar. Al soltarlo, todo se limpia y el `Process` se libera.

**Contra el código de main**, las 3 fallan, y con el cambio pasan.

**En Linux, a mano:** `dotnet test tests/Rodaje.Media.Tests -p:EnableWindowsTargeting=true --filter FullyQualifiedName~FfmpegPipeSink` da 16 pasadas y 18 omitidas (antes 13 y 18). La suite completa de `Rodaje.Media.Tests` en Linux da 5 fallos, que ya fallan igual en main en Linux y no son de esta tarea:
- `ExportTemporariesTests.A_temporary_still_open…`
- `VideoColorTests.Conversion_through_swscale…`
- tres de `ProxyStoreTests`

Esos fallos dependen del bloqueo de archivos de Windows o de FFmpeg, y la CI de Linux no corre `Rodaje.Media.Tests`.

## Pruebas omitidas

Ninguna nueva. Las omitidas en `Rodaje.Media.Tests` siguen igual (18 en el filtro del sink en Linux, por la plataforma: falta ffmpeg.exe).

## Decisiones

- **Esperar 1 s y después fallar**, en lugar de esperar sin tope. Una cancelación normal ya termina en menos de 1 s, y la pendiente solo existe si ffmpeg.exe no murió. Bloquear `Start` más tiempo dejaría la interfaz sin respuesta.
- **El registro es privado** de `FfmpegPipeSink`, no de `ExportOutputGuard`, porque solo lo usan el sink y su limpieza. Así la tarea no toca `ExportOutputGuard.cs`.
- **Choque por prefijo con `-` o `.`.** Es conservador: cubre los hermanos que `DeleteTemporary` borraría y el `.ffmeta`.
- **Un solo commit.** Las dos partes tocan los mismos métodos (`DeferCleanup`, `DeleteFiles`), así que se separaron en dos puntos solo en la descripción.
- **«Revisión en bloque 6».** La cola la cita, pero no encontré el documento en `docs/`. Me basé en el texto de la tarea y en la entrega de la 44.

## Posibles choques

Ninguna otra rama `claude/*` sin unir toca `FfmpegPipeSink.cs` ni `ExportOutputGuard.cs`.

## Qué comprobar en Windows

1. **`ABRIR.bat`.** Que pasen `FfmpegPipeSinkPendingCleanupTests`, `FfmpegPipeSinkFakeRaceTests`, `FfmpegPipeSinkStuckKillTests` y `FfmpegPipeSinkFailureTests` (las diez cancelaciones).
2. **En la app**, cancelar una exportación y volver a exportar enseguida al mismo archivo. Lo normal es que funcione, porque ffmpeg.exe muere en menos de 1 s. Si alguna vez sale «todavía se está cerrando», al reintentar unos segundos después tiene que funcionar.
