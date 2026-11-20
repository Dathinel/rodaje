# Rodaje en Linux y macOS: resultado de la fase 0

Fase 0 de D-066 ([propuestas/mac-y-linux.md](propuestas/mac-y-linux.md)): las pruebas de concepto que deciden si se sigue con Uno Platform y con Vulkan detrás de `IGpu`. Hecha el 16 de noviembre de 2026 sobre `main` 227883a6. Los prototipos están en [`experimentos/linux-fase0/`](../experimentos/linux-fase0/README.md), fuera de `Rodaje.slnx`, y corren en GitHub Actions con [`linux-fase0.yml`](../.github/workflows/linux-fase0.yml) (a mano o al subir a una rama `linux/fase0*`); en el equipo de Daniel no se instaló nada.

## Veredicto

- **Uno Platform: confirmado.** Un ejecutable publicado con Native AOT para `linux-x64` arranca bajo Xvfb, carga un `.rodaje` con `Rodaje.Core` y responde a clics; lo mismo arranca en macOS arm64. Los 38 XAML de `Rodaje.App` y el 99,3 % de sus archivos C# compilan contra Uno sin tocar una línea. No hace falta el plan B (Avalonia). Lo que no está probado es que las vistas reales se comporten bien en ejecución: eso es la fase 8.
- **Vulkan detrás de `IGpu`: confirmado, con una corrida pendiente.** El hwaccel Vulkan de FFmpeg decodifica NV12 y P010 en un `VkDevice` propio y se muestrea sin copias en la RTX 3050 (en Windows); en lavapipe funciona el camino de subida; en macOS, VideoToolbox → `MTLTexture` → `VkImage` por plano da los mismos bytes; las 77 variantes de `EffectShaders` compilan a SPIR-V y se ejecutan con `TerraFX.Interop.Vulkan` y AOT. **Falta la corrida de 10 minutos en Linux con el driver de NVIDIA** (Daniel, USB).
- **Un criterio del diseño no se sostiene:** «≤ 1e-3 frente a WARP en todas las variantes». Lo cumplen 116 de 168 casos, igual en lavapipe que en la GPU real, y la causa no es la traducción del HLSL sino el filtro bilineal de cada rasterizador. Hay que reescribirlo antes de la fase 4 (propuesta en (d)).
- **El protocolo de módulos sigue en la versión 1** en Linux y macOS: 19 de 19 comprobaciones en los dos.
- **Semanas:** ~13,5 para Linux publicable (antes 14) y ~17,5 con Mac (antes 18).

## Resultado por parte

| Parte | Qué se probó | Resultado | Dónde |
|---|---|---|---|
| (a) Uno, AOT, `linux-x64` | Arranca bajo Xvfb, carga un proyecto, responde a clics; % de `Rodaje.App` que compila | **Pasa.** Ventana lista en 0,17–0,25 s en caliente; 3 clics de 3; XAML 100 %, C# 99,3 % de archivos | `uno/`, CI y Windows |
| (b) Vulkan Video en un `VkDevice` propio | NV12 y P010, muestrear, leer, reciclar | **Pasa en la RTX 3050 (Windows)** y, por subida, en lavapipe. **Pendiente: Linux con NVIDIA, 10 min** | `vulkan-video/`, Windows y CI |
| (c) VideoToolbox → `MTLTexture` → `VkImage` | Importar cada plano con MoltenVK y leerlo desde Vulkan | **Pasa:** 3 780 + 2 610 cuadros idénticos byte a byte. GPU paravirtual: sin cifras de rendimiento | `mac-vt/`, CI |
| (d) HLSL por DXC frente a WARP | 77 variantes, 168 casos, umbral 1e-3 | Compilan **77 de 77** (con `-HV 2018` o 7 líneas cambiadas). Píxeles: **116 de 168**; **no cumple** el criterio como está escrito | `shaders/`, Windows y CI |
| (e) Eco de módulos en Unix | Socket, credenciales, memoria compartida, muerte del padre | **Pasa:** 19 de 19 en Linux y en macOS | `eco/`, CI |
| (f) Base de cifras en Windows | Composición, arranque, 4K60 HEVC 10 bits | Tomada **de fondo y sin audio**: composición 0,2–0,46 ms por cuadro, interfaz usable en 0,9 s. La cadencia de presentación no vale de fondo | Windows |

### (a) Uno Platform con Native AOT

