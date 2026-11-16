# Rodaje en Linux y macOS: resultado de la fase 0

Fase 0 de D-066 ([propuestas/mac-y-linux.md](propuestas/mac-y-linux.md)): las pruebas de concepto que deciden si se sigue con Uno Platform y con Vulkan detrás de `IGpu`. Hecha el 16 de noviembre de 2026 sobre `main` 227883a6. Los prototipos están en [`experimentos/linux-fase0/`](../experimentos/linux-fase0/README.md), fuera de `Rodaje.slnx`, y corren en GitHub Actions con [`linux-fase0.yml`](../.github/workflows/linux-fase0.yml) (a mano o al subir a una rama `linux/fase0*`); en el equipo de Daniel no se instaló nada.

## Veredicto

[[VEREDICTO]]

## Resultado por parte

| Parte | Qué se probó | Resultado | Dónde |
|---|---|---|---|
[[TABLA]]

### (a) Uno Platform con Native AOT

Prototipo `uno/Fase0.Uno`: Uno.Sdk 7.0.22 (Skia, `net10.0-desktop`), una página con `x:Bind`, `ListView` con plantilla y dos botones, que carga un `.rodaje` con `Rodaje.Core` (`tests/Rodaje.Core.Tests/Serialization/ejemplos/proyecto-v16.rodaje`). La 6.6 de la propuesta ya no es la vigente; se usó la estable de hoy.

| Medida | Linux (`ubuntu-24.04`, Xvfb, GL por software) | macOS (`macos-15`, arm64) |
|---|---|---|
| Publicar con AOT | 158 s, 3 avisos IL (de `Uno.UI.Runtime.MacOS` y `Win32`, no del código propio) | 310 s, los mismos 3 |
| Ejecutable / carpeta | 59,8 MB / 82,4 MB | 58,1 MB / 89,3 MB |
| Carga el proyecto (esquema 16, 6 clips) | sí | sí |
| Ventana lista, primera vez / mejor de 3 en caliente | 778 ms / **238 ms** | 3077 ms / **791 ms** |
| Clics de fuera (`xdotool`) | 3 de 3 en «Contar», 1 en «Cerrar», salida 0 | sin probar (inyectar clics pide permiso de accesibilidad) |

Los tiempos son desde que nace el proceso hasta `Loaded` de la página, en un corredor compartido: sirven de orden de magnitud, no de cifra del producto.

**Cuánto de `Rodaje.App` compila sin cambios** (`uno/medir-vistas.ps1`, en Windows: enlaza los 38 XAML y los 556 `.cs` de `src/Rodaje.App` en un proyecto `net10.0-desktop` y cuenta errores por archivo):

| | Archivos sin errores | Líneas en archivos sin errores |
|---|---|---|
| XAML | 38 de 38 (100 %) | 4 857 de 4 857 |
| C# | 552 de 556 (99,3 %) | 97 988 de 99 252 (98,7 %) |

- **6 errores en total**, todos de CsWinRT (`WinRT.CastExtensions`, `WinRT.ComWrappersSupport`, `IWinRTObject`) en `Program.cs`, `LibraryView.xaml.cs`, `LibraryScrollMeasurement.cs` y `SwapChainPanelNative.cs`, más `BindableMetadataProvider` en el código generado de `App.xaml`.
- Compilar no es correr. El analizador de Uno marca **91 usos de 18 miembros de WinUI sin implementar** en 26 archivos: `SwapChainPanel` (el visor, ya previsto: fase 8), `LineGeometry.StartPoint`/`EndPoint` (48 usos), `Win32Interop.GetWindowFromWindowId`, `AppWindow.MoveAndResize`, `DisplayArea.*`, `UIElement.IsDoubleTapEnabled`, `BitmapCache`/`CacheMode`, `Brush.Transform`, `UISettings.UIElementColor` y `DispatcherQueueSynchronizationContext`. Lista completa en `salida/vistas.json` al correr el script.
- **147 archivos** (85 de `Views`, 49 de `Services`, 12 de `Shell`) llaman a algo marcado solo para Windows. Casi todo es porque Media, Engine, Render, Audio y Export son hoy `net10.0-windows` enteros; baja sola cuando la fase 1 les dé destino `net10.0`.
- Hubo que quitar los `using` implícitos de Uno.Sdk (`System.Windows.Input`, `Windows.Storage`…): chocan con `Rodaje.Core.Commands.ICommand` y otros 70 nombres. Es una línea de proyecto, no un cambio de código.

Sin comprobar: que las vistas reales de Rodaje **se vean y respondan** bajo Uno (esto solo midió compilación), IME, arrastrar y soltar, selector de archivos y accesibilidad.

### (b) Vulkan en Linux: hwaccel de FFmpeg en un `VkDevice` propio

[[B]]

### (c) macOS: VideoToolbox → `MTLTexture` → `VkImage`

Prototipo `mac-vt/vt_vulkan.m` (Objective-C, un archivo): decodifica con el hwaccel VideoToolbox de FFmpeg, toma el `IOSurface` del `CVPixelBuffer`, crea un `MTLTexture` por plano en el `MTLDevice` que exporta MoltenVK, lo importa como `VkImage` con `VK_EXT_metal_objects` (`VkImportMetalTextureInfoEXT`), crea su vista, lo lee desde Vulkan (`vkCmdCopyImageToBuffer`) y lo compara byte a byte con el `CVPixelBuffer`. En `macos-15` (15.8.2, arm64, «Apple Paravirtual device»), FFmpeg 9.0.2 y MoltenVK 1.4.2 de Homebrew, 30 s por formato:

