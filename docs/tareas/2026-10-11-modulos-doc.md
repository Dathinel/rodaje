# Documentación del protocolo para terceros

Rama: `claude/modulos-doc`. Tarea 26 de la cola.

## Hecho

`docs/protocolo-modulos.md` (nuevo): guía para escribir un módulo de terceros del protocolo v1 (D-031), con estas secciones: transporte (proceso, argumentos, tubería, marco), saludo y versión, los 11 mensajes, progreso, cancelación, errores (códigos, qué corta la conexión, excepciones del editor), memoria compartida (regiones, búferes, video y audio), límites (campos, plazos y cantidades), un módulo mínimo en C# y otro en Python, empaquetado (`paquete.json`) y la licencia del módulo (término 4 de `LICENSE`).

- **Ejemplos JSON reales.** Un programa de usar y tirar, fuera del repo, conectó el `ModuleClient` real con un `ModuleHost` que registra las funciones del eco, sobre un socket Unix en Linux, y registró cada marco en los dos sentidos. Así salieron el saludo, `invoke`/`result`, `ping`/`pong`, `shutdown`, `progress` (con y sin el filtro de 50 ms), la cancelación, `unknown-function`, `internal`, `busy`, `version-incompatible` y `malformed`, con sus tiempos. Los mensajes con búferes y el `log` salen del serializador real del protocolo (el `ProtocolJsonContext` interno, por reflexión), porque en Linux no hay memoria compartida con nombre y el anfitrión de C# no manda `log`. Solo cambian los valores de ejemplo (`appVersion`, `culture`, parámetros, nombre de región).
- **Ejemplo en C#** (`ModuleHost`, funciones `contar-palabras` e `invertir`): compila sin advertencias con .NET 10.0.401; conectado a un `ModuleClient` por socket da `{"palabras":2}` e `invalid-parameters` donde debe; sin `--rodaje-pipe` sale con 64. El `.csproj` del texto es el mismo que se compiló, salvo la ruta del `ProjectReference`.
- **Ejemplo en Python** (sin dependencias, de una llamada a la vez, sin `cancel`): `serve()` probado sobre un socket contra el `ModuleClient` real: saludo, resultado, `unknown-function`, `invalid-parameters`, `ping`, `shutdown` (sale con 0) y `version-incompatible` (sale con 2; el editor lanza `ModuleVersionException`). Los bloques de código del documento se compararon con los archivos probados y son iguales.
- Cada dato cita su archivo (`Messages.cs`, `MessageChannel.cs`, `ModuleClient.cs`, `ModuleHost.cs`, `ModuleProcess.cs`, `SharedMemoryRegion.cs`, `ModuleExceptions.cs`) o la prueba que lo fija. Lo que es del plan y no tiene código va marcado como «previsto»; lo que no está decidido, como «no está decidido».
- No se tocó código ni pruebas: omitidas antes 0 nuevas y después 0.

## Pendiente

- Probar en Windows el ejemplo de Python (abrir `\\.\pipe\<nombre>` con `open(..., "r+b", buffering=0)`) y la función `invertir` del ejemplo de C# con una región real. Aquí no se pudo: la tubería y la memoria compartida con nombre son de Windows.
- Cuando exista el `ModuleManager` (ronda A), actualizar las partes «previsto»: `appVersion` que manda la app, uso de `cancellable`/`reportsProgress`/`inputs`/`outputs`/`device`, ping de vigilancia, cierre por ocio, Job Object, qué hace la app con `log`.
- Cuando se decida cómo sale la biblioteca a su propio repositorio (D-031), cambiar la ruta del `ProjectReference` del ejemplo.

## Decisiones

- Los ejemplos se muestran tal cual los escribe el serializador de .NET, con las letras no ASCII como escapes (`ó`), en lugar de pasarlos a letras: así son copia exacta del cable. La guía explica que las dos formas valen.
- La sección de licencia cita literalmente los incisos a), b) y c) del término 4 y la frase sobre el núcleo; no dice nada más allá de `LICENSE`. Lo dudoso queda como pregunta (abajo).
- Se incluyó un ejemplo en otro lenguaje (Python) porque una guía para terceros no debería obligar a usar C#; queda marcado qué parte no se probó.

## Cosas que encontré al documentar (no corregidas: fuera de los caminos)

1. **`ModuleHost` no puede mandar `log`.** `ModuleCall` no lo expone y el anfitrión tampoco, aunque el editor sí lo recibe (`ModuleClient.Log`). Un módulo de C# hoy no tiene forma de escribir en el registro del editor.
2. **El `id` de `ping` y `pong` no se valida** (`PingMessage`/`PongMessage` no tienen `Validate`); los demás ids tienen que ser > 0. Inofensivo, pero distinto del resto.
3. **`level`, `inputs`/`outputs` y `code` aceptan cualquier identificador**, aunque los comentarios nombran `info`/`warn`/`error` y `video`/`audio`/`bytes`, y los códigos de `ErrorCodes`. Si se quiere una lista cerrada, hay que decidirlo antes de que haya módulos de terceros.
4. **Formato de los cuadros sin fijar:** orden de las filas, espacio de color y alfa premultiplicado o no. Conviene decidirlo antes del agente H (`modulo-vision`).
5. **Comprobación de límites del lado del módulo:** `SharedMemoryRegion.Open` toma como capacidad la de la vista (`_view.Capacity`), que en Windows se redondea a la página; el editor comprueba contra la capacidad exacta. El módulo podría aceptar un búfer que se sale hasta casi 4 KB del final de la región, y el editor lo rechazaría al volver en `result` (corta la conexión). A comprobar en Windows.
6. **Licencia de la biblioteca para módulos privativos:** no está claro si un módulo que incluye `Rodaje.Modules.Protocol` (GPL-3.0-only, con este repositorio) entra en el término 4 o necesita otro permiso. Pregunta para Daniel y el abogado (`docs/legal.md`); la guía lo deja dicho como no resuelto.
7. Que las funciones del `welcome` coincidan con `module.functions` del `paquete.json` no se comprueba en ningún sitio; tampoco está decidido qué hacer si no coinciden.

## Choques

Ninguno. Solo archivos nuevos: `docs/protocolo-modulos.md` y esta entrega.

## CI

Desde las 18:52 UTC del 11-oct todos los trabajos de GitHub Actions fallan en unos 3 s sin logs (según el coordinador, parece la facturación). No es por este PR, que solo toca documentación; no se investigó.

## Qué comprobar (Daniel, en Windows)

- Leer la guía de punta a punta: ¿se entiende sin conocer el código?
- Si se quiere, correr el ejemplo de Python como módulo con el eco del paso 0 como modelo (hace falta un editor que lo abra; hoy solo las pruebas de `Rodaje.Modules.Protocol.Tests` abren módulos).
- Responder la pregunta 6 (licencia de la biblioteca) y, si se decide, el punto 4 (formato de los cuadros).
