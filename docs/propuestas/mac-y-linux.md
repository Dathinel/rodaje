# Rodaje en macOS y Linux

Diseño del bloque aprobado por Daniel el 15-nov, para después de la 1.0.3. Es un trabajo del tamaño de un programa nuevo (regla 1): reconocimiento, stack comparado y plan por fases. **No se escribe código hasta que Daniel lo apruebe.** Decisión propuesta: **D-066** (si otro bloque la toma antes, la siguiente libre). Revisado por ChatGPT (codex, solo lectura) el 15-nov: sus 15 observaciones están incorporadas (lista al final).

**Fase 0 hecha el 16-nov:** resultado, veredicto (Uno y Vulkan confirmados; falta una corrida con NVIDIA en Linux) y semanas reajustadas en [../mac-y-linux-fase0.md](../mac-y-linux-fase0.md); borrador de `IGpu` en [../igpu-borrador.md](../igpu-borrador.md). **Fases 1 y 2 hechas el 16-nov:** [../mac-y-linux-fase1.md](../mac-y-linux-fase1.md).

Lo que pide Daniel: **la misma idea de optimización, sin reescribir.** En Rodaje esa idea es: el cuadro nace en la GPU (D3D11VA), se compone en la GPU (RGBA16F, D-016), se ve sin copias (`SwapChainPanel`) y se codifica por hardware; Native AOT, sin instalador, arranque medido. (La exportación ya hoy lee el cuadro a la CPU y lo pasa a FFmpeg por tubería, `ExportFrameConverter` → `FfmpegPipeSink`; eso se conserva igual en los tres sistemas. «Sin copias» se exige en la reproducción.)

## Resumen

- **Interfaz: Uno Platform** (WinUI sobre Skia) solo en macOS y Linux. En Windows se sigue compilando contra WinUI 3 de verdad: no cambia nada de lo que justificó «Por qué sigue con WinUI 3» (`docs/arranque-y-distribucion.md`).
- **Render: una capa fina propia (`IGpu`) con dos motores**: el D3D11 de hoy, sin cambios de resultado, y **Vulkan** para Linux y macOS (MoltenVK; KosmicKrisp cuando haya macOS 26). Los shaders siguen escritos una vez en HLSL; para Vulkan, DXC los compila a SPIR-V al compilar, todas las variantes.
- **Video por hardware**: Linux con el hwaccel Vulkan de FFmpeg en el mismo dispositivo del compositor (el equivalente de D3D11VA); Mac con VideoToolbox → IOSurface → `MTLTexture` → `VkImage`. Codificación NVENC/VAAPI/Vulkan en Linux y VideoToolbox en Mac.
- **Orden: Linux primero** (se prueba en el PC de Daniel con su RTX 3050); Mac después, **solo Apple Silicon**.
- **Tamaño: ~14 semanas para Linux publicable y ~18 con Mac**, con la Cuadrilla en paralelo. La fase 0 (1,5 semanas) puede cambiar la cifra: es la puerta para seguir.

## 1. Qué resuelve y cómo lo hacen otros

Quien edita en un Mac o en Linux no puede usar Rodaje, y quien empieza en Windows y termina en otro equipo (casa, universidad) tampoco puede abrir su proyecto. Ideas de producto (solo ideas; nada de su código ni recursos):

| Programa | Plataformas | Idea que sirve |
|---|---|---|
| Resolve | Windows, Mac, Linux | El mismo proyecto abre en los tres; **mapeo de rutas** (una tabla «`D:\Material` ↔ `/Volumes/Material`») en vez de reenlazar archivo por archivo. Su versión gratuita en Linux no decodifica H.264/AAC: ahí un editor libre con FFmpeg gana. |
| Premiere | Windows, Mac | Proyecto compartido entre los dos; aceleración propia por plataforma. |
| Final Cut | Mac | Fluidez por el camino nativo de Apple (VideoToolbox, Metal, EDR): la vara de fluidez en Mac. |
| Kdenlive (GPLv3, Qt + MLT LGPL) | los tres | Núcleo (MLT) e interfaz Qt; en Linux la aceleración por VAAPI es su punto flojo histórico. |
| Shotcut (GPLv3, Qt + MLT) | los tres | Sin instalar: AppImage en Linux, `.dmg` firmado en Mac. |
| Olive (GPLv3) | los tres | Compositor propio en OpenGL: muestra el costo de un motor gráfico propio entre plataformas. |
| OpenTimelineIO (Apache 2.0) | — | Intercambio de líneas de tiempo; no hace falta aquí. |

Se toma: **un solo formato de proyecto para los tres**, **mapeo de rutas** al abrir un proyecto de otro sistema y **AppImage / `.app` sin instalar**. No se estudia ni copia código GPL.

## 2. Reconocimiento (medido en `main`, 77f2bd1c)

