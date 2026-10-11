# Protocolo de módulos v1: guía para terceros

Cómo escribir un módulo que Rodaje pueda abrir: un programa aparte que habla con el editor por una tubería con mensajes JSON y recibe cuadros y audio por memoria compartida. Describe la versión 1 del protocolo tal como está en el código de `src/Rodaje.Modules.Protocol/` (decisión D-031, plan en [fase-7.md](fase-7.md)); cada dato cita el archivo de donde sale.

**Estado (11 de octubre de 2026).** El protocolo, el anfitrión para módulos en C# (`ModuleHost`) y el módulo de ejemplo (`modules/Rodaje.Module.Echo/`) existen y tienen pruebas (`tests/Rodaje.Modules.Protocol.Tests/`). **La app todavía no abre módulos**: el `ModuleManager` que los arranca, vigila y cierra es la ronda A de la fase 7 y no está hecho, y el instalador de paquetes solo guarda los módulos sin ejecutarlos ([formato-paquete.md](formato-paquete.md#módulos-de-ia)). Lo que aquí se marca como «previsto» viene del plan y puede cambiar.

**Ejemplos JSON.** Todos los mensajes de esta guía son marcos reales: salen de conectar el `ModuleClient` (el lado del editor) con un `ModuleHost` que registra las funciones del módulo de eco, con el serializador del protocolo, y copiar lo que pasó por el cable. Solo cambian los valores de ejemplo (`appVersion`, `culture`, parámetros). El serializador de .NET escribe las letras no ASCII como escapes (`"parámetros"`); en esta guía se muestran así, tal cual salen. Las dos formas son JSON válido y el lector acepta cualquiera.

## Índice

1. [Transporte](#transporte)
2. [Saludo y versión](#saludo-y-versión)
3. [Mensajes](#mensajes)

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
| `appVersion` | texto, hasta 64 | Su versión. El valor por defecto de la biblioteca es `0.0.0`; el que mandará la app lo fijará el `ModuleManager` (previsto). |
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

Fuente: `WelcomeMessage`, `ModuleInfo` y `FunctionInfo` en `Messages.cs`. Hoy el editor guarda la descripción (`ModuleClient.Module`) pero **no usa** `cancellable`, `reportsProgress`, `inputs`, `outputs` ni `device` para decidir nada: los usará el `ModuleManager` (previsto). Tampoco se comprueba todavía que estas funciones coincidan con `module.functions` del `paquete.json`; qué pasa si no coinciden no está decidido.

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

`?` = opcional. Los mensajes de error del anfitrión de C# van hacia el editor, pero el anfitrión también manda `error` sin `id` justo antes de cerrar por un mensaje malformado del editor.

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

El módulo contesta cada `ping` con un `pong` del mismo `id`, aunque tenga llamadas en curso (el anfitrión de C# contesta desde el bucle de lectura, sin esperar a las funciones). `ModuleClient.PingAsync` devuelve el tiempo de ida y vuelta (p50 de 83–118 µs en el paso 0, [fase-7.md](fase-7.md#cifras-del-paso-0-11-oct)). A diferencia de los demás, el `id` de `ping` y `pong` no se valida (`PingMessage` y `PongMessage` no tienen `Validate`); el editor usa ids positivos. Solo el editor manda `ping`: un `ping` del módulo corta la conexión. **Previsto** para el `ModuleManager` (ronda A): un `ping` cada 5 s mientras haya una llamada en curso; sin respuesta en 10 s, el módulo se da por colgado.

### `log`

```json
{"type":"log","level":"info","text":"Modelo cargado en 840 ms."}
```

(Del serializador; el texto es de ejemplo.) `level` es un identificador; el comentario del código nombra `info`, `warn` y `error`, pero el validador acepta cualquier identificador. `text` tiene hasta 4096 caracteres. El editor lo pasa al evento `ModuleClient.Log`; qué hace la app con él no está decidido. **El anfitrión de C# no tiene todavía una forma de mandar `log`** (`ModuleCall` no la expone); un módulo escrito en otro lenguaje sí puede mandarlo.

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
