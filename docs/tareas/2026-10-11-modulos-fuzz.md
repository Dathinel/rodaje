# Fuzz del protocolo de módulos

Rama: `claude/modulos-fuzz` (tarea 25 de la cola).

## Hecho

Archivos nuevos en `tests/Rodaje.Modules.Protocol.Tests/Fuzz/`. Del código solo se tocó `src/Rodaje.Modules.Protocol/MessageChannel.cs`, para corregir el defecto 6; Daniel amplió los caminos de la tarea para eso tras revisar el PR.

- **`FuzzSupport.cs`**: cada caso tiene su semilla (`BaseSeed` 20261011 + n). Los casos corren en paralelo, de 8 en 8, y si alguno falla se informa su semilla para repetirlo solo. Cada paso tiene un tope de 10 s (`CaseCap`) y pasarlo cuenta como cuelgue. Incluye un lector de marcos que tolera cortes, una lectura continua para que el otro lado nunca se bloquee al escribir, y `WindowsFact`.
- **`FuzzFrames.cs`**: generador determinista de mensajes y marcos en los dos sentidos. Cubre:
  - todos los tipos del protocolo, más tipos desconocidos;
  - ids de llamadas en curso, ids repetidos y fuera de rango (`0`, `-1`, `2^63`, `"1"`, `1e400`);
  - versiones raras en `hello` y `welcome`, y textos justo en el límite y uno por encima;
  - `\ud800` y `\u0000`, avances fuera de 0–1 (`NaN`, `1e308`) y anidado de 28 a 40 niveles;
  - listas de búferes con regiones ajenas o nombres inválidos, rangos que desbordan y formatos de video o audio raros;
  - campos que faltan, sobran, se repiten o van antes que `type`;
  - mutaciones de bytes, UTF-8 inválido, BOM, JSON que no es objeto y mensajes de 1 MiB ± 8 bytes;
  - marcos con el largo en 0, negativo, más de 1 MiB, mayor o menor que el contenido.

  `ValidFrame` da mensajes que `Parse` acepta pero con valores al azar, para llegar más hondo que con basura.
- **`MessageFuzzTests.cs`**, sin tubería:
  - 20 000 cargas por sentido contra `MessageChannel.Parse`: solo puede salir un mensaje válido, `null` (tipo desconocido) o `ModuleProtocolException`. Lo aceptado se vuelve a escribir y se lee igual.
  - 2000 flujos de marcos contra `ReceiveAsync`: terminan limpios o con `ModuleProtocolException`.
- **`ClientFuzzTests.cs`**: un módulo hostil escrito a mano habla con el `ModuleClient` real por una tubería real.
  - El módulo saluda bien o con un `welcome` raro, manda hasta 13 marcos y luego cierra, se calla, manda basura o contesta todo bien.
  - El cliente hace de 1 a 3 llamadas con cancelación de 20 a 300 ms, a veces un ping, y luego `DisposeAsync` y `Completion`.
  - Se exige que nada pase del tope y que solo salgan `ModuleException` u `OperationCanceledException`. Un resultado aceptado no trae búferes (el cliente no creó regiones) y ningún avance fuera de 0–1 llega al llamador.
  - Un cuelgue falla mostrando el estado interno del cliente (leído por reflexión: fallo, `_stop`, bucle de lectura, llamadas y pings pendientes, candado), el del módulo hostil y cómo terminó el caso. No se tolera ningún defecto.
  - `Seeds_that_hung_on_windows_finish` corre 20 veces cada semilla que se colgaba en Windows: 20261282, 20261032, 21261787, 21261056 y 21261076.
  - Hay 300 casos en la suite normal y 5000 con `Categoria=TiempoReal`.
- **`HostFuzzTests.cs`**: un editor hostil habla con el `ModuleHost` real, el de las funciones `eco`, `contar`, `fallar` y `copiar` o sin funciones.
  - Versiones al azar en los dos lados. El editor manda hasta 19 marcos: llamadas con ids nuevos o repetidos, `cancel` y `ping` a ids al azar, búferes de regiones que no existen y basura.
  - Termina cerrando, pidiendo `shutdown`, mandando basura o esperando antes de cerrar.
  - Se exige que `RunAsync` termine dentro del tope, devuelva un `bool` y no lance. Todo lo que escribe el módulo tiene que ser un mensaje válido que el editor acepte.
  - Hay 300 casos normales y 5000 con `TiempoReal`.
