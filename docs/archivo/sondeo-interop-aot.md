# Sondeo: interop nativa compatible con Native AOT (fase 2)

Fecha: 9 de octubre de 2026. Equipo: Ryzen 7 7435HS (16 hilos), RTX 3050 Laptop 4 GB (driver 610.62), AtlasOS 11. SDK .NET 10.0.401. FFmpeg n9.0.2 `lgpl-shared` de BtbN (`avcodec-63`, `avformat-63`, `avutil-61`, `swresample-7`).

El código ya no está en el árbol de trabajo: se conserva en la etiqueta de git `sondeo-interop-aot`, en `herramientas/sondeos/AotInterop/` (`git checkout sondeo-interop-aot -- herramientas/sondeos` lo recupera). Es un proyecto de consola fuera de `Rodaje.slnx` con `PublishAot`, `IsAotCompatible`, los analizadores de recorte, AOT y archivo único, y `TreatWarningsAsErrors` heredado. Dos propiedades de MSBuild eligen qué se compila:

- `-p:Variante=` elige los enlaces de FFmpeg: `Enlazado`, `Cargado`, `AutoGen` o `NAudio`.
- `-p:Graficos=` elige la biblioteca de D3D11 y XAudio2: `TerraFX` o `Vortice`.

Cada combinación tiene su propio `obj\` y su `packages.<sabor>.lock.json`. `sondear.ps1` publica cada combinación, cuenta sus advertencias IL y ejecuta todas las pruebas.

## Resultado en una tabla

| Pieza | Opción | Advertencias IL al publicar con AOT | ¿Funciona en AOT? | Exe |
|---|---|---|---|---|
| FFmpeg | `FFmpeg.AutoGen.Bindings.DynamicallyLinked` 9.0.1.1 (DllImport) | **0** | Sí | 3,04 MB |
| FFmpeg | `FFmpeg.AutoGen.Bindings.DynamicallyLoaded` 9.0.1.1 (GetProcAddress y delegados) | **0** | Sí | 3,25 MB |
| FFmpeg | `FFmpeg.AutoGen` 9.0.1.1, todo en uno con delegados | **0** | Sí | 3,19 MB |
| D3D11, DXGI y XAudio2 | `TerraFX.Interop.Windows` 10.0.26100.6 | **0** | Sí | (incluido arriba) |
| D3D11, DXGI y XAudio2 | `Vortice.*` 3.8.3 (SharpGen.Runtime 2.4.2-beta) | **2** (IL2067 e IL2072 en `SharpGen.Runtime.TypeDataStorage`) | Sí, pero rompe `TreatWarningsAsErrors` | 3,98 MB |
| Audio | WASAPI propio con `[GeneratedComInterface]` (unas 150 líneas) | **0** | Sí | — |
| Audio | `NAudio.Wasapi` 3.1.0 (`WasapiPlayer`) | **0** | Sí | +0,3 MB |
| Audio | XAudio2 (con TerraFX o con Vortice) | 0 con TerraFX, 2 con Vortice | Sí | — |

Las advertencias de Vortice aparecen aunque solo se usen D3D11 y DXGI, sin XAudio2: vienen del camino de reflexión que SharpGen.Runtime usa para las vtables de callbacks. Con `TreatWarningsAsErrors` la publicación falla.

La hipótesis de partida sobre NAudio era falsa. NAudio 3.1.0 ya no usa `[ComImport]`, sino `[GeneratedComInterface]` con `StrategyBasedComWrappers`, y compila con cero advertencias. Su `WasapiOut` está marcado obsoleto (CS0618) y hay que usar `WasapiPlayerBuilder`.

Las licencias se comprobaron en nuget.org. FFmpeg.AutoGen 9.0.1.1 lleva un archivo `LICENSE.txt` MIT con la línea "Copyright (c) 2025 Ruslan Balanukhin (Rationale One)". Vortice 3.8.3, TerraFX.Interop.Windows 10.0.26100.6 y NAudio 3.1.0 declaran la expresión de licencia MIT.

## Mediciones

Las mediciones se hicieron con todas las combinaciones. Los rangos son entre corridas.

**Arranque.** El modo `version` arranca el exe, carga las DLL de FFmpeg y llama a `av_version_info`. En frío tarda de 100 a 170 ms. En caliente, de 14 a 18 ms medidos desde fuera del proceso. La carga de FFmpeg en sí tarda unos 2 ms desde `Main`. Crear el dispositivo D3D11 cuesta de 160 a 300 ms, casi todo en el driver.

**Costo de cada llamada.** Llamar 10 millones de veces a `avutil_version()` cuesta 8,2 ns por llamada con delegados (Cargado y AutoGen) y 9,3 ns con DllImport. La diferencia es irrelevante, porque las llamadas a FFmpeg son pocas y gruesas.

**Decodificación de H.264 1080p60.** El clip de prueba son 1200 cuadros a 20 Mb/s hechos con NVENC sobre `testsrc2`. Es contenido sintético y fácil, así que las cifras de software son optimistas.

| Modo | fps | CPU (del total de 16 hilos) |
|---|---|---|
| Software, `thread_count=0` (16 hilos) | 890 a 1280 | 56 a 63 % (unos 10 s de CPU por 20 s de video) |
| d3d11va con nuestro dispositivo, cuadros en la GPU | 625 a 667 | 0,8 a 2 % |
| d3d11va más `av_hwframe_transfer_data` a la CPU | 300 a 400 | 6,6 a 7 % |

La decodificación por hardware rinde unas 11 veces el tiempo real de 60 fps y casi no gasta CPU. El software corre más rápido en este clip, pero se come más de la mitad de la CPU, que hará falta para efectos, audio e interfaz. Para la vista previa lo correcto es d3d11va con los cuadros sin salir de la GPU. La copia a la CPU cuesta la mitad del rendimiento, así que solo se usa para miniaturas y análisis.

**El dispositivo compartido funciona y es exacto.** En H.264 de 8 bits, los cuadros salen como `AV_PIX_FMT_D3D11` y `sw_format=nv12`, en una textura NV12 1280x720 que es un arreglo de 25 capas con `BindFlags=Decoder|ShaderResource`. `ID3D11Texture2D::GetDevice` devuelve **nuestro** dispositivo. El plano Y que se lee de vuelta coincide byte a byte con la decodificación por software (diferencia máxima de 0 en tres cuadros).

En HEVC Main10 la textura es P010 y mide 1920x1152 para un video de 1920x1080. La diferencia máxima contra `yuv420p10le` también es 0, comparando el valor P010 desplazado `>> 6`.

**Audio.** El AAC mono a 44,1 kHz se pasa por `swr_convert` a float estéreo a 48 kHz. Salen exactamente 96 000 muestras (2,0000 s) y la frecuencia estimada es de 999,5 Hz. Esas muestras se reproducen con cada backend durante 2 s, registrando la posición del reloj de audio contra el `Stopwatch` cada 10 ms:

| Backend | Granularidad de la posición | Retraso respecto al reloj de pared | Pendiente | Residuo máx. contra la recta | Latencia |
|---|---|---|---|---|---|
| WASAPI propio (`IAudioClock::GetPosition`) | fina (frecuencia de 384 000/s) **más marca QPC** | 6,2 a 6,5 ms | 0,99994 a 1,00010 | 0,52 a 0,74 ms | buffer de 22 ms, periodo de 10 ms (mínimo 3 ms) |
| NAudio `WasapiPlayer.GetPosition()` | en bytes, sin QPC | 7,1 ms | 0,99987 a 1,00016 | 0,67 a 1,0 ms | 20 ms; 10 ms con `WithLowLatency` (IAudioClient3 activo) |
| XAudio2 (`GetState().SamplesPlayed`) | **por pasos de 10 ms** (480 muestras, el quantum del motor) | 3 ms | 0,9989 a 0,9999 | 0,7 a 6,3 ms | `CurrentLatencyInSamples` de unas 1880 muestras (39 ms) |

Hay dos defectos de XAudio2 como reloj maestro. `SamplesPlayed` avanza a saltos de 10 ms, así que hay que interpolar con un reloj externo y sin una marca de tiempo que ancle la lectura. Y vuelve a 0 cuando la voz termina su buffer con `END_OF_STREAM`. WASAPI entrega en `GetPosition` el instante QPC exacto en que se muestreó la posición; esa marca tenía como mucho 0,06 ms de antigüedad al leerla. Con la pareja (posición, QPC) el reloj maestro se interpola sin error acumulado. `GetStreamLatency` devolvió 0 en modo compartido por eventos, así que la latencia útil se calcula con `GetBufferSize` y el periodo.

## Recomendación (versiones para fijar)

1. **FFmpeg: `FFmpeg.AutoGen.Bindings.DynamicallyLinked` 9.0.1.1**, que trae como dependencia `FFmpeg.AutoGen.Abstractions` 9.0.1.1. Es la opción de exe más pequeño y usa P/Invoke directo resuelto en la compilación AOT, sin delegados. Con un `NativeLibrary.SetDllImportResolver` Rodaje controla la ruta exacta de cada DLL. `DynamicallyLoaded` es una alternativa equivalente: también da cero advertencias, cuesta 0,2 MB más y se configura con `LibrariesPath`. El paquete `FFmpeg.AutoGen` todo en uno también funciona, pero no tiene ventaja. Las tres variantes exigen exactamente FFmpeg 9 (`avcodec-63`).
2. **D3D11, DXGI y XAudio2: `TerraFX.Interop.Windows` 10.0.26100.6.** Es la única opción probada con cero advertencias. El precio es una API de punteros crudos (`ID3D11Device*`, `->Release()`), que conviene envolver en pocas clases propias del motor. Vortice 3.8.3 tiene la API más cómoda, pero sus 2 advertencias de SharpGen.Runtime incumplen la regla de cero advertencias. Solo se podría usar silenciando IL2067 e IL2072 en todo el proyecto, y eso taparía también los errores propios.
3. **Audio y reloj maestro: WASAPI propio con `[GeneratedComInterface]`**, que es `AudioWasapi.cs` del sondeo. No añade dependencias, da la posición con marca QPC y deja controlar el buffer y el periodo. Si más adelante hacen falta elegir dispositivo, recibir notificaciones de cambio de salida o IAudioClient3, **NAudio.Wasapi 3.1.0** (`WasapiPlayerBuilder`) es una alternativa válida y ya compatible con AOT. Su `GetPosition()` no da QPC: habría que pedir el `AudioClockClient` aparte. XAudio2 queda descartado como reloj maestro.

Esto corrige el plan de `CLAUDE.md`, que decía "WASAPI con NAudio". NAudio no está bloqueado por AOT, pero el reloj con QPC se consigue mejor con un WASAPI propio.

## Detalles que hay que respetar

- **Ruta de las DLL.** Con `DynamicallyLinked`, el resolver debe cargar por ruta absoluta (`NativeLibrary.Load(Path.Combine(carpeta, nombre + ".dll"))`) y **fallar** si no existe. Si devuelve 0, Windows busca por el orden normal, incluido el `PATH`, y podría cargar un FFmpeg GPL u otra versión. Las dependencias entre las DLL de FFmpeg, como avformat → avcodec → avutil, se resuelven solas: .NET carga por ruta absoluta con `LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR`. No hizo falta `SetDllDirectory`, que además cambia el estado de todo el proceso. En `dist\` lo más simple es poner las DLL en una subcarpeta `ffmpeg\` y pasar esa ruta.
- **Choque de nombres.** `FFmpeg.AutoGen.Abstractions` define structs opacos `ID3D11Device` e `ID3D11DeviceContext` que chocan con los de Vortice y TerraFX. FFmpeg no se importa con `global using`. En el código que mezcla ambos mundos, el puntero se asigna como `*(nint*)&d3d->device = ...`.
- **Callbacks sin delegados.** `get_format` se asigna como puntero a función de un método `[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]` (`new AVCodecContext_get_format_func { Pointer = (nint)(delegate* unmanaged[Cdecl]<...>)&ElegirFormato }`). Una excepción no puede cruzar a código nativo, así que el método la atrapa y devuelve `AV_PIX_FMT_NONE`.
- **Receta para que FFmpeg decodifique en nuestro dispositivo D3D11:**
  1. Crear el dispositivo con `D3D11_CREATE_DEVICE_VIDEO_SUPPORT`, porque sin él FFmpeg no obtiene `ID3D11VideoDevice`.
  2. `av_hwdevice_ctx_alloc(AV_HWDEVICE_TYPE_D3D11VA)`, después `((AVD3D11VADeviceContext*)((AVHWDeviceContext*)ref->data)->hwctx)->device = nuestroDispositivo` tras un **`AddRef`**, porque FFmpeg hace `Release` al liberar, y por último `av_hwdevice_ctx_init`. Init rellena el contexto inmediato, el video device y el video context, crea el candado y llama a `ID3D10Multithread::SetMultithreadProtected(TRUE)`.
  3. `codecCtx->hw_device_ctx = av_buffer_ref(ref)` y un `get_format` propio.
  4. Dentro de `get_format`, al ver `AV_PIX_FMT_D3D11`, se llama a `avcodec_get_hw_frames_parameters(ctx, ctx->hw_device_ctx, AV_PIX_FMT_D3D11, &frames)` y se añade `D3D11_BIND_SHADER_RESOURCE` a `((AVD3D11VAFramesContext*)fc->hwctx)->BindFlags`. También se sube `initial_pool_size`, unas 8 texturas más, para que la caché de cuadros pueda retener texturas sin que el decodificador se quede sin ellas. Después vienen `av_hwframe_ctx_init` y `ctx->hw_frames_ctx = frames`. Si algo falla se devuelve el primer formato de software, que es la caída automática.
  5. Cada `AVFrame` trae `data[0]`, que es `ID3D11Texture2D*` (un arreglo compartido por todo el pool), y `(intptr)data[1]`, que es el índice de la capa. Para muestrear se crea una SRV de `Texture2DArray` con `FirstArraySlice = índice`: en NV12, `R8_UNORM` para Y y `R8G8_UNORM` para UV; en P010, `R16_UNORM` y `R16G16_UNORM`. La textura conserva su referencia mientras viva el `AVFrame` (`av_frame_ref` y `av_frame_unref`).
  6. **Tamaño acolchado.** La textura puede medir más que el cuadro, por ejemplo 1152 líneas para un HEVC 1080p. Siempre se usan `frame->width` y `frame->height` y se recorta en el shader.
  7. **Hilos.** El contexto inmediato es compartido con el decodificador. `SetMultithreadProtected` serializa cada llamada, pero las secuencias de varias llamadas, como una copia seguida de un `Map`, deben ir entre `AVD3D11VADeviceContext.lock` y `unlock` si el decodificador corre en otro hilo.
- **Formatos de píxel.** H.264 y HEVC de 8 bits dan NV12. HEVC Main10 da P010, con el dato en los 10 bits **altos** de cada palabra de 16; el software da `yuv420p10le`, con el dato en los bits bajos.
- **Audio.** `swr_alloc_set_opts2` recibe `AVChannelLayout` (la API vieja de máscaras ya no existe en FFmpeg 9). Hay que vaciar el remuestreador al final con `swr_convert(..., null, 0)`. WASAPI en modo compartido con `AUTOCONVERTPCM | SRC_DEFAULT_QUALITY` acepta float a 48 kHz aunque el mezclador use otra frecuencia.
- **Native AOT y link.exe.** Igual que en `publicar-aot-prueba.ps1`, hace falta la carpeta de `vswhere.exe` en el `PATH` del proceso, y `sondear.ps1` ya la añade.

## Cómo reproducirlo

Los archivos de prueba se generan con el `ffmpeg.exe` de la misma compilación LGPL. Ese exe incluye NVENC, que es un codificador por hardware y no una biblioteca GPL:

```
ffmpeg -f lavfi -i testsrc2=s=1280x720:r=30:d=2 -f lavfi -i sine=f=1000:sample_rate=44100:d=2 -c:v h264_nvenc -pix_fmt yuv420p -c:a aac -b:a 128k -shortest corto.mp4
ffmpeg -f lavfi -i testsrc2=s=1920x1080:r=60:d=20 -c:v h264_nvenc -b:v 20M -g 120 -pix_fmt yuv420p bench1080.mp4
ffmpeg -f lavfi -i testsrc2=s=1920x1080:r=30:d=2 -c:v hevc_nvenc -profile:v main10 -pix_fmt p010le hevc10.mp4
```

Después se recupera el sondeo desde la etiqueta y se ejecuta:

```
git checkout sondeo-interop-aot -- herramientas/sondeos
powershell -NoProfile -ExecutionPolicy Bypass -File herramientas\sondeos\AotInterop\sondear.ps1 -Muestras <carpeta> [-Ffmpeg <carpeta con las DLL>]
```

Sin `-Ffmpeg`, el exe busca las DLL junto a sí mismo, en `RODAJE_FFMPEG` o en el primer `.entorno\ffmpeg\bin` que encuentre subiendo de carpeta. La combinación por defecto (`Enlazado` y `TerraFX`) se publica en modo estricto con `dotnet-local.cmd publish herramientas\sondeos\AotInterop\AotInterop.csproj -c Release`, sin errores ni advertencias.

## Pendiente para la fase 2

- **WinUI 3.** Falta presentar en un `SwapChainPanel` mediante `ISwapChainPanelNative`, que es de WinAppSDK. TerraFX no lo trae y habrá que declararlo a mano con `[GeneratedComInterface]` o como una vtable cruda. Sigue sin estar probado con AOT junto a WinUI.
- **Medir con material real.** Las cifras de arriba vienen de clips sintéticos. Hay que repetirlas con videos de cámara o de teléfono, con GOP largo y B-frames, y con HEVC a 4K.