Prototipo `uno/Fase0.Uno`: Uno.Sdk 7.0.22 (Skia, `net10.0-desktop`), una página con `x:Bind`, `ListView` con plantilla y dos botones, que carga un `.rodaje` con `Rodaje.Core` (`tests/Rodaje.Core.Tests/Serialization/ejemplos/proyecto-v16.rodaje`). La 6.6 de la propuesta ya no es la vigente; se usó la estable de hoy.

| Medida | Linux (`ubuntu-24.04`, Xvfb, GL por software) | macOS (`macos-15`, arm64) |
|---|---|---|
| Publicar con AOT | 158 s, 3 avisos IL (de `Uno.UI.Runtime.MacOS` y `Win32`, no del código propio) | 310 s, los mismos 3 |
| Ejecutable / carpeta | 59,8 MB / 82,4 MB | 58,1 MB / 89,3 MB |
| Carga el proyecto (esquema 16, 6 clips) | sí | sí |
| Ventana lista, primera vez / mejor de 3 en caliente | 778 ms / **238 ms** (en otras tres ejecuciones: 170, 251 y 251 ms en caliente) | 3077 ms / **791 ms** |
| Clics de fuera (`xdotool`) | 3 de 3 en «Contar», 1 en «Cerrar», salida 0. En 1 de 5 ejecuciones el primer clic se perdió por lanzarse en el instante de `Loaded`; con medio segundo de espera, 0 reintentos | sin probar (inyectar clics pide permiso de accesibilidad) |

Los tiempos son desde que nace el proceso hasta `Loaded` de la página, en un corredor compartido: sirven de orden de magnitud, no de cifra del producto.

**Cuánto de `Rodaje.App` compila sin cambios** (`uno/medir-vistas.ps1`, en Windows: enlaza los 38 XAML y los 556 `.cs` de `src/Rodaje.App` en un proyecto `net10.0-desktop` y cuenta errores por archivo):

| | Archivos sin errores | Líneas en archivos sin errores |
|---|---|---|
| XAML | 38 de 38 (100 %) | 4 857 de 4 857 |
| C# | 552 de 556 (99,3 %) | 97 988 de 99 252 (98,7 %) |

- **6 errores en total**, todos de CsWinRT (`WinRT.CastExtensions`, `WinRT.ComWrappersSupport`, `IWinRTObject`) en `Program.cs`, `LibraryView.xaml.cs`, `LibraryScrollMeasurement.cs` y `SwapChainPanelNative.cs`, más `BindableMetadataProvider` en el código generado de `App.xaml`.
- Compilar no es correr. El analizador de Uno marca **91 usos de 18 miembros de WinUI sin implementar** en 26 archivos: `SwapChainPanel` (el visor, ya previsto: fase 8), `LineGeometry.StartPoint`/`EndPoint` (48 usos), `Win32Interop.GetWindowFromWindowId`, `AppWindow.MoveAndResize`, `DisplayArea.*`, `UIElement.IsDoubleTapEnabled`, `BitmapCache`/`CacheMode`, `Brush.Transform`, `UISettings.UIElementColor` y `DispatcherQueueSynchronizationContext`. Lista completa en `salida/vistas.json` al correr el script.
- **147 archivos** (85 de `Views`, 49 de `Services`, 12 de `Shell`) llaman a algo marcado solo para Windows. Casi todo es porque Media, Engine, Render, Audio y Export son hoy `net10.0-windows` enteros; baja sola cuando la fase 1 les dé destino `net10.0`.
- Hubo que quitar los `using` implícitos de Uno.Sdk (`System.Windows.Input`, `Windows.Storage`…): con ellos salen 71 errores de nombre ambiguo (`ICommand` de `Rodaje.Core.Commands` frente al de `System.Windows.Input`, entre otros). Se quitan en el proyecto, sin cambiar código.

Sin comprobar: que las vistas reales de Rodaje **se vean y respondan** bajo Uno (esto solo midió compilación), IME, arrastrar y soltar, selector de archivos y accesibilidad.

### (b) Vulkan en Linux: hwaccel de FFmpeg en un `VkDevice` propio

