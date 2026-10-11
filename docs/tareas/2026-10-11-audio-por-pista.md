# De qué pistas de video sale el audio (tarea 15)

Rama `claude/audio-por-pista`, PR #16. Segunda mitad de «Personalización» de `docs/pendientes.md` (la primera, pistas por defecto, fue la tarea 7).

## Hecho

- **Regla:** `Services/Settings/LinkedAudioTracks.cs` (nuevo). «Todas» (`LinkedAudioTracks.All`, lo de siempre, también para pistas que se añadan después) o una lista de números de pista (`Only`), donde 1 = «Video 1», la de más abajo y principal. Las pistas se cuentan por su lugar en `Sequence.VideoTracks`, no por nombre ni id, para que la elección sirva en cualquier proyecto. `Allows(sequence, trackId)` no limita las pistas que no son de video. `Toggle` pasa de «todas» a la lista 1–8 (o más, si la secuencia tiene más pistas) sin la pista tocada.
- **Ajuste:** `AppSettings.LinkedAudioVideoTracks` en `ajustes.json` (`linkedAudioVideoTracks`): `null` = todas (un `ajustes.json` viejo, sin el campo, sigue igual); una lista = solo esas; lista vacía = ninguna. `Normalize` quita repetidos, ordena y descarta números fuera de 1–64. `AppSettings.LinkedAudioTracks` la entrega como regla.
- **Dónde se aplica** (sin tocar `Rodaje.Core`):
  - Soltar un medio en la línea de tiempo: `TimelinePlanning.PlanDrop` lee `DragOptions.LinkedAudio` (propiedad nueva en `ClipDrag.cs`, que `TimelineView.Options()` llena con la de la sesión). Vale para la pista magnética y para las demás pistas de video.
  - Añadir al final (botón/doble clic de la biblioteca y archivos arrastrados a la ventana, vía `OpenFilesAsync`): `EditorSession.AppendToTimeline`.
  - `EditorSession.LinkedAudioTracks` (en `EditorSession.Files.cs`); `MainWindow` la pone desde los ajustes al crear la sesión.
- **Ajustes en la interfaz:** como en la tarea 7, no hay página de Ajustes; va en el menú Ver → «Pistas de video que traen su audio»: «Todas» y una casilla por «Video 1» a «Video 8». Cada clic guarda en `ajustes.json` en el acto y rige desde el siguiente medio que se suelte o añada.
- **Pruebas nuevas:** `tests/Rodaje.App.Tests/Settings/LinkedAudioTracksTests.cs`, 14 casos, con la sesión real (`EditorSession` + `TimelinePlanning`): regla y guardado (por defecto todas, lista, alternar, lugar de la pista, `Normalize`, guardar/leer `null`/lista/vacía, ajustes viejos sin el campo); con 3 pistas de video y la lista {1, 2}: Video 1 y 2 traen su audio enlazado, Video 3 entra sin clip de audio y deshacer lo quita; «todas» como siempre; la principal sin permiso; música y pistas de audio no limitadas; «añadir al final» y arrastrar archivos a la ventana respetan la regla.

## Decisión: «sin clip de audio» y no «audio desactivado»

Si el video cae en una pista que no trae su audio, el medio entra **sin clip de audio**. Motivos:

1. El sonido solo suena desde las pistas de audio (`TimelinePlan` recorre `AudioTracks`); el clip de video no suena por sí mismo, así que no hace falta un clip «apagado» para silenciarlo.
2. Un clip desactivado ocuparía tramo en una pista de audio: taparía o desplazaría lo que haya (música, voz), y al soltar en pistas no magnéticas la búsqueda de «la primera pista de audio libre en ese tramo» lo vería como ocupado. Justo lo que la persona quiere evitar al decir «de Video 3 no».
3. Ensuciaría la línea de tiempo y las ediciones con enlazados (ripple, recortes) con clips que no se oyen.
4. Si luego quiere el sonido de ese clip, la tarea 18 («separar el audio») crea el clip de audio enlazado bajo demanda; con un clip desactivado habría dos maneras de lo mismo.

Otras decisiones:

- La regla no toca medios sin imagen (música): siguen yendo a la última pista de audio. Tampoco un video soltado directamente sobre una pista de audio: la persona eligió esa pista.
- El menú muestra Video 1–8 (el máximo de pistas de un proyecto nuevo). Desde «todas», quitar una pista crea la lista explícita 1–8 (o hasta el número de pistas de la secuencia si son más) sin ella; marcar todas otra vez deja una lista, no «Todas»: para volver a «todas, también las futuras» está el elemento «Todas».

## Pendiente

- Nada de lo pedido. Una página de Ajustes propia, si llega, debería reunir este submenú y los de la tarea 7.

## Choques

- `TitleBar.xaml(.cs)`, `MainWindow.xaml.cs` y `TimelineView.Input.cs` los tocan también otras tareas de interfaz: posibles conflictos triviales.
- La tarea 18 (separar audio) crea clips de audio enlazados: no choca con la regla, que solo rige al importar/soltar.

## Pruebas omitidas

- `Rodaje.App.Tests` en Linux (`-p:EnableWindowsTargeting=true`): antes 564 pruebas, 1 omitida; después 578, 1 omitida. La omitida es `PackageContentEndToEndTests` (necesita GPU/D3D11 y los paquetes de contenido; preexistente, no la toqué).
- En Linux fallan 2–3 pruebas que también fallan igual en `origin/main` sin mis cambios (`ProxyJobListTests.CicloCompleto_EnColaGenerandoListo`, `ThumbnailCacheTests.Disk_names_carry_fingerprint_height_and_time` y, a veces, `ClipVisualsTests.Single_requests_snap…`): dependen de rutas o tiempos de Windows. En la CI de Windows se verifican. Los grupos tocados (Settings, PlanningTests, LinkedAudio) pasan: 55 de 55.

## Qué comprobar en Windows

1. Que compile la App WinUI (`TitleBar.xaml(.cs)`, `MainWindow.xaml.cs`, `TimelineView.Input.cs`): no se puede en Linux.
2. Ver → «Pistas de video que traen su audio»: con «Todas» marcada están marcadas todas las casillas; quitar «Video 3» deja «Todas» sin marcar y Video 1, 2, 4–8 marcadas.
3. Proyecto con 3 pistas de video: soltar un video con sonido en Video 1 y en Video 2 → aparece su audio enlazado; en Video 3 → solo el video, sin clip de audio (el fantasma de arrastre no muestra el del audio).
4. Con «Video 1» quitada: doble clic en la biblioteca o arrastrar archivos a la ventana → el video va al final de la principal sin audio.
5. Cerrar y abrir la app: la elección se conserva (`%LOCALAPPDATA%\Rodaje\ajustes.json`, campo `linkedAudioVideoTracks`).
