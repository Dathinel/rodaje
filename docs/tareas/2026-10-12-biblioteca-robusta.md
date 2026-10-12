# Entrega: biblioteca, proyectos que comparten organización y lectura de códecs (tarea 54)

Rama `claude/biblioteca-robusta`, PR #56. Hallazgos de la revisión en bloque 4 sobre la tarea 30 (PR #38).

## Lo hecho

1. **Original y copia ya no comparten la organización** (commit 1).
   - `LibraryOrganizationStore.KeyFor(projectId, projectFilePath)`: con el proyecto guardado, la clave es el id más la ruta del `.rodaje` en mayúsculas (Windows no distingue mayúsculas; `/` cuenta como `\`). Así un `.rodaje` copiado, que conserva `Project.Id`, guarda sus carpetas aparte. Si el proyecto nunca se guardó, la clave es solo el id, que es nuevo en cada proyecto. `PathFor` convierte la clave en un resumen `h-…` porque tiene caracteres que no son seguros.
   - `Load(key, fallbackKey)`: si la clave aún no tiene archivo, la organización parte de la que está guardada con el id (la de antes de esta versión, o la del proyecto antes de su primer guardado) y ese archivo no se toca. Nadie pierde sus carpetas al actualizar. Una copia empieza con las del original y desde ahí guarda las suyas aparte.
   - La vista ya no llama a `Prune` al cargar. Los medios que el proyecto no tiene se quedan en el archivo, y la vista ya filtraba por los medios del proyecto (`LibraryFilter.Apply`, `CountIn`, `IsFavorite` por ficha). Además, deshacer «Quitar del proyecto» devuelve el medio a su carpeta.
   - La vista recuerda qué instancia de `Project` tiene cargada. Si es la misma y cambió `FilePath`, sea por el primer guardado o por «Guardar como» (escucha `PropertyChanged` de la sesión), la organización pasa al archivo nuevo y la del anterior queda como estaba. Abrir otro proyecto, aunque tenga el mismo id, carga la suya.
2. **Lectura de códecs** (commit 2). `Logic/MediaCodecCache.cs` es nuevo y no usa WinUI.
   - Captura **cualquier** excepción, una por medio, así que un medio raro ya no corta la lectura de los demás.
   - Guarda lo leído, también los fallos, con la fecha de modificación y el tamaño del archivo (`MediaFileStamp`). Un archivo que cambia se vuelve a leer. Si el archivo no está, no se guarda nada y se reintenta. Si el archivo cambia mientras se lee, el resultado no se guarda con la marca vieja.
   - `MediaItem.CodecsFailed`: los medios que fallaron se vuelven a mirar en cada lectura, pero solo se abren si su archivo cambió. La caché es segura entre hilos y ahora se consulta en el hilo de fondo; antes se consultaba en el de la interfaz.
3. **Menores** (commit 3):
   - Un JSON de una **versión futura** se aparta como `<nombre>.vN.json` y no se pisa al guardar.
   - **Nombres reservados de Windows** (CON, PRN, AUX, NUL, COM0–9, LPT0–9, sin mirar mayúsculas): usan el resumen `h-…`.
   - **`KindOf`**: un medio cuenta como imagen si tiene extensión de imagen, o si no tiene tasa de cuadros ni sonido y dura menos de 1 s. Un video sin tasa de cuadros sigue siendo video.
   - **`SearchText.Fold`** quita las marcas combinantes (U+0300–036F, 1AB0–1AFF, 1DC0–1DFF, 20D0–20FF y FE20–FE2F), así que las tildes en NFD se pliegan. No usa `string.Normalize`, porque la app corre con globalización invariante.
   - **`NewId`**: `folder-` más 12 cifras hexadecimales al azar. Ya no reutiliza el id de una carpeta borrada.
   - **Guardar sin trabar la interfaz**: `Prepare` toma la foto de la organización en el hilo de la interfaz y `Write` escribe con `Flush(true)` en segundo plano, en una cadena de tareas que mantiene el orden. Si la escritura falla, se avisa como antes y el próximo cambio la reintenta. `Save` sigue existiendo (`Prepare` y luego `Write`).

Pruebas nuevas, en archivos nuevos: `LibraryOrganizationCopyTests` (6), `MediaCodecCacheTests` (5) y `LibraryRobustnessTests` (13 casos). Las pruebas de la tarea 30 no cambian y siguen pasando. En Linux, con un proyecto de prueba aparte (sin subir) que compila `Views/Library/Logic` y `tests/Rodaje.App.Tests/Library`, pasan las 52. Las pruebas omitidas no cambian: no se omite ninguna nueva.

## Decisiones

- **Clave con la ruta en lugar de solo el hash**: la clave legible pasa por `PathFor`, que ya resumía todo lo que no es seguro, así que el archivo queda `h-<32 hex>.json` sin código nuevo.
- **Partir del archivo por id y no empezar vacía**: sin esto, todos los proyectos ya organizados se verían sin carpetas al actualizar. La consecuencia: una copia hecha después del cambio también parte de lo guardado por id, que puede estar viejo (lo último que se ordenó antes del primer guardado o antes de esta versión). Es solo el punto de partida y nunca se pierde un medio.
- **No se poda al guardar**: el archivo puede acumular ids de medios que ya no están. Son pocos bytes, y a cambio deshacer no pierde la carpeta. `LibraryOrganization.Prune` se queda (tiene pruebas) aunque la vista ya no lo usa.
- **Los fallos de códec se guardan con la marca** en lugar de no guardarse: así un archivo que siempre falla no se vuelve a abrir en cada refresco.
- **Imagen sin extensión**: el límite de 1 s, sin sonido y sin tasa de cuadros, es un criterio práctico. Una foto que FFmpeg lee como un solo cuadro dura una fracción de segundo.

## Pendiente

- La escritura en segundo plano no se espera al cerrar la app. Es un archivo de pocos KB que se escribe enseguida tras cada cambio, pero si se cierra en ese mismo instante se puede perder el último cambio de carpetas. El `.tmp` y el `Move` evitan que quede un archivo a medias.
- Mover o renombrar el `.rodaje` desde el Explorador cambia la clave: la organización vuelve a partir de la del id.
- `Load` sigue leyendo el archivo en el hilo de la interfaz, como antes; la tarea solo pedía mover el guardado.

## Posibles choques

No hay otro PR abierto que toque `Views/Library` ni `Rodaje.App.Tests/Library`. En `LibraryView.xaml.cs` solo se quitó el campo `_organizationProjectId`, que pasa al parcial `LibraryView.Folders.cs` como `_organizationProject` y `_organizationKey`, y se agregó en `Attach` la escucha de `FilePath`.

## Qué comprobar Daniel en Windows

1. Organizar un proyecto guardado en carpetas y con favoritos. Copiar el `.rodaje` en el Explorador y abrir la copia: empieza con las mismas carpetas. Mover un medio en la copia, quitar otro, cerrar y abrir el original: está como se dejó.
2. Proyecto nuevo sin guardar: crear carpetas, guardarlo (primer guardado), cerrar y volver a abrir: las carpetas siguen. Repetir con «Guardar como» a otro nombre.
3. Quitar un medio que está en una carpeta y deshacer: vuelve a la misma carpeta.
4. Vista de lista con un archivo que FFmpeg no lea (por ejemplo, un `.mp4` truncado): los demás medios muestran su códec y no se quedan en «…».
5. Buscar «montaña» con un medio cuyo nombre venga de un Mac (en NFD): aparece.
