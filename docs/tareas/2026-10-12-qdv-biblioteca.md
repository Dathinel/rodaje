# Entrega: biblioteca con carpetas, búsqueda y favoritos (tarea 30)

Rama `claude/qdv-biblioteca`, PR #38. Función 38 de `docs/pendientes.md`.

## Lo hecho

En la categoría **Medios** de la biblioteca:

- **Carpetas.** Botón «Nueva carpeta» en la cabecera (crea «Carpeta nueva» y pide el nombre). Las carpetas de la carpeta abierta salen como fichas encima de los medios con lo que tienen («3 medios · 1 carpeta»). Clic abre; la barra de ruta («Todos los medios › Viaje › Playa») tiene «Subir». El menú de la ficha permite abrirla, renombrarla y eliminarla. Al eliminarla, sus medios y subcarpetas pasan a la carpeta de arriba, así que ningún medio sale del proyecto.
- **Mover medios.** Hay dos formas: arrastrar un medio a una ficha de carpeta (o a «Subir», para sacarlo de la carpeta), o usar el menú del medio, «Mover a», que muestra el árbol completo y permite crear una carpeta nueva. Arrastrar a la línea de tiempo sigue igual.
- **Búsqueda.** Busca por nombre y por nombre de archivo en **todas** las carpetas, como CapCut, sin distinguir mayúsculas ni tildes («montana» encuentra «Montaña»). Si se escriben varias palabras, tienen que aparecer todas.
- **Filtro por tipo.** Hay un botón de embudo con Todo, Videos, Imágenes y Audio. Cuenta como imagen un medio con imagen sin tasa de cuadros o con extensión de foto. El filtro se aplica en la carpeta abierta.
- **Favoritos.** «Añadir a favoritos» o «Quitar de favoritos» en el menú del medio; una estrella naranja aparece en la ficha y en la fila. El botón de estrella de la barra muestra solo los favoritos de todas las carpetas.
- **Columna de datos.** Un botón cambia la cuadrícula por una lista. Cada fila lleva el nombre y, debajo, cuatro columnas con su cabecera: resolución, duración, fps y códec («H.264 · AAC»). El proyecto no guarda el códec, así que se lee del archivo en segundo plano y de a uno: solo los medios que se ven y solo con la lista abierta. Queda en caché por ruta. La ayuda de cada medio también muestra todos sus datos.
- **Mensajes vacíos** que explican qué pasa: «Nada coincide con «x»», «Aún no hay favoritos…», «No hay medios de este tipo aquí.», «Carpeta vacía…».
- Las fichas de medio ahora se **reutilizan** entre refrescos, cuando nada de lo que muestran cambió. Así no se pierden la portada, el estado del proxy ni los códecs al filtrar o al cambiar de carpeta. Las portadas y el avance del proxy se actualizan también en los medios que no se ven en ese momento.

Archivos:

| Archivo | Qué es |
|---|---|
| `src/Rodaje.App/Views/Library/Logic/LibraryOrganization.cs` | Carpetas (crear, renombrar, borrar, mover sin ciclos), medio → carpeta, favoritos y reparación de lo leído del disco |
| `src/Rodaje.App/Views/Library/Logic/LibraryFilter.cs` | Consulta (carpeta, texto, tipo, favoritos), tipo de cada medio, búsqueda sin tildes (`SearchText`), mensajes vacíos y resumen de la carpeta |
| `src/Rodaje.App/Views/Library/Logic/MediaDetails.cs` | Textos de resolución, fps, audio, códecs y resumen |
| `src/Rodaje.App/Views/Library/Logic/LibraryOrganizationStore.cs` | Lectura y escritura del archivo por proyecto |
| `src/Rodaje.App/Views/Library/LibraryView.Folders.cs` | Parcial nuevo de la vista: lo conecta todo |
| `LibraryView.xaml`, `LibraryView.xaml.cs`, `MediaItem.cs` | Barra, fichas de carpeta, lista con datos, menú y fichas reutilizadas |
| `tests/Rodaje.App.Tests/Library/*.cs` | 28 pruebas nuevas; `Rodaje.App.Tests.csproj` compila ahora `Views/Library/Logic` |

## Decisiones