| Proyecto | Líneas C# | Atado a Windows | Qué lo ata |
|---|---|---|---|
| Core, Packages, Playback, Modules.Protocol, Analysis | 41 200 | poco, pero no nada | Core/Packages/Playback ya corren en Linux en la CI (`build/pruebas-linux.sh`). Core: rutas con semántica de Windows (`MediaRef.cs:54` resuelve con el `Path` del anfitrión, `MediaRelink.cs:149` compara sin mayúsculas). Protocol: memoria compartida con nombre (`SharedMemoryRegion`) y PID del cliente por `kernel32` (`ModuleProcess.cs:314`). Packages: índice firmado sin plataforma (`SignedIndex.cs:41`, `:85`), versión de paquete entera (`ManifestReader.cs:108`), sin permisos de ejecución al instalar (`PackageInstaller.cs:501`) |
| Media | 15 000 | 20 archivos | D3D11VA (`Gpu/*`, contexto inmediato compartido bajo candado en `GpuDevice.cs:82`, superficies retenidas en `D3D11TexturePayload.cs:16`), `ChildProcessJob` (Job Objects), `FfmpegLibrary.cs:231` fuerza `.dll`, catálogo de codificadores |
| Engine | 14 200 | 12 archivos | tipos del pool de D3D11VA en `ClipPlayer` y `TimelineScheduler` |
| Render | 13 600 | 32 archivos (10 300 líneas con `ID3D11`/`IDXGI`) | D3D11 por TerraFX; HLSL compilado en ejecución (`ShaderCompiler`, `D3DCompile`): 20 puntos de entrada en 5 archivos, pero **77 variantes solo en efectos** (`EffectShaders.cs:83`, `:1207`) más las de exportación HDR y mapeo de tonos; títulos con Direct2D/DirectWrite, **maquetación incluida** (`TitleRasterizer.Text.cs:130`, `TitleRasterizer.Units.cs:153`; 2 550 líneas en `Text/`) |
| Audio | 6 700 | 5 archivos | WASAPI: salida con posición y tiempo del dispositivo y cambio de salida (`WasapiOutput.cs:607`), captura |
| Export | 3 900 | indirecto | Sin APIs propias, pero crea el dispositivo (`ExportJob.cs:214`) y depende de Render |
| App | 93 100 | ver abajo | WinUI 3; `Rodaje.App.csproj:18` fija RID de Windows |

App por dentro: `Services/` 255 archivos y 33 200 líneas, **solo 4** tocan Win32/WinUI (más `ModuleJobObject`); `Views/` + `Shell/` 58 800 líneas y **36 XAML**, con **209 `x:Bind` y ningún `{Binding}`** (bueno para AOT); 76 archivos `Logic/` sin interfaz (12 700 líneas); **136 archivos** usan `Microsoft.UI` (Uno usa los mismos espacios de nombres); **20** con interop Win32/TerraFX; 8 con `Microsoft.UI.Composition`. El visor es un `SwapChainPanel` **con XAML encima**: `FrameCanvas`, los tiradores de `Logic/*Handles` y un `TextBox` para editar títulos sobre el video (`PlayerView.xaml:169`).

Lo que el render usa de D3D11 es acotado: 135 métodos distintos, casi todo texturas, búferes, SRV/UAV, `Dispatch`, `Draw` de pantalla completa, `Map`/`CopyResource` y consultas de tiempo. Por eso cabe una capa fina en vez de un segundo motor entero.

## 3. Stack comparado

### Interfaz

| Opción | Qué se aprovecha | Gana | Pierde | Costo |
|---|---|---|---|---|
| **Uno Platform 6.6** (Skia; Metal en Mac, OpenGL o Vulkan en Linux), solo para Mac/Linux | Los 36 XAML y la mayoría de `Views`/`Shell`; `Services` y `Logic` enteros. Estimado ≥ 85 % por líneas (lo mide la fase 0) | Windows sigue con WinUI real; AOT en Linux y Mac (su prueba: arranque 0,35 s Linux, 0,56 s Mac, ago-2026); lector de pantalla e IME en Skia desde 6.6; aloja vistas nativas (X11, `NSView`) con controles encima | Cobertura de WinUI incompleta; sobre una vista nativa el XAML se superpone pero **sin mezcla alfa**; Wayland sin vista nativa (va por XWayland); el arranque y el RID se adaptan aparte (no basta compartir espacios de nombres) | 3–4 semanas |
| Avalonia 12.1 | `Services` y `Logic` (≈ 46 000 líneas) | Escritorio Linux más maduro (Wayland nativo, accesibilidad), AOT documentado, `NativeControlHost` en los tres | Otro dialecto de XAML: **reescribir las 36 vistas y su código detrás** (≈ 59 000 líneas), o mantener dos interfaces. Interop GPU con Vulkan en Windows/Linux; en Mac solo por OpenGL compartido | 3–5 meses |
| Interfaz nativa por plataforma (AppKit, GTK4) | `Services` y `Logic` | Lo más nativo | Tres interfaces para una persona | Inviable |