- **`RegionFuzzTests.cs`** (solo Windows, con `WindowsFact`), con memoria compartida real y capacidades múltiplos de 4096:
  - Un módulo hostil devuelve un búfer al azar. El cliente lo acepta solo si está entero en una región suya; si no, `ModuleProtocolException`.
  - `copiar` con dos búferes al azar: copia, o `buffer-out-of-range` si uno se sale, o `internal` si el destino es corto. La conexión sigue sirviendo.
  - Son 200 casos cada una.
- **`ProtocolDefectTests.cs`**: una prueba de reproducción por cada uno de los seis defectos de abajo.
  - Las de los defectos 1–5 están omitidas con `Skip = "Defecto: …"`. La del 6 es de regresión.
  - Todas tienen tope de tiempo: si el defecto vuelve, fallan en vez de colgarse.
  - Comprobé que las seis fallaban con el código sin corregir.

Tiempos en Linux: la suite normal del fuzz tarda unos 4 s, y las dos pruebas `TiempoReal` unos 25 s juntas. Con el arreglo del defecto 6 corrí el proyecto completo 7 veces y el fuzz largo 4 veces, todo en verde (salvo los 8 fallos de solo Windows que ya estaban).

## Defectos encontrados (1–5 sin corregir; 6 corregido)

Los seis se reprodujeron en Linux antes de declararlos.

1. **Un `cancel` que llega cuando la llamada termina cierra la conexión.**
   - Dónde: `ModuleHost.RunAsync`, en `case CancelMessage`.
   - Qué pasa: hace `active.TryGetValue` y luego `CancelAsync()`. Entretanto, la tarea de la llamada ya hizo `TryRemove` y `cts.Dispose()`. La `ObjectDisposedException` cae en el `catch` de «el editor cerró», así que `RunAsync` devuelve `true` y corta todas las llamadas en curso.
   - Le puede pasar a un `ModuleClient` normal que cancela una llamada que estaba por terminar. Pasó en 3 a 12 de cada 200 rondas.
   - Prueba: `Cancel_racing_the_end_of_a_call_does_not_close_the_connection`.
2. **`RunAsync` lanza `ObjectDisposedException` al cerrar.**
   - Dónde: el `finally` de `ModuleHost.RunAsync`, por la misma carrera con `CancelAsync`.
   - Qué pasa: si al cerrar (por `shutdown` o porque el editor se fue) una llamada termina a la vez, la excepción sale de `RunAsync`. También se saltan la espera de las demás llamadas y la liberación de las regiones. En un módulo de verdad, el proceso terminaría con una excepción no controlada en vez de con 0.
   - Pasó en unas 6 de cada 300 rondas.
   - Prueba: `Shutdown_with_calls_finishing_ends_the_host_without_throwing`.
3. **Una respuesta de más de 1 MiB se pierde en silencio.**
   - Dónde: `ModuleHost.TrySendAsync`.
   - Qué pasa: `SendAsync` lanza `ModuleProtocolException` y `TrySendAsync` la traga, así que no sale ni `result` ni `error`. La llamada del editor espera para siempre o hasta que la cancele, aunque la conexión siga viva (el ping responde).
   - Prueba: `A_result_over_the_frame_limit_is_answered_with_an_error`.
4. **`Parse` acepta un sustituto UTF-16 suelto escapado (`"\ud800"`) dentro de `parameters` o `value`.**
   - Por qué: esos campos son `JsonElement` y nadie revisa su texto. D-031 pide «UTF-8 estricto».
   - Qué pasa: el mensaje no se puede volver a escribir. Con `eco`, la respuesta lanza `JsonException`, que `TrySendAsync` no traga, y el editor no recibe respuesta. Si el editor cierra mientras esa llamada sigue en `active`, la `JsonException` sale además de `RunAsync` por el `Task.WhenAll` del `finally`. Lo halló la corrida larga del fuzz con la semilla 22262352.
   - Del lado del editor, un módulo puede colar el mismo valor en `result.value`.
   - Prueba: `A_lone_surrogate_in_parameters_is_rejected`.
5. **Un módulo que deja de leer cuelga al editor.**
   - Qué pasa: cuando se llena el búfer de la tubería, `ModuleClient.SendAsync` queda bloqueado escribiendo con `_stop.Token` y no con el token del llamador, y nadie llama a `Fail`. Entonces:
     - `InvokeAsync` ignora su cancelación;
     - las demás llamadas esperan el candado de escritura;
     - `DisposeAsync` se queda en el `shutdown`;
     - `ModuleProcess.DisposeAsync` espera a `Client.DisposeAsync` antes de matar el proceso, así que tampoco termina.
   - Se reprodujo en Linux con 8 llamadas de 900 KB canceladas a los 500 ms: seguían colgadas a los 12 s.
   - Prueba: `A_module_that_stops_reading_does_not_hang_the_editor`.
