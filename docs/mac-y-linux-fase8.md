# Linux y macOS, fase 8: la interfaz con Uno en Linux

Resultado de la fase 8 de D-066 ([propuestas/mac-y-linux.md](propuestas/mac-y-linux.md)), 17-nov. Solo Linux: Mac sigue aparcado (Daniel, 17-nov).

## Qué quedó

- **Un proyecto más, mismo código** (#611): `src/Rodaje.App.Desktop/Rodaje.App.Desktop.csproj` (Uno.Sdk 7.0.22, Skia sobre X11, `net10.0-desktop`, RID `linux-x64`, Native AOT) compila todos los `.cs` y los 38 XAML de `src/Rodaje.App` enlazados. Lo de cada sistema va en archivos partidos: `*.Windows.cs` solo entra en la app de WinUI (`Rodaje.App.csproj` quita los `*.Desktop.cs`) y `*.Desktop.cs` solo en la de Linux. No se usó un único `.csproj` con dos destinos porque obligaba a pasar la app de Windows a Uno.Sdk; así `Rodaje.App.csproj` solo gana una línea. Queda fuera de `Rodaje.slnx`.
- **Partidos por sistema** (12 pares): arranque (`Program`: WinUI con CsWinRT o el anfitrión X11 de Uno), visor (`PlayerEngine`, `PlayerView`), ventana principal (DPI, barra de título, colocación, ventana de fondo), armazón de arranque (en Linux no hay), diálogos de archivos y carpetas, distribución del teclado, tiempo de doble clic, molde de objetos XAML (`XamlCast`), `SwapChainPanelNative` y la autoprueba (`SelfTestRun.RunAsync`). En Windows el código se movió sin cambiar orden ni llamadas (revisado por Gemini: sin hallazgos). En `Services/` bastó una guarda `OperatingSystem.IsWindows()` en `ModuleJobObject`.
- **Visor por lectura a la CPU**: `PlayerEngine.Desktop.cs` abre `VulkanGpu`, compone con el mismo `Compositor` y los títulos de `SkiaTitleRasterizer`, lee el cuadro (`ReadbackRgba`) y `PlayerView.Desktop.cs` lo copia a un `WriteableBitmap` dentro del panel; el XAML de encima (guías, asas, título) sigue igual. Sin cadena de intercambio, el hilo de render marca 60 Hz con un reloj. Lo que depende del dispositivo D3D11 queda apagado en Linux: monitores (scopes), visor en otra pantalla, miniaturas de looks y transiciones, medida de GPU por consultas y HDR/10 bits en el visor.
- **Selector de archivos**: `zenity` o, si no está, `kdialog`, modales como los de Windows (abrir, varios, guardar con extensión, carpeta). **Portapapeles y arrastrar y soltar**: el código de WinUI de siempre, que Uno implementa en X11; el portapapeles se prueba en la autoprueba, arrastrar desde el gestor de archivos no se puede en la CI.
- **Mapeo de rutas en Preferencias** (#614): Preferencias › General › «Mapeo de rutas», una fila por línea `origen | destino`; al abrir un proyecto, las rutas de los medios pasan por `PortablePath.ApplyMappings` (fase 1). En los dos sistemas; vacío no cambia nada.
- `ChildProcessGuard.SweepOrphans()` al arrancar en Linux (fase 1 lo dejaba para aquí).
- **CI**: trabajo nuevo *Interfaz Uno en Linux (AOT, Xvfb)* en `ci.yml` con `build/app-linux.sh --autoprueba`: publica con AOT, genera el clip de prueba con el ffmpeg del sistema, corre la autoprueba bajo Xvfb con lavapipe, mide el arranque en caliente y sube capturas, registros y cifras (`app-linux`).

## «Hecho cuando», punto por punto

| Punto | Estado | Cifra (corredor `ubuntu-24.04`, lavapipe) |
|---|---|---|
| `Rodaje.App` compila para `net10.0-desktop` además de Windows, con archivos partidos | **Hecho** | 0 errores; publicación AOT 235–332 s; exe 87,7 MB, carpeta 243,8 MB (con FFmpeg aparte, del `.entorno`) |
| Selector de archivos, portapapeles, arrastrar y soltar | **Hecho / sin probar con ratón** | Portapapeles de ida y vuelta en la autoprueba; zenity y arrastrar desde Nautilus, en `pruebas-a-mano.md` |
| Mapeo de rutas en Preferencias | **Hecho** | 3 pruebas de App y 1 de Core (corre en Linux) |
| Visor por lectura a la CPU | **Hecho** | Cuadros en el panel a los 451 ms de arrancar; se ve en `captura-01.png` del artefacto |
| Autoprueba del exe AOT en Linux (Xvfb + lavapipe) en la CI | **Hecho** | 6,3 s, código 0 |
| Abrir, editar, reproducir y exportar el proyecto de aceptación | **Hecho** | Guardar y reabrir `aceptacion.rodaje` en 111 ms; dividir, deshacer y rehacer; reproducir hasta 1,43 s con el visor de llvmpipe; exportar 1 s a ProRes 422 HQ en 3,7 s (comprobado con ffprobe) |
| Interfaz usable < 1 s en el PC de Daniel | **Sin medir** (pregunta) | En la CI, en caliente: **375, 384 y 388 ms**; en frío, la primera vez, 604–1 793 ms |
| Windows igual | **Hecho** | Mismo comportamiento (revisión de Gemini); CI de Windows con sus pruebas y la autoprueba del exe AOT en verde |

El «proyecto de aceptación» no existía: es el clip de prueba (`h264-1080p-23976-aac-*.mp4`, 5 s) importado en un proyecto nuevo que la autoprueba guarda como `aceptacion.rodaje`, vuelve a abrir, edita, reproduce y exporta.

## Lo que no quedó, y por qué

- **Tiempo y duración del transporte en 0:00.000.** Los `x:Bind` de `PlayerView` a `Session.PositionText`/`DurationText` no se actualizan en Uno (la línea de tiempo y el cursor sí); un `Bindings.Update` al cargarse no bastó. Hace falta depurarlo en una máquina con Linux: va a la fase 9.
- **Líneas de `LineGeometry`.** Uno no implementa `StartPoint`/`EndPoint` (48 usos: curvas del inspector, mezclador, regla, guías): pueden no dibujarse. Lista entera de lo que Uno no implementa en `publish.log` (83 avisos `Uno0001`).
- **Tamaño de la ventana.** Uno en X11 no implementa mover ni cambiar el tamaño de `AppWindow` (lanza `NotImplementedException`): abre con el tamaño por omisión (1024×640) y no recuerda el guardado. Barra de título del sistema, sin la propia extendida.
- **Teclado español en los nombres de atajos**: sin leer la distribución de X11 salen con los nombres de EE. UU. (anotado como atajo en el código).
- **165 avisos CA1416** (llamadas solo de Windows alcanzables en Linux): son las ramas de D3D11 que el motor no toma en Linux (`_gpu` nulo) y las que dependen de él; se quitan al pasar esas funciones a `IGpu` (fase 9).
- **Visor nativo, HDR y monitores**: fase 9, como estaba previsto.
- **Captura de ventana de la autoprueba** (`WindowCapture`): las capturas las toma el script con `import` cada 4 s.

## Preguntas para Daniel (se sigue con la recomendada)

1. **Arranque < 1 s en tu PC.** En la CI la interfaz responde a los 0,38 s en caliente con lavapipe. Recomendado: medirlo tú con el USB (paso 1 de «Rodaje en Linux» en `pruebas-a-mano.md`) y dar la cifra por buena si baja de 1 s.
2. **Dos proyectos en vez de uno con dos destinos.** Recomendado: dejarlo así (Windows no cambia de SDK). La alternativa, un solo `.csproj` con Uno.Sdk para los dos, toca la compilación de la app de Windows.
3. **Mapeo de rutas también en Windows.** Está en Preferencias de los dos sistemas (sirve para abrir en Windows un proyecto de Linux). Recomendado: dejarlo; si prefieres Windows sin cambios visibles, se esconde allí.
4. **Diálogos con zenity/kdialog.** Bloquean la ventana mientras están abiertos (como en Windows, pero sin repintar). Recomendado: dejarlos; la alternativa es el portal de escritorio (asíncrono, D-Bus), más código.
5. **Tamaño de la carpeta** (243,8 MB sin FFmpeg, con todas las fuentes y nativos de Uno y Skia). La propuesta pide AppImage ≤ 1,3 × la carpeta de Windows; se mide en la fase 10.

## Ayudantes

| Pieza | Quién | Cómo fue |
|---|---|---|
| Proyecto, partido por sistema, visor, autoprueba, mapeo, CI | Claude | Sin compilación local (el PC estaba ocupado con Unicomp): todo por la CI, unas 10 vueltas |
| Revisión de que Windows no cambia | Gemini (gemini-3.8-flash-high, solo lectura) | Sin hallazgos de comportamiento; señaló un espacio que faltaba |

ChatGPT estuvo sin cuota toda la sesión; lo mecánico (los `.Desktop.cs` triviales) era poco y dependía de cada vuelta de la CI, así que no se delegó.
