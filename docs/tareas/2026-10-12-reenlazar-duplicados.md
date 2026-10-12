# Reenlazar: un archivo asignado a dos medios

Rama `claude/reenlazar-duplicados` (tarea 48 de la cola), PR #47. Sigue a la tarea 28 (`2026-10-12-qdv-reenlazar.md`).

## Comprobado en origin/main antes de tocar nada

- `MediaRelinkSearch.cs:142`: `taken` solo tenía las rutas de los medios que no faltan. Correcto.
- `RelinkDialog.cs:225`: tras «Elegir…», `PickAsync` lanza `SearchAsync(folder)` con los que faltan; lo resuelto (`pending`) no llegaba a la búsqueda. Correcto: con A y B iguales (1080p, 10 s) y `\nuevo\b.mp4` elegido para B, la pasada «Renamed» se lo daba a A.
- La pasada «Renamed» está en `MediaRelinkSearch.cs:199-209` (no en la línea 253, que es `Choose`). Enlazaba un único candidato sin comparar contenido.
- `MediaRelinkSearch.cs:28`: `AttributesToSkip = FileAttributes.System`. Correcto.
- `RelinkDialog.cs:262`: `if (now.Count > 0)` y nada más. Correcto.

## Hecho (un commit por punto)

1. **Un archivo no va a dos medios.**
   - `RelinkSearchOptions.TakenPaths`: rutas que la búsqueda no da a ningún medio, además de las de los medios que no faltan (sin distinguir mayúsculas).
   - `MediaRelinkEdits.CheckTaken(project, media, path, pending)`: dice por qué un archivo no sirve si ya es el de otro medio del proyecto o el que se eligió para otro en `pending`. La ruta de antes de un medio que está en `pending` cuenta como libre, porque ese medio se va a enlazar con otro archivo.
   - `TargetFor` recibe `pending` (opcional) y lanza `EditException` con ese motivo.
   - `MediaRelinkService.SearchAsync(..., taken, ct)` y `CheckFileAsync(..., pending)`; el diálogo pasa lo que ya tiene resuelto y lo que tiene propuesto.
2. **Lo encontrado con otro nombre se confirma.** `RelinkMatch.NeedsConfirmation` (`Kind == Renamed`). El diálogo no lo mete en `pending`: lo deja como propuesto, con el texto «¿Es este? Tiene otro nombre…» y un botón «Confirmar». «Volver a enlazar» solo aplica lo confirmado. Lo que queda sin confirmar sigue en el aviso de medios que faltan. Si se elige a mano un archivo que estaba propuesto para otro medio, ese otro medio deja de tenerlo propuesto y se explica por qué.
3. **Archivos que solo están en la nube.** `RelinkFile.CloudOnly`. `DiskRelinkFileSystem` lo marca con `Offline`, `FILE_ATTRIBUTE_RECALL_ON_OPEN` (0x40000) o `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` (0x400000): .NET no nombra los dos últimos, así que van en `CloudOnlyAttributes`. La pasada «Renamed» los salta.
4. **«Volver a enlazar» sin nada que enlazar.** Si al volver a mirar ya no falta nada, sale «Ya están todos los medios». Si además el reproductor tiene medios fallidos (`PlayerEngine.LatestStats.FailedMedia > 0`), se recarga la secuencia (`Player.Load` y `Seek` a la misma posición). Hace falta porque el motor guarda el fallo en `MediaState.Failure`, por medio y ruta, y no lo limpia en un `Reload`. Cuando el archivo vuelve a la misma ruta no hay edición que lo reabra.

## Decisiones