6. **Si el cliente falla mientras una llamada o un ping espera el candado de escritura, esa operación no termina nunca.**
   - Dónde: `ModuleClient.Fail` hace `_stop.Cancel()` y enseguida `_channel.Dispose()`, que desecha el `SemaphoreSlim` de las escrituras (`MessageChannel._writeLock`).
   - Qué pasa: la operación que esperaba el candado con `_stop.Token` procesa su cancelación en otro hilo. Si para entonces el semáforo ya está desechado, `SemaphoreSlim` no la saca de la cola y su tarea queda pendiente para siempre, aunque el llamador haya pasado su propia cancelación. Lo comprobé aparte con `WaitAsync(token)`, `Cancel()` y `Dispose()` seguidos: 48 de 50 esperas quedaron colgadas.
   - Así falló el fuzz del cliente en la CI de Windows: 9 y 11 de 300 casos, todos con ping. El estado era siempre el mismo: el cliente ya había fallado (`failure=ModuleProtocolException`, `stop=True`, bucle de lectura terminado), el ping seguía en `_pings` y el candado estaba en 0. Ejemplo: semilla 20261032. En Linux esas semillas no fallan, porque las escrituras terminan antes.
   - Reproducción en Linux: una tubería cuyas escrituras esperan hasta que las cancelen, como si el búfer estuviera lleno. Una llamada y 8 pings esperan el candado, el módulo manda basura y el cliente falla. Quedó colgado en 45 y 46 de 50 rondas.
   - No es el defecto 5: allí nadie llama a `Fail`; aquí `Fail` ya corrió y la operación sigue colgada.
   - Prueba de regresión: `Operations_waiting_to_write_finish_when_the_client_fails`.
   - **Corregido** en `MessageChannel.Dispose`, que ahora solo cierra el flujo y ya no desecha el semáforo:
     - Toda espera termina por su token, que en el cliente es `_stop`, ya cancelado.
     - Una escritura que llegue después falla con el flujo cerrado (`ObjectDisposedException`), que los dos lados ya atendían.
     - `Release` ya no lanza sobre un semáforo desechado.
     - El semáforo no tiene nada que liberar mientras nadie pida `AvailableWaitHandle`, y ningún sitio lo pide.
   - **Comprobación:** sin el arreglo, la prueba de regresión colgó 8 operaciones en la primera ronda; con él pasa.
   - **Carreras nuevas, revisadas:**
     - Del lado del módulo, las tareas de las llamadas que escriben después de cerrar el canal ya no reciben `ObjectDisposedException` del semáforo sino del flujo. Es el mismo tipo, y `TrySendAsync` y `ReportProgressAsync` ya lo atendían.
     - Quien espera con un token que nunca se cancela sigue dependiendo de que el que tiene el candado lo suelte. Ese es el defecto 5, que no cambia.
   - **Alcance:** el arreglo no corrige ninguno de los otros cinco defectos.

El fuzz del anfitrión tolera solo los defectos 2 y 4, y los cuenta. El del cliente ya no tolera nada. El 4 se tolera solo si el caso mandó un escape de sustituto. El fuzz de mensajes cuenta aparte los aceptados que no se pueden reescribir por el defecto 4. Al corregirlos se quitan los `Skip` y esos `catch`.

**Observaciones del código, sin prueba propia y sin declararlas defectos:**
- Un `error` con código `cancelled` para una llamada que el editor no canceló llega como `OperationCanceledException`, así que un módulo puede hacer pasar un fallo por una cancelación.
- `ModuleHost` valida el `result` antes de enviarlo, pero no el `error` que sale de una `ModuleException` con un código que no es identificador, ni el `stage` de `ReportProgressAsync` de más de 200 caracteres. El editor cortaría la conexión por un fallo del autor del módulo.

## Pruebas omitidas

- **Antes:** 0 omitidas en `Rodaje.Modules.Protocol.Tests` (83 pruebas).
- **Después:** 103 pruebas (20 nuevas), con 7 omitidas en Linux y 5 en Windows:
  - 5 por defecto concreto del código (1–5), en `ProtocolDefectTests` (arriba);
  - 2 por plataforma, solo en Linux: `RegionFuzzTests`, porque no hay memoria compartida con nombre («Named maps are not supported»).
- Además hay 2 pruebas nuevas con `Categoria=TiempoReal` (las corridas de 5000 casos). La CI las excluye, igual que las demás `TiempoReal`, y `probar.ps1` las corre en su pasada aparte.