- **Carpetas y favoritos fuera del `.rodaje`.** Se guardan en `%LOCALAPPDATA%\Rodaje\biblioteca\<id del proyecto>.json` (o en la carpeta de `RODAJE_DATOS`). La tarea permitía cambiar el modelo, pero no convenía, por tres razones:
  - El esquema se genera desde el modelo (`ProjectSchema.Export`) y el de la versión 5 no se puede cambiar una vez publicado. Hacía falta un formato 6, con su migración en `Serialization/` y su esquema en `schema/`, y los dos están fuera de los caminos de la tarea.
  - Cada cambio del proyecto pasa por comandos con deshacer (`Core/Commands`), que también están fuera de los caminos.
  - Las carpetas organizan la vista y no cambian el video.
  
  El precio: un proyecto copiado a otro PC llega sin sus carpetas, y todos sus medios quedan en la raíz. Un «Guardar como» comparte las carpetas con el original, porque tiene el mismo id. Si Daniel quiere que viajen con el proyecto, la lógica ya está separada y bastaría un formato 6 con `folders`, `media[].folderId` y `media[].favorite`. `docs/formato-proyecto.md` no cambia.
- **Medio en una sola carpeta.** Moverlo cambia su carpeta. Borrar una carpeta nunca quita medios del proyecto.
- **Sin deshacer** para carpetas y favoritos, porque no son ediciones del proyecto. Por eso borrar una carpeta no pide confirmación: lo único que hace es subir su contenido un nivel.
- **Buscar y «solo favoritos» miran toda la biblioteca.** El tipo, en cambio, se aplica en la carpeta abierta.
- **Búsqueda sin tildes plegada a mano.** La app corre con `InvariantGlobalization`, así que `CompareOptions.IgnoreNonSpace` no sirve.
- **Archivo dañado.** Se aparta como `<id>.dañado.json` y la biblioteca empieza sin carpetas, igual que los atajos. Un id de proyecto con caracteres raros se guarda con un nombre seguro (`h-<sha256>`).
- **Solo en Medios.** La categoría Audio, los paneles de títulos y de análisis no se tocaron.

## Pruebas

- 28 pruebas nuevas en `tests/Rodaje.App.Tests/Library/` (organización, filtro y búsqueda, datos del medio y archivo). Ese proyecto es `net10.0-windows`, así que en Linux las corrí con un proyecto temporal fuera del repo. Compilaba los mismos fuentes de `Logic/` y las pruebas contra `Rodaje.Core`, con `TreatWarningsAsErrors`, `latest-recommended` e `InvariantGlobalization`: **28 de 28 en verde**. En la CI corren dentro de `Rodaje.App.Tests`, en el trabajo de Windows.
- No toqué Core, Paquetes ni Playback, así que las pruebas de Linux de la CI no cambian.
- **Pruebas omitidas:** ninguna nueva. Las omitidas siguen siendo las mismas que antes.

## Posibles choques

- `tests/Rodaje.App.Tests/Rodaje.App.Tests.csproj`: una línea `Compile Include` nueva. Otra tarea que añada su carpeta `Logic` en el mismo lugar chocaría solo por estar en líneas vecinas.
- `LibraryView.xaml`, `LibraryView.xaml.cs` y `MediaItem.cs`: el panel de medios cambió de estructura. `MediaGrid` sigue existiendo, ahora dentro de `MediaPanel`, y `LibraryScrollMeasurement` funciona igual.
- No toqué `EditorSession*`, `Autosave/`, `Core/Model`, `MainWindow` ni `Rodaje.slnx`.

## Qué comprobar en Windows

1. Importar varios videos, una foto y un MP3. Crear carpetas anidadas, renombrarlas (probar un nombre vacío y uno repetido: el diálogo no debe cerrarse y debe decir por qué) y eliminarlas.
2. Arrastrar un medio a una ficha de carpeta, a «Subir» y a la línea de tiempo (lo último debe seguir funcionando igual).
3. Buscar con y sin tildes; probar el filtro por tipo y «solo favoritos».
4. Activar la lista: los códecs pasan de «…» a «H.264 · AAC» en uno o dos segundos y la vista previa no se traba mientras tanto.
5. Cerrar y volver a abrir el proyecto: las carpetas y los favoritos siguen ahí.
6. Con el panel de la biblioteca en 280 px, la barra no se corta.
7. `app.ps1 -Autoprueba` y `-Medir` (con `RODAJE_MEDIR_BIBLIOTECA`): las portadas cargan igual que antes.

## Pendiente

- Seleccionar varios medios para moverlos de una vez. La vista usa selección simple; `LibraryOrganization.MoveMedia` ya acepta varios.
- Arrastrar una carpeta dentro de otra. `MoveFolder` ya existe, pero la vista todavía no lo usa.
- Que las carpetas viajen dentro del `.rodaje` (formato 6), si Daniel lo prefiere.
- Recordar entre sesiones si la lista estaba abierta.
