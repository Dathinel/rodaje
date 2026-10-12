# Exportar el fotograma actual como imagen

Rama `claude/qdv-fotograma` (tarea 29 de la cola, función 34 de `docs/pendientes.md`), PR #32.

## Hecho

- **`src/Rodaje.Export/Still/`** (nuevo):
  - `StillSettings`: PNG o JPEG, calidad JPEG de 1 a 100 (92 por omisión), alfa (solo PNG), tamaño, forma (barras, recorte, estirado), filtro de escala y recorte. `FromPreset` toma el tamaño, la forma, el filtro, el recorte y el alfa del video de un `ExportPreset`; `FormatFor` elige el formato por la extensión.
  - `StillPlan` (sin GPU ni procesos): el cuadro del cursor (`Time.ToFrames`, hacia abajo, como el visor), el tamaño con el mismo `VideoExportSettings.OutputSize` de la exportación (sin forzar medidas pares, porque es RGB), el formato de píxel que baja la GPU (`Rgb24`, o `Rgba` con alfa recto) y la línea de `ffmpeg.exe`. Con ajustes que no valen lanza `ExportException(InvalidSettings)` con el motivo en español.
  - `StillExporter`: el mismo camino que `ExportJob` (D-028): un `GpuDevice` propio, `TitleRasterizer` con las fuentes de los paquetes, `OfflineTimeline.Open/Prepare/Render` del cuadro (exacto, sin proxies), comprobación de fuentes que faltan, `Compositor.RenderStrict` (con `TransparentBackground` si hay alfa), `ExportFrameConverter` a RGB(A) en la GPU y `StillEncoder`. La política de contenido que falta es la misma de la exportación: sin `ContinueWithoutMissing` para con `ContentMissing`/`EffectFailed`. Un cursor tras el final exporta el último cuadro. `ExportAsync` corre en un hilo propio.
  - `StillEncoder`: escribe el cuadro con el `FfmpegPipeSink` de la exportación como `ImageSequence` en una carpeta temporal `…​.fotograma.rodaje-tmp` junto al destino, y renombra la única imagen al archivo final (mismo disco, reemplaza si existe). Comprueba antes de la GPU que el destino no sea un medio del proyecto, que se pueda escribir y que no esté abierto, y registra el temporal en `ExportJournal`.
- **Interfaz (lo mínimo):** «Archivo → Exportar fotograma...» y el atajo **Ctrl+Mayús+E** (acción `file.exportFrame` en `EditorShortcuts`). `Views/Export/StillExportCommand.cs` abre el diálogo de guardar de Windows (PNG o JPEG; carpeta del proyecto o Imágenes; nombre «proyecto - cuadro N»), exporta al tamaño de la secuencia y avisa con el resultado o el error (`ExportErrorText`), con el botón «exportar sin él» si falta contenido.
- **Pruebas** (`tests/Rodaje.Export.Tests/Still/`, archivos nuevos): ver abajo.

## Decisiones

- **Codificación por la FFmpeg que ya existe, no por WIC.** Reutiliza `FfmpegPipeSink` entero (exe LGPL verificado, `ChildProcessJob`, temporal `.rodaje-tmp`, registro para limpiar tras un apagón, disco y destino comprobados), no añade interop COM nuevo y la codificación se prueba en la CI sin GPU. El costo es arrancar un proceso (decenas de ms), que en una acción de un clic no se nota.
- **JPEG estándar: BT.601 de rango completo, 4:4:4.** El `mjpeg` del catálogo recibe Y'CbCr limitado con `-strict unofficial` (sirve para video MJPEG, pero como foto se vería lavada en cualquier visor). Aquí la GPU baja RGB y `ffmpeg.exe` convierte con `scale=out_color_matrix=bt601:out_range=full,format=yuv444p`, con `-color_range pc -colorspace bt470bg`. Sin `-strict`: si el rango no llegara completo, el codificador falla en vez de escribir un JPEG mal. 4:4:4 porque el croma submuestreado se nota en los bordes de los títulos. Calidad 1–100 → `-q:v` 31–2 lineal.
- **«Alfa si la secuencia lo tiene»:** el modelo no guarda alfa por secuencia; como en la exportación de video (`VideoExportSettings.Alpha`), es una opción: PNG con fondo transparente donde no hay capas. Sin ella, fondo negro como el visor. `FromPreset` lo toma del preajuste.
- **El atajo Ctrl+Mayús+E** estaba libre en los tres estilos; sigue a Ctrl+E (Exportar).
- **Lógica duplicada de `ExportJob`:** `CheckFonts` y la composición estricta con su respaldo están copiadas (unas 40 líneas) porque son privadas de `ExportJob` y `Running/` no está entre los caminos de la tarea. Si se quiere, se pueden sacar a un ayudante interno común en una tarea aparte.