**Recomendación: Uno.** Es la única que cumple «sin reescribir». Plan B Avalonia si la fase 0 no consigue un ejecutable **AOT publicado** que abra, edite y reproduzca (el % de vistas que compilan es solo una cifra secundaria).

### Render

| Opción | Gana | Pierde |
|---|---|---|
| **`IGpu` + D3D11 (Windows) + Vulkan (Linux; Mac por MoltenVK, KosmicKrisp en macOS 26)** | Un solo motor nuevo; mismo HLSL; el hwaccel Vulkan de FFmpeg (`AVVkFrame`) entrega el cuadro en el mismo `VkDevice`; MoltenVK importa `MTLTexture` (`VK_EXT_metal_objects`) | Vulkan es verboso y exige sincronización explícita (layouts, colas, semáforos); MoltenVK no es conformante; KosmicKrisp sí (Vulkan 1.4) pero **solo macOS 26 y Apple Silicon** (LunarG, oct-2026) |
| Vulkan en Linux + Metal propio en Mac | Lo más rápido en Mac, EDR directo | Tres motores |
| Vulkan también en Windows | Un motor | Reescribir lo que funciona y está probado |
| WebGPU (Dawn/wgpu) o SDL3 GPU | Una API | Sin camino estándar para importar las superficies del decodificador; otra dependencia nativa grande |

Enlaces: la familia que ya se usa, **TerraFX** (`TerraFX.Interop.Vulkan`, MIT, sin reflexión), si en la fase 0 está al día; si no, `Silk.NET.Vulkan` 2.x (MIT; la 3.0 sigue en vista previa). Shaders: **DXC** (licencia LLVM/NCSA) al compilar, con un **manifiesto generado de todas las variantes** (perfil, recursos, constantes); el SPIR-V va incrustado. En Windows sigue `D3DCompile`.

Títulos: en Linux y Mac hace falta **maquetación** (saltos de línea, clústeres, respaldo de fuentes, unidades animadas), no solo dibujo: HarfBuzzSharp + SkiaSharp (vienen con Uno, MIT) y la maquetación propia que hoy hace DirectWrite. Es un trabajo propio con casos de aceptación, fuera de la tolerancia global de píxeles.

### Lo demás

| Pieza | Linux | macOS (Apple Silicon) |
|---|---|---|
| FFmpeg LGPL 9.0.x | `compilar-ffmpeg-lgpl` con la receta linux64 de BtbN **y las mismas exclusiones que hoy** (sin chromaprint/FFTW, libzvbi ni plugins GPL de lcms2), dependencias auditadas por RID, fuente correspondiente publicada; con VAAPI, Vulkan y `nv-codec-headers` (MIT); `.so` en vez de `.dll` | Compilado en la CI con VideoToolbox, mismas exclusiones y auditoría; `.dylib` |
| Decodificación GPU | hwaccel **Vulkan** en el `VkDevice` del compositor; VAAPI con copia de respaldo; CPU | **VideoToolbox** → IOSurface → `MTLTexture` por plano → `VkImage`; CPU de respaldo |
| Codificación | `h264/hevc/av1_nvenc`, `*_vaapi`, `*_vulkan` | `h264/hevc/prores_videotoolbox` |
| Audio | **API asíncrona** de PulseAudio (`pa_stream`, con tiempo y latencia; funciona sobre PipeWire), salida y captura | AudioUnit (CoreAudio) |
| Tuberías de módulos | socket Unix; ruta absoluta corta; PID del cliente por `SO_PEERCRED` | ídem, `LOCAL_PEERPID`; **la ruta por defecto se pasa de 104 bytes** |
| Memoria compartida | archivo en `$XDG_RUNTIME_DIR` (tmpfs): creación exclusiva (`O_EXCL|O_NOFOLLOW`), 0600, dueño y tamaño comprobados, borrado tras caídas | ídem en una carpeta propia 0700 |
| Procesos hijos (módulos, proxies, exportadores FFmpeg) | módulos: `PR_SET_PDEATHSIG` desde el propio módulo + EOF; FFmpeg y demás: grupo de procesos propio y archivo de PID con hora de inicio que el editor limpia al arrancar | módulos: `kqueue` `NOTE_EXIT` sobre el padre + EOF; el resto igual que Linux |
| IA (ONNX Runtime 1.30) | CPU; CUDA como paquete aparte más adelante | **CoreML** |
| LLamaSharp / Whisper.net | backends Vulkan | Metal / CoreML |
| AOT | `linux-x64` | `osx-arm64` |
| Entrega | **AppImage**; Flatpak después | `.app` en `.zip`/`.dmg`, firmado y **notarizado** (cuenta de Apple Developer) |
| HDR en el visor | SDR con mapeo de tonos al principio | EDR en la fase Mac |

