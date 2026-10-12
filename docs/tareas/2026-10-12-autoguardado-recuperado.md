# Tarea 55: autoguardado, «(recuperado N)» huérfanos al cancelar la apertura

Rama `claude/autoguardado-recuperado`, PR #69. Sigue a la tarea 34 (`2026-10-12-autoguardado-pruebas.md`).

## Hecho

**Borrar lo escrito si la apertura no sale (`src/Rodaje.App/Services/Autosave/AutosaveService.cs`):**

- Antes, `RecoverOnceAsync` escribía `«nombre» (recuperado).rodaje` y luego llamaba a `EditorSession.OpenProjectAsync`. Si la persona cancelaba el aviso de cambios sin guardar, la apertura devolvía `false` y el archivo quedaba. Como `RecoveredPathNextTo` no pisa lo que existe, cada reintento dejaba otro «(recuperado 2)», «(recuperado 3)»…
- Ahora, si la apertura devuelve `false` (aviso cancelado, editor ocupado, archivo que no se deja leer), se borra el archivo recién escrito. La copia sigue en el autoguardado y el reintento vuelve a escribir con el primer nombre libre.
- Solo se borra si el archivo no existía antes de escribir. Si la persona eligió en el diálogo pisar un archivo que ya existía, se deja.
- Si el borrado falla (`IOException` o `UnauthorizedAccessException`), se ignora: queda el huérfano, como antes.

**Pruebas (`tests/Rodaje.App.Tests/Autosave/AutosaveRecoveryCancelTests.cs`, 3 nuevas):**

- Cancelar dos veces el aviso de cambios no deja ningún `*(recuperado*).rodaje`; a la tercera, con «Descartar», se recupera como `Corto (recuperado).rodaje`.
- Proyecto que nunca se guardó: con el aviso cancelado se borra el archivo elegido; el reintento vuelve a preguntar dónde y recupera.
- Un archivo elegido que ya existía no se borra aunque se cancele.
- Sin la corrección fallan las dos primeras.

**Arnés sin `Task.Delay` (`AutosaveHarness.cs`, `AutosaveRecoveryServiceTests.cs`):**

- `AutosaveHarness.NotificationAsync(match)` devuelve una tarea (un `TaskCompletionSource`) que termina con el primer aviso que cumple la condición, ya llegado o por llegar.
- `Recuperar_DobleClicEnElBotonDelAviso_RecuperaUnaSolaVez` la usa en lugar de sondear con `WaitFor` y esperar 200 ms.
  - Al llegar el aviso de éxito no queda nada en curso: ese aviso es lo último de la primera recuperación. Además, el segundo clic se descarta en `RecoverAsync` antes del primer `await`, así que ya terminó cuando `Run` vuelve.

## Decisiones

- **Se borra en vez de reutilizar la ruta.** La tarea daba las dos opciones. Con la de borrar no queda nada huérfano aunque la persona no vuelva a intentar y cierre el editor. En cambio, la de reutilizar la ruta dejaba el archivo hasta el siguiente intento, y en el próximo arranque se crearía «(recuperado 2)».
- **Caminos.** La tarea decía «archivos nuevos» en `tests/Rodaje.App.Tests/Autosave/`, pero también pedía cambiar la prueba del doble clic «en el arnés». Por eso se tocan dos archivos de prueba que ya existían (`AutosaveHarness.cs` y `AutosaveRecoveryServiceTests.cs`) con el cambio mínimo. Las pruebas nuevas van en un archivo nuevo.
- `WaitFor` sigue en `AutosaveRecoveryServiceTests` porque lo usa la primera prueba de la oferta. Esa prueba no tenía `Task.Delay` fijo y no se tocó.

## Pendiente

- La prueba del doble clic con el botón del aviso no puede esperar la tarea del segundo clic, porque `NotificationAction.Run` la descarta. La prueba determinista de la guarda sigue siendo `Recuperar_DobleClic_RecuperaUnaSolaVez`, que espera las dos tareas.

## Posibles choques

- Ninguno conocido. Desde la tarea 34, nada en `origin/main` tocó `AutosaveService.cs` ni `tests/Rodaje.App.Tests/Autosave/`.

## Pruebas

- Linux, `dotnet test tests/Rodaje.App.Tests -p:EnableWindowsTargeting=true --filter FullyQualifiedName~Autosave`: 26 de 26 (antes, 23).
- `Rodaje.App.Tests` completo en Linux, en la rama y en `origin/main`:
  - Fallan las mismas 28 en las dos: módulos (procesos y Job Object de Windows), voz, IA de audio, exportar, proxies, miniaturas, CinemaDNG y el título en el visor. Necesitan Windows o la FFmpeg de `.entorno`, y ninguna toca el autoguardado.
  - Se omiten 7, tanto antes como después, por hardware o medios de prueba. Esta tarea no añade ni quita omisiones.

## CI

- En `d5dad3a9`, los dos trabajos («Linux (Core, Paquetes, Playback)» y «Windows (solución completa)») salieron en verde. El de Windows corre la solución completa, incluidas estas pruebas.

## Qué comprobar en Windows

- Abrir un proyecto con cambios sin guardar y forzar el cierre del editor (Administrador de tareas). Al volver a abrir, editar algo para que el proyecto quede sucio y pulsar «Recuperar». En el aviso de cambios sin guardar, pulsar «Cancelar» dos veces. Junto al original no debe aparecer ningún «(recuperado)». A la tercera, con «No guardar», se abre «(recuperado)».