| Formato | Cuadros | Iguales en los dos planos | ms por cuadro (importar, leer y comparar) | Memoria residente inicio → final |
|---|---|---|---|---|
| H.264 8 bits 1080p → `420v` (NV12; R8 + RG8) | 3 780 | **3 780** | 2,36 (peor 65,6) | 41,8 → 40,2 MB |
| HEVC Main10 1080p → `x420` (P010; R16 + RG16) | 2 610 | **2 610** | 7,02 (peor 113,7) | 61,7 → 49,3 MB |

Ningún cuadro llegó por software ni sin `IOSurface`; ninguna importación falló. El `VkImage` es la superficie del decodificador: no hay copia entre VideoToolbox y Vulkan. Funcionó al primer intento.

Lo que el corredor no deja comprobar, o no se hizo:

- La GPU es paravirtual: no hay cifra de rendimiento que valga, ni se sabe si VideoToolbox decodificó por hardware.
- Se leyó con una copia, no con un sombreador: la vista se crea con uso `SAMPLED`, pero no se ejecutó una pasada que muestree. Esa pasada sí está probada en (b) y (d); falta juntarlas en Mac.
- 30 s por formato, no 10 minutos. Presentar en pantalla (`CAMetalLayer`), EDR y KosmicKrisp (macOS 26) quedan para la fase 11.
- La imagen parte de `VK_IMAGE_LAYOUT_UNDEFINED` en cada cuadro; MoltenVK conserva el contenido (Metal no tiene layouts), pero la especificación no lo garantiza: con KosmicKrisp hay que volver a probarlo.
- El FFmpeg de Homebrew es una compilación GPL: solo es herramienta de la prueba en el corredor. El de Rodaje para Mac se compila LGPL en la fase 2.

### (d) Un HLSL, dos motores: DXC a SPIR-V frente a D3D11 WARP

[[D]]

### (e) Protocolo de módulos por socket Unix y memoria compartida

[[E]]

### (f) Base de cifras en Windows

Exe de `main` (1300f047, AOT, 42,2 MB) en el equipo de Daniel: RTX 3050 Laptop, D3D11VA. **Medido de fondo** (ventana fuera de pantalla, sin foco: `--medir`/`--arranque` con el modo de #505), **sin audio** (`--sin-audio`: el reloj es el del sistema, no el del dispositivo) y con los ayudantes de la Cuadrilla compilando a la vez. Dos vueltas de 10 s por escenario; `build\probar-fondo.ps1` vigiló las ocho: ninguna ventana a la vista.

| Escenario | Composición en GPU, ms por cuadro (mediana / p95) | CPU del render por cuadro (mediana) | Cuadros mostrados / perdidos |
|---|---|---|---|
| `efectos` (1080p, 3 capas con efectos, 4 transiciones) | 0,18 / 0,97 y 0,24 / 1,55 | 0,22 ms | 1 932 / 6 y 1 941 / 3 |
| 4K60 HEVC Main10 (un clip `hevc_nvenc` de `testsrc2`, sin proxies) | 0,46 / 0,47 y 0,46 / 2,28 | 0,25 y 0,18 ms | 710 / 4 y 709 / 6 (de ~720) |
| `capas` (3 capas a 1×) | sin muestras | sin muestras | 831 / 3 y 828 / 0 |
| un clip 1080p H.264 | sin muestras | sin muestras | **1 / 0** (no reprodujo) |

Arranque en caliente, 10 vueltas (`build\medir-arranque.ps1`, ms; mediana, mínimo, máximo): ventana lista 724 / 662 / 2 090; primer render 834 / 766 / 2 370; **interfaz usable 900 / 824 / 2 495**; sesión abierta 1 199 / 1 099 / 3 013.

Qué vale y qué no de esta base:

- Valen como referencia para «≤ 1,2 × D3D11»: la composición por cuadro (0,2 ms con efectos, 0,46 ms en 4K) y el arranque (interfaz usable en 0,9 s).
- **No vale la cadencia de presentación**: de fondo la ventana no la compone el escritorio y el intervalo sale fijo en 5,00 ms (200 por segundo) en vez del refresco de la pantalla. La base de fluidez (intervalo entre cuadros, cuadros tarde) hay que tomarla con `--visible`, cuando Daniel no esté usando el equipo.
- De fondo y sin audio, el escenario de un clip **no reproduce** (1 cuadro mostrado en las dos vueltas) y `capas` no deja muestras de composición. No se investigó: queda como defecto de `--medir` de fondo.
- No se midió la exportación (NVENC) ni el contador de transferencias a la CPU, que no existe todavía (lo crea la fase 1).

## Borrador de `IGpu`

En [igpu-borrador.md](igpu-borrador.md): el contrato (recursos, cuadro de órdenes, cadena de intercambio, superficie del decodificador), el contrato de sincronización en ocho reglas con lo que hace cada motor, las convenciones que Vulkan iguala a D3D11 y lo que pide Unicomp (píxel `R32_UINT`, captura a PNG, MSAA, acumulación). [[IGPU-NOTA]]

## Semanas reajustadas

[[SEMANAS]]

## Dependencias nuevas que usan los prototipos

| Paquete | Licencia | Dónde |
|---|---|---|
[[LICENCIAS]]

Ninguna entra en el producto todavía: `LICENCIAS.md` y `THIRD-PARTY-NOTICES.txt` se actualizan cuando la fase que las adopte las añada a `src/`.

## Lo que queda para Daniel

[[DANIEL]]

## Cómo se hizo

[[COMO]]
