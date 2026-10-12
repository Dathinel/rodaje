# Exportar fotograma: extensión, cancelación y reentrada

Rama `claude/fotograma-robusto` (tarea 49 de la cola), PR #46. Sigue a la tarea 29 (`2026-10-12-qdv-fotograma.md`).

## Hecho

1. **Extensión.** `StillSettings` gana `HasExtensionOf`, `WithExtension` y `ExtensionProblem`. `StillPlan.Create` rechaza
   con `ExportErrorCode.InvalidSettings` una ruta cuya extensión no es la del formato: `foto.bmp`, `foto` sin extensión,
   un JPEG en `foto.png` o un PNG en `foto.jpg`. Solo valen `.png` para PNG y `.jpg`/`.jpeg` para JPEG (sin distinguir
   mayúsculas). Como `StillExporter` crea el plan antes que el `StillEncoder`, no se llega a escribir nada.
   «Exportar fotograma» añade la extensión del formato a lo que se eligió en el diálogo (`foto.bmp` → `foto.bmp.png`):
   se añade en vez de cambiarla para no perder parte de un nombre con puntos (`Proyecto v1.2 - cuadro 10`).
   `FormatFor` no cambia (PNG para lo que no es `.jpg`/`.jpeg`).
2. **Cancelación y reentrada** (`src/Rodaje.App/Views/Export/StillExportCommand.cs`). La línea que citaba la tarea
   (`:76`, `exporter.ExportAsync(request)` sin token) era correcta en origin/main. Ahora `Bind` devuelve un `Runner` por
   ventana que guarda la exportación en marcha (`CancellationTokenSource`):
   - mientras hay una (desde que se abre el diálogo de guardar hasta el aviso final), Ctrl+Mayús+E y el menú no lanzan
     otra; vuelven a mostrar el aviso de «exportando». La acción sigue consumiendo la tecla (no se usa `canExecute`,
     que la dejaría seguir su camino);
   - el indicador es un aviso informativo «Exportando fotograma...» con el botón «Cancelar», que cancela el token que
     reciben `Task.Run` (buscar `ffmpeg.exe`) y `StillExporter.ExportAsync`; al cancelar sale «Fotograma cancelado. No se
     guardó ningún archivo.»;
   - al cerrar la ventana (`Dispose` del enlace) se cancela la que haya, sin aviso;
   - el reintento «Exportar sin él» pasa por el mismo control;
   - los números para el usuario (cuadro, tamaño, nombre propuesto) van con `SpanishNumbers.Format`, no con la cultura
     del hilo.

## Decisiones

- **Rechazar en la biblioteca y añadir en la interfaz.** La biblioteca no adivina: una ruta que miente es un error de
  ajustes. Quien elige la ruta (el diálogo) es quien la corrige.
- **El ayudante de `StillPlanTests`** usaba `/tmp/foto.png` también para JPEG, que ahora es un caso rechazado; cambia
  una línea para usar la extensión del formato (`"/tmp/foto." + StillSettings.Extension(settings.Format)`). Las
  pruebas nuevas van en `StillExtensionTests.cs`.
- **Indicador como aviso**, no como estado de la barra: la barra de mensajes ya es por donde llegan el resultado y el
  error, y no hacía falta tocar archivos fuera de los caminos de la tarea.

## Pruebas

- `StillExtensionTests` (nuevo, 14 casos, Linux y Windows): extensiones que valen, las que se rechazan con
  `InvalidSettings` y el motivo, `WithExtension` como la usa el comando.
- En Linux, `Still.*`: 32 pasan y 12 omitidas de 44 (antes 18 pasan y 12 omitidas de 30). Las 12 omitidas son las
  mismas de la tarea 29: `StillEncoderTests` (6) y `StillExporterTests` (6) piden el `ffmpeg.exe` LGPL y Direct3D 11.
- `StillExportCommand` no tiene pruebas automáticas (es interfaz WinUI con diálogo modal); no compila en Linux (el
  compilador XAML es de Windows): lo verifica la CI de Windows.

## Pendiente

- Nada de la tarea. Si se quiere, un indicador en la barra de estado en vez del aviso.

## Posibles choques

- Ninguno conocido: solo `Still/`, `StillExportCommand.cs` y pruebas de `Still/`. No toca `FfmpegPipeSink.cs` (tarea 44).

## Qué debe comprobar Daniel en Windows

1. Ctrl+Mayús+E, guardar como `prueba.bmp`: debe quedar `prueba.bmp.png` y abrirse como PNG.
2. Ctrl+Mayús+E y, mientras sale «Exportando fotograma...», volver a pulsarlo varias veces: no se abre otro diálogo ni
   otra exportación; al final, un solo archivo y ninguna carpeta `.fotograma.rodaje-tmp` junto a él.
3. En un proyecto 4K pesado, pulsar «Cancelar» en el aviso: «Fotograma cancelado» y sin archivo ni temporal.
