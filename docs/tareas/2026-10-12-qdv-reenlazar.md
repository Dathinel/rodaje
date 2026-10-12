# Volver a enlazar medios movidos o renombrados

Rama `claude/qdv-reenlazar` (tarea 28 de la cola, función 32 de `docs/pendientes.md`), PR #30.

## Hecho

- **Núcleo** (`src/Rodaje.Core/Editing/MediaRelink.cs`, `MediaRelinkSearch.cs`; `src/Rodaje.Core/Commands/RelinkMediaCommand.cs`):
  - `MediaRelinkEdits.FindMissing` (medios cuyo archivo no está), `CountClips`, `CheckReplacement` (un archivo elegido a mano no sirve si le falta la imagen o el sonido que usan sus clips o si es más corto que lo que usan) y `TargetFor` (solo la ruta si el archivo coincide con el medio; también sus datos si no).
  - `MediaRelinkSearch.Search` sobre un `IRelinkFileSystem` (en la app, `DiskRelinkFileSystem` con FFmpeg para leer los datos). Primero por **nombre** (sin importar mayúsculas, en subcarpetas) con las mismas pistas, el mismo tamaño de imagen y la misma duración (±50 ms); después, para los que siguen sin archivo, **renombrados** (misma extensión y mismos datos). Si encajan varios, gana el que conserva más carpetas de la ruta de antes; si siguen empatados se compara la **huella** (SHA-256 de los primeros 4 MB más el largo): copias idénticas → cualquiera; distintas → se elige a mano. Un archivo no se da a dos medios ni a uno que ya tiene el suyo. Límite de 100 000 archivos (avisa si no se vio la carpeta entera) y cancelable.
  - `RelinkMediaCommand`: un solo paso del historial («Volver a enlazar medio» / «Volver a enlazar N medios»); cambia la ruta y, si se dan, los datos del medio; valida con `ProjectGuard` y deshace dejando el medio idéntico. Clips, efectos y keyframes no se tocan: apuntan al identificador del medio.
- **App, sin WinUI** (`src/Rodaje.App/Services/Relink/MediaRelinkService.cs`): qué falta al abrir (fuera del hilo de la interfaz), búsqueda en carpeta, comprobación de un archivo elegido a mano, `Apply` en un paso y `Attach` a `EditorSession.ProjectOpened` (cubre abrir un proyecto, también al arrancar con un `.rodaje`).
- **Diálogo** (`src/Rodaje.App/Views/Relink/RelinkDialog.cs`, `FolderDialog.cs`): «Falta un medio» / «Faltan N medios» con la lista (nombre, ruta de antes, en cuántos clips), «Buscar en carpeta…» y «Elegir…» por medio. Al elegir uno a mano se buscan los demás en su carpeta (lo normal es que se movió la carpeta entera). «Volver a enlazar» aplica todo en un paso; «Más tarde» deja un aviso con el botón «Volver a enlazar», que vuelve a mirar qué falta. `FolderDialog` es un `IFileOpenDialog` con `FOS_PICKFOLDERS`, hecho como `Shell/FileDialogs`.
- **Pruebas**: 21 en `tests/Rodaje.Core.Tests/Editing/Relink/` (búsqueda con un disco en memoria y con carpetas temporales; comando y edición) y 4 en `tests/Rodaje.App.Tests/Relink/` (abrir un proyecto guardado cuyo medio se movió, buscar, aplicar y deshacer; elegir a mano; aviso). Todas pasan en Linux. La suite entera de `Rodaje.Core.Tests` en Linux: 1344 de 1345; la que falla es `ProjectFileTests.Save_TempLockedByAnotherProgram_ThrowsAndKeepsExistingFile`, que también falla sin estos cambios (Linux no bloquea un archivo abierto por otro programa como Windows). No se omitió ni excluyó ninguna prueba.

## Decisiones

- **«Tamaño» es el de la imagen**: el proyecto no guarda el tamaño en bytes del archivo (ver `docs/formato-proyecto.md`), así que se comparan ancho y alto, pistas y duración. Por la misma razón la huella no se compara con la del original (no está guardada): sirve para resolver dudas entre candidatos, no para confirmar uno solo.
- **Renombrados solo con la misma extensión**: hay que leer cada candidato con FFmpeg; limitarlo a la extensión del original evita leer toda una carpeta de fotos para buscar un video.
- **El nombre visible no cambia** al enlazar con un archivo renombrado.
- **Archivo elegido a mano distinto del original** (otra duración o resolución): se acepta si cubre lo que usan los clips y se actualizan los datos del medio; si no, se explica por qué no sirve.
- La búsqueda vive en el núcleo (con el disco abstracto) para probarla en Linux sin WinUI ni FFmpeg.

## Posibles choques

- `src/Rodaje.App/Shell/MainWindow.xaml.cs`: **una línea** después de `MissingContent.Attach(session, packages);`: `Views.Relink.RelinkDialog.Attach(_services, _hwnd);`.
- Ningún otro archivo existente cambia.

## Pendiente

- Entrada en la biblioteca («Volver a enlazar…» en el menú de un medio, y una marca en los medios que faltan): `Views/Library` no estaba en los caminos de la tarea.
- `docs/arquitectura/app.md` no se actualizó (fuera de los caminos); conviene añadir `Services/Relink` a la tabla de carpetas.
- La app no se pudo compilar entera en Linux (el compilador de XAML y `mt.exe` son de Windows). Se compiló el C# de la app saltando XAML y el manifiesto: los archivos nuevos y la línea de `MainWindow` no dan errores ni avisos de analizadores; la CI de Windows lo confirma.

## Qué comprobar en Windows

1. Guardar un proyecto con varios clips (con efectos y keyframes), cerrar Rodaje, mover la carpeta del material a otro sitio (y renombrar uno de los archivos) y abrir el proyecto: debe salir «Faltan N medios».
2. «Buscar en carpeta…» en la carpeta nueva: los encuentra todos (el renombrado como «Encontrado con otro nombre»); «Volver a enlazar»: la vista previa y las miniaturas vuelven; los clips, efectos y keyframes están igual; Ctrl+Z deja los medios como faltantes en un solo paso.
3. «Elegir…» en uno: tras elegirlo, se buscan solos los demás en esa carpeta. Elegir un archivo más corto o sin sonido: se explica por qué no sirve.
4. «Más tarde»: aparece el aviso con «Volver a enlazar», que abre otra vez el diálogo.
5. Arrancar con `Rodaje.bat proyecto.rodaje` con medios que faltan: sale el diálogo o, si la ventana aún no estaba lista, el aviso.
6. Una carpeta grande (por ejemplo `C:\Users\<usuario>`): el diálogo sigue respondiendo mientras busca y cuenta archivos.