Prototipo `vulkan-video/` (C#, `FFmpeg.AutoGen` 9.0.1.1 como en `Rodaje.Media`, `TerraFX.Interop.Vulkan`, AOT): crea la instancia y el `VkDevice`, rellena a mano el `AVVulkanDeviceContext` (FFmpeg no crea nada), decodifica con `get_format` → `AV_PIX_FMT_VULKAN`, y por cada cuadro bloquea el `AVVkFrame`, espera su semáforo de línea de tiempo, muestrea los dos planos en una pasada de cómputo propia (Y'CbCr BT.709 → RGBA 1080p), deja la imagen en su layout, escribe de vuelta el valor del semáforo, suelta el candado y lo recicla. Cada 30 cuadros lee el RGBA y lo compara con el mismo cuadro decodificado por software. Si el driver no tiene Vulkan Video, decodifica por software y **sube** a cuadros Vulkan del mismo dispositivo (`av_hwframe_transfer_data`), con el mismo muestreo y reciclado.

| | RTX 3050 Laptop, driver 610.62 (**Windows**, equipo de Daniel) | lavapipe (Mesa 25.2.8, `ubuntu-24.04`, CI) |
|---|---|---|
| Camino | **`vulkan-video`**: `VK_KHR_video_queue`, `_decode_queue`, `_decode_h264`, `_decode_h265` | **`subida`**: lavapipe no anuncia ninguna `VK_KHR_video_*` |
| Duración | 120 s | 60 s |
| Cuadros 1080p: H.264 8 bits (NV12) / HEVC Main10 (P010) | 11 026 / 10 999 | 2 085 / 1 710 |
| Cadencia (decodificar + muestrear + reciclar) | 183,4 por segundo; 2,23 ms p50, 5,70 ms p95 | 63,2 por segundo; 11,2 ms p50, 14,2 ms p95 |
| Transferencias a la CPU fuera de la verificación | 0 | 0 (la subida es CPU → GPU por definición) |
| RGBA comparado con software (`swscale`); error medio | 740 cuadros; 1,44/255 (PSNR 33,4 dB) | 130 cuadros; 1,46/255 (PSNR 32,4 dB) |
| **Planos Y y CbCr descargados, byte a byte frente al software** | **370 de 370 NV12 y 370 de 370 P010 idénticos** | 70 de 70 NV12 y 60 de 60 P010 idénticos (lo subido, descargado, frente al cuadro de origen) |
| `VkImage` y `AVFrame` vivos al final | 0 y 0 | 0 y 0 |
| Memoria del proceso: inicio → mitad → final | 91 → 392 → 373 MB | 104 → 271 → 168 MB |
| Capas de validación de Vulkan | no se activaron (`RODAJE_VK_VALIDAR=1` y tenerlas instaladas) | no hay en el corredor |

Repetido por Claude en la RTX 3050 (30 s): 5 914 cuadros, 100 de 100 NV12 y 99 de 99 P010 idénticos, salida 0.

Qué queda demostrado y qué no:

- **Demostrado:** el hwaccel Vulkan de FFmpeg 9.0 acepta un `VkDevice` ajeno y entrega NV12 y P010 que se muestrean en ese mismo dispositivo sin pasar por la CPU, con la sincronización por cuadro (candado, semáforo, layout) hecha desde C# con AOT. En un driver real de NVIDIA, aunque en Windows.
- **No demostrado:** lo mismo **en Linux con el driver propietario** (la corrida del USB, abajo): la puerta pedía Linux. Tampoco el candado de cola compartido con FFmpeg (`lock_queue`): el prototipo decodifica y envía en un solo hilo, así que no hubo dos hilos disputando la cola. Ni las capas de validación, que no se activaron.
- **Dos comprobaciones distintas:** la exacta dice que lo que decodifica el hardware en nuestro dispositivo es bit a bit lo que decodifica el software (descargando el `AVVkFrame`, solo para verificar). La de RGBA dice que nuestra pasada muestrea el cuadro correcto; su 1,4/255 es el mismo en los dos caminos (también cuando se muestrea el propio cuadro de software subido): es diferencia de conversión de color y de filtrado del croma frente a `swscale`, no de decodificación.
- **Memoria:** llega a su techo en los primeros segundos y no sigue subiendo (en la RTX 3050: 425 → 411 MB a los 15 y 30 s; 392 → 373 MB a los 60 y 120 s); la mayor parte son las referencias de software que el propio prototipo guarda para comparar. Los contadores dan 0 recursos vivos. No es la prueba de 10 minutos que pedía la puerta: esa queda para la corrida en Linux.
- **Presentar:** no se presentó en una ventana (sin pantalla en la CI y sin abrir ventanas en el equipo de Daniel); se leyó el resultado.
- En la CI no hay `hevc_nvenc`: la muestra de 10 bits salió de `libkvazaar` (LGPL, viene en el build de BtbN) y la de 8 bits de `libopenh264`.

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

Prototipo `shaders/` (detalle, tabla por caso y mapa de registros en su [README](../experimentos/linux-fase0/shaders/README.md)): `compilar` pasa las 77 variantes de `EffectShaders.All` por DXC 1.9.2609.5 (`-spirv -fspv-target-env=vulkan1.1 -O3`, `ps_6_0`), `referencia` dibuja 168 casos (las 77 variantes, entradas por fórmula, 64 × 64 RGBA16F) con D3D11 WARP como hoy, y `ejecutar-vulkan` (C#, `TerraFX.Interop.Vulkan`, AOT) dibuja los mismos casos con los `.spv` y compara.

**Compilación.**

| Modo de DXC | Variantes que compilan |
|---|---|
| Tal cual (HLSL 2021, el modo por defecto de DXC) | **0 de 77**: 7 líneas usan un vector como condición de `?:` o de `&&`, que HLSL 2021 ya no admite |
| `-HV 2018` | **77 de 77**, sin avisos |
| HLSL 2021 con esas 7 líneas cambiadas a `select()`/`and()` (y dos macros para `fxc`) | **77 de 77** con DXC y **77 de 77** con `D3DCompile` |

**Píxeles, con el umbral de 1e-3 sobre el error máximo:**

| Comparación | Casos ≤ 1e-3 | Variantes con todos sus casos dentro |
|---|---|---|
| Vulkan en **lavapipe** (Mesa, LLVM 20.1.2; CI) frente a WARP | **116 de 168** | 40 de 77 |
| Vulkan en la RTX 3050 frente a WARP | 116 de 168 | 41 de 77 |
| Control: Vulkan frente a D3D11 **en la misma RTX 3050** | **164 de 168** | 73 de 77 |

El trabajo de la CI comprueba que la comparación corre entera y que no baja de 116 casos; no exige los 168. **El criterio «≤ 1e-3 frente a WARP en todas las variantes» no se cumple**, ni en lavapipe ni en la GPU real. La causa está localizada y no es la traducción de HLSL a SPIR-V:

- **El filtro bilineal no es el mismo en cada rasterizador.** WARP trunca la fracción a 1/256; la RTX 3050 y lavapipe no. Solo, eso da hasta 1,5e-3 (un caso de una sola lectura lo reproduce emulado). Leyendo en centros de texel o en fracciones exactas en 8 bits, la diferencia baja a 4,9e-4 (1 ULP de media precisión).
- **Las funciones con mucha pendiente lo amplifican.** 40 de los 52 casos que fallan en lavapipe llevan `ADVANCED` (croma, calificador, curvas): el error medio sigue en ~1e-4, pero unos pocos píxeles en el borde de una rampa llegan a 2e-2. El mismo croma con 4:4:4 en vez de 4:2:0 da 7,6e-6.
- **Las funciones trascendentes.** `fxc` expande `pow`, `atan2` y `asin` en instrucciones; DXC emite las de GLSL.std.450 y las implementa cada driver. En HDR (PQ, exponente 78,84) una diferencia de 1 ULP se nota; un caso con una discontinuidad justo en un centro de píxel (`degradado2`) salta de 0 a 1 en 32 píxeles, también entre D3D11 y Vulkan en la misma GPU.

Reparto de los 52 casos de lavapipe por error máximo: 6 hasta 2e-3, 33 hasta 1e-2, 11 hasta 1e-1 y 2 por encima (los dos de arriba: HDR con `ADVANCED`, 0,95 en 3 valores, y `degradado2`).

Consecuencias para las fases 3 y 4:

1. **El HLSL necesita un cambio de 7 líneas** (o compilar con `-HV 2018`, un modo heredado). Se recomienda el cambio: compila igual con los dos compiladores y da los mismos resultados.
2. **El criterio de píxeles de la fase 4 hay que reescribirlo.** Propuesta: (i) 1e-3 de error máximo en los casos de centros de texel y fracciones exactas, que comprueban la traducción; (ii) en los demás, error medio ≤ 2e-4 y percentil 99,9 ≤ 4e-3 frente a WARP; (iii) en GPU real, comparar contra D3D11 de la misma GPU, no contra WARP. Medido hoy en lavapipe: el error medio es ≤ 2e-4 en 165 de 168 casos (fuera quedan los dos degenerados, 4,8e-3 y 2,6e-3, y `transition-0-rebote`, 3,2e-4); el percentil no se midió, así que esos dos umbrales son una propuesta por afinar con datos, no un resultado.
3. **Manifiesto de variantes:** DXC elimina los recursos que una variante no usa; lo simple es un único layout con los 14 bindings (`b` +0, `t` +16, `s` +32, `u` +48). Los planos del decodificador son `Texture2DArray`: la vista de Vulkan debe ser `2D_ARRAY`. Los `cbuffer` son solo `float4` y coinciden (272 y 416 bytes).
4. **DXC solo en Windows por NuGet:** los `.spv` se generan al compilar en Windows o en la CI y viajan incrustados, como decía el diseño.
5. **Enlace:** `TerraFX.Interop.Vulkan` 1.4.328.1 sirve tal cual (resuelve `libvulkan.so.1`; AOT de 2,95 MB sin avisos en Windows y publicado y ejecutado en Linux en la CI). No hizo falta Silk.NET.

### (e) Protocolo de módulos por socket Unix y memoria compartida

Prototipo `eco/` ([README](../experimentos/linux-fase0/eco/README.md)): un ejecutable AOT que hace de editor y de módulo. Usa el marco y los mensajes **reales** del protocolo v1 (`MessageChannel.cs`, `Messages.cs` y `ModuleExceptions.cs` enlazados desde `externo/sala/src/Rodaje.Modules.Protocol/` — antes `src/Rodaje.Modules.Protocol/` —, sin cambios) sobre un socket de dominio Unix, con la región de memoria compartida como archivo mapeado en una carpeta 0700.

| Comprobación | Linux (`ubuntu-24.04`) | macOS (`macos-15`, arm64) |
|---|---|---|
| Batería completa | **19 de 19** | **19 de 19** |
| Ruta del socket (`…/rodaje/e.<pid>/m.<8 hex>`) frente a `sun_path` | 39 B de 108 | 33 B de 104 |
| Riesgo: `$TMPDIR` + `CoreFxPipe_Rodaje.Mod.pipe.<32 hex>` | 64 B de 108: cabe | **108 B de 104: no cabe**, el `bind` falla (el riesgo del diseño, confirmado) |
| PID y UID del otro extremo; un intruso del mismo usuario es rechazado | `SO_PEERCRED`: pasa | `LOCAL_PEERPID` + `LOCAL_PEERCRED`: pasa |
| Región: creación exclusiva 0600, rechaza enlace simbólico, otro tamaño y otros permisos; se borra al cerrar; huérfanos barridos | pasa | pasa |
| Eco de un cuadro 1080p RGBA (8,3 MB) verificado byte a byte; 200 cuadros | 764 cuadros/s | 972 cuadros/s |
| `ping` de ida y vuelta, 1000 (p50 / p95) | 19,9 / 30,8 µs | 25,5 / 65,7 µs |
| El editor muere con SIGKILL: el módulo desaparece | 0 ms (`PR_SET_PDEATHSIG`) | 93 ms (`kqueue` `NOTE_EXIT`) |

Para la fase 1: el protocolo **sigue en la versión 1** (mismos bytes por el cable); cambia el transporte (socket en ruta corta en vez de tubería con nombre), la autenticación (credenciales del socket: PID **y** UID) y la región (nombre resuelto a un archivo de la carpeta del socket). La creación con modo 0600 va por la API de .NET (`UnixCreateMode`), no por `open` con P/Invoke: `open` es variádica y en arm64 de Apple el modo llegaría mal. Las pruebas hostiles de #496 no se pasaron aquí; estas son su versión mínima.

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
- **No vale la cadencia de presentación**: de fondo la ventana no la compone el escritorio y el intervalo sale fijo en 5,00 ms (200 por segundo) en vez del refresco de la pantalla; «mostrados» cuenta esas presentaciones (por eso `efectos` da unas 1 900 en 10 s con un clip de 24 cuadros por segundo). La base de fluidez (intervalo entre cuadros, cuadros tarde) hay que tomarla con `--visible`, cuando Daniel no esté usando el equipo.
- De fondo y sin audio, el escenario de un clip **no reproduce** (1 cuadro mostrado en las dos vueltas) y `capas` no deja muestras de composición. No se investigó: queda como defecto de `--medir` de fondo.
- No se midió la exportación (NVENC) ni el contador de transferencias a la CPU, que no existe todavía (lo crea la fase 1).

## Borrador de `IGpu`

En [igpu-borrador.md](igpu-borrador.md): el contrato (recursos, cuadro de órdenes, cadena de intercambio, superficie del decodificador), el contrato de sincronización en ocho reglas con lo que hace cada motor, las convenciones que Vulkan iguala a D3D11 y lo que pide Unicomp (píxel `R32_UINT`, captura a PNG, MSAA, acumulación). Lo que la fase 0 le añadió: los desplazamientos de registro y el layout único de (d), el candado de cola compartido con FFmpeg y la escritura de vuelta de layout y semáforo de (b), y la importación por plano sin semáforo de (c).

## Semanas reajustadas

La fase 0 costó un día de agentes, no semana y media: la holgura pasa al camino crítico. Lo demás se mueve poco, y en los dos sentidos:

| Fase | Antes | Ahora | Por qué |
|---|---|---|---|
| 0. Pruebas de concepto | 1,5 | hecha (queda la corrida con NVIDIA en Linux, de una tarde) | — |
| 1. Núcleo portátil | 2 | 2 | El transporte Unix ya está escrito y probado en (e); queda integrarlo con las hostiles de #496 |
| 3. Capa `IGpu` en Windows | 2 | 2 | El contrato está en borrador; se añade el cambio de 7 líneas de HLSL y el layout único de bindings |
| 4. Motor Vulkan | 4 | **4,5** | Hay que rehacer el criterio de píxeles y las referencias (lavapipe no iguala a WARP a 1e-3) antes de dar por buena una variante |
| 8. Interfaz Uno, Linux | 3 | 3 | Compila el 99 % sin tocar, mejor que el 85 % estimado; a cambio hay 18 miembros sin implementar que sustituir y ninguna vista real probada en ejecución |
| 11. Mac | 4 | 4 | La importación de superficies funcionó a la primera; sigue sin haber Mac real |

**Camino crítico:** 1 → 3 → 4 → 9 → 10 = 2 + 2 + 4,5 + 2,5 + 1 = **12 semanas**, **~13,5 con holgura** para Linux publicable (antes 14) y **~17,5 con Mac** (antes 18). El límite sigue siendo la revisión, no los agentes.

## Dependencias nuevas que usan los prototipos

| Paquete | Licencia | Dónde |
|---|---|---|
| `Uno.Sdk` / `Uno.WinUI` 7.0.22 (arrastra SkiaSharp y HarfBuzzSharp) | Apache-2.0 (Skia BSD-3, HarfBuzz MIT) | (a) |
| `TerraFX.Interop.Vulkan` 1.4.328.1 | MIT | (b), (d) |
| `Microsoft.Direct3D.DXC` 1.9.2609.5 (`dxc.exe`, `dxcompiler.dll`) | LLVM/NCSA | (d), solo al compilar |
| FFmpeg n9.0 `linux64-lgpl-shared` de BtbN | LGPL-2.1+ | (b), descargado en el corredor |
| MoltenVK 1.4.2, cabeceras de Vulkan | Apache-2.0 | (c), de Homebrew en el corredor |
| FFmpeg 9.0.2 de Homebrew | GPL (compilación con x264/x265) | (c), **solo herramienta del corredor**; no se distribuye ni se enlaza con Rodaje |
| Mesa lavapipe | MIT | (b), (d), del corredor |

Ninguna entra en el producto todavía: `LICENCIAS.md` y `THIRD-PARTY-NOTICES.txt` se actualizan cuando la fase que las adopte las añada a `src/`.

## Lo que queda para Daniel

1. **La corrida de (b) en Linux con NVIDIA** (lo único que le falta a la puerta). En un USB persistente con Ubuntu 24.04 (no toca Windows). Órdenes sin probar en ese equipo; las de compilar y ejecutar son las mismas que corren en la CI:

   ```bash
   sudo ubuntu-drivers install && sudo reboot          # driver propietario de NVIDIA
   sudo apt-get install -y git curl xz-utils clang zlib1g-dev libvulkan1 vulkan-tools
   vulkaninfo | grep VK_KHR_video_decode               # deben salir _h264 y _h265; si no, el driver no sirve y se anota
   curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --version 10.0.403 --install-dir "$HOME/dotnet"
   export DOTNET_ROOT="$HOME/dotnet" PATH="$HOME/dotnet:$PATH"
   mkdir -p "$HOME/ffmpeg" && curl -sSL https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-linux64-lgpl-shared-9.0.tar.xz | tar -xJ -C "$HOME/ffmpeg" --strip-components=1
   git clone https://github.com/Dathinel/rodaje && cd rodaje/experimentos/linux-fase0/vulkan-video
   export RODAJE_FFMPEG="$HOME/ffmpeg" LD_LIBRARY_PATH="$HOME/ffmpeg/lib" RODAJE_VK_GPU=NVIDIA
   dotnet publish VulkanVideo.csproj -r linux-x64 -c Release -p:PublishAot=true -o publish
   ./publish/vulkan-video --segundos 600 --salida resultado-nvidia-linux.json; echo "salida=$?"
   ```

   Pasa si: salida 0, `"camino": "vulkan-video"`, los planos idénticos al software, 0 recursos vivos y la memoria de la mitad y la del final casi iguales. El `resultado-nvidia-linux.json` se pega en un issue o se le pasa a Claude. Si `vulkaninfo` no lista las extensiones, ese es el resultado: toca VAAPI o CPU de respaldo en ese driver.
2. **Decidir el criterio de píxeles de la fase 4** (recomendado: el de tres partes de (d)). No bloquea: se sigue con el recomendado.
3. **Aceptar el cambio de 7 líneas en el HLSL de los efectos** (`select()`/`and()` en vez de `?:` y `&&` con vectores). Recomendado: sí; entra en la fase 3 con sus pruebas de WARP iguales.
4. **Base de fluidez con la ventana a la vista**, un rato que no esté usando el equipo: `dist\Rodaje\app\Rodaje.exe --medir 10 --visible` (con el exe recién publicado). La cadencia de presentación medida de fondo no sirve de referencia.
5. **Defectos vistos al medir, sin arreglar:** de fondo y sin audio, `--medir` con un solo clip no reproduce (1 cuadro) y `capas` no deja muestras de composición; `-Medir` suena por los altavoces aunque la ventana esté fuera de pantalla.
6. **Las preguntas 2, 4 y 5 de la propuesta siguen abiertas** (cuenta de Apple Developer, cómo probar en un Mac real, USB con Ubuntu): ninguna bloquea las fases 1 a 4.

## Cómo se hizo

- **Sin tocar el equipo:** nada instalado en Windows; Linux y macOS, solo en corredores de GitHub (`ubuntu-24.04`, `macos-15`, `windows-latest`). Ejecución de referencia, con los ocho trabajos en verde: [43901750096](https://github.com/Dathinel/rodaje/actions/runs/43901750096); todas, en [Actions › Fase 0 Linux y macOS](https://github.com/Dathinel/rodaje/actions/workflows/linux-fase0.yml). Los corredores de macOS arm64 tuvieron colas de 15 minutos o más y GitHub canceló dos veces trabajos sin llegar a asignarles máquina.
- **Los prototipos se unen a `main`** en `experimentos/linux-fase0/` (fuera de `Rodaje.slnx`, con su propio `Directory.Build.props`): las fases 1, 3 y 4 parten de ellos y el workflow solo corre a mano o en ramas `linux/fase0*`.
- **Reparto:** Claude hizo (a), (c), (f), el borrador de `IGpu`, el workflow y la revisión; Devin (`claude-opus-5-5-medium`, de pago) hizo (d); Devin (`swe-2-high`) hizo (e); Gemini (`gemini-3.8-flash-high`) hizo (b), con correcciones de Claude (nombres de las bibliotecas en Linux, codificadores de respaldo) y una segunda vuelta. ChatGPT no participó: sin cuota hasta las 13:02. El diseño le daba (b) y (c) a ChatGPT y (d) a Gemini.
- **Incidente:** la primera base de cifras (f) se lanzó con `-SinPublicar` sobre el exe de `dist\` de la 1.0.3, anterior al modo de fondo, y la ventana le tapó la pantalla a Daniel. Arreglado en #565: los scripts ya no lanzan un exe sin modo de fondo y `build\probar-fondo.ps1` comprueba que una corrida no asoma.
