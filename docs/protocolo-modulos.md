# Protocolo de módulos v1: guía para terceros

Cómo escribir un módulo que Rodaje pueda abrir: un programa aparte que habla con el editor por una tubería con mensajes JSON y recibe cuadros y audio por memoria compartida. Describe la versión 1 del protocolo tal como está en el código de `src/Rodaje.Modules.Protocol/` (decisión D-031, plan en [fase-7.md](fase-7.md)); cada dato cita el archivo de donde sale.

**Estado (12 de octubre de 2026).** El protocolo, el anfitrión para módulos en C# (`ModuleHost`) y el módulo de ejemplo (`modules/Rodaje.Module.Echo/`) existen y tienen pruebas (`tests/Rodaje.Modules.Protocol.Tests/`). **La app ya abre módulos** con el `ModuleManager` (ronda A de la fase 7, `src/Rodaje.App/Services/Modules/`): los busca en disco (`ModuleCatalog`), abre un proceso por módulo con la primera llamada, lo vigila y lo cierra; sus reglas están en [Límites](#límites). Lo que aquí se marca como «previsto» viene del plan y puede cambiar.

**Ejemplos JSON.** Todos los mensajes de esta guía son marcos reales: salen de conectar el `ModuleClient` (el lado del editor) con un `ModuleHost` que registra las funciones del módulo de eco, con el serializador del protocolo, y copiar lo que pasó por el cable. Solo cambian los valores de ejemplo (`appVersion`, `culture`, parámetros). El serializador de .NET escribe las letras no ASCII como escapes (`"parámetros"`); en esta guía se muestran así, tal cual salen. Las dos formas son JSON válido y el lector acepta cualquiera.

## Índice