## Pruebas

| Grupo | Casos | Dónde corre | Qué comprueba |
|---|---|---|---|
| `StillPlanTests` | 18 | Linux y Windows | tamaño de la secuencia, de un preajuste (9:16, 4K), de un lado o del recorte; cuadro del cursor a 29,97; Rgb24/Rgba; argumentos y filtros de JPEG; escala de calidad; validación |
| `StillEncoderTests` | 6 | Windows con el `ffmpeg.exe` LGPL (incluida la CI) | PNG exacto con filas con relleno, PNG con alfa exacto, JPEG `pc` 4:4:4 con PSNR > 38 dB y el gris medio intacto, menos calidad = archivo más chico, reemplaza el que existe, no pisa un medio |
| `StillExporterTests` (títulos) | 2 | Windows con GPU con texturas (vale WARP) | con alfa: fondo transparente y título opaco; sin alfa, PSNR ≥ 45 dB **contra la vista previa asentada**; la fuente que falta para sin dejar archivos y con el respaldo exporta |
| `StillExporterTests` (medios) | 4 | Windows con GPU y D3D11VA | PNG de dos cuadros de la secuencia de dos pistas con color, PiP y un título: PSNR ≥ 45 dB **contra la vista previa** y el título visible; JPEG al 95 ≥ 35 dB; preajuste vertical 1080×1920 con barras; cursor tras el final |

La comparación con la vista previa usa el mismo método que `ExportTitleTests`: sesión, rasterizador y compositor como el reproductor en pausa, ya asentado.

**Omitidas.** En Linux: antes 0 de este grupo (no existía), ahora 12 de 30 (las 6 de `StillEncoderTests` por falta de las bibliotecas FFmpeg 9 y del `ffmpeg.exe` LGPL; las 6 de `StillExporterTests` por lo mismo, y sin Direct3D 11). En la CI de Windows: las 4 de medios se omiten por no haber decodificación D3D11VA (WARP); las de codificación y las de títulos deberían correr (en la primera corrida se omitían todas porque pedían los codificadores de proxies; el commit «que corran en la CI de Windows» lo corrige: solo piden el `ffmpeg.exe` LGPL verificado y, las de títulos, la GPU con texturas). Cifras de la CI en el PR.

## Pendiente

- Ajustes del fotograma en la interfaz (calidad JPEG, alfa, tamaño del preajuste): la biblioteca los admite; el menú usa lo de por omisión (tamaño de la secuencia, PNG opaco o JPEG al 92). Lo natural es un pequeño diálogo o una opción en la página Exportar.
- Documentar `Still/` en `docs/arquitectura/exportacion.md` (fuera de los caminos de esta tarea).

## Posibles choques

- `src/Rodaje.App/Shell/MainWindow.xaml.cs`: **una línea** tras `AnalysisCommands.Bind` (`StillExportCommand.Bind(_router, _services, _hwnd)`).
- `src/Rodaje.App/Shell/TitleBar.xaml`: una línea en el menú Archivo tras «Exportar...».
- `src/Rodaje.App/Services/Shortcuts/EditorShortcuts.cs`: una constante en `ActionIds` y una línea en `Catalog`.
- Tarea 31 (catálogo único de codificadores): `StillPlan` toma `png` y `mjpeg` de `ExportEncoderCatalog.Find`, no de `ExportEncoderCandidates`; si el catálogo cambia de nombre o de forma, solo cambian esas dos líneas.

## Qué debe comprobar Daniel en Windows

1. `ABRIR.bat` con la RTX: `StillExporterTests` las 6 en verde (salen las PSNR en la salida de cada prueba).
2. En la app, con un proyecto con video y un título: pausar en un cuadro, Ctrl+Mayús+E, guardar como PNG y como JPEG; abrir los dos en el visor de fotos de Windows y compararlos con el visor de Rodaje (mismos colores y contraste, el título igual).
3. Con una LUT de un paquete desinstalado: el aviso «Falta contenido» con el botón para exportar sin ella.
4. Guardar encima de un archivo existente y encima de un medio del proyecto (este último debe negarse).