- **El archivo de la nube con el mismo nombre sí se abre**, en la pasada por nombre: es uno solo y casi seguro el que se busca, así que descargarlo es lo esperado. La tarea pide excluirlos solo en la pasada de renombrados.
- **`RelinkSearchResult.Targets` sigue incluyendo lo que pide confirmación**, para no cambiar su contrato ni la prueba de la app que lo usa; su documentación lo dice. El diálogo es el que no lo aplica sin confirmar.
- **Lo propuesto también cuenta como ocupado** en las búsquedas siguientes, para no proponer el mismo archivo a dos medios. Lo elegido a mano manda sobre lo propuesto.
- **Recargar el reproductor** con `services.Player.Load` desde el diálogo: `EditorSession.cs` no estaba en los caminos de la tarea. Es lo mismo que hace `EditorSession.ReplaceProject` y lo que hace `PlayerEngine` al cambiar de proxies (abre la sesión otra vez y busca la misma posición).
- **No se rechazan dos medios con la misma ruta en `MediaRelinkEdits.Relink`** (el comando): el cambio se hizo en `TargetFor`, en el servicio y en la búsqueda, como pedía la tarea, para no cambiar la validación del comando.

## Pruebas

- Nuevas en `tests/Rodaje.Core.Tests/Editing/Relink/`:
  - `MediaRelinkDuplicatesTests.cs` (7): búsqueda por tandas como en el diálogo (el caso de A y B), mayúsculas, dos tandas seguidas, rechazo en `TargetFor` por `pending` y por otro medio, ruta liberada y `NeedsConfirmation`.
  - `MediaRelinkCloudOnlyTests.cs` (3).
- `Rodaje.Core.Tests` en Linux con el filtro `Categoria!=SoloWindows&Categoria!=TiempoReal&Categoria!=Rendimiento`: 1382 de 1382, ninguna omitida (antes y después: 0 omitidas).
- La app no compila en Linux (el compilador de XAML es de Windows); la comprueba el trabajo «Windows» de la CI. No se añadieron pruebas en `tests/Rodaje.App.Tests` porque no estaba en los caminos de la tarea. Las que ya había siguen llamando a `SearchAsync` y `CheckFileAsync` con la firma anterior, que sigue valiendo (los parámetros nuevos son opcionales).

## Posibles choques

- Solo cambian los cuatro archivos de la tarea 28 (`MediaRelinkSearch.cs`, `MediaRelink.cs`, `MediaRelinkService.cs` y `RelinkDialog.cs`) y hay dos archivos de prueba nuevos. `SearchAsync` tiene un parámetro nuevo antes del `CancellationToken`: quien lo llamara pasando el token por posición tendría que nombrarlo. Ahora mismo nadie lo hace.

## Pendiente

- Una prueba de la app (Windows) del diálogo con un renombrado que se confirma, si se quiere cubrir la interfaz.
- `MediaRelinkEdits.CheckReplacement` (de la tarea 28, `MediaRelink.cs`) formatea la duración con `{…:0.##}` y la cultura del hilo: con `InvariantGlobalization` la app mostraría «10.5 s». El núcleo no tiene `SpanishNumbers` (está en `Rodaje.App/Services/Format`), así que no se tocó aquí. El diálogo de esta tarea solo muestra enteros y no usa `CultureInfo.CurrentCulture`.

## Qué comprobar en Windows

1. Dos clips 1080p de 10 s (`a.mp4`, `b.mp4`). Guardar, cerrar, borrar `a.mp4` y mover `b.mp4` a otra carpeta. Al abrir, «Elegir…» en B con el `b.mp4` movido: A debe seguir en «Falta», no «Encontrado con otro nombre: …b.mp4».
2. En el mismo diálogo, «Elegir…» en A con ese mismo `b.mp4`: debe decir que ya está elegido para B.
3. Renombrar un archivo y buscar en su carpeta: sale «¿Es este? Tiene otro nombre…» con «Confirmar». «Volver a enlazar» no se activa hasta confirmarlo. Si se cierra sin confirmar, el medio sigue en el aviso.
4. Una carpeta de OneDrive con archivos solo en la nube (icono de nube): la búsqueda de renombrados no los descarga (no cambian a «disponible en este dispositivo»).
5. Abrir un proyecto con un medio que falta y pulsar «Más tarde». Devolver el archivo a su sitio con el Explorador y pulsar «Volver a enlazar» en el aviso: sale «Ya están todos los medios» y la vista previa vuelve a mostrar el clip en la misma posición.
