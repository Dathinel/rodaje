# Sondeo de la fase 4: WinUI 3 sin empaquetar con Native AOT

Antes de construir la interfaz de D-018 había que comprobar que el stack de D-003 y D-004 (WinUI 3, sin MSIX, autocontenido, Native AOT) funciona de verdad en este equipo con el motor de Rodaje, y que el reproductor dentro de XAML no pierde nada frente al visor Win32 de la fase 3. El código está en `src/Rodaje.App` y la medición se repite con:

```
powershell -NoProfile -ExecutionPolicy Bypass -File herramientas\sondeo-winui.ps1 [-Segundos 20] [-SinPublicar]
```

El script publica, cuenta advertencias y tamaños, copia las DLL LGPL de FFmpeg junto al exe y corre `Rodaje.exe --medir N`, que reproduce, mide y escribe sus cifras en `TestResults\sondeo-winui-*`. Equipo: RTX 3050 Laptop, AtlasOS (Windows 11 26100), pantalla 1920×1080 al 100 %, 10 de octubre de 2026.

## Qué funciona

- **Windows App SDK 2.5.1** (la última estable 2.x), sin empaquetar (`WindowsPackageType=None`) y autocontenido (`WindowsAppSDKSelfContained=true`), con `PublishAot=true`. El exe arranca desde una copia de `dist\` en una ruta con espacios y eñe; los módulos cargados (`Microsoft.UI.Xaml.dll`, `Microsoft.WindowsAppRuntime.dll`, etc.) salen de la carpeta de la app y no de los Windows App Runtime 1.1 a 2.x instalados en el sistema. No hace falta bootstrapper ni instalar el runtime. Ninguna DLL depende de `vcruntime140` ni `msvcp140`: solo de la UCRT, que trae Windows 10 y 11.
- **Advertencias: cero.** Ni de recorte ni de AOT (IL2xxx, IL3xxx), con `TreatWarningsAsErrors`, los analizadores de recorte, AOT y archivo único activos, y `CsWinRTAotWarningLevel=2`. La única supresión que queda es la `IL2121` que el SDK pone por defecto. No hace falta el plan B de D-004 (ReadyToRun).
- **XAML compilado.** `x:Bind` para textos, el glifo de reproducir y los clics, y un `PlayerState` con `INotifyPropertyChanged`; nada usa `{Binding}` ni reflexión.
- **Distribución D-018.** Barra de título propia (`ExtendsContentIntoTitleBar`) con menú y EXPORTAR, riel de íconos con la biblioteca, reproductor, propiedades con pestañas y rombos de keyframe, y la línea de tiempo con la barra estilo Vegas y tres pistas de muestra. La paleta es grafito, con el acento `#E8833A` y `ControlCornerRadius` y `OverlayCornerRadius` a 2.
- **Reproductor en un `SwapChainPanel`.** Usa nuestro `GpuDevice` (TerraFX), una cadena `CreateSwapChainForComposition` FLIP_DISCARD con latencia máxima 1 y objeto de espera, y `Compositor` más `Presenter` sobre una `TimelineSession`. `ISwapChainPanelNative::SetSwapChain` se llama por su tabla virtual (puntero de función `unmanaged[Stdcall]` sobre el `IInspectable` de CsWinRT), así que no hacen falta `ComImport` ni reflexión.
- **Hilos.** Un hilo de render es dueño del dispositivo, la sesión, el compositor y la cadena, y mantiene el contrato de `TimelineSession`: transporte, `Tick` y `Dispose` desde un mismo hilo. La interfaz solo encola órdenes y lee una instantánea inmutable, y nunca espera al render. El motor arranca en `Main` antes que WinUI, así que FFmpeg, la GPU y la apertura de los medios corren en paralelo con la creación de la ventana.
- **Tamaño y DPI.** El búfer mide `ActualSize × CompositionScale` en píxeles físicos y `SetMatrixTransform(1/escala)` lo devuelve a unidades del panel. Se comprobó redimensionando la ventana tres veces y aplicando una `ScaleTransform` de 1,5 al panel, que cambia `CompositionScaleX` igual que un monitor al 150 %: la cadena pasó a 1200×770 px para un panel de 800×513 DIP.
- **Espacio global.** Con el foco en EXPORTAR, seis pulsaciones de Espacio cambiaron seis veces la reproducción y no pulsaron el botón ninguna vez.

## Tamaños (sin `.pdb`)

| | Win32 (`dist\Rodaje-visor`) | WinUI 3 (`dist\sondeo-winui`) |
|---|---|---|
| Exe | 5,7 MB | 8,5 MB (incluye la proyección de CsWinRT y el código de XAML) |
| Windows App SDK junto al exe | — | 57,3 MB en unos 240 archivos |
| FFmpeg | 150 MB (con `ffmpeg.exe` y las DLL de proxies) | 114,5 MB (solo las 5 DLL del reproductor) |
| Carpeta | 156 MB | 180 MB |

