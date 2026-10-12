# Tarea 59: cinco defectos del protocolo de módulos hallados por el fuzz

Rama `claude/protocolo-defectos`, PR #62. Corrige los defectos 1 a 5 de `ProtocolDefectTests` (entrega de la tarea 25, `docs/tareas/2026-10-11-modulos-fuzz.md`) y quita sus `Skip`. Además, por encargo del coordinador, el punto 6 viene de la entrega de la tarea 57: el `HandshakeTimeout` del anfitrión.

## Hecho (un commit por punto)

1. **Un `cancel` que llega cuando la llamada termina** (`ModuleHost.cs`). `RunAsync` cancela con `TryCancelAsync`, que ignora la `ObjectDisposedException` del `CancellationTokenSource` que la llamada ya desechó en su hilo. En ese momento la llamada ya terminó y no hay nada que cancelar. Se mantuvo el `Dispose` del `cts` al terminar cada llamada. No desecharlo hasta cerrar la conexión dejaría un `cts` enlazado a `stop` por cada llamada en una conexión larga.
2. **`RunAsync` no lanza al cerrar.** El `finally` cancela con la misma protección. Si una llamada terminó con error, `Task.WhenAll` ya no hace salir la excepción, porque solo lanza otra cosa que `TimeoutException` cuando todas terminaron. Así siempre se espera a las llamadas y se liberan las regiones. Se quitó la tolerancia de la `ObjectDisposedException` en `HostFuzzTests`.
3. **Una respuesta de más de 1 MiB se contesta con error.** `TrySendAsync` ahora devuelve el motivo cuando el mensaje no se puede escribir, por tamaño (`ModuleProtocolException`) o por serialización (`JsonException`, `ArgumentException`). En esos casos no llegó nada al cable, porque `MessageChannel.SendAsync` serializa y mide antes de tomar el candado. `SendReplyAsync` contesta entonces el `result` con `error` `internal` «No se pudo enviar el resultado: …».
4. **Sustitutos UTF-16 sueltos.** `InvokeMessage.Validate` y `ResultMessage.Validate` revisan `parameters` y `value` con `Limits.Json`, que lee todos los textos y nombres de propiedad. `JsonElement.GetString` lanza `InvalidOperationException` con un sustituto suelto: lo comprobé con `"\ud800"`, `"a\udc00b"`, `"\ud800A"` y una clave `{"\ud800":1}`, y un par bien formado pasa. Con eso, `Parse` corta por malformado. El `result` que valida `ModuleHost` antes de enviarlo también cae aquí y sale como «Resultado inválido». Se quitó la tolerancia del sustituto en `HostFuzzTests` y `MessageFuzzTests`, y también el ayudante `FuzzSupport.HasSurrogateEscape`, que quedó sin uso.
5. **Un módulo que deja de leer ya no cuelga al editor.**
   - `ModuleClient.SendAsync` escribe con el token del llamador enlazado a `_stop` y con un plazo nuevo, `ModuleClientOptions.WriteTimeout`. Son 10 s, contados desde que tiene el candado.
   - `MessageChannel.SendAsync` tiene una sobrecarga con ese plazo. Si la escritura se corta a mitad, por cancelación o por plazo, el marco pudo quedar cortado. Entonces el canal cierra el flujo y marca `Broken`.
   - En el cliente hay tres casos:
     - Si se cancela mientras espera su turno, solo se cancela esa operación.
     - Si se cancela a mitad de la escritura, la operación se cancela y la conexión se corta.
     - Si vence el plazo, `Fail` corta con «El módulo dejó de leer: …».
   - `InvokeAsync` y `PingAsync` pasan su token. El aviso `cancel` sale con `CancellationToken.None`, porque el token del llamador ya está cancelado, pero sigue limitado por el plazo.
   - Con esto `InvokeAsync` y `DisposeAsync` siempre terminan. Así se completa el defecto 6: quien tiene el candado ya no lo retiene para siempre. Lo dice el comentario de `MessageChannel.Dispose`.
6. **`ModuleHost.HandshakeTimeout` sube de 10 s a 30 s por defecto**, igual que `ModuleManagerOptions.StartTimeout`. Se quitó el ajuste propio de `modules/Rodaje.Module.Raw/Program.cs`, que quedó redundante. Era esa línea y su comentario de dos líneas, que solo la explicaba. La conexión a la tubería del anfitrión sigue en 10 s.

`docs/protocolo-modulos.md` se actualizó en cada punto que cambia un comportamiento descrito:
- código `internal` para un resultado que no se puede enviar;
- UTF-8 estricto también dentro de `parameters` y `value`;
- marco cortado: se cierra la tubería;
- el módulo que no lee corta la conexión;
- `WriteTimeout` en la tabla de plazos;
- el anfitrión cierra si su respuesta vence a mitad de la escritura;
- `HandshakeTimeout` del anfitrión en 30 s.

## Pruebas

- **Nuevas:** `tests/Rodaje.Modules.Protocol.Tests/WriteTimeoutTests.cs`, con 3 pruebas:
  - el plazo de escritura corta la conexión sin token del llamador;
  - cancelar mientras se espera el turno no la corta, y la conexión sigue sirviendo después;
  - cancelar a mitad de una escritura sí la corta.

  Usan una tubería con las escrituras detenidas a voluntad. Solo tienen topes contra cuelgues (`FuzzSupport.Capped`), sin cotas de reloj.