## 4. Cómo encaja en la arquitectura

Regla de oro: **Windows no cambia de comportamiento.** Lo de Windows pasa sus ~11 570 pruebas igual antes y después de cada fase.

- **Destinos.** Core, Packages, Playback, Analysis y Modules.Protocol: `net10.0` puro. Media, Engine, Render, Audio y Export: `net10.0` con archivos por plataforma (`*.Windows.cs`, `*.Unix.cs`, `*.Linux.cs`, `*.Mac.cs`) incluidos por condición en el `.csproj`, sin `#if` sueltos; `[SupportedOSPlatform]` para que el analizador avise. App: `net10.0-windows10.0.26100.0` (WinUI, como hoy) **y** `net10.0-desktop` (Uno), con RID y arranque por destino.
- **Rutas (Core).** `schemaVersion` sigue en **15**, sin cambios de formato. Nuevo resolvedor de rutas guardadas que reconoce por su forma las de Windows (unidad, UNC, `\`) y las POSIX, y **antes de resolver** aplica el mapeo de rutas del usuario (en las preferencias, no en el proyecto); en Linux, si no existe con esas mayúsculas, busca sin distinguirlas y avisa. Cambia `MediaRef` y `MediaRelink`, con pruebas en los tres sistemas.
- **Render.** `Rodaje.Render/Gpu/IGpu.cs`: texturas, búferes, vistas, `Dispatch`, pasada de pantalla completa, copia, lectura, consultas de tiempo, **y el contrato de vida y sincronización**: adquirir una superficie (del decodificador o propia), enviar trabajo, saber cuándo terminó y reciclarla; en D3D11 es el candado del contexto de hoy, en Vulkan son layouts, familia de colas y semáforos por imagen (y los de `AVVkFrame`). `Compositor`, `EffectPipeline`, `LayerRenderer`, `Scopes`, `Export/*` y `Raw/*` hablan con `IGpu`; `Gpu/D3D11Gpu.cs` envuelve lo de hoy y `Gpu/VulkanGpu.cs` es lo nuevo. `build/compilar-shaders` (consola C#) genera el manifiesto de variantes y el SPIR-V. `Text/TitleRasterizer.*` se parte en maquetación y dibujo por plataforma.
- **Media.** `GpuDevice`, `DecoderSurfacePool`, `HardwareDecodeSupport` y `D3D11TexturePayload` pasan a «superficie del decodificador» (`IDecoderSurface`) con D3D11VA, Vulkan y VideoToolbox. `FfmpegLibrary` carga `.dll`/`.so`/`.dylib`. `ExportEncoderCatalog` gana los codificadores de cada sistema. `ChildProcessJob` → `ChildProcessGuard` por plataforma.
- **Engine.** Los tipos del pool dejan de ser D3D11 (12 archivos: tipos y nombres, lógica igual).
- **Audio.** `AudioOutput`/`AudioCapture` ya son la abstracción; se añaden Pulse y CoreAudio con el mismo contrato: reloj continuo, pausa, salto, reconexión al cambiar de salida y captura sincronizada.
- **Módulos.** `SharedMemoryRegion` resuelve el nombre a un archivo en Unix con las garantías de la tabla; la región sigue identificada por nombre, así que **el protocolo sigue en la versión 1** si las pruebas hostiles (#496) pasan también en Unix; si no, sube de versión. Tubería con ruta absoluta corta y autenticación del extremo por credenciales del socket.
- **Paquetes y catálogo.** Los editores viejos no saben de plataformas: **el índice de Windows no cambia** y cada sistema nuevo lee su propio índice firmado (`indice-linux-x64`, `indice-osx-arm64`) en el catálogo `v9`. Los paquetes de módulo para Linux/Mac llevan la plataforma en `paquete.json` y una versión de formato nueva que un editor viejo rechaza; `PackageInstaller` restaura el bit de ejecución.
- **App.** Los 20 archivos de `Views`/`Shell` con Win32 y los 4 de `Services` se parten en `.Windows.cs`/`.Desktop.cs`. Visor en Uno: primero lectura a la CPU y `SKImage` (una copia, todo el XAML encima funciona igual); después vista nativa (X11 / `NSView` + `CAMetalLayer`) con la cadena de intercambio Vulkan, y lo que necesite mezcla alfa (guías, tiradores semitransparentes) se dibuja en una última pasada del visor desde la geometría de `Views/Player/Logic`. El `TextBox` de títulos sigue en XAML encima (superponer sí se puede); foco, captura de puntero e IME se prueban sobre el visor real antes de decidir.
- **Arranque.** `Rodaje.Launcher` (el armazón de 50 ms) es solo de Windows; en Mac/Linux se mide primero sin armazón.
- **Entorno.** `build/dotnet-local.sh` y `preparar-entorno` en `pwsh` (mismas huellas); `.entorno/` por RID.
- **Pruebas sin WinUI.** Core/Engine/Render/Media/Export/Audio en `ubuntu-latest` y en Mac; Render en Linux contra **lavapipe** (Vulkan por software), en Mac contra MoltenVK. Matriz explícita de corredores: Ubuntu 24.04 + lavapipe; `macos-15` arm64 + MoltenVK; macOS 26 arm64 + KosmicKrisp cuando GitHub tenga esa imagen (`macos-13` ya no existe). Categoría nueva `SoloGpuReal`. Autoprueba (`--autoprueba`) del exe Uno AOT en Linux con Xvfb y en Mac.

### Cifras objetivo

Se miden con lo que ya existe (`GpuTimer`, `ClipPlayerStats`, `Rodaje.bat -Medir`, autoprueba), con un **contador de transferencias a la CPU** nuevo en la reproducción. La base se toma en Windows en la fase 0, en el **mismo PC de Daniel** con Linux en USB:

| Medida | Linux, RTX 3050 (driver propietario) | Sin GPU (lavapipe/llvmpipe) | Mac |
|---|---|---|---|
| Reproducción 4K HEVC 10 bits, 1 pista | 60 fps, 0 transferencias a la CPU | — | 60 fps (sin medir hasta tener equipo) |
| Composición, ms por cuadro (línea de tiempo de las pruebas de rendimiento) | ≤ 1,2 × D3D11 en el mismo equipo | 1080p30 de una pista ≥ 24 fps | ≤ 1,2 × Linux |
| Exportación 1080p H.264 por hardware | ≥ 0,85 × NVENC en Windows | — | cifra de referencia |
| Interfaz usable en caliente | < 1 s | < 1,5 s | < 1 s |
| Píxeles D3D11 (WARP) frente a Vulkan (lavapipe), todas las variantes | ≤ 1/255 en 8 bits, ≤ 1e-3 en RGBA16F | igual | igual |
| Títulos | misma maquetación que en Windows con la misma fuente (líneas idénticas, avance ±1 px); píxeles fuera de la tolerancia global | igual | igual |
| Tamaño | AppImage ≤ 1,3 × la carpeta de Windows | — | `.app` ≤ 1,3 × |

## 5. Fases

Cada una en su rama y worktree. La columna «Depende de» marca el orden; las que no dependen entre sí van en paralelo y tocan carpetas distintas. Cada fase cierra con revisión de bloque de otro modelo (regla 4).

| Fase | Semanas | Depende de | Qué | Hecho cuando | Quién |
|---|---|---|---|---|---|
| **0. Pruebas de concepto** | 1,5 | — | (a) Uno: `net10.0-desktop` en una rama de prueba, **publicado AOT para `linux-x64`** y abierto con Xvfb: arranca, carga un proyecto y responde a clics; % de vistas que compilan sin cambios. (b) Vulkan en Linux: decodificar NV12 y P010 reales con el hwaccel Vulkan en nuestro `VkDevice`, muestrear, presentar y reciclar durante 10 min sin fugas ni transferencias (con el driver de NVIDIA, en el PC de Daniel por USB, y en lavapipe por software). (c) Mac en el corredor: VideoToolbox → `MTLTexture` → `VkImage` por plano con MoltenVK. (d) Un shader con variantes (`EffectShaders`) por DXC frente a WARP. (e) Eco por tubería y memoria compartida en Linux y Mac. (f) Base de cifras en Windows. | Informe con: binario AOT Uno que pasa (a); (b) y (c) sin copias y estables; (d) ≤ 1e-3; (e) en verde. **Aquí se confirma Uno o se pasa a Avalonia, y se reajustan las semanas.** | Claude (a, informe), codex (b, c), agy (d, revisión), devin (e) |
| **1. Núcleo portátil** | 2 | 0 | Destinos y archivos por plataforma en Media, Engine, Audio (sin GPU), Modules.Protocol y Packages; rutas portátiles y mapeo en Core; `FfmpegLibrary` por plataforma; `ChildProcessGuard`; tubería, autenticación y memoria compartida en Unix; índices de catálogo por plataforma; `pruebas-linux.sh` a todos los proyectos sin GPU; trabajo de Mac en `ci.yml`. | Pruebas de esos proyectos en verde en Linux y Mac (salvo `SoloWindows`/`SoloGpuReal`), incluidas las hostiles de #496 y una de muerte abrupta del editor que no deja hijos vivos; Windows igual en sus ~11 570. | codex (destinos, Unix), Claude (protocolo, memoria compartida, catálogo), devin (renombres, `[SupportedOSPlatform]`), agy (pruebas nuevas) |
| **2. Entorno y FFmpeg** | 1 | 0 | FFmpeg LGPL linux-x64 y osx-arm64 con las exclusiones de hoy, huellas, auditoría de dependencias y fuente; `dotnet-local.sh`; `preparar-entorno` en `pwsh`; `docs/legal.md` y `LICENCIAS.md`. | `preparar-entorno` reproduce `.entorno` en los tres con las mismas huellas; la auditoría no encuentra nada GPL ni no libre por RID. | devin (scripts), codex (receta), agy (legal) |
| **3. Capa `IGpu` en Windows** | 2 | 1 | Render y Export pasan a `IGpu` con `D3D11Gpu` sin cambiar resultados, con el contrato de sincronización escrito; manifiesto de variantes y herramienta DXC. | Pruebas de Render/Export/App iguales **píxel a píxel**; autoprueba del exe AOT; arranque y ms por cuadro sin empeorar más del 2 %. | Claude (diseño de `IGpu`), codex (mover `Compositor`, `EffectPipeline`, etc.) |
| **4. Motor Vulkan** | 4 | 3 | `VulkanGpu` con todas las variantes, lectura y consultas de tiempo; Render y Export en lavapipe en la CI. | Pruebas de Render pasan en Vulkan con las tolerancias; exportación ProRes/FFV1 por CPU con los mismos cuadros (hash) que en Windows. | codex (motor), Claude (sincronización, memoria, revisión), agy (comparaciones), devin (pruebas sencillas) |
| **5. Títulos** | 2 | 1 | Maquetación y dibujo con HarfBuzz/Skia en Unix. | Casos de aceptación de títulos (saltos, respaldo de fuentes, unidades animadas) iguales en maquetación a Windows. | codex, agy (casos) |
| **6. Audio** | 2 | 1 | Pulse (asíncrono) y CoreAudio. | Pruebas de reloj, deriva, pausa, salto, cambio de salida y captura sincronizada en los dos; latencia medida. | codex, agy (pruebas) |
| **7. Módulos e IA** | 2 | 1 | Módulos por RID, `empaquetar-modulo.ps1` por plataforma, CoreML en Mac, CPU en Linux. | Pruebas de cada módulo en Linux y Mac; un editor viejo no ve los paquetes de otro sistema. | codex (módulos), devin (scripts), Claude (revisión de firma) |
| **8. Interfaz Uno, Linux** | 3 | 1 (y 4 para el visor) | `net10.0-desktop`; archivos partidos; selector de archivos, portapapeles, arrastrar y soltar; mapeo de rutas en Preferencias; visor por lectura a la CPU. | Autoprueba del exe AOT en Linux (Xvfb + lavapipe) en la CI; abrir, editar, reproducir y exportar el proyecto de aceptación; interfaz usable < 1 s en el PC de Daniel. | codex (partido por plataforma), devin (`.Desktop.cs` triviales), agy (aceptación), Claude (visor) |
| **9. GPU de punta a punta en Linux; visor nativo** | 2,5 | 4, 8 | Superficies Vulkan del decodificador en el compositor, VAAPI de respaldo, NVENC/VAAPI/Vulkan para exportar; vista nativa X11; pasada de guías y tiradores; prueba de foco, puntero e IME sobre el visor. | Cifras de la tabla en el RTX 3050; 0 transferencias en 4K; `pruebas-a-mano.md` con la lista de Linux. | Claude (interop de superficies), codex (codificadores, vista nativa), agy (mediciones, docs) |
| **10. Linux publicable** | 1 | 2, 5, 6, 7, 9 | AppImage; `distribuir` y `publicar-release` con el artefacto de Linux; Lanzador y catálogo por plataforma. | AppImage pasa la autoprueba en un Ubuntu 24.04 limpio (contenedor de la CI); revisión de bloque hecha. **Daniel decide si sale.** | devin (scripts), codex (empaquetado), agy (revisión), Claude (cierre) |
| **11. Mac** | 4 | 10 | VideoToolbox → Vulkan en el compositor; codificadores VideoToolbox; `NSView` + `CAMetalLayer`; EDR; `.app`, firma y notarización. | Autoprueba en `macos-15` (MoltenVK) y en macOS 26 (KosmicKrisp) cuando haya corredor; `.app` notarizado pasa `spctl -a`; cifras de Mac cuando haya un equipo real. | Claude (interop), codex (resto), agy, devin |

**Camino crítico:** 0 → 1 → 3 → 4 → 9 → 10 = 1,5 + 2 + 2 + 4 + 2,5 + 1 = **13 semanas** si 5–8 caben en paralelo (son de otras carpetas: `Text/`, `Rodaje.Audio`, `modules/`, `Rodaje.App`), **14 contando holgura**, y **~18 con Mac**. El paralelo es de agentes, pero la revisión de Claude y de Daniel no se multiplica: si la revisión se vuelve el cuello de botella, 5–7 se corren una tras otra y Linux pasa a ~16. Esfuerzo sumado ≈ 28 semanas-agente.

## 6. Lo que se comparte y lo que no

- **Igual en los tres:** Core (con rutas portátiles), Packages, Playback, Analysis, Modules.Protocol, la lógica de Engine y Export, Compositor/EffectPipeline sobre `IGpu`, los shaders HLSL, `Services`, `Logic`, los 36 XAML y el formato `.rodaje` (versión 15).
- **Por plataforma:** motor GPU (D3D11 / Vulkan), superficies del decodificador, maquetación y dibujo de títulos, audio, guarda de procesos hijos, memoria compartida y autenticación de la tubería, visor, índice del catálogo, empaquetado, el armazón de arranque (solo Windows).
- **Fuera de este bloque:** Linux arm64, Mac Intel, Wayland nativo para el visor, CUDA en los módulos, Flatpak, HDR en el visor de Linux, Subtitular/Rastrear/Lanzador en Mac/Linux (se benefician después por el submódulo).

## 7. Riesgos

| Riesgo | Efecto | Qué se hace |
|---|---|---|
| Uno no cubre partes de WinUI que usa Rodaje, o su AOT falla en ejecución | Más archivos partidos o pantallas vacías | Fase 0 exige un binario AOT que funcione, no solo que compile; plan B Avalonia |
| Mezcla alfa sobre la vista nativa; foco e IME del `TextBox` de títulos | Guías invisibles, edición de títulos rota | Visor por lectura a la CPU hasta la fase 9; pasada propia para lo semitransparente; prueba de foco e IME |
| Sincronización de Vulkan y vida de las superficies del decodificador | Parpadeos, cuadros viejos, bloqueos | Contrato en `IGpu` desde la fase 3; prueba de 10 min en la fase 0 |
| Ruta del socket > 104 bytes en Mac (`TMPDIR` ≈ 49 + `CoreFxPipe_Rodaje.Mod.pipe.<32 hex>` ≈ 59) | Ningún módulo arranca en Mac | Ruta absoluta corta (fase 1, con prueba) |
| Memoria compartida y tubería sin las garantías de Windows | Otro proceso del usuario lee o cambia cuadros | Creación exclusiva, permisos, dueño, credenciales del socket; hostiles de #496 en Unix |
| Procesos hijos huérfanos si el editor muere | FFmpeg o módulos vivos gastando CPU/GPU | `ChildProcessGuard` + limpieza al arrancar; prueba de muerte abrupta |
| Editores viejos confundidos por paquetes de otro sistema | Instalan algo que no corre | Índice por plataforma; el de Windows no cambia |
| MoltenVK no conformante; KosmicKrisp solo macOS 26 | Fallos raros en Mac | Pruebas de Render en los dos; Mac como vista previa |
| Sin Mac real | Cifras y fluidez sin verificar | «Vista previa» hasta que alguien lo pruebe |
| Drivers de Linux (NVIDIA propietario frente a NVK; Mesa viejo sin Vulkan Video) | Sin decodificación por GPU | Respaldo VAAPI y CPU automáticos |
| FFmpeg por plataforma arrastra algo GPL o sin fuente | Problema de licencia al publicar | Mismas exclusiones, auditoría por RID y fuente correspondiente (fase 2) |
| Rutas de Windows en un proyecto abierto en Linux | Medios sin encontrar | Resolvedor y mapeo de rutas (fase 1) |
| Uno cambia de versión a menudo | Roturas al actualizar | Versión fijada con lockfile |

## 8. Preguntas para Daniel (se sigue con la recomendada)

1. **¿Linux primero y Mac después?** Recomendada: sí (Linux se prueba en tu PC; Mac no).
2. **¿Cuenta de Apple Developer (99 USD/año) para firmar y notarizar?** Sin ella macOS bloquea la app y hay que abrirla con clic derecho. Recomendada mientras tanto: Mac como vista previa sin notarizar. **Es dinero y publicación: se para aquí antes de publicar Mac.**
3. **¿Solo Mac con Apple Silicon?** Apple deja Intel tras macOS 26 y KosmicKrisp no corre en Intel. Recomendada: sí, solo arm64.
4. **¿Cómo se prueba en Mac?** Comprar o pedir prestado un Mac con Apple Silicon, o solo los corredores de GitHub. Recomendada: corredores y «vista previa».
5. **¿Linux en tu PC?** Recomendada: USB persistente con Ubuntu 24.04 LTS y el driver propietario de NVIDIA (no toca Windows).
6. **¿Qué distribuciones Linux?** Recomendada: AppImage x64 probado en Ubuntu 24.04 LTS; Flatpak más adelante.
7. **¿IA en Linux con CUDA?** Recomendada: CPU primero; CUDA como complemento aparte si lo piden.
8. **¿Versión?** Recomendada: Linux en la versión que siga a la 1.0.3 y la 1.1; Mac en la siguiente.

## 9. Decisión propuesta

**D-066. Rodaje en Linux y macOS: Uno solo fuera de Windows, Vulkan detrás de una capa `IGpu`, Linux primero** (15 de noviembre de 2026; propuesta, pendiente de Daniel; después de la 1.0.3).

- **Contexto.** Daniel pide versiones para macOS y Linux con la misma idea de optimización, sin reescribir.
- **Decisión.** WinUI 3 se queda en Windows; Uno Platform (Skia) da la misma interfaz en Linux y Mac (Apple Silicon). El render pasa por `IGpu` (con su contrato de sincronización) con D3D11 en Windows y Vulkan en Linux/Mac; HLSL único, todas sus variantes compiladas a SPIR-V con DXC. Decodificación en el mismo dispositivo (Vulkan Video / VideoToolbox), codificación por hardware de cada sistema. AppImage y `.app` sin instalador. Formato de proyecto 15 sin cambios; rutas portátiles y mapeo de rutas en las preferencias. Protocolo de módulos en la versión 1 si pasa las pruebas hostiles en Unix. Índice de catálogo por plataforma; el de Windows no cambia.
- **Consecuencia.** Media, Engine, Render, Audio y Export con archivos por plataforma; formato de paquete con plataforma para Linux/Mac; CI con Linux y Mac completos.
- **Verificación.** Fases y cifras de `docs/mac-y-linux.md`; la fase 0 es la puerta.

## Revisión de ChatGPT (15-nov) y qué se hizo

Las 15 observaciones se aceptaron; se comprobaron a mano el esquema 15 (`Project.cs:13`), el `.dll` forzado (`FfmpegLibrary.cs:231`), el `TextBox` sobre el visor (`PlayerView.xaml:169`) y los requisitos de KosmicKrisp (LunarG, oct-2026). Cambios: formato 15 y rutas portátiles en Core (1); exportación y `FfmpegLibrary` en el alcance, «sin copias» limitado a la reproducción (2); manifiesto de las 77+ variantes (3); contrato de sincronización en `IGpu` (4); prueba real de superficies en la fase 0 (5); Mac solo Apple Silicon, matriz de corredores corregida (6); superponer sí, mezclar no, y prueba de foco e IME (7); fase 0 exige binario AOT (8); títulos como fase propia con maquetación (9); `ChildProcessGuard` para módulos, proxies y exportadores (10); autenticación y ciclo de vida de la memoria compartida (11); índice por plataforma (12); Pulse asíncrono y dos semanas de audio (13); exclusiones de FFmpeg y auditoría (14); calendario recalculado con dependencias (15). **Nada descartado.** Solo se matizó el 15: no se repartió la capacidad por persona porque los agentes corren en paralelo; el límite real es la revisión, y se dice cuánto se alarga si se satura.

## Fuentes (consultadas el 15-nov-2026)

- Uno Platform 6.6 y Native AOT: platform.uno/docs/articles/features/native-aot.html; infoq.com/news/2026/08/uno-platform-6-6; vistas nativas en Skia: platform.uno/docs/articles/features/using-skia-hosting-native-controls.html
- Avalonia 12 / 12.1: avaloniaui.net/blog/avalonia-12, avaloniaui.net/blog/release-12-1; interop GPU: github.com/AvaloniaUI/Avalonia/issues/9925
- Vulkan en Apple: lunarg.com/the-state-of-vulkan-on-apple-oct-2026/ (KosmicKrisp: macOS 26 y Apple Silicon); MoltenVK 1.4.2: github.com/KhronosGroup/MoltenVK/releases
- FFmpeg 9.0 / Vulkan: phoronix.com/news/FFmpeg-9.0-Released; `AVVkFrame`: ffmpeg.org/doxygen/9.0/structAVVkFrame.html; licencia: ffmpeg.org/legal.html
- ONNX Runtime 1.31 (CoreML, WebGPU; DirectML en desuso desde Build 2025): github.com/microsoft/onnxruntime/releases
- Silk.NET 3.0 en vista previa: dotnet.github.io/Silk.NET/docs/v3/CHANGELOG
- Corredores de GitHub: `macos-13` retirado (github.blog/changelog/2025-09-19)

## Decisiones y acuerdos del 15-nov

- **Daniel:** Linux primero, Mac después; sin reescribir.
- **Un solo stack de GPU con Unicomp:** Unicomp (D-017, commit 40852a2 de su repo) usará esta misma `IGpu` con su motor Vulkan por `externo/rodaje` en vez de Dawn/WebGPU; su interfaz irá con Qt 6. Su ronda X0 espera a que `IGpu` exista. Además del video, `IGpu` debe cubrir lo que necesita Unicomp:
  - selección por id: leer un píxel de un objetivo `R32_UINT`;
  - captura fuera de pantalla a PNG (su `--capturar` y su autoprueba);
  - objetivos con MSAA;
  - más adelante, acumulación de muestras para su «Render final».
