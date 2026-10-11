# Protocolo de módulos v1: guía para terceros

Cómo escribir un módulo que Rodaje pueda abrir: un programa aparte que habla con el editor por una tubería con mensajes JSON y recibe cuadros y audio por memoria compartida. Describe la versión 1 del protocolo tal como está en el código de `src/Rodaje.Modules.Protocol/` (decisión D-031, plan en [fase-7.md](fase-7.md)); cada dato cita el archivo de donde sale.

**Estado (11 de octubre de 2026).** El protocolo, el anfitrión para módulos en C# (`ModuleHost`) y el módulo de ejemplo (`modules/Rodaje.Module.Echo/`) existen y tienen pruebas (`tests/Rodaje.Modules.Protocol.Tests/`). **La app todavía no abre módulos**: el `ModuleManager` que los arranca, vigila y cierra es la ronda A de la fase 7 y no está hecho, y el instalador de paquetes solo guarda los módulos sin ejecutarlos ([formato-paquete.md](formato-paquete.md#módulos-de-ia)). Lo que aquí se marca como «previsto» viene del plan y puede cambiar.

**Ejemplos JSON.** Todos los mensajes de esta guía son marcos reales: salen de conectar el `ModuleClient` (el lado del editor) con un `ModuleHost` que registra las funciones del módulo de eco, con el serializador del protocolo, y copiar lo que pasó por el cable. Solo cambian los valores de ejemplo (`appVersion`, `culture`, parámetros). El serializador de .NET escribe las letras no ASCII como escapes (`"parámetros"`); en esta guía se muestran así, tal cual salen. Las dos formas son JSON válido y el lector acepta cualquiera.

## Índice

1. [Transporte](#transporte)
2. [Saludo y versión](#saludo-y-versión)

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