- **`A_result_over_the_frame_limit_is_answered_with_an_error`** tenía `WaitAsync(5 s)` y comprobaba un ping de menos de 5 s. Ahora usa el tope contra cuelgues del fuzz, sin comprobar latencia, como pidió la tarea 57.
- **Comprobado:** con el código de main, las pruebas de los defectos 1 a 5 fallan. El 3 y el 5 por cuelgue a los 10 s; el 1, el 2 y el 4 enseguida.
- **Linux, proyecto sin `TiempoReal`:**
  - Main: 98 pruebas; 85 pasan, 6 fallan y 7 se omiten.
  - Esta rama: 101 pruebas; 93 pasan, 6 fallan y 2 se omiten.
  - Lo corrí 8 veces seguidas con el mismo resultado.
  - Las 6 que fallan son las mismas de main y no son regresión: las 5 de `ProcessTests` que no son `TiempoReal` (no hay `Rodaje.Module.Echo.exe` en Linux) y `ConnectionTests.Shared_memory_copy_is_exact_and_bounds_are_checked` (memoria compartida con nombre).
- **Fuzz largo (`FullyQualifiedName~Fuzz&Categoria=TiempoReal`):** 3 corridas en Linux, 2 de 2 cada vez, unos 39 s por corrida.
- **`Categoria=TiempoReal` entera:** 3 de 7, igual que en main (lo corrí en main: 39 s). Las 4 de `ProcessTests` fallan por el mismo motivo de plataforma.
- **Módulo RAW:** compila en Linux.

## Pruebas omitidas

- **Antes (main):** 7 omitidas en Linux y 5 en Windows. Las 5 de `ProtocolDefectTests` por defecto y las 2 de `RegionFuzzTests` solo en Linux, por plataforma («Named maps are not supported»).
- **Después:** 2 en Linux y 0 en Windows. Quedan solo las 2 de `RegionFuzzTests`, por plataforma.
- Ninguna omisión nueva.

## Decisiones

- **Cancelar a mitad de una escritura corta la conexión.** No hay forma de saber cuántos bytes salieron, y un marco cortado haría que el otro lado leyera basura. Antes no pasaba porque la escritura del editor ignoraba el token del llamador. Con un módulo sano es raro: el módulo siempre lee, y un mensaje de hasta 1 MiB sale enseguida. Si pasa, el `ModuleManager` vuelve a abrir el módulo en la llamada siguiente.
- **El anfitrión también cierra si su envío de 1 s vence a mitad de la escritura.** Antes dejaba un marco cortado y el editor cortaba por mensaje malformado; ahora la conexión termina limpia. Si vence esperando el candado no cambia nada.
- **`WriteTimeout` de 10 s**, el mismo plazo que el `PingTimeout` del `ModuleManager`. Cuenta desde que se tiene el candado, para que una operación que espera turno no venza por culpa de la que escribe; esa vence antes y la desbloquea.
- **`DisposeAsync` no tiene un plazo propio para el `shutdown`:** usa `WriteTimeout`. Con un módulo que no lee, `ModuleProcess.DisposeAsync` puede tardar hasta 10 s más antes de matar el proceso.
- **Quien canceló recibe `OperationCanceledException` con su token,** aunque el bucle de lectura haya fallado antes al ver el flujo cerrado. Encontré esa carrera en una de las corridas.

## Observaciones sin tocar

- **Carrera que ya existe en main:** tras un `DisposeAsync` normal, `Completion` puede terminar con `ModuleDisconnectedException` («El módulo cerró la conexión») en vez de limpio. Pasa si el anfitrión cierra tras el `shutdown` antes de que corra `Fail(null)`. `WriteTimeoutTests` no lo comprueba por eso. Valdría una tarea aparte si la app mira `Completion`.
- **`InvokeAsync`:** si la conexión ya cayó cuando se manda el `cancel`, sale `ModuleDisconnectedException` en vez de la cancelación. Así era antes.

## Posibles choques

- Toca `ModuleHost.cs`, `ModuleClient.cs`, `MessageChannel.cs` y `Messages.cs`.
- La API pública solo suma `ModuleClientOptions.WriteTimeout`. `ModuleConnection` usa `options.ClientOptions with { … }`, así que no le afecta.
- Toca `docs/protocolo-modulos.md` y `modules/Rodaje.Module.Raw/Program.cs`, este último por la aclaración del coordinador. En el módulo RAW solo cambia la línea del `ModuleHost`.
- No toca `ProcessTests.cs` ni `ConnectionTests.cs`.

## Qué comprobar en Windows

1. `ABRIR.bat` en verde: `Rodaje.Modules.Protocol.Tests` sin omitidas por defecto. Las 2 de `RegionFuzzTests` y las de `ProcessTests` tienen que pasar.
2. Con `RODAJE_PRUEBAS_LARGAS=1`, el fuzz largo y las gemelas `TiempoReal` de `ProcessTests` en verde.
3. En la app, cancelar desde Trabajos una transcripción o un revelado RAW a mitad: termina cancelado y la siguiente llamada funciona.
4. Importar una carpeta CinemaDNG con el equipo ocupado: el módulo RAW abre, ahora con el plazo de 30 s por defecto del anfitrión.
