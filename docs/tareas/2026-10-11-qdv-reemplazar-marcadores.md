# Reemplazar medio y lista de marcadores

Rama `claude/qdv-reemplazar-marcadores` (tarea 19 de la cola), PR #23. Funciones 5 y 8 de `docs/pendientes.md`.

## Hecho

- **Núcleo, reemplazar medio** (`src/Rodaje.Core/Editing/TimelineEdits.ReplaceMedia.cs`):
  - `TimelineEdits.ReplaceMedia(ctx, clipId, mediaId)` cambia el medio del clip y el de sus enlazados que usaban el mismo medio (un audio enlazado de otro medio, como una música, no se toca). Conserva posición, duración, velocidad, efectos y keyframes.
  - Si el tramo `[SourceIn, SourceOut)` cabe en el medio nuevo se usa el mismo; si no, el mismo largo desde el inicio del medio nuevo, y los keyframes de los efectos (en tiempo del origen) se corren lo mismo para quedar en su sitio dentro del clip.
  - Medio nuevo más corto que ese largo: el clip se acorta por el final hasta lo que dura el medio (no se corre lo que viene después). Un clip con velocidad no se acorta solo: falla pidiendo recortarlo antes.
  - `TimelineEdits.PreviewReplaceMedia(project, sequence, clipId, mediaId)` devuelve `ReplaceMediaPreview(Shortened, OldDuration, NewDuration, ClipCount)` sin tocar nada, para avisar antes; lanza `EditException` con el motivo si no se puede (título, medio inexistente o igual, sin imagen para pista de video, sin sonido para pista de audio enlazada, pista bloqueada, velocidad).
  - Comando `Edits.ReplaceMedia` (`src/Rodaje.Core/Commands/Edits.ReplaceMedia.cs`), un paso del historial.
- **Núcleo, marcadores** (`TimelineEdits.MarkerList.cs`, `Edits.MarkerList.cs`): `RenameMarker` (recorta espacios, rechaza el nombre vacío), `NextMarker` y `PreviousMarker` (estrictamente después o antes del instante), comando `Edits.RenameMarker`.
- **App, panel de marcadores** (`src/Rodaje.App/Views/Markers/MarkersPanel.xaml(.cs)`): lista en orden de tiempo con nombre y código de tiempo; doble clic o Entrar llevan el cursor al marcador; «Anterior»/«Siguiente» saltan desde el cursor; «Añadir» deja uno en el cursor (mismo nombre «Marcador N» que la tecla M); renombrar con el botón del lápiz, el menú contextual o F2 (diálogo con `DialogGate`); quitar con la papelera, el menú o Supr. Filas al estilo del historial (D-027).
- **App, biblioteca** (`src/Rodaje.App/Views/Library/`): pestaña «Marcadores» en el riel que muestra el panel, y en el menú de un medio «Reemplazar el clip seleccionado» (activo si hay un clip de medio seleccionado). Si el medio nuevo es más corto pide confirmar («Reemplazar y acortar», Cancelar por defecto); si no se puede, avisa con el motivo.
- **Pruebas** (archivos nuevos): `tests/Rodaje.Core.Tests/Editing/Placement/ReplaceMediaTests.cs` (8) y `MarkerListTests.cs` (3).

## Pendiente y bloqueo

- **Color de los marcadores: bloqueado.** `Marker` (`src/Rodaje.Core/Model/Marker.cs`) solo tiene `Id`, `Time` y `Name`; añadir un color exige tocar `src/Rodaje.Core/Model`, `SequenceSnapshot.MarkerEquals` (para el historial), la serialización (formato nuevo con migración) y el dibujo de la línea de tiempo (`TimelineRenderer.RealizeMarkers`), todo fuera de los caminos de la tarea. El panel muestra el color fijo de los marcadores (`KeyframeBrush`). Propuesta de tarea: `Marker.Color` como cadena opcional `#RRGGBB` con una paleta corta (6 colores), formato siguiente con migración que lo deja nulo, `RecolorMarker` y un selector en la fila del panel.
- La lista no se ve sin abrir la pestaña; un atajo para «marcador siguiente/anterior» iría en `Services/Shortcuts` (fuera de los caminos).
- Reemplazar con un medio sin sonido un clip con audio enlazado falla («Desenlázalo o quítalo antes»); CapCut lo deja mudo. Si se prefiere así, bastaría con quitar o desenlazar el audio dentro de la misma operación.

## Decisiones

- Se conserva el tramo del origen cuando cabe (sirve para cambiar una toma por su versión corregida); si no cabe se empieza en el inicio del medio nuevo.
- Más corto: se acorta el clip y no se corre lo demás (queda hueco), para no mover nada sin pedirlo; el aviso lo dice.
- El panel vive en la biblioteca porque su riel está dentro de los caminos; `MainWindow` y el inspector no.

## Verificación

- Linux: `dotnet test tests/Rodaje.Core.Tests --filter "Categoria!=SoloWindows"` → 1238 pasan, 0 fallan, 0 omitidas (la categoría `SoloWindows` la excluye también la CI de Linux; sin el filtro falla `Save_TempLockedByAnotherProgram…`, que depende del bloqueo de archivos de Windows y no tiene que ver con esta rama). No se omitió ninguna prueba nueva: 0 omitidas antes y después.
- La App no compila en Linux. Comprobé solo la sintaxis de `MarkersPanel.xaml.cs` y `LibraryView.xaml.cs` con Roslyn y que los dos XAML son XML válido, fuera del repo.
- **La CI no arrancó**: desde las 18:52 UTC todos los trabajos de GitHub fallan en unos 3 s sin logs (según la sesión que coordina, por facturación; no es por este PR). Falta la verificación en Windows.

## Posibles choques

- Ninguna rama `claude/*` del remoto toca `Views/Library` ni `TimelineEdits.Markers.cs`. Otra tarea que añada una categoría a la biblioteca tocaría las mismas líneas del riel y de `UpdateVisibility`.

## Qué comprobar en Windows

1. Que compile la App (`ABRIR.bat` / CI «Windows») y que AOT siga sin advertencias (`MarkerItem` se enlaza con `x:Bind`).
2. Biblioteca → pestaña Marcadores (ícono de bandera): pulsar M en la línea de tiempo y ver la fila; doble clic lleva el cursor; F2 y el lápiz renombran; Supr y la papelera quitan; Ctrl+Z deshace cada paso; «Anterior»/«Siguiente».
3. Seleccionar un clip, clic derecho en otro medio → «Reemplazar el clip seleccionado»: se conserva duración, efectos y keyframes (abrir Propiedades). Con un medio más corto aparece el aviso y el clip se acorta. Con una imagen sobre un video con audio enlazado aparece el aviso de que no tiene sonido.