## Linux y Windows

- El proyecto apunta a `net10.0-windows`, pero **compila y corre en Linux** (`dotnet test tests/Rodaje.Modules.Protocol.Tests`). Las tuberías con nombre funcionan (son sockets de Unix).
- Sin relación con esta tarea, 8 pruebas que ya existían fallan en Linux por ser de Windows:
  - las 7 de `ProcessTests`, porque `Rodaje.Module.Echo.exe` no existe en Linux;
  - `ConnectionTests.Shared_memory_copy_is_exact_and_bounds_are_checked`, porque usa memoria con nombre.

  La CI de Linux no corre este proyecto; la de Windows sí.
- Todas las pruebas nuevas pasan en Linux, salvo las omitidas.
- Al restaurar en Linux se reescribe `modules/Rodaje.Module.Echo/packages.lock.json` (de `win-x64` a `linux-x64`, por `PublishAot`). No lo subí.

**CI:** el 11 de octubre no arrancó: desde las 18:52 UTC todos los trabajos fallaban en unos 3 s y sin registros, por algo ajeno al PR. Cuando volvió:
- Sobre 84fda79 y 13e5a06, el trabajo de Windows falló por `ClientFuzzTests.A_hostile_module_never_hangs_or_crashes_the_client` (9 y luego 11 de 300 casos). Era el defecto 6, ahora tolerado y contado.
- En esas corridas, en Windows pasó todo lo demás del proyecto: 86 pruebas, entre ellas las 2 de `RegionFuzzTests` y las que ya existían de procesos y memoria compartida, con 5 omitidas por defecto.
- También fallaron `TitleEditorTests` y `TitleRasterizerTests`, que fallan igual en `main` y no son de este PR.
- Sobre e03fe26, el trabajo de Windows terminó en rojo, pero desde aquí no se pueden leer sus registros: el proxy devuelve 403. Daniel informó después que el fuzz del cliente seguía colgándose en Windows en todas las corridas, ahora con `pendiente[2]=Faulted`. Por eso se corrigió el defecto 6 en vez de tolerarlo.
- **Falta ver la CI de Windows sobre el arreglo** (3d2cac9 y lo que sigue).

## Qué comprobar en Windows

1. `dotnet test tests/Rodaje.Modules.Protocol.Tests --filter "FullyQualifiedName~Fuzz&Categoria!=TiempoReal"`. Tienen que pasar:
   - las de mensajes;
   - las de cliente y anfitrión, con `Seeds_that_hung_on_windows_finish`;
   - `Operations_waiting_to_write_finish_when_the_client_fails`;
   - las 2 de regiones, que ya pasaron en la CI.
2. Las largas: `--filter "FullyQualifiedName~Fuzz&Categoria=TiempoReal"`. En Linux tardan unos 25 s.
3. Para confirmar los defectos en Windows, quitar el `Skip` de cada prueba de `ProtocolDefectTests` y ver que falla. El 5 debería fallar antes, porque el búfer de la tubería es de 64 KB.

## Decisiones

- **El tope de tiempo es por paso** (conexión, cada llamada, `DisposeAsync`, `RunAsync`) y no por caso, para que el mensaje diga qué se colgó.
- **El módulo hostil del fuzz siempre lee lo que le llega.** Si no leyera, el defecto 5 colgaría casos al azar. Ese caso tiene su propia prueba.
- **Las regiones usan capacidades múltiplos de 4096.** El módulo abre la región con capacidad 0, así que su `Capacity` es la de la vista y puede ser mayor que la del editor si no es múltiplo de página. Así la prueba no depende de eso.
- **Se mantiene viva la referencia a los dos extremos de la tubería.** En un experimento con `Support.HostileAsync(_ => Task.Delay(Timeout.Infinite))`, el extremo del módulo se cerró solo («El módulo cerró la conexión»). Parece que es porque nada retiene el estado del lambda, y el finalizador cierra la tubería. No afecta a las pruebas existentes, pero conviene saberlo al escribir módulos hostiles.

## Choques

Ninguno.
- Los cambios son archivos nuevos en `tests/Rodaje.Modules.Protocol.Tests/Fuzz/`, el arreglo en `MessageChannel.cs` y esta entrega.
- Desde 2d7db6d, `origin/main` (db55ba7) cambió `ModuleProcess.StartAsync`, que tiene un parámetro `started` nuevo antes del token, y `packages.lock.json` del protocolo. Estas pruebas no usan `ModuleProcess`.
- `main` no tocó `ModuleClient.cs` ni `MessageChannel.cs`.
