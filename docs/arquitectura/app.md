# App: `Rodaje.App` y el visor provisional

Hay dos ejecutables que juntan [Motor](motor.md) y [Render](render.md) en una ventana. `Rodaje.Visor` es la ventana Win32 provisional de las fases 2 y 3 y se borra cuando la app la reemplace. `Rodaje.App` será la interfaz final en WinUI 3 (fase 4, en construcción). Este documento describe el estado actual; el diseño aprobado es la maqueta de [D-018](../decisiones.md).

## `Rodaje.App` (WinUI 3)

Es la interfaz final, sin paquete MSIX, autocontenida y con Native AOT (el [sondeo](../sondeo-winui.md) lo confirmó). `herramientas\app.ps1` la publica en `dist\Rodaje\` con FFmpeg LGPL y la abre; la usan `Rodaje.bat` y `app.ps1 -Medir N`, que reproduce, mide y cierra. El Windows App SDK ocupa unos 57 MB junto al exe y sus términos están en [`LICENCIAS.md`](../../LICENCIAS.md).

- **Proyecto.** `Rodaje.exe` es `net10.0-windows10.0.26100.0` para win-x64, con `WindowsPackageType=None`, Windows App SDK autocontenido y `PublishAot`. `EnableMsixTooling` se deja activo (sin él no hay `resources.pri`), y se usan los paquetes sueltos `WinUI`, `Foundation` e `InteractiveExperiences` en vez del metapaquete.

### Carpetas

| Carpeta | Qué hay | Depende de WinUI |
|---|---|---|
| `Views/Timeline/Logic/` | La lógica de la línea de tiempo (ventana, regla, filas, clic, ajuste, arrastres, soltar medios) | **No**: también la compila `tests/Rodaje.App.Tests` |
| `Services/` | `EditorSession` (fuente única de verdad), `TimelineState`, atajos (`Shortcuts/`), ajustes (`Settings/`), miniaturas y formas de onda (`Media/`), complementos (`Packages/`), `IPlaybackEngine`, `IMediaProber`, `IEditorDialogs` | **No**: `tests/Rodaje.App.Tests` compila estos archivos tal cual, así que usar WinUI aquí rompe las pruebas |
| `Shell/` | `MainWindow` (distribución con separadores), `TitleBar`, `KeyboardShortcutHost` (atajos en el túnel de la raíz), `PaneSplitter`, `ShellDialogs` y `FileDialogs` (diálogos de Windows por su vtable), `EditorServices` e `IEditorView`, `MeasurementRun` | Sí |
| `Theme/` | `Palette.xaml` (colores de D-018 y claves de WinUI redefinidas), `Typography.xaml`, `Controls.xaml` (estilos con esquinas de 2 px y transiciones de 60 ms) | Sí |
| `Views/<Área>/` | Una vista por carpeta: `Library`, `Player` (con `PlayerEngine` y la cadena de intercambio), `Meters`, `Inspector` (pestañas `Properties`, `Filters`, `History`, `Jobs`), `Timeline`, `Shortcuts`, `Plugins` | Sí |

### Cómo se habla con el editor

```mermaid
flowchart LR
    V["Vistas (Views/*)"] -->|"Execute(ICommand), Undo, Seek,<br/>ImportMediaAsync, Select..."| S["EditorSession<br/>(hilo de interfaz)"]
    S -->|"ProjectOpened, ProjectEdited, HistoryChanged,<br/>SelectionChanged, TransportChanged, PropertyChanged"| V
    S -->|"Load / Reload (copia del proyecto)<br/>Play, Seek, StepFrames"| P["PlayerEngine<br/>(hilo 'Rodaje render')"]
    P -->|"PlaybackSnapshot (volatile)"| T["Temporizador de MainWindow (30 Hz)"] -->|UpdateTransport| S
    K["KeyboardShortcutHost<br/>(PreviewKeyDown)"] -->|KeyChord| R["ShortcutRouter"] -->|"acción unida"| S