Con el metapaquete `Microsoft.WindowsAppSDK` la carpeta pesaba 134 MB sin FFmpeg, porque trae Windows ML (`onnxruntime.dll` y `DirectML.dll`, 38 MB), Widgets, Search y AI. Por eso se referencian solo los componentes `WinUI`, `Foundation` e `InteractiveExperiences` en las versiones que fija el 2.5.1.

## Cifras

Clip H.264 1080p a 23,976 fps en una secuencia de 60 Hz, con el panel a 800×513 px. La "reproducción limpia" son 20 s después de 1,5 s de calentamiento, sin teclas ni cambios de tamaño.

| | Win32 (visor, mismo clip, `--prueba 20`) | WinUI 3, 1 capa | WinUI 3, 4 capas 1080p (H.264, HEVC, HEVC 10 bits) |
|---|---|---|---|
| Ventana visible (medido desde fuera, 5 corridas) | 207-272 ms | 263-273 ms | |
| Primer cuadro desde que nace el proceso | no lo mide | 502-523 ms en caliente; 1578 ms en la primera corrida tras publicar | 553 ms |
| Intervalo entre presentaciones (mediana / máx.) | 16,66 / 20,6 ms en la parte continua | 16,66 / 18,05 ms, p99 17,8; 0 refrescos perdidos de 1199 | 16,66 / 18,48 ms; 0 perdidos |
| Composición en GPU (mediana / máx.) | 0,86 / 5,9 ms | 0,74 / 5,4 ms | 1,92 / 7,6 ms |
| CPU del render por cuadro (Tick, componer y Present) | | 0,15 ms mediana, 0,31 máx. | 0,14 / 1,81 ms |
| Cola del hilo de la interfaz (ping cada 50 ms) | no aplica: un solo hilo | 0,13 ms mediana, 0,36 máx. | 0,12 / 1,17 ms |
| Tecla → `KeyDown` en la interfaz | | 0,30 ms mediana (máx. 0,44) | 0,24 ms |
| `KeyDown` → la sesión cambia | 0,2-2,5 ms ("la orden tardó") | 13 ms mediana, 22 máx. | 3 / 13 ms |
| La sesión cambia → `Present` | 16-17 ms ("en pantalla a los") | 15 ms (un refresco) | 16 ms |
| Redimensionar → primer `Present` al tamaño nuevo | Windows detiene el bucle mientras se arrastra | 31-34 ms (2 refrescos) | 32-44 ms |
| Cuadros perdidos, tarde, faltas de audio | 0 en la parte continua | 0, 0, 0 | 0, 0, 0 |
| CPU del proceso | 6,5 % de un núcleo (con ediciones y seeks) | 10,5 % de un núcleo | 12,7 % |
| Memoria privada | | 320 MB | 518 MB |

Cómo pesa el refresco de la interfaz en la CPU (15 s, 1 capa): con `CompositionTarget.Rendering`, que actualiza el cursor y el tiempo en cada cuadro de XAML, 13,3 %; con un temporizador a 30 Hz, 12,1 %; a 4 Hz, 8,4 %. El ritmo de presentación fue el mismo en los tres casos. El valor por defecto es 30 Hz (`--ui-hz`).

## Trampas encontradas

