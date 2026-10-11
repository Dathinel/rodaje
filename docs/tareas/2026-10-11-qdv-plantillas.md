# Plantillas de proyecto (tarea 24)

Rama `claude/qdv-plantillas`, PR #19. Función 33 de `docs/pendientes.md`.

## Hecho

- `src/Rodaje.App/Services/Templates/` (nuevo):
  - `ProjectTemplate`: tamaño, tasa, frecuencia de audio y pistas de video y de audio con nombre y función (`TemplateTrack`). `CreateProject` arma la secuencia con ese formato y esas pistas en el mismo orden (la primera de video sigue siendo la principal); `FromSequence` es «Guardar como plantilla» (formato, pistas, nombres y funciones; sin clips, efectos ni estados de silencio o bloqueo). `Normalized` corrige lo que llega de un archivo: nombres sin caracteres de control y de 80 como mucho (sin partir un emoji), lados pares entre 16 y 16 384, de 1 a 8 pistas por tipo (`NewProjectTracks.Min/Max`), pistas sin nombre como «Video N»; descarta las que no tienen arreglo (sin id o nombre, tasa fuera de 1–240 fps, frecuencia fuera de 8–384 kHz).
  - `BuiltInTemplates`: Horizontal 16:9 1080p (1920×1080), Vertical 9:16 1080×1920 (TikTok, Reels, Shorts), Cuadrado 1:1 (1080×1080) y Horizontal 16:9 4K (3840×2160); todas a 30 fps, 48 kHz, 2 pistas de video y 2 de audio. Identificadores `fabrica.*`.
  - `ProjectTemplateStore`: las propias en `%LOCALAPPDATA%\Rodaje\plantillas.json` (JSON generado en compilación, apto para AOT, escritura atómica con `AppDataPaths`). Guardar con un nombre que ya existe (sin distinguir mayúsculas) reemplaza; máximo 50; no se pisa una de fábrica; si la escritura falla no cambia nada en memoria; archivo dañado se aparta como `plantillas.dañado.json`; más de 1 MB no se lee.
  - `EditorSession.Templates.cs` (parcial de la sesión): `NewProjectFromTemplateAsync` (como «Proyecto nuevo»: pregunta si guardar, historial vacío) y `CreateTemplateFromSequence`. `KeepsTemplateFormat`: mientras siga abierto el proyecto que salió de una plantilla, el primer video importado no le impone su tamaño ni su tasa.
- `EditorSession.Files.cs`: una condición más en `AdoptFormatIfEmpty` (`|| KeepsTemplateFormat`). «Proyecto nuevo» corriente sigue adoptando el formato del primer video como hasta ahora.
- Interfaz (lo mínimo; no hay diálogo de proyecto nuevo, así que va en el menú Archivo de `TitleBar`): «Proyecto nuevo desde plantilla» (las de fábrica, las propias y «Borrar plantilla propia»; el resumen del formato sale como tooltip) y «Guardar como plantilla...» (diálogo con el nombre, por `DialogGate`, como «Guardar como transición»).
- Pruebas nuevas en `tests/Rodaje.App.Tests/Templates/`: `ProjectTemplateTests` (13), `ProjectTemplateStoreTests` (9), `TemplateSessionTests` (4). En Linux con `-p:EnableWindowsTargeting=true`: 62 de 62 en Templates, NewProjectTracks y EditorSessionTests.

## Pruebas

- `Rodaje.App.Tests` completo en Linux (sin TiempoReal, Rendimiento ni SoloWindows): los únicos fallos son 4 que ya fallan en `origin/main` en Linux (`ProxyJobListTests`, `ThumbnailCacheTests`, 2 de `ExportPreferencesTests`, rutas y medios de Windows) y `ClipVisualsTests.Single_requests_snap…`, intermitente (pasa 2 de 3 veces solo; no toca nada de esta tarea).
- Pruebas omitidas: ninguna omitida ni excluida; 0 antes, 0 después.

## Decisiones

- Las de fábrica traen 2 + 2 pistas fijas y no siguen el ajuste «Pistas de un proyecto nuevo» (tarea 7), que rige solo para «Proyecto nuevo» corriente; así la plantilla dice siempre lo mismo.
- Un proyecto creado desde plantilla conserva su formato al importar; se identifica por la instancia del proyecto, sin tocar el formato `.rodaje`. Al guardarlo y reabrirlo vuelve a la regla de siempre (solo se adopta si no tiene medios ni clips).
- La función de la pista (`Role`, por ejemplo subtítulos) se guarda en la plantilla; los estados (silencio, bloqueo, oculta) no.
- «Estilos» de la función 33 (texto) quedan para cuando exista la fase 5b en la app.

## Pendiente

- Elegir la tasa de cuadros al crear (las de fábrica van a 30 fps; para 25/60 se guarda una propia desde un proyecto con esa tasa). Un diálogo de proyecto nuevo con tamaño y tasa a mano sería otra tarea.
- Renombrar plantillas propias (hoy se reemplazan guardando con el mismo nombre o se borran).

## Choques posibles

- `TitleBar.xaml(.cs)` lo tocan otras tareas de interfaz (zoom a la selección, autoguardado si añade algo al menú Archivo): conflicto trivial de líneas.
- `EditorSession.Files.cs`: una sola línea en `AdoptFormatIfEmpty`.

## Qué comprobar en Windows

1. Archivo → Proyecto nuevo desde plantilla → Vertical 9:16: el visor queda vertical 1080×1920; importar un video horizontal no cambia el formato (queda con barras).
2. Archivo → Proyecto nuevo (corriente) y luego importar un video 4K: la secuencia toma 4K como antes.
3. Renombrar pistas, Guardar como plantilla «Mi canal», cerrar y abrir la app: aparece en el submenú con sus pistas y nombres; «Borrar plantilla propia» la quita.
4. Que el tooltip con el resumen se vea al pasar sobre cada plantilla.
