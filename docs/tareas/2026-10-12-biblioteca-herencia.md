# Entrega: biblioteca, organización vacía y herencia de la copia (tarea 65)

Rama `claude/biblioteca-herencia`, PR #66. Hallazgos de la revisión en bloque 7 sobre la tarea 54 (`2026-10-12-biblioteca-robusta.md`).

## Lo hecho

1. **Vaciar una organización heredada queda guardado** (commit 1).
   - `LibraryOrganizationStore.Load(key, fallbackKey)`: si la clave no tiene archivo propio y el de `fallbackKey` existe, el archivo de la clave queda anotado como heredado (`_inherited`).
   - `Prepare` marca `Empty` solo si la organización está vacía **y** la clave no se heredó. Así `Write` escribe el vacío y en la siguiente apertura `Load` encuentra el archivo propio vacío en lugar de volver a heredar.
   - Sin nada que heredar (no hay archivo por id), el vacío sigue sin crear archivo: `PrepararVacia_SinArchivo_NoCreaArchivo` no cambia de comportamiento; solo se le añadió un comentario que dice que vale para el caso no heredado.
   - Prueba nueva `Heredada_Vaciar_SeGuardaYNoReaparece` (Load(copia) → borrar carpeta y favorito → guardar → Load): falla sin el cambio y pasa con él. `SinNadaQueHeredar_VaciaNoCreaArchivo` cubre el otro caso.
2. **El archivo por id se mantiene al día** (commit 2).
   - `LibraryOrganizationStore.PrepareWithTemplate(key, templateKey, organization)` devuelve lo que hay que escribir para la clave y, si es otra, para la clave por id, en ese orden. `Save(key, templateKey, organization)` hace lo mismo de una vez (lo usan las pruebas).
   - `LibraryView.SaveOrganization` usa `PrepareWithTemplate(_organizationKey, _organizationProject?.Id, …)` y escribe las dos en la misma cadena de tareas de fondo. Si una escritura falla, el aviso y el reintento funcionan como antes.
   - Pruebas nuevas: `CopiaHechaDespues_HeredaElEstadoActualDelOriginal`, `GuardarConPlantilla_ElOriginalSigueConSuArchivo` y `ProyectoSinGuardar_LaPlantillaEsSuPropioArchivo`, en `tests/Rodaje.App.Tests/Library/LibraryOrganizationInheritanceTests.cs` (archivo nuevo).

## Decisión del punto 2: actualizar el archivo por id

Se eligió **actualizar también el archivo por id al guardar**, que queda como plantilla, en lugar de no heredar nada con la clave nueva:

- Si no se heredara nada, quien tenga un proyecto organizado antes de la tarea 54 lo vería sin carpetas al actualizar. Desde `Load` no se puede distinguir ese caso del de una copia: los dos tienen solo el archivo por id.
- Con la plantilla al día, la comprobación 1 de la entrega de la 54 se cumple: la copia empieza con las carpetas que tiene ahora el original.
- **Consecuencia:** la plantilla guarda lo último que se ordenó en *cualquier* proyecto con ese id, sea el original o una copia, porque las dos escriben en ella. Una copia nueva parte de lo último que se ordenó, no siempre del original. Es solo el punto de partida y el archivo propio de cada `.rodaje` no lo toca nadie más. La prueba `GuardarConPlantilla_ElOriginalSigueConSuArchivo` lo fija.
- `Load` sigue sin escribir el archivo por id: solo lo escribe el guardado (`SinArchivoPropio_PartePorElDelIdSinTocarlo` sigue igual y pasa, porque usa `Save(key, organization)` sin plantilla).

## Pendiente

- Lo mismo que dejó la 54: la escritura en segundo plano no se espera al cerrar la app, y mover el `.rodaje` desde el Explorador cambia la clave (ahora parte de la plantilla, que está al día).
- Cada cambio de carpetas escribe dos archivos pequeños en lugar de uno. Son pocos KB, en segundo plano.

## Posibles choques

No hay otro PR abierto que toque `Views/Library` ni `tests/Rodaje.App.Tests/Library` (revisado al entregar). En `LibraryView.Folders.cs` solo cambia el cuerpo de `SaveOrganization`.

## Pruebas

- En Linux, con un proyecto de prueba aparte (sin subir, como en la 54) que compila `Views/Library/Logic` y `tests/Rodaje.App.Tests/Library` contra `Rodaje.Core`: **57 de 57 pasan** (las 52 anteriores, más 5 nuevas). La prueba del punto 1 falla con el código de `main`.
- `tests/Rodaje.App.Tests` compila en Linux con `-p:EnableWindowsTargeting=true`, sin advertencias. No se corre allí, porque es `net10.0-windows` con `win-x64`; lo corre el trabajo Windows de la CI.
- `src/Rodaje.App` no compila en Linux porque `XamlCompiler.exe` es de Windows (`Exec format error`). El cambio en `LibraryView.Folders.cs` lo compila el trabajo Windows de la CI.
- **Pruebas omitidas:** ninguna nueva. Antes 0 y después 0 en el conjunto de la biblioteca. El resto de omisiones del repo no cambia.

## Qué debe comprobar Daniel en Windows

1. Proyecto guardado y organizado en carpetas. Copiar el `.rodaje` y abrir la copia: tiene las carpetas. Borrar en la copia todas las carpetas y favoritos, cerrar y volver a abrirla: siguen vacías.
2. Con el original ya guardado, agregar una carpeta nueva y mover un medio a ella. Después copiar el `.rodaje` y abrir la copia: aparece la carpeta nueva con su medio.
3. Ordenar algo en la copia y abrir el original: está como se dejó.

## Integración continua

En el commit `8ce050c` (código y entrega) pasaron los dos trabajos: «Linux (Core, Paquetes, Playback)» y «Windows (solución completa)». El commit que añade esta sección solo cambia este archivo.