```

- **Una sola fuente de verdad.** `EditorSession` tiene el `Project`, la secuencia que se ve, el `UndoStack`, la selección, el transporte que publica el render, el archivo y si hay cambios. Solo se usa desde el hilo de la interfaz (`VerifyAccess`). Las vistas leen el modelo pero lo cambian solo con comandos (`Execute`); los gestos largos van entre `BeginGesture` y `EndGesture` para deshacerse en un paso, y `BreakMerge` corta la fusión al soltar un deslizador.
- **El render nunca lee el modelo que se edita.** Cada cambio llega a `PlayerEngine` como `Project.Clone()`; el hilo de render solo aplica la última copia pendiente (`TimelineSession.Reload`).
- **Rutas.** En memoria, las rutas de los medios son absolutas; al guardar se escriben relativas a la carpeta del `.rodaje` (`ProjectPaths`).
- **Importar.** `ImportMediaAsync` lee los archivos fuera del hilo de la interfaz y los agrega a la biblioteca en un paso; la secuencia vacía toma el formato del primer video. Doble clic en la biblioteca llama a `AppendToTimeline` (al final de la pista principal, con el audio enlazado). Los archivos de la línea de comandos se leen mientras se crea la ventana (`PrefetchingProber`) y se ponen seguidos en la pista principal.
- **Atajos.** `ShortcutRegistry` guarda el catálogo (`EditorShortcuts.Catalog` más lo que registre cada vista) con teclas por estilo (Rodaje, CapCut, Vegas) y los cambios de la persona por estilo en `%LOCALAPPDATA%\Rodaje\atajos.json`. `ShortcutRouter` une cada acción con quien la ejecuta (gana el último que puede) y decide qué hacer con cada tecla: con el foco en un campo de texto solo pasan las acciones marcadas `WorksInTextInput`. Los menús lanzan acciones por su identificador, así muestran la tecla vigente.
- **Página de atajos** (`Views/Shortcuts`). Grupos en dos columnas (orden de `ShortcutGroups.Order`); cada fila muestra `Label`, `Description` (una frase sencilla), sus teclas como fichas y los botones Cambiar, añadir, quitar y volver a la de fábrica. La captura (`KeyCapture`) suspende el router y se queda con cada tecla hasta soltarla, para que ni lance una acción ni active el botón con el foco; rechaza lo reservado (`ReservedKeys`: tecla Windows, Alt+F4, Alt+Tab, Tab, Impr Pant...) y Esc la cancela. Cada cambio pasa por `ShortcutCustomizer`, que intercambia teclas si chocan y devuelve un aviso con Deshacer («"S" era de "Cortar en el cursor": se intercambiaron»). `ShortcutSearch` busca sin tildes ni mayúsculas por texto o por tecla («s», «ctrl+b», «ñ»). «Ver teclado» dibuja un teclado ISO español (`KeyboardGeometry`) con la acción de cada tecla en el color de su grupo; Ctrl, Mayús y Alt del dibujo cambian de capa y un clic filtra la lista. «Hoja rápida» (`CheatSheet`) junta los 15 atajos más útiles y los guarda en Documentos como HTML imprimible o PNG.
- **Teclas de signos y distribución.** Los archivos y el catálogo nombran las teclas VK_OEM_* por su posición estadounidense («Alt+[»). Para mostrarlas, `KeyNames.DisplayLayout` (la ventana pone `SystemKeyboardLayout`, que consulta `MapVirtualKeyEx` con la distribución activa) da el carácter impreso: en un teclado español esa misma tecla se ve «Alt+'». El teclado dibujado pide a la distribución la tecla virtual de cada posición física (código de exploración), así cada acción cae en la tecla correcta.
- **Ajustes.** `ajustes.json` guarda carpetas, recientes, tamaños de paneles y ventana, estilo de atajos y proxies. Los dos archivos se escriben de forma atómica y, si están dañados, se apartan con el sufijo `.dañado`.
- **Cerrar.** Con cambios sin guardar se pregunta (Guardar, No guardar, Cancelar).

### Miniaturas y formas de onda (`Services/Media`)

`IClipVisuals` es lo que la línea de tiempo y la biblioteca usan para dibujar medios. Las tres llamadas se hacen desde el hilo de la interfaz y nunca esperan: devuelven lo que ya está en memoria, piden lo que falta y, cuando llega, `Changed` avisa (también en el hilo de la interfaz, una vez por cada medio que usa el mismo archivo) para que se vuelva a pedir.

```csharp
event EventHandler<ClipVisualsChangedEventArgs>? Changed;                 // MediaId, Kind (Thumbnails | Waveform)
Filmstrip GetFilmstrip(string mediaId, Time sourceStart, Time sourceEnd, int count, int height);
WaveformSlice GetWaveform(string mediaId, Time sourceStart, Time sourceEnd, int buckets);
BgraImage? GetThumbnail(string mediaId, Time sourceTime, int height);
```

- **Miniaturas.** `ThumbnailDecoder` (en `Rodaje.Media`) decodifica en la CPU sin filtro de desbloqueo y saltándose los cuadros que nadie usa como referencia, y reduce con swscale a BGRA del alto pedido con la rotación aplicada. Va en la CPU a propósito: así no toca el presupuesto de VRAM de la reproducción ([D-019](../decisiones.md)) y una miniatura de 1080p sale en unos 50 ms. Si el siguiente instante queda poco más adelante sigue decodificando sin buscar, así una tira en orden recorre el archivo una vez.
- **Rejilla.** Los instantes de una tira se ajustan a una rejilla de potencias de dos (1/32 s, 1/16 s… según la separación) y los altos a unas pocas clases (32 a 256): al hacer zoom la rejilla fina contiene a la gruesa y al desplazar casi todo ya está hecho.
- **Hilos.** Uno de baja prioridad para miniaturas, con hasta tres decodificadores abiertos que se cierran tras 20 s sin uso, y otro para formas de onda. Los pedidos llegan en tandas; la más reciente va primero y una tanda vieja cede el paso en cuanto llega otra.
- **Cachés.** En memoria, 128 MB con expulsión de la menos usada; en disco, `cache\miniaturas` (512 MB) y `cache\ondas` (128 MB), con la huella del original en el nombre: si el archivo cambia, no se reutiliza nada viejo. Al arrancar se recorta lo que pase del presupuesto.
- **Formas de onda.** `WaveformPeaks` guarda mínimo y máximo por cada 256 muestras a 48 kHz y niveles reducidos de 4 en 4; cada pedido usa el más grueso que todavía da un pico por tramo. Mientras se calcula crece por el final y se puede leer a la vez.
- **Adaptador WinUI.** `Views/Shared/BgraBitmaps` convierte un `BgraImage` en `WriteableBitmap` una sola vez por imagen.
- **Para la línea de tiempo.** Su contrato es otro (`Views/Timeline/IClipVisuals`: una miniatura por llamada y la forma de onda entera); `Views/Shared/TimelineClipVisuals` lo cumple con `ClipVisuals` y la ventana lo asigna a `TimelineView.ClipVisuals`. La separación entre miniaturas se deduce de dos pedidos seguidos del mismo medio y decide la rejilla (`GetNearestThumbnail`); mientras falta, se devuelve la más cercana que ya esté en memoria. La forma de onda llega completa, con picos de 1024 muestras (21 ms).

### Complementos (`Services/Packages`)

`PackageService` junta el catálogo que viaja en el ejecutable (`catalogo.json`), lo instalado y el instalador de [`Rodaje.Paquetes`](../formato-paquete.md); lee el disco fuera del hilo de la interfaz y avisa con `Changed`. `LibraryElements` decide qué muestra cada categoría de la biblioteca: lo que trae el motor (con «Próximamente» lo que llega en la fase 5), lo de los paquetes y lo propio. Las dos cosas llegan a las vistas por `EditorServices.Visuals` y `EditorServices.Packages`.

- **Biblioteca** (`Views/Library`). Medios con portada real, duración y resolución; importar, soltar archivos (un `.paquete` soltado se instala), doble clic para añadir a la pista principal, arrastrar a la línea de tiempo con el formato `rodaje/media-id` (el texto es el id del medio) y menú contextual: añadir, generar proxy (con el avance sobre la portada), mostrar en el Explorador y quitar si no se usa. Las portadas se piden solo para lo que la cuadrícula hace visible. Texto, Efectos, Transiciones y Filtros se arrastran con `rodaje/elemento`. Con `RODAJE_MEDIR_BIBLIOTECA=archivo` la vista mide el desplazamiento y cierra la app (`LibraryScrollMeasurement`).
- **Complementos** (`Views/Plugins`). Contenido y módulos de IA con su estado, «Funciona en», enlace al repositorio, Instalar desde archivo y Desinstalar; la descarga desde GitHub queda para la fase 7 detrás de `IPackageDownloader`.

### Reproductor y medidores

- **Cabecera:** proporción (con resolución y cuadros por segundo en la ayuda), avisos de lanzadera («▶ ×4») y de «Proxy» cuando una capa en pantalla sale de un proxy, zoom (Ajustar, 50 %, 100 %: píxeles de la secuencia por píxel físico, centrado), márgenes seguros (acción 93 %, títulos 90 %, EBU R 95) y pantalla completa (F, doble clic o el botón; Esc sale). En pantalla completa la ventana oculta los demás paneles y queda solo la imagen.
- **Transporte:** código de tiempo en el acento, cuadro anterior, reproducir o pausar (botón de acento), cuadro siguiente y duración.
- **Lanzadera J/K/L:** L reproduce y luego dobla hasta ×8, J va hacia atrás desde ×1, K detiene. A ×1 reproduce de verdad; a otras velocidades `PlayerEngine` hace un seek por refresco en pausa en cuanto el anterior muestra su cuadro, hacia la posición que corresponde a la velocidad (sin audio).
- **Medidores** (`Views/Meters`): barras L/R con la escala IEC, verde hasta −18 dB, amarillo hasta −6 y rojo encima, franja del RMS (300 ms), raya del pico retenido, aviso de saturación que queda encendido hasta hacer clic y lectura del mayor pico. Se dibujan con `SpriteVisual` en `CompositionTarget.Rendering`, sin diseño de XAML ni reservas por cuadro, y se desuscriben cuando no queda nada que animar. La caída (12, 20 o 40 dB/s) se elige con clic derecho y se guarda en `ajustes.json`. Ver [Audio](audio.md#medidores-de-nivel-metering).
- **Ediciones mientras reproduce:** el hilo de render vuelve a tomar la última copia del proyecto después de esperar el vsync, justo antes de componer: de 24 a 10 ms de mediana entre `Reload` y el cuadro presentado (`edicion.a_present` en `app.ps1 -Medir`).

### Línea de tiempo

- **Barra estilo Vegas:** herramientas normal (V), envolvente (E), selección y zoom (Z); interruptores de pista principal magnética, ajuste (N), ripple automático con su modo (pistas afectadas, todas, o pistas, marcadores y regiones), fundido cruzado automático, bloquear envolventes e ignorar grupos (se guardan en `ajustes.json`, sección `timeline`, junto con el zoom); y cortar (S), recortar inicio y final, borrar, marcador (M), región (R), deshacer, rehacer y zoom. Cada botón lanza su acción por el `ShortcutRouter` y su ayuda muestra la tecla vigente.
- **Lienzo:** regla con marcas que se adaptan al zoom (cuadros por debajo del segundo), marcadores y regiones; cabeceras con nombre, «principal · magnética» o «libre» y los interruptores de ocultar, silenciar y bloquear (`Edits.SetTrackState`); clips con el color de su tipo, nombre, tira de película o forma de onda provisionales, rombos de keyframes, envolvente de volumen con la herramienta de envolvente, marcas de fundido en los cortes de la principal y contorno de selección.
- **Interacción:** clic y Ctrl+clic seleccionan; arrastrar en vacío dibuja un recuadro que selecciona y marca un rango (la región usa el rango, si no lo seleccionado, si no el clip bajo el cursor); clic en vacío o en la regla mueve el cursor y la regla se puede arrastrar; los marcadores se arrastran y se quitan con clic derecho. Arrastrar un clip lo mueve sin pisar nada en pistas libres (con ajuste a bordes, cursor, marcadores y regiones), lo reordena en la principal magnética (`Edits.ReorderOnMainTrack`) y lo saca de ella o lo mete cerrando o abriendo el hueco también en las pistas de los enlazados (`MagneticEdits`); los bordes recortan (con ripple en la principal o con ripple automático) y Alt+arrastrar hace slip. Todo arrastre es un gesto: los pasos se ejecutan en vivo (se prueban antes sobre una copia, así un paso imposible no avisa ni cambia nada), se funden por clave y quedan como un paso del historial; Esc lo revierte. Rueda: desplaza; Mayús+rueda: pistas; Ctrl+rueda: zoom alrededor del puntero; botón central: arrastrar la vista.
- **Soltar medios de la biblioteca:** formato `rodaje/media-id` (el texto es el identificador del medio). En la principal magnética se inserta en el corte más cercano con el sonido en la primera pista de audio; en las demás pistas tapa lo que haya (con Ctrl, inserta) y el sonido va a la primera pista de audio libre en ese tramo.
- **Dibujo:** `TimelineRenderer` recicla elementos de XAML y solo arma los clips de una ventana de tres anchos de pantalla: desplazarse dentro de ella es cambiar una transformación. Durante un zoom continuo lo dibujado se estira como una imagen (`BitmapCache`) y se rearma nítido 150 ms después del último paso o al pasar de ×1,6. Los bloques son solo fondos (un trazo o una esquina redondeada obligaría a rasterizar en cada cambio de tamaño) y la tira de película es un degradado que se repite. La lógica sin WinUI (`Views/Timeline/Logic`: ventana y regla, filas, clic, ajuste, arrastres, soltar, regiones, decoraciones) se prueba en `tests/Rodaje.App.Tests/TimelineLogic`.
- **Miniaturas y formas de onda** (`IClipVisuals`, en `TimelineView.ClipVisuals`): `GetThumbnail(mediaId, tiempoDelOrigen, alto)` devuelve la miniatura más cercana o `null` (y la pide), `GetWaveform(mediaId)` devuelve los picos (`WaveformPeaks`: un máximo de 0 a 1 por tramo fijo del origen) o `null`, y `Changed(mediaId)` avisa que hay algo nuevo. Todo en el hilo de la interfaz y sin esperar; sin proveedor se dibujan los marcadores de posición.
- **Medida** (`RODAJE_MEDIR_LINEA=informe.txt`, con `app.ps1 video.mp4`): 200 clips (80 en la principal con su audio y 40 encima), RTX 3050 Laptop a 60 Hz. Desplazarse: mediana 16,7 ms por cuadro (p95 17,6), el dibujo propio 0,2 ms y 7 rearmados en 300 cuadros. Zoom continuo de 5 a 45 px/s: mediana 21,8 ms (p95 31) estirando, 31,8 ms rearmando en cada cuadro; el código propio tarda 0,15 ms y el resto es el dibujo de XAML. Arrastrar un borde hasta que el motor publica la secuencia editada: 0,8 ms de mediana (máximo 16 ms).

### Cómo se suma una vista

1. Un `UserControl` en `Views/<Área>/` que implementa `IEditorView`; `MainWindow` (o el anfitrión, como `InspectorView`) lo crea desde XAML y llama a `Attach(EditorServices)` una vez.
2. En `Attach` se suscribe a los eventos de `services.Session`, registra sus acciones con `services.ShortcutRegistry.Register(new ShortcutAction(...))` y las une con `services.Shortcuts.Bind(id, ejecutar, puedeEjecutar)`. Los botones llaman a `services.Shortcuts.Invoke(id)`.
3. Solo `x:Bind` (con `Bindings.Update()` al final de `Attach`) y colores del tema (`StaticResource`), nunca sueltos.

## `Rodaje.Visor` (provisional)

Es una ventana Win32 publicada con Native AOT en `dist\Rodaje-visor\` por [`herramientas/visor.ps1`](../../herramientas/visor.ps1), que también copia las DLL de FFmpeg LGPL y el `ffmpeg.exe` de los proxies. La abre `ABRIR.bat visor`.

- **Bucle.** Un solo hilo con `MsgWaitForMultipleObjectsEx` sobre el objeto de espera de la cadena. Sesión, composición y presentación corren en el hilo de la ventana, y Windows detiene el bucle mientras se arrastra la ventana.
- **Argumentos** ([`VisorArguments.cs`](../../src/Rodaje.Visor/VisorArguments.cs)): `[videos | proyecto.rodaje] [--prueba s] [--registro ruta] [--sin-audio] [--cpu] [--proxies [siempre]]`. Sin argumentos abre un diálogo para elegir archivos.
- **Teclas** (detalle en [`docs/prueba-visor.md`](../prueba-visor.md)):
  - Espacio reproduce o pausa; ←/→ avanza un cuadro y Shift+←/→ salta 5 s.
  - 1-9 oculta o muestra una pista; Ctrl+1-9 selecciona una capa.
  - Alt+flechas mueve la capa, +/− la escala, R la rota 15° y O cambia la opacidad.
  - Ctrl+Z/Y deshace y rehace, Ctrl+S guarda, F pone pantalla completa e I muestra las estadísticas.
- **Ediciones.** [`LayerEdits.cs`](../../src/Rodaje.Visor/LayerEdits.cs) edita `rodaje.transform` con comandos del `UndoStack` y claves de fusión: es el primer uso real del núcleo desde una ventana.
- **Recorrido automático.** `--prueba` ([`AutomatedRun.cs`](../../src/Rodaje.Visor/AutomatedRun.cs)) reproduce, oculta, edita, deshace, hace seek y pasos, guarda y vuelve a abrir, e imprime las estadísticas. Con `visor.ps1 -Prueba 20 video.mp4` lo corre desde la consola.
- **Pruebas.** `tests/Rodaje.Visor.Tests` comprueba cómo se arma el proyecto de demostración con dos videos, un audio y dos imágenes.