1. [Transporte](#transporte)
2. [Saludo y versión](#saludo-y-versión)
3. [Mensajes](#mensajes)
4. [Progreso](#progreso)
5. [Cancelación](#cancelación)
6. [Errores](#errores)
7. [Memoria compartida](#memoria-compartida)
8. [Límites](#límites)
9. [Un módulo mínimo](#un-módulo-mínimo)
10. [Empaquetarlo](#empaquetarlo)
11. [Licencia de tu módulo](#licencia-de-tu-módulo)

## Transporte

### El proceso

- El editor abre el ejecutable del módulo como un proceso aparte (`ModuleProcess.StartAsync`, `ModuleProcess.cs`). En un paquete, ese ejecutable es `module.entry` del `paquete.json` ([formato-paquete.md](formato-paquete.md#módulos-de-ia)).
- Al final de la línea de órdenes agrega dos argumentos: `--rodaje-pipe <nombre>` (`ModuleHost.PipeArgument`, `ModuleHost.cs`). Los argumentos propios que pase el editor van antes; busca la bandera en cualquier posición, como hace `ModuleHost.RunAsync(string[])`.
- El directorio de trabajo es la carpeta del ejecutable, sin ventana (`CreateNoWindow`) (`ModuleProcess.cs`).
- **La salida estándar no sirve para nada:** el editor la lee y la descarta. La salida de errores sí se guarda: los últimos 2048 caracteres (`StderrTail`) se agregan al mensaje de error cuando el proceso termina («El módulo terminó con el código 3. …», `ModuleExitedException`, `ModuleExceptions.cs`). La entrada estándar no se redirige: no la uses.
- Si alguien abre el módulo a mano, sin `--rodaje-pipe`, el anfitrión de C# escribe en la salida de errores «Este programa es un módulo de Rodaje (…); lo abre el editor.» y sale con el código 64. Códigos de salida de `ModuleHost.RunAsync(string[])`: **0** bien, **2** error de protocolo o de conexión, **64** sin tubería (`ModuleHost.cs`; el 64 lo fija la prueba `Started_by_hand_the_module_explains_itself_and_exits`).
- Si el proceso termina antes de conectarse, el editor falla con el código de salida y la cola de la salida de errores. Si termina a mitad de una llamada, la llamada falla en menos de 1 s (criterio de [fase-7.md](fase-7.md), prueba `A_module_that_dies_mid_call_fails_the_call_fast`).

### La tubería

- Es una **tubería con nombre de Windows** que crea el editor (`NamedPipeServerStream`, `ModuleProcess.cs`): nombre `Rodaje.Mod.pipe.<guid>` (el guid en 32 cifras hexadecimales), en modo bytes, de una sola instancia, asíncrona, con **`CurrentUserOnly`** (solo el usuario actual la abre) y búferes de entrada y salida de 64 KB (`PipeBufferBytes = 64 * 1024`).
- El módulo se conecta como cliente a `\\.\pipe\<nombre>`. El anfitrión de C# lo hace con `NamedPipeClientStream(".", nombre, InOut, Asynchronous | CurrentUserOnly)` y espera hasta 10 s; antes comprueba que el nombre tenga de 1 a 128 caracteres entre letras y dígitos ASCII, `.`, `-` y `_` (`SharedMemoryRegion.IsValidName`, `Limits.RegionName` en `Messages.cs`).
- El editor espera la conexión hasta `ModuleClientOptions.HandshakeTimeout`, 10 s por defecto (`ModuleClient.cs`); si no llega, falla con «El módulo no se conectó en 10 s.» y mata el proceso.
- El protocolo y la memoria compartida son **solo de Windows** en esta versión: las clases llevan `[SupportedOSPlatform("windows")]`. El código de los mensajes y el marco no depende del sistema (con él se generaron los ejemplos de esta guía sobre otro transporte), pero el editor solo abre módulos por tubería de Windows.

### El marco

Cada mensaje es un **marco**: 4 bytes con el largo del JSON como entero de 32 bits con signo en little-endian, y después el JSON en UTF-8 (`MessageChannel`, `MessageChannel.cs`).

```
66 00 00 00  {"type":"hello","minProtocol":1,"maxProtocol":1,"app":"rodaje","appVersion":"0.7.0","culture":"es-CO"}
└ 102 bytes ┘ └──────────────────────────── 102 bytes de JSON en UTF-8 ────────────────────────────────────────────┘
```

- El largo va de 1 a `ProtocolVersion.MaxFrameBytes` = 1 048 576 bytes (1 MiB, `1 << 20`) sin contar la cabecera. Un largo de 0, negativo o mayor corta la conexión sin reservar memoria (`ReceiveAsync`; prueba `Frame_lengths_out_of_range_are_rejected_without_allocating`). Quien envía tampoco manda un mensaje más grande: `SendAsync` lanza `ModuleProtocolException`.
- El JSON es un objeto con el campo **`type`**, que puede ir en cualquier posición (`AllowOutOfOrderMetadataProperties`; prueba `Type_can_come_after_other_properties`). Los nombres de campo van en camelCase.
- Los campos opcionales con valor nulo no se escriben (`DefaultIgnoreCondition = WhenWritingNull`). Al leer, un campo obligatorio que falta o un `null` donde no se admite hacen inválido el mensaje (`RespectRequiredConstructorParameters`, `RespectNullableAnnotations`).
- **UTF-8 estricto:** bytes que no son UTF-8 válido hacen inválido el mensaje (prueba `Invalid_utf8_is_rejected`). Anidamiento máximo: 32 niveles en todo el mensaje, `parameters` y `value` incluidos (`MaxDepth = 32`; prueba `Deep_nesting_is_rejected`).
- Las escrituras de un lado se serializan (un marco no se mezcla con otro) y cada marco se escribe entero con un `Flush`.
- Si la conexión se corta entre dos marcos, es un cierre normal; si se corta a mitad de uno (en la cabecera o en el JSON), es un error de protocolo (prueba `A_truncated_frame_is_an_error_but_a_clean_end_is_not`).

**Compatibilidad hacia delante** (`MessageChannel.Parse`):

- Un campo desconocido se ignora: `{"type":"ping","id":2,"nuevo":{"a":[1]}}` se lee como un `ping` (prueba `Unknown_fields_are_ignored`).
- Un mensaje con un `type` desconocido se salta y se lee el siguiente, siempre que ese `type` sea un identificador válido (minúsculas, dígitos, `.` y `-`; ver [Límites](#límites)). `{"type":"futuro","x":1}` se salta; `{"type":"Futuro"}` es un mensaje malformado y corta la conexión.
- Cualquier otro mensaje inválido corta la conexión (ver [Errores](#errores)).

## Saludo y versión

Lo primero que pasa por la tubería es el saludo, siempre en este orden: el editor manda `hello` y el módulo contesta `welcome`, o un `error` si no comparten versión. El módulo no manda nada antes de recibir `hello`.

### `hello` (editor → módulo)

```json
{"type":"hello","minProtocol":1,"maxProtocol":1,"app":"rodaje","appVersion":"0.7.0","culture":"es-CO"}
```

| Campo | Tipo | Qué es |
|---|---|---|
| `minProtocol`, `maxProtocol` | entero | El rango de versiones del protocolo que entiende el editor. `minProtocol` ≥ 1 y `maxProtocol` ≥ `minProtocol`. |
| `app` | texto, hasta 64 | El programa que abre el módulo. Por defecto `rodaje` (`ModuleClientOptions.App`). |
| `appVersion` | texto, hasta 64 | Su versión. El valor por defecto de la biblioteca es `0.0.0`; la app manda la versión de su ensamblado con tres números (`ModuleManagerOptions.ClientOptions`, armado en `MainWindow.xaml.cs`). |
| `culture` | texto, hasta 64, opcional | El idioma de la interfaz, para que el módulo responda en él (`es-CO` en el ejemplo). Puede faltar. |

Fuente: `HelloMessage` en `Messages.cs` y `ModuleClientOptions` en `ModuleClient.cs`. La versión actual es `ProtocolVersion.Current = 1` (`MessageChannel.cs`); el editor manda 1–1 por defecto.

### `welcome` (módulo → editor)

El módulo elige **la mayor versión común**: `min(maxProtocol del módulo, maxProtocol del editor)`, y la acepta si es al menos `max(minProtocol del módulo, minProtocol del editor)` (`ModuleHost.HandshakeAsync`). Contesta con esa versión y con la descripción de lo que ofrece. Este es el `welcome` real del módulo de eco (`modules/Rodaje.Module.Echo/Program.cs`):

```json
{"type":"welcome","protocol":1,"module":{"id":"rodaje-eco","version":"0.1.0","name":"Eco","functions":[{"id":"eco","name":"Devolver los parámetros","cancellable":true,"reportsProgress":false},{"id":"contar","name":"Contar con avance","cancellable":true,"reportsProgress":true},{"id":"copiar","name":"Copiar el primer búfer en el segundo","cancellable":true,"reportsProgress":false,"inputs":["video"],"outputs":["video"]},{"id":"morir","name":"Terminar el proceso a mitad de una llamada","cancellable":false,"reportsProgress":false},{"id":"colgar","name":"Ignorar la cancelación","cancellable":false,"reportsProgress":false},{"id":"fallar","name":"Lanzar una excepción","cancellable":true,"reportsProgress":false}],"device":"cpu"}}
```

| Campo | Tipo | Qué es |
|---|---|---|
| `protocol` | entero ≥ 1 | La versión elegida. |
| `module.id` | identificador | Id del módulo (reglas en [Límites](#límites)). |
| `module.version` | texto, hasta 64 | Su versión. El protocolo no exige un formato; el `paquete.json` sí pide `mayor.menor.parche`. |
| `module.name` | texto, hasta 200 | Nombre para mostrar. |
| `module.device` | texto, hasta 200, opcional | Dónde corre (`cpu` en el eco). |
| `module.functions` | lista, hasta 128 | Las funciones, sin ids repetidos ni elementos nulos. Puede estar vacía. |
| `functions[].id` | identificador | El nombre con que el editor la llama en `invoke`. |
| `functions[].name` | texto, hasta 200 | Nombre para mostrar. |
| `functions[].cancellable` | booleano | Si atiende `cancel`. Si falta vale `true`. |
| `functions[].reportsProgress` | booleano | Si manda `progress`. Si falta vale `true`. |
| `functions[].inputs`, `functions[].outputs` | listas opcionales, hasta 16 | Los tipos de búfer que recibe y devuelve. El comentario del código nombra `video`, `audio` y `bytes`; el validador solo exige que cada uno sea un identificador. |

Fuente: `WelcomeMessage`, `ModuleInfo` y `FunctionInfo` en `Messages.cs`. Hoy el editor guarda la descripción (`ModuleClient.Module`, que el `ModuleManager` deja ver en `ModuleCall.Info`) pero **no usa** `cancellable`, `reportsProgress`, `inputs`, `outputs` ni `device` para decidir nada: el `ModuleManager` elige el módulo por `module.functions` del `paquete.json` (`FindByFunction`) y decide si usa la GPU por `module.vramMb`. Tampoco se comprueba todavía que estas funciones coincidan con las del `paquete.json`; qué pasa si no coinciden no está decidido.

### Sin versión común

El módulo contesta con un `error` sin `id` y el código `version-incompatible`, cuyo mensaje nombra los dos rangos, y cierra. Marco real, con un editor que pide 2–3 y el módulo de eco que habla 1–1:

```json
{"type":"error","code":"version-incompatible","message":"El módulo habla el protocolo 1–1 y el editor 2–3."}
```

El editor lo convierte en `ModuleVersionException` con ese mensaje (`ModuleClient.HandshakeAsync`), y el anfitrión de C# sale con el código 2. El editor también rechaza un `welcome` cuya versión quede fuera de su rango («El módulo eligió el protocolo …; el editor habla …»; prueba `A_welcome_with_a_version_outside_the_range_is_refused`).

### Plazos y errores del saludo

- El editor espera el `welcome` hasta `HandshakeTimeout` (10 s; `ModuleClient.cs`). Si no llega: «El módulo no saludó en 10 s.» (`ModuleProtocolException`). Si el módulo cierra: `ModuleDisconnectedException`. Cualquier otra respuesta que no sea `welcome` ni `error` `version-incompatible` es un error de protocolo («El módulo no respondió al saludo con «welcome».»).
- El anfitrión de C# espera el `hello` hasta su propio `HandshakeTimeout` (10 s; `ModuleHost.cs`). Si lo primero que llega no es `hello`, contesta un `error` `malformed` sin id y cierra. Marco real, tras recibir `{"type":"ping","id":1}` como primer mensaje:

```json
{"type":"error","code":"malformed","message":"Lo primero tiene que ser «hello»."}
```

## Mensajes

Hay 11 tipos (`ModuleMessage` en `Messages.cs`). Cada lado solo puede mandar los suyos: si el editor recibe un tipo que no le toca, corta la conexión («El módulo no manda «…».», `ModuleClient.Dispatch`), y el anfitrión hace lo mismo («El editor no manda «…».», `ModuleHost.RunAsync`).

| `type` | Sentido | Campos | Para qué |
|---|---|---|---|
| `hello` | editor → módulo | `minProtocol`, `maxProtocol`, `app`, `appVersion`, `culture`? | Saludo ([arriba](#saludo-y-versión)). |
| `welcome` | módulo → editor | `protocol`, `module` | Respuesta al saludo. |
| `invoke` | editor → módulo | `id`, `function`, `parameters`?, `buffers`? | Ejecutar una función. |
| `progress` | módulo → editor | `id`, `fraction`, `stage`? | Avance de una llamada ([Progreso](#progreso)). |
| `result` | módulo → editor | `id`, `value`?, `buffers`? | La llamada terminó bien. |
| `error` | módulo → editor | `code`, `message`, `id`? | La llamada falló (con `id`) o la conexión no sirve (sin `id`) ([Errores](#errores)). |
| `cancel` | editor → módulo | `id` | Cancelar una llamada ([Cancelación](#cancelación)). |
| `ping` | editor → módulo | `id` | Comprobar que el módulo responde. |
| `pong` | módulo → editor | `id` | Respuesta al `ping` con el mismo `id`. |
| `log` | módulo → editor | `level`, `text` | Una línea para el registro del editor. |
| `shutdown` | editor → módulo | — | Terminar. |

`?` = opcional. Un `error` sin `id` solo lo manda el módulo en el saludo (`version-incompatible`) o justo antes de cerrar porque el editor mandó algo inválido (`malformed`).

### `invoke` y `result`

El editor elige el `id` de cada llamada: un entero de 64 bits mayor que 0, único en la conexión (el `ModuleClient` usa un contador que empieza en 1 y comparte con los `ping`). `function` es el id de una función del `welcome`. `parameters` es cualquier valor JSON (o falta): su forma la define cada función. Llamada real a la función `eco`, que devuelve sus parámetros:

```json
{"type":"invoke","id":1,"function":"eco","parameters":{"texto":"hola","n":3}}
```
```json
{"type":"result","id":1,"value":{"texto":"hola","n":3}}
```

`value` también es cualquier valor JSON, o falta; un número suelto vale:

```json
{"type":"invoke","id":2,"function":"eco","parameters":1}
```
```json
{"type":"result","id":2,"value":1}
```

Con búferes de memoria compartida (ver [Memoria compartida](#memoria-compartida)), la llamada nombra la región, el desplazamiento, el largo y la forma de los datos. Este `invoke` y su `result` salen del serializador del protocolo (en Linux no hay regiones con nombre para hacer la llamada de verdad); la función `copiar` del eco copia el primer búfer en el segundo y devuelve el segundo:

```json
{"type":"invoke","id":9,"function":"copiar","buffers":[{"region":"Rodaje.Mod.3f1c0d9a8b7e4c2a9d6f5e4b3a2c1d0e","offset":0,"length":8294400,"video":{"width":1920,"height":1080,"stride":7680,"pixelFormat":"rgba8"}},{"region":"Rodaje.Mod.3f1c0d9a8b7e4c2a9d6f5e4b3a2c1d0e","offset":8294400,"length":8294400,"video":{"width":1920,"height":1080,"stride":7680,"pixelFormat":"rgba8"}}]}
```
```json
{"type":"result","id":9,"buffers":[{"region":"Rodaje.Mod.3f1c0d9a8b7e4c2a9d6f5e4b3a2c1d0e","offset":8294400,"length":8294400,"video":{"width":1920,"height":1080,"stride":7680,"pixelFormat":"rgba8"}}]}
```

Reglas (`InvokeMessage`, `ResultMessage` en `Messages.cs`; `ModuleHost.StartAsync`; `ModuleClient.Dispatch`):

- Cada `invoke` recibe **exactamente una** respuesta final: un `result` o un `error` con el mismo `id`. Antes puede haber `progress`.
- Las llamadas corren **en paralelo**: el anfitrión de C# atiende hasta `MaxConcurrentCalls` = 4 a la vez; la quinta recibe `error` `busy` (ver [Errores](#errores)). Las respuestas pueden llegar en cualquier orden; el editor las empareja por `id` (prueba `Concurrent_calls_get_their_own_answers`).
- Un `invoke` con el `id` de una llamada que sigue en curso es un error de protocolo y el anfitrión cierra.
- Una función que está en el `welcome` pero que el anfitrión no tiene registrada contesta `unknown-function`, igual que una que no existe.
- El editor **ignora** `progress`, `result` y `error` de una llamada que no conoce o que ya terminó (prueba `Late_replies_and_progress_for_unknown_calls_are_ignored`).
- Los búferes de un `result` tienen que estar dentro de una región que creó el editor en esa conexión; si no, el editor corta la conexión («El módulo devolvió un búfer fuera de las regiones del editor.»).
- Si la función del anfitrión de C# devuelve un resultado que no pasa la validación (por ejemplo, más de 16 búferes), se manda en su lugar un `error` `internal` «Resultado inválido: …».

### `ping` y `pong`

```json
{"type":"ping","id":2}
```
```json
{"type":"pong","id":2}
```

El módulo contesta cada `ping` con un `pong` del mismo `id`, aunque tenga llamadas en curso (el anfitrión de C# contesta desde el bucle de lectura, sin esperar a las funciones). `ModuleClient.PingAsync` devuelve el tiempo de ida y vuelta (p50 de 83–118 µs en el paso 0, [fase-7.md](fase-7.md#cifras-del-paso-0-11-oct)). A diferencia de los demás, el `id` de `ping` y `pong` no se valida (`PingMessage` y `PongMessage` no tienen `Validate`); el editor usa ids positivos. Solo el editor manda `ping`: un `ping` del módulo corta la conexión. El `ModuleManager` manda un `ping` cada 5 s mientras haya una llamada en curso (`ModuleManagerOptions.PingInterval`); sin respuesta en 10 s (`PingTimeout`) da el módulo por colgado, lo mata y el trabajo falla con `ModuleHungException` (`ModuleConnection.WatchAsync`).

### `log`

```json
{"type":"log","level":"info","text":"Modelo cargado en 840 ms."}
```

(Del serializador; el texto es de ejemplo.) `level` es un identificador; el comentario del código nombra `info`, `warn` y `error`, pero el validador acepta cualquier identificador. `text` tiene hasta 4096 caracteres. El editor lo pasa al evento `ModuleClient.Log`; el `ModuleManager` no lo escucha, así que hoy la app lo descarta (qué hacer con él no está decidido). **El anfitrión de C# no tiene todavía una forma de mandar `log`** (`ModuleCall` no la expone); un módulo escrito en otro lenguaje sí puede mandarlo.

### `shutdown` y cierre

```json
{"type":"shutdown"}
```

El editor lo manda al cerrar el cliente (`ModuleClient.DisposeAsync`). El módulo debe cancelar lo que tenga en curso, liberar lo que use y salir. El anfitrión de C# hace lo mismo si el editor cierra la tubería: cancela las llamadas activas, espera hasta 2 s a que terminen, cierra las regiones de memoria compartida y sale con 0 (`ModuleHost.RunAsync`; si alguna función no atiende la cancelación, deja las regiones abiertas y el proceso sale igual). Del lado del editor, `ModuleProcess.DisposeAsync` espera 2 s a que el proceso termine; si no, lo mata con todo su árbol de procesos y espera otros 2 s.

Una conversación completa, tal como pasó por el cable (saludo, una llamada, un `ping` y el cierre):

```
editor → módulo  {"type":"hello","minProtocol":1,"maxProtocol":1,"app":"rodaje","appVersion":"0.7.0","culture":"es-CO"}
módulo → editor  {"type":"welcome","protocol":1,"module":{"id":"rodaje-eco", … }}
editor → módulo  {"type":"invoke","id":1,"function":"eco","parameters":{"texto":"hola","n":3}}
módulo → editor  {"type":"result","id":1,"value":{"texto":"hola","n":3}}
editor → módulo  {"type":"ping","id":2}
módulo → editor  {"type":"pong","id":2}
editor → módulo  {"type":"shutdown"}
```

## Progreso

```json
{"type":"progress","id":1,"fraction":0.25,"stage":"contando"}
```

- `fraction` es el avance de 0 a 1, finito; `stage`, opcional y de hasta 200 caracteres, nombra la etapa en curso. Un `progress` con `fraction` fuera de 0–1 (o `NaN`) **corta la conexión** (`ProgressMessage.Validate` en `Messages.cs`; pruebas en `MessageTests`).
- **Como mucho uno cada 50 ms por llamada** (D-031). El anfitrión de C# lo cumple solo (`ModuleCall.ReportProgressAsync` en `ModuleHost.cs`): descarta los avances que llegan antes de 50 ms del anterior, salvo el primero, los que cambian de etapa y el de 1, que siempre salen. También lleva `fraction` a 0–1 y cambia `NaN` o infinito por 0. El editor no rechaza avances más seguidos, pero un módulo en otro lenguaje debe respetar el límite.
- Va antes del `result`; el último debería ser el de 1. El editor entrega cada uno al `IProgress<ModuleProgress>` de la llamada (prueba `Progress_arrives_in_order_and_ends_at_one`).
- Solo tiene sentido en funciones con `reportsProgress: true`, aunque el editor no lo comprueba.

Marcos reales de la función `contar` del eco con `{"pasos":4,"msPorPaso":60}` (un paso cada 60 ms, más que el límite, así que salen todos):

```
   36 ms editor → módulo  {"type":"invoke","id":1,"function":"contar","parameters":{"pasos":4,"msPorPaso":60}}
  100 ms módulo → editor  {"type":"progress","id":1,"fraction":0.25,"stage":"contando"}
  161 ms módulo → editor  {"type":"progress","id":1,"fraction":0.5,"stage":"contando"}
  221 ms módulo → editor  {"type":"progress","id":1,"fraction":0.75,"stage":"contando"}
  282 ms módulo → editor  {"type":"progress","id":1,"fraction":1,"stage":"contando"}
  282 ms módulo → editor  {"type":"result","id":1,"value":{"pasos":4}}
```

Y con `{"pasos":20,"msPorPaso":5}` (un paso cada 5 ms): de 20 avances salen 4, separados por al menos 50 ms, y el de 1.

```
  290 ms módulo → editor  {"type":"progress","id":1,"fraction":0.05,"stage":"contando"}
  346 ms módulo → editor  {"type":"progress","id":1,"fraction":0.4,"stage":"contando"}
  402 ms módulo → editor  {"type":"progress","id":1,"fraction":0.75,"stage":"contando"}
  442 ms módulo → editor  {"type":"progress","id":1,"fraction":1,"stage":"contando"}
  442 ms módulo → editor  {"type":"result","id":1,"value":{"pasos":20}}
```

## Cancelación

El editor manda `cancel` con el `id` de la llamada. El módulo deja de trabajar y **confirma** con un `error` de código `cancelled` y el mismo `id`. Marcos reales: el editor cancela `contar` a los 130 ms y la conexión sigue sirviendo para la llamada siguiente.

```
  444 ms editor → módulo  {"type":"invoke","id":1,"function":"contar","parameters":{"pasos":100,"msPorPaso":50}}
  495 ms módulo → editor  {"type":"progress","id":1,"fraction":0.01,"stage":"contando"}
  547 ms módulo → editor  {"type":"progress","id":1,"fraction":0.02,"stage":"contando"}
  574 ms editor → módulo  {"type":"cancel","id":1}
  576 ms módulo → editor  {"type":"error","code":"cancelled","message":"Cancelado.","id":1}
  577 ms editor → módulo  {"type":"invoke","id":2,"function":"eco","parameters":1}
  577 ms módulo → editor  {"type":"result","id":2,"value":1}
```

- **Plazo de gracia:** el editor espera la confirmación hasta `ModuleClientOptions.CancelGrace`, **2 s** por defecto (`ModuleClient.InvokeAsync`). Si no llega, da el módulo por colgado: corta la conexión, falla todas las llamadas en curso con `ModuleDisconnectedException` («El módulo no confirmó la cancelación en 2 s.») y `ModuleProcess` mata el proceso (prueba `A_module_that_ignores_cancel_is_killed_after_the_grace_period`). Una función que no puede detenerse a tiempo debería declararse `cancellable: false`; el editor todavía no usa ese dato: el `ModuleManager` deja cancelar cualquier trabajo desde Trabajos.
- Si en el plazo llega un `result` en vez de la confirmación, el `ModuleClient` devuelve ese resultado; si llega cualquier `error`, la llamada termina como cancelada (`OperationCanceledException`).
- Un `cancel` de una llamada que ya terminó o que no existe se ignora (`ModuleHost.RunAsync`).
- En el anfitrión de C#, `cancel` cancela el `CancellationToken` que recibe la función. Si la función lanza `OperationCanceledException` con ese token cancelado, el anfitrión manda el `error` `cancelled`; si termina antes, manda su `result`. Una `OperationCanceledException` sin que se haya pedido cancelar cuenta como error `internal`.
- En el paso 0 la confirmación tardó una mediana de 0,6 ms entre procesos ([fase-7.md](fase-7.md#cifras-del-paso-0-11-oct)).

## Errores

### Errores de una llamada

Un `error` **con `id`** termina esa llamada y la conexión sigue sirviendo (prueba `Function_errors_do_not_close_the_connection`). Los códigos estables son los de `ErrorCodes` en `Messages.cs`:

| Código | Quién lo manda y cuándo |
|---|---|
| `version-incompatible` | El módulo, en el saludo y sin `id`, si no hay versión común. |
| `unknown-function` | El módulo, si no tiene la función pedida. |
| `invalid-parameters` | La función, si `parameters` o los búferes no le sirven (el eco lo usa en `copiar`). El anfitrión no lo genera solo. |
| `cancelled` | El módulo, para confirmar un `cancel`. |
| `busy` | El módulo, si ya atiende el máximo de llamadas a la vez (4 en el anfitrión de C#). |
| `out-of-memory` | El anfitrión de C#, si la función lanza `OutOfMemoryException` o `InsufficientExecutionStackException`. |
| `buffer-out-of-range` | Un búfer que no se puede usar: región con nombre inválido o que no existe, búfer de otra región, o fuera de los límites de la región (`SharedMemoryRegion.cs`). |
| `malformed` | El módulo, sin `id`, justo antes de cerrar porque el editor mandó algo inválido. |
| `internal` | Cualquier otro fallo. El anfitrión de C# manda «`Tipo`: mensaje» de la excepción, «Resultado inválido: …», o «No se pudo enviar el resultado: …» si el `result` no cabe en un marco de 1 MiB o no se puede serializar (`ModuleHost.SendReplyAsync`; prueba `A_result_over_the_frame_limit_is_answered_with_an_error`). |

`message` es un texto de hasta 4096 caracteres (el anfitrión de C# recorta los más largos con «…»). Escríbelo para una persona, en el idioma de `hello.culture` si puedes: la app mostrará los errores del módulo en lenguaje simple, sin trazas ([fase-7.md](fase-7.md#interfaz)). Marcos reales:

```json
{"type":"error","code":"unknown-function","message":"El m\u00F3dulo no tiene la funci\u00F3n \u00ABnada\u00BB.","id":1}
```
```json
{"type":"error","code":"internal","message":"InvalidOperationException: Fallo a prop\u00F3sito.","id":2}
```
```json
{"type":"error","code":"busy","message":"El m\u00F3dulo est\u00E1 ocupado.","id":2}
```

En un módulo de C#, lanza `ModuleCallException(código, mensaje)` (o cualquier `ModuleException`) y el anfitrión manda ese código y ese mensaje. `code` solo tiene que ser un identificador válido: el editor acepta códigos fuera de la tabla y los pasa tal cual, pero usa los de la tabla para que la app sepa qué hacer.

Del lado del editor (`ModuleClient.Dispatch`, `ModuleExceptions.cs`): `cancelled` se convierte en `OperationCanceledException`; cualquier otro código, en `ModuleCallException` con `Code` y `Message`.

### Errores que cierran la conexión

El editor **desconfía del módulo** (D-031): ante cualquiera de estos casos corta la conexión, falla todas las llamadas en curso con una `ModuleException`, mata el proceso (`ModuleProcess`) y sigue funcionando (prueba `A_hostile_module_fails_the_call_and_closes_without_hurting_the_editor`).

- Un marco con un largo fuera de 1 B–1 MiB, cortado a mitad, o que no es JSON válido en UTF-8.
- Un mensaje que no pasa la validación: campos obligatorios que faltan, tipos equivocados (un `id` como texto, un número que no cabe), valores fuera de [Límites](#límites).
- Un `type` que no es un identificador válido.
- Un mensaje que el módulo no puede mandar (`hello`, `invoke`, `cancel`, `ping`, `shutdown`, o un `welcome` después del saludo).
- Un `error` **sin `id`** después del saludo («El módulo informó un error de conexión: …»).
- Un `result` con búferes fuera de las regiones del editor.
- No confirmar un `cancel` dentro del plazo de gracia.
- El proceso termina o cierra la tubería (`ModuleDisconnectedException`, o `ModuleExitedException` con `ExitCode`).

Excepciones del editor (`ModuleExceptions.cs`), todas derivadas de `ModuleException`, con su `Code`:

| Excepción | `Code` | Qué pasó | ¿Sigue la conexión? |
|---|---|---|---|
| `ModuleCallException` | el que mandó el módulo | La función falló. | Sí |
| `ModuleProtocolException` | `malformed` | El otro lado no cumplió el protocolo, o no saludó a tiempo. | No |
| `ModuleVersionException` | `version-incompatible` | Sin versión común. | No |
| `ModuleDisconnectedException` | `disconnected` | El módulo cerró, se colgó o se cortó la tubería. | No |
| `ModuleExitedException` | `disconnected` | El proceso terminó; trae `ExitCode`. | No |

`disconnected` es solo el código de esas dos excepciones del editor; no está en `ErrorCodes` y no viaja por el cable.

El anfitrión de C# también desconfía del editor: un mensaje inválido le hace mandar un `error` `malformed` sin `id` y salir con el código 2 (prueba `A_hostile_editor_stops_the_host`). Marco real, tras un `invoke` con `"id":0`:

```json
{"type":"error","code":"malformed","message":"Mensaje \u00ABInvokeMessage\u00BB inv\u00E1lido: id de llamada inv\u00E1lido."}
```

## Memoria compartida

Los cuadros y el audio **nunca van por la tubería** (el máximo de un mensaje es 1 MiB): van en regiones de memoria compartida y el mensaje solo las nombra (`SharedMemoryRegion.cs`, D-031).

- **Las regiones las crea siempre el editor** (`ModuleClient.CreateRegion(capacidad)`): un mapeo de archivo con nombre de Windows, `Local\Rodaje.Mod.<guid>` (el guid en 32 cifras hexadecimales; `SharedMemoryRegion.NamePrefix = "Rodaje.Mod."`). En los mensajes, `region` es el nombre **sin** `Local\`. La región vive hasta que el editor la libera (`ReleaseRegion`) o cierra el cliente.
- **El módulo la abre por nombre** con permiso de lectura y escritura (`MemoryMappedFile.OpenExisting(@"Local\" + nombre, ReadWrite)` en `SharedMemoryRegion.Open`). En C#, `ModuleCall.Map(búfer)` hace todo: abre la región la primera vez (y la guarda abierta mientras dure la conexión), comprueba los límites y devuelve un `Span<byte>`. En otro lenguaje, lo equivalente en Win32 es abrir el mapeo por nombre (`OpenFileMappingW`) y mapear una vista (`MapViewOfFile`); esto no se probó con un módulo que no sea de C#.
- El editor **decodifica**: los módulos no abren archivos del usuario ni traen FFmpeg; reciben los cuadros o el audio ya en memoria (D-031).
- El módulo **solo escribe en los búferes de la llamada** (D-031). Lo que devuelve en `result.buffers` tiene que estar dentro de una región que creó el editor en esa conexión y dentro de su capacidad, o el editor corta la conexión. La biblioteca no impide escribir en otro punto de una región abierta: es una regla del protocolo.
- Cada acceso comprueba los límites contra el tamaño real de la región: un búfer que se sale da `buffer-out-of-range` («Búfer *desplazamiento*+*largo* fuera de la región de *N* bytes.», `SharedMemoryRegion.GetSpan`).
- El editor solo puede nombrar en un `invoke` regiones que creó con ese cliente (`ModuleClient.InvokeAsync` lanza `ArgumentException` si no).

### Un búfer (`SharedBufferRef`)

| Campo | Qué es |
|---|---|
| `region` | Nombre de la región: de 1 a 128 caracteres entre letras y dígitos ASCII, `.`, `-` y `_`. |
| `offset` | Desplazamiento en bytes desde el inicio de la región, ≥ 0. |
| `length` | Largo en bytes, de 1 a 2 147 483 647 (`int.MaxValue`); `offset + length` no puede desbordar. |
| `video` | Opcional: forma de un cuadro. |
| `audio` | Opcional: forma de un bloque de audio. |

Un búfer es de video, de audio o de bytes sueltos (sin `video` ni `audio`), nunca de los dos. Como mucho 16 búferes por mensaje. Fuente: `SharedBufferRef.Validate` y `Limits` en `Messages.cs`.

**Video** (`VideoLayout`): `width` y `height` de 1 a 16 384; `stride` (bytes por fila) ≥ `width` × bytes por píxel; `stride` × `height` ≤ `length`. `pixelFormat`:

| `pixelFormat` | Bytes por píxel |
|---|---|
| `rgba8` | 4 |
| `bgra8` | 4 |
| `rgba16f` | 8 |
| `gray8` | 1 |

Las filas van una tras otra cada `stride` bytes desde `offset`. El código no fija el orden de las filas (de arriba abajo o al revés), el espacio de color ni si el alfa va premultiplicado: **no está decidido**. Según el plan, la app mandará los cuadros a la resolución de análisis (512–1024 px) y recibirá máscaras `gray8` ([fase-7.md](fase-7.md#arquitectura)).

**Audio** (`AudioLayout`): muestras `f32` **intercaladas** (`sampleFormat`, el único valor admitido, `SampleFormats.F32`); `sampleRate` de 8000 a 384 000; `channels` de 1 a 32; `frames` (muestras por canal) ≥ 0; `frames` × `channels` × 4 ≤ `length`. Ejemplo del serializador, 1 s estéreo a 48 kHz:

```json
{"type":"invoke","id":10,"function":"ruido","buffers":[{"region":"Rodaje.Mod.3f1c0d9a8b7e4c2a9d6f5e4b3a2c1d0e","offset":0,"length":384000,"audio":{"sampleRate":48000,"channels":2,"frames":48000,"sampleFormat":"f32"}}]}
```

(`ruido` es un nombre de ejemplo de [fase-7.md](fase-7.md#formatos), no una función que exista.)

**Rendimiento** medido en el paso 0 entre dos procesos ([fase-7.md](fase-7.md#cifras-del-paso-0-11-oct)): un cuadro 1080p por referencia cuesta 0,1 ms; escribirlo, copiarlo en el módulo y leerlo, 1,3–1,4 ms (691–750 cuadros/s).

## Límites

Todo lo que llega se valida; un valor fuera de rango hace inválido el mensaje y **corta la conexión** (ver [Errores](#errores)). Fuente: `Limits` y los `Validate` de `Messages.cs`, salvo donde se indica.

| Qué | Límite |
|---|---|
| Un mensaje (JSON, sin la cabecera) | 1 a 1 048 576 bytes (`ProtocolVersion.MaxFrameBytes`, `MessageChannel.cs`) |
| Anidamiento del JSON | 32 niveles (`MaxDepth`, `MessageChannel.cs`) |
| Identificadores (`type` desconocidos, `module.id`, `functions[].id`, `function`, `inputs`/`outputs`, `code`, `level`) | 1 a 64 caracteres: minúsculas ASCII, dígitos, `.` y `-`; sin empezar ni terminar con `.` o `-` y sin dos seguidos (`Limits.Identifier`). Valen `rodaje-eco`, `modulo.vision`, `v2`; no valen `Eco`, `-eco`, `eco-`, `a..b`, `../eco` |
| `app`, `appVersion`, `culture`, `module.version` | hasta 64 caracteres (`MaxId`) |
| `module.name`, `module.device`, `functions[].name`, `progress.stage` | hasta 200 caracteres (`MaxName`) |
| `error.message`, `log.text` | hasta 4096 caracteres (`MaxText`) |
| Funciones en `welcome` | hasta 128, ids sin repetir (`MaxFunctions`) |
| Búferes por mensaje; `inputs` y `outputs` por función | hasta 16 (`MaxBuffers`) |
| `id` de `invoke`, `progress`, `result`, `error`, `cancel` | entero de 64 bits mayor que 0 |
| `id` de `ping` y `pong` | cualquier entero de 64 bits (no se valida) |
| `progress.fraction` | finito, de 0 a 1 |
| `hello.minProtocol`, `welcome.protocol` | ≥ 1; `maxProtocol` ≥ `minProtocol` |
| Nombre de región o de tubería | 1 a 128 caracteres: letras y dígitos ASCII, `.`, `-`, `_` (`Limits.RegionName`) |
| Búfer | ver [Un búfer](#un-búfer-sharedbufferref) |

Los largos de texto se cuentan en unidades UTF-16 de .NET (`string.Length`), no en bytes.

Plazos y cantidades del lado del editor y del anfitrión de C#:

| Qué | Valor | Dónde |
|---|---|---|
| Conectarse a la tubería (editor) | 10 s (`HandshakeTimeout`) | `ModuleProcess.cs`, `ModuleClient.cs` |
| Contestar el saludo con `welcome` (editor) | 10 s (`HandshakeTimeout`) | `ModuleClient.cs` |
| Conectarse a la tubería y recibir `hello` (anfitrión) | 10 s cada uno | `ModuleHost.cs` |
| Confirmar un `cancel` | 2 s (`CancelGrace`) | `ModuleClient.cs` |
| Llamadas a la vez en el anfitrión de C# | 4 (`MaxConcurrentCalls`); después, `busy` | `ModuleHost.cs` |
| Avance | uno cada 50 ms por llamada como mucho | `ModuleHost.cs` (`ModuleCall`) |
| Salir tras `shutdown` antes de que lo maten | 2 s | `ModuleProcess.DisposeAsync` |
| Funciones activas al cerrar la conexión (anfitrión) | 2 s para terminar | `ModuleHost.RunAsync` |
| Enviar la respuesta final si el editor no lee (anfitrión) | 1 s; después se descarta. Un `result` que no se puede escribir se contesta con `error` `internal` | `ModuleHost.TrySendAsync`, `ModuleHost.SendReplyAsync` |
| Cola de la salida de errores en el mensaje de salida | 2048 caracteres | `ModuleProcess.cs` |
| Búfer de la tubería, en cada sentido | 64 KB | `ModuleProcess.PipeBufferBytes` |

Lo que añade el `ModuleManager` de la app (ronda A, [fase-7.md](fase-7.md#arquitectura); `src/Rodaje.App/Services/Modules/`):

| Qué | Valor | Dónde |
|---|---|---|
| Procesos | uno por módulo, abierto con la primera llamada; si muere, la siguiente lo vuelve a abrir | `ModuleConnection.cs` |
| Abrir y saludar | 30 s (`StartTimeout`, en lugar de los 10 s de `HandshakeTimeout`: el primer arranque de un exe recién instalado pasa por el antivirus) | `ModuleManagerOptions` en `ModuleCall.cs` |
| Cerrar un módulo ocioso | tras 2 min sin llamadas (`IdleTimeout`) | `ModuleConnection.EndCall` |
| Vigilancia | `ping` cada 5 s con una llamada en curso (`PingInterval`); colgado si no contesta en 10 s (`PingTimeout`) | `ModuleConnection.WatchAsync` |
| Que ninguno sobreviva al editor | Job Object con «matar al cerrar»; al salir, `shutdown` y después matar | `ModuleJobObject.cs`, `ModuleManager.DisposeAsync` |
| Memoria de video | `module.vramMb` mientras vive el proceso; un solo módulo de 1024 MB o más a la vez, y la suma no pasa de la VRAM dedicada menos 3/8 para la vista previa (uno que no cabe ni solo corre solo); para hacer sitio se cierran módulos ociosos | `VramBudget.cs`, `ModuleManager.ReserveVramAsync` |
| Vista previa | pasa a proxies mientras corre un módulo con `vramMb` > 0 | `IModulePreview` en `ModuleCall.cs` |
| Fallos | un módulo que muere, se cuelga o manda algo inválido solo falla su trabajo, con un aviso que dice que el proyecto no cambió | `ModuleManager.Friendly` |

Cada llamada es un trabajo en Trabajos, con su avance y su botón de cancelar. Los resultados no editan el proyecto: quien llama los convierte en una propuesta (`ModuleProposal`) que la persona aplica en un paso del historial o descarta.

## Un módulo mínimo

### En C#, con el anfitrión de Rodaje

La forma más corta: referenciar `Rodaje.Modules.Protocol`, describir el módulo con `ModuleInfo`, registrar cada función en un `ModuleHost` y llamar a `RunAsync(args)`. El anfitrión hace el saludo, la versión, el marco, la validación, el paralelismo, el límite del avance, la cancelación y la conversión de excepciones en `error`. Plantilla completa: `modules/Rodaje.Module.Echo/`.

`Program.cs`:

```csharp
using System.Text.Json;
using Rodaje.Modules.Protocol;

ModuleInfo info = new("mi-modulo", "1.0.0", "Mi módulo",
[
    new FunctionInfo("contar-palabras", "Contar palabras", ReportsProgress: false),
    new FunctionInfo("invertir", "Invertir los colores", Inputs: ["video"], Outputs: ["video"]),
], Device: "cpu");

ModuleHost host = new(info);

host.Register("contar-palabras", (call, _) =>
{
    if (call.Parameters is not { ValueKind: JsonValueKind.Object } p || !p.TryGetProperty("texto", out JsonElement t) || t.ValueKind != JsonValueKind.String)
        throw new ModuleCallException(ErrorCodes.InvalidParameters, "Falta «texto».");
    int palabras = t.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    using JsonDocument doc = JsonDocument.Parse($$"""{"palabras":{{palabras}}}""");
    return ValueTask.FromResult(new ModuleOutput(doc.RootElement.Clone()));
});

host.Register("invertir", async (call, ct) =>
{
    if (call.Buffers is not [{ Video: { PixelFormat: PixelFormats.Rgba8 } video } buffer])
        throw new ModuleCallException(ErrorCodes.InvalidParameters, "«invertir» necesita un búfer rgba8.");
    for (int y = 0; y < video.Height; y++)
    {
        ct.ThrowIfCancellationRequested();
        Span<byte> row = call.Map(buffer).Slice(y * video.Stride, video.Width * 4);
        for (int i = 0; i < row.Length; i += 4)
        {
            row[i] = (byte)(255 - row[i]);
            row[i + 1] = (byte)(255 - row[i + 1]);
            row[i + 2] = (byte)(255 - row[i + 2]);
        }
        await call.ReportProgressAsync((y + 1.0) / video.Height, "invirtiendo", ct).ConfigureAwait(false);
    }
    return new ModuleOutput(Buffers: [buffer]);
});

return await host.RunAsync(args).ConfigureAwait(false);
```

`MiModulo.csproj` (la ruta del `ProjectReference` depende de dónde tengas la biblioteca):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\rodaje\src\Rodaje.Modules.Protocol\Rodaje.Modules.Protocol.csproj" />
  </ItemGroup>
</Project>
```

Notas:

- Cada función recibe un `ModuleCall` (`Id`, `Function`, `Parameters`, `Buffers`, `Map` y `ReportProgressAsync`) y un `CancellationToken`, y devuelve un `ModuleOutput(Value, Buffers)`. `Register` falla si la función no está en `ModuleInfo.Functions`, y `new ModuleHost(info)` falla si `info` no cumple los [límites](#límites) (`ModuleHost.cs`).
- Para un error con código, lanza `ModuleCallException(ErrorCodes.…, mensaje)`; para cancelar, deja que salga la `OperationCanceledException` del token.
- `MaxConcurrentCalls` (4) y `HandshakeTimeout` (10 s) se cambian al crear el anfitrión (`new ModuleHost(info) { MaxConcurrentCalls = 1 }`). Si tu módulo habla varias versiones, pásalas como `minProtocol` y `maxProtocol` del constructor.
- **Comprobado:** compila sin advertencias con .NET 10 y, conectado a un `ModuleClient` por un socket en Linux, `contar-palabras` devuelve `{"palabras":2}` para `{"texto":"hola mundo"}`, y sin parámetros, o `invertir` sin búfer, dan `invalid-parameters`. `invertir` con un búfer real no se probó: la memoria compartida con nombre solo existe en Windows. Sin `--rodaje-pipe` sale con 64.
- Hoy la biblioteca solo está en este repositorio. Cómo la recibirá un tercero (repositorio propio, paquete NuGet) **no está decidido**: D-031 dice que sale a su propio repositorio con el segundo programa de Dathinel.
- El eco se publica con Native AOT (3,3 MB, arranque y saludo en 31–33 ms; [fase-7.md](fase-7.md#cifras-del-paso-0-11-oct)). Un módulo que no admita AOT puede salir autocontenido con JIT (provisional, pregunta 3 de la fase 7).

### En otro lenguaje

El protocolo es solo el marco y el JSON, así que sirve cualquier lenguaje que abra una tubería de Windows. Este módulo en Python (sin dependencias) atiende las llamadas de una en una: contesta cada `invoke` antes de leer el siguiente mensaje, así que no maneja `cancel`, y por eso declara su función con `cancellable: false`. Un módulo con llamadas largas necesita leer en paralelo para atender `cancel` y `ping`.

```python
import json
import struct
import sys

MAX_FRAME = 1 << 20

INFO = {
    "id": "mi-modulo-py",
    "version": "1.0.0",
    "name": "Mi módulo en Python",
    "functions": [{"id": "contar-palabras", "name": "Contar palabras", "cancellable": False, "reportsProgress": False}],
}


def read_exactly(pipe, n):
    data = b""
    while len(data) < n:
        chunk = pipe.read(n - len(data))
        if not chunk:
            if data:
                raise EOFError("conexión cortada a mitad de un mensaje")
            return None
        data += chunk
    return data


def receive(pipe):
    header = read_exactly(pipe, 4)
    if header is None:
        return None
    (length,) = struct.unpack("<i", header)
    if length <= 0 or length > MAX_FRAME:
        raise ValueError("marco fuera de rango")
    return json.loads(read_exactly(pipe, length).decode("utf-8"))


def send(pipe, message):
    payload = json.dumps(message, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    pipe.write(struct.pack("<i", len(payload)) + payload)
    pipe.flush()


def serve(pipe):
    hello = receive(pipe)
    if not hello or hello.get("type") != "hello":
        send(pipe, {"type": "error", "code": "malformed", "message": "Lo primero tiene que ser «hello»."})
        return 2
    if not hello["minProtocol"] <= 1 <= hello["maxProtocol"]:
        send(pipe, {"type": "error", "code": "version-incompatible",
                    "message": f"El módulo habla el protocolo 1–1 y el editor {hello['minProtocol']}–{hello['maxProtocol']}."})
        return 2
    send(pipe, {"type": "welcome", "protocol": 1, "module": INFO})
    while True:
        message = receive(pipe)
        kind = message and message.get("type")
        if kind is None or kind == "shutdown":
            return 0
        if kind == "ping":
            send(pipe, {"type": "pong", "id": message["id"]})
        elif kind == "invoke":
            call_id, params = message["id"], message.get("parameters")
            if message["function"] != "contar-palabras":
                send(pipe, {"type": "error", "code": "unknown-function", "message": "Función desconocida.", "id": call_id})
            elif not isinstance(params, dict) or not isinstance(params.get("texto"), str):
                send(pipe, {"type": "error", "code": "invalid-parameters", "message": "Falta «texto».", "id": call_id})
            else:
                send(pipe, {"type": "result", "id": call_id, "value": {"palabras": len(params["texto"].split())}})
        # «cancel» y tipos desconocidos se ignoran: este módulo responde cada llamada antes de leer la siguiente.


def main(args):
    if "--rodaje-pipe" not in args or args.index("--rodaje-pipe") + 1 >= len(args):
        print("Este programa es un módulo de Rodaje; lo abre el editor.", file=sys.stderr)
        return 64
    name = args[args.index("--rodaje-pipe") + 1]
    with open(r"\\.\pipe" + "\\" + name, "r+b", buffering=0) as pipe:
        return serve(pipe)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
```

**Comprobado:** `serve()` se probó sobre un socket en Linux contra el `ModuleClient` real: saluda (protocolo 1), `contar-palabras` devuelve `{"palabras":3}`, una función desconocida da `unknown-function`, parámetros malos dan `invalid-parameters`, `ping` responde y `shutdown` lo cierra con 0; con un editor que pide 2–2 contesta `version-incompatible` y el editor lanza `ModuleVersionException`. **No se probó** la apertura de la tubería en Windows (`open(r"\\.\pipe\…", "r+b")`).

Lo mínimo que tiene que hacer cualquier módulo:

1. Leer el nombre de la tubería después de `--rodaje-pipe` y conectarse en menos de 10 s.
2. Esperar `hello`, elegir versión y contestar `welcome` (o `error` `version-incompatible`) en menos de 10 s.
3. Contestar cada `invoke` con un `result` o un `error` con su `id`, y cada `ping` con su `pong`.
4. Confirmar cada `cancel` con `error` `cancelled` en menos de 2 s (o declarar la función `cancellable: false` y terminar rápido).
5. Salir al recibir `shutdown` o cuando se cierra la tubería, en menos de 2 s.
6. No escribir el protocolo por la salida estándar (se descarta) y dejar los mensajes de diagnóstico en la de errores.

## Empaquetarlo

Un módulo se instala como un paquete con `"kind": "module"` y un bloque `module` en su `paquete.json` ([formato-paquete.md](formato-paquete.md#módulos-de-ia)):

```json
"module": { "entry": "bin/modulo-subtitulos.exe", "protocol": 1, "functions": ["subtitulos"] }
```

`entry` es la ruta relativa al ejecutable dentro del paquete, `protocol` la versión del protocolo y `functions` lo que ofrece (`ManifestReader.Module` en `src/Rodaje.Paquetes/ManifestReader.cs`). Se instala en `%LOCALAPPDATA%\Rodaje\complementos\modulos\<id>\`. El `ModuleManager` busca módulos ahí, en `modulos\` junto al ejecutable (los que trae la distribución completa) y, si Rodaje corre desde el repositorio, en su carpeta `modules\`; si un id aparece en varias, gana la versión mayor (`ModuleCatalog.Scan`). Solo abre los que funcionan en `rodaje` y dicen `"protocol": 1`; uno con otra versión del protocolo, o con un `paquete.json` que no se puede leer, queda en la lista de problemas (`ModuleManager.Problems`).

`ModuleCatalog` lee además dos campos de la fase 7 que el lector de manifiestos todavía no expone: `vramMb` (entero; 0 o ausente = solo CPU, acotado a 65 536) y `runtime` (texto de hasta 64 caracteres, guardado pero sin uso por ahora). **Previsto** ([fase-7.md](fase-7.md#formatos)): `models` (modelos con hash, licencia y URL, compartidos entre módulos) y `protocol: 0` para herramientas de línea de órdenes que no hablan este protocolo.

## Licencia de tu módulo

Rodaje se licencia bajo la GNU GPL versión 3 solamente, con términos adicionales de la Sección 7 (archivo `LICENSE`, D-032). El **término 4** de esos términos adicionales, «Excepción para complementos y extensiones», da permiso expreso para hacer interactuar Rodaje con módulos independientes, complementos, efectos o extensiones a través de sus interfaces públicas, puntos de extensión o mecanismos de comunicación entre procesos (IPC/CLI), como este protocolo. Según ese término, los complementos de terceros:

> a) No se considerarán obras derivadas de este software por el mero hecho de conectarse a través de dichas interfaces públicas.
> b) Podrán ser distribuidos, licenciados o comercializados bajo los términos legales y comerciales que su respectivo autor determine (incluidas licencias privativas y de pago).
> c) No están obligados a publicar su propio código fuente bajo la GNU GPLv3.

Es decir: un módulo que habla con Rodaje por este protocolo puede llevar **su propia licencia**, también privativa o de pago. El mismo término aclara que la excepción no autoriza modificar ni distribuir el núcleo de Rodaje sin cumplir la atribución y el copyleft de los términos 1 a 3.

Lo que esta guía **no** resuelve, y queda para Daniel y el abogado ([legal.md](legal.md)):

- Si un módulo que **incluye** la biblioteca `Rodaje.Modules.Protocol` (código de este repositorio, bajo la misma licencia) cuenta como «conectarse a través de las interfaces públicas» o necesita otro permiso. Un módulo que implementa el protocolo por su cuenta, como el ejemplo en Python, solo se comunica por IPC. La licencia con que saldrá la biblioteca en su propio repositorio no está decidida.
- Las licencias de los modelos, voces y bibliotecas que use tu módulo son aparte y las cumples tú.
