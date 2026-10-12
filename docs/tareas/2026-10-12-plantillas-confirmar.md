# Tarea 50: confirmar al borrar una plantilla propia

Rama `claude/plantillas-confirmar`. Sale de lo pendiente de la tarea 35 (`docs/tareas/2026-10-12-limpiezas-bloque-1.md`): «Archivo › Proyecto nuevo desde plantilla › Borrar plantilla propia» borraba al primer clic, sin preguntar.

## Lo hecho (un commit por punto)

1. **Confirmación genérica en `IEditorDialogs`.** `IEditorDialogs` no tenía ninguna, así que se añade `Task<bool> ConfirmAsync(string title, string message, string confirmText)`:
   - Devuelve `true` solo si se pulsa el botón de confirmar. Cancelar, cerrar o no poder mostrar el diálogo dan `false`.
   - En la ventana (`Shell/ShellDialogs.cs`) es un `ContentDialog` con el estilo de la app (D-027): tema oscuro, `DialogGate.ShowAsync` y el texto con ajuste de línea. El botón por defecto es **Cancelar**, para que Intro no confirme algo que no se deshace.
   - El doble de pruebas `FakeDialogs` (`tests/Rodaje.App.Tests/Support/Fakes.cs`) guarda cada confirmación pedida (título, mensaje y botón) en `Confirmations`. Contesta `ConfirmAnswer`, que por defecto es `false`.
2. **Borrar con confirmación.**
   - Nuevo `EditorSession.DeleteUserTemplateAsync(store, id)` en `Services/Templates/EditorSession.Templates.cs`. Pregunta «¿Borrar la plantilla?» con el nombre entre comillas y avisa de que no se puede deshacer y de que los proyectos ya creados no cambian. El botón dice «Borrar». Solo si se confirma llama a `ProjectTemplateStore.Remove`.
   - Las de fábrica y los identificadores que no están no preguntan y devuelven `false`.
   - `TitleBar.OnDeleteTemplateClick` pasa a `async void` y usa ese método. Si no se confirma, sale sin rehacer el menú. Los errores de disco (`IOException`, `UnauthorizedAccessException`) se siguen mostrando como antes.
   - Pruebas nuevas en `tests/Rodaje.App.Tests/Templates/TemplateDeleteConfirmTests.cs`, 5 en total:
     - confirmar borra la plantilla y la quita del archivo;
     - cancelar no toca ni la lista ni el archivo;
     - por defecto el doble no confirma;
     - las de fábrica y los ids desconocidos no preguntan;
     - solo se borra la plantilla elegida.

## Decisiones

- **La pregunta vive en la sesión y no en la barra de título.** Así se prueba sin WinUI con `FakeDialogs`. Es el mismo patrón que `AskSaveChangesAsync`. La barra no tiene acceso a `IEditorDialogs`, y `EditorServices` queda fuera de los caminos de la tarea.
- **Confirmación genérica (título, mensaje y botón)** en vez de una específica de plantillas, para que la reutilicen otros borrados que no se deshacen.
- **Cancelar como botón por defecto.** En «¿Guardar los cambios?» el botón por defecto es Guardar, pero aquí la acción destruye datos.
- El mensaje no lleva números, así que no necesita `SpanishNumbers.Format`. Las vigilancias de cultura (`CurrentCultureNumbersTests` y `UnformattedNumbersTests`) pasan.

## Fuera de los caminos (cambio mínimo imprescindible)

- `src/Rodaje.App/Shell/ShellDialogs.cs` es la única implementación real de `IEditorDialogs`. Sin el método nuevo, la App no compila.
- `tests/Rodaje.App.Tests/Support/Fakes.cs` es el único doble de pruebas que implementa `IEditorDialogs`. Lo pidió el coordinador.

## Pruebas (Linux, dotnet 10.0.401)

- `Rodaje.App.Tests` compila en Linux con `-p:EnableWindowsTargeting=true -p:RuntimeIdentifier=linux-x64`.
- `Templates` y `Support` (incluidas las vigilancias de cultura): 73 de 73 pasan.
- Suite entera con `Categoria!=SoloWindows&Categoria!=TiempoReal&Categoria!=Rendimiento`:
  - en `origin/main` (`78785de`), 893 pasan, 29 fallan y 6 se omiten;
  - en esta rama, 898 pasan, 29 fallan y 6 se omiten.
  - Las 29 fallas son las mismas en las dos, una por una, y son de plataforma (tuberías y rutas de Windows, módulos, voz, miniaturas, exportación).
- **Pruebas omitidas:** ninguna nueva. Se omitían 6 en Linux antes y se omiten 6 después.
- Los `packages.lock.json` regenerados en Linux se deshicieron.

## Posibles choques

- Si otra rama implementa `IEditorDialogs` o cambia `FakeDialogs`, tendrá que añadir `ConfirmAsync`. Hoy no hay más implementaciones en `origin/main`.
- `TitleBar.xaml.cs`: solo cambia `OnDeleteTemplateClick`.

## Qué comprobar en Windows

- La CI de Windows compila la App (`ShellDialogs.ConfirmAsync`, `TitleBar` con `async void`).
- **A mano:**
  - Guarda una plantilla y elige «Borrar plantilla propia» y luego esa plantilla. Tiene que salir «¿Borrar la plantilla?» con el nombre.
  - «Cancelar», Esc o Intro no borran nada.
  - «Borrar» la quita del menú.