1. **Sin `EnableMsixTooling=true`, una app sin empaquetar no genera su `resources.pri`.** `InitializeComponent` no encuentra el XAML y el proceso muere al instante con `0xC000027B` (excepción guardada de XAML), sin mensaje ni entrada en el registro de eventos. Además, `Program.cs` escribe cualquier error sin capturar en `rodaje-error.txt` junto al exe, porque una app de ventana no tiene consola.
2. **El metapaquete arrastra 38 MB de IA** que Rodaje no usa (ver Tamaños). Al subir de versión hay que copiar las versiones de los componentes del `.nuspec` del metapaquete.
3. **`SatelliteResourceLanguages` no recorta las 86 carpetas de idioma de WinUI** (archivos `.mui`, 3,4 MB). Se dejan: borrarlas a mano rompería la búsqueda de recursos de Windows en idiomas de reserva.
4. **CsWinRT1028 con `CsWinRTAotWarningLevel=2`:** una clase con `IDisposable` cuenta como tipo WinRT, porque se proyecta como `IClosable`, y debe ser `partial`. Saltó en `PlayerEngine` y en `CompositionSwapChain`; se corrigió marcándolas `partial`. Es la única familia de avisos que apareció.
5. **Bootstrap autocontenido:** no hay que llamar a `Bootstrap.Initialize`. El paquete genera la activación sin registro y un inicializador automático. Con `Main` propio (`DISABLE_XAML_GENERATED_MAIN`) hay que llamar a `ComWrappersSupport.InitializeComWrappers()` antes de `Application.Start`.
6. **Atajos globales y foco:** un `Button` con el foco se activa con Espacio al soltar la tecla. Por eso el atajo se intercepta en `PreviewKeyDown` y también en `PreviewKeyUp` sobre la raíz, salvo cuando el foco está en un campo de texto. El futuro sistema de atajos (página D-018) debe vivir en esa capa de túnel, no en `KeyboardAccelerator` sueltos.
7. **La entrada inyectada:** Windows no deja que un proceso lanzado en segundo plano se ponga al frente, y `SendInput` escribiría en otra ventana. Cuando la ventana no está al frente, la medición envía `WM_KEYDOWN` y `WM_KEYUP` a la ventana `InputSite` de WinUI, y así lo dice el registro. Mide la cola de mensajes, no el hilo de entrada en bruto del sistema.
8. **Latencia de las órdenes:** reproduciendo, el hilo de render espera el objeto de latencia de la cadena, así que una orden espera hasta un refresco antes de aplicarse (13 ms de mediana). Si hiciera falta, se puede esperar a la vez el objeto y un evento de órdenes (`WaitForMultipleObjects`). Hoy Espacio → imagen tarda unos 30 ms, dos refrescos, frente a uno del visor.
9. **DPI real:** el equipo está al 100 %. La ruta de escala se verificó con `ScaleTransform`, que da el mismo `CompositionScaleChanged`, pero falta probar un monitor al 125 o 150 % y mover la ventana entre monitores.
10. **AtlasOS:** no faltó nada. Están las fuentes Segoe Fluent Icons, Segoe MDL2 y Segoe UI Variable. WebView2 no hace falta: el paquete solo pone `WebView2Loader.dll` y `Microsoft.Web.WebView2.Core.dll`, y el runtime solo se usaría con un control WebView2.
11. **Licencia:** los binarios del Windows App SDK exigen que usuarios y distribuidores acepten términos que protejan a Microsoft (la MIT sola no basta) e incluyen una cláusula de indemnización y un aviso de recolección de datos. Detalle en `LICENCIAS.md`; antes de publicar, `docs/legal.md` debe añadir el texto de usuario final y los avisos de WebView2 y CsWinRT a `THIRD-PARTY-NOTICES.txt`.

## Lo que solo puede juzgar Daniel

Abrir `dist\sondeo-winui\Rodaje.exe` con un video arrastrado encima (o sin argumentos, para el clip de prueba) y comprobar cuatro cosas:
- si se ve fluido;
- si al arrastrar o redimensionar la ventana con el ratón el video sigue moviéndose (en el visor se congelaba);
- si Espacio pausa siempre, aunque se haya hecho clic antes en un botón o en el menú;
- si la paleta y las esquinas se sienten como en la maqueta.

## Recomendación: seguir con WinUI 3 (D-003 y D-018 sin cambios)

Las tres dudas que podían tumbarlo salieron bien: Native AOT con cero advertencias, distribución autocontenida que no depende de lo instalado y un reproductor que en XAML presenta igual que el Win32, sin refrescos perdidos y con el hilo de la interfaz libre (0,1 ms de cola). Lo que cuesta:

- 57 MB más en `dist\`;
- unos 60 ms más hasta ver la ventana;
- 4 a 6 puntos más de CPU de un núcleo;
- los términos de Microsoft para sus binarios.

Las alternativas comparadas:

| Opción | A favor | En contra |
|---|---|---|
| **WinUI 3 (recomendada)** | Controles, texto, IME, accesibilidad, menús, DPI y temas hechos; `SwapChainPanel` sin copias; AOT probado | 57 MB y licencia de Microsoft; un hilo de interfaz que no hay que bloquear nunca |
| Win32 con interfaz dibujada a mano | Exe de 6 MB, cero dependencias, latencia de un refresco | Hay que escribir de cero la edición de texto, el IME, la accesibilidad, los menús, el foco, el desplazamiento y el DPI: meses de trabajo que no aportan al editor |
| WPF con `D3DImage` | Herramientas maduras | WPF no admite recorte ni Native AOT (rompe D-004: runtime completo de unos 150 MB o más). `D3DImage` pasa por D3D9 y copia cada cuadro |

El siguiente paso de la fase 4 es convertir este sondeo en la app: mover `CompositionSwapChain` a `Rodaje.Render` (que cumple también el pendiente de sacar `VideoRenderer` del visor), separar la vista en controles y borrar el visor cuando la app lo iguale.
