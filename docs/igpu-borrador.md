# `IGpu`: borrador del contrato

**Superado:** el contrato vigente está en [igpu.md](igpu.md) (fase 3). Esto queda como historia de la fase 0 y de la revisión de Unicomp.

Borrador de la fase 0 de D-066 ([propuestas/mac-y-linux.md](propuestas/mac-y-linux.md)); resultado de la fase en [mac-y-linux-fase0.md](mac-y-linux-fase0.md). **Solo es el contrato**: `Rodaje.Render` no se mueve a `IGpu` hasta la fase 3. Lo usarán dos motores (D3D11 en Windows, Vulkan en Linux y macOS) y dos programas: Rodaje y Unicomp (D-017 de Unicomp, por `externo/rodaje`).

## De dónde sale

Lo que `Rodaje.Render`, `Rodaje.Media/Gpu` y `Rodaje.Export` llaman hoy de D3D11 (contado en `main`, 227883a6):

- **Contexto:** `Map`/`Unmap` (18), `PSSetShaderResources` (17), `CSSetShader` (12), `CSSetUnorderedAccessViews` (11), `CSSetShaderResources` (10), `ClearRenderTargetView` (10), `Dispatch` (7), `CopySubresourceRegion` (7), `CopyResource` (6), `GenerateMips` (3), consultas (`Begin`/`End`/`GetData`), un solo `Draw` (triángulo de pantalla completa de `QuadPipeline`), `UpdateSubresource`, `Flush`, `ClearState`.
- **Dispositivo:** `CreateTexture2D` (16), `CreateBuffer` (12), vistas SRV/UAV/RTV, `CreateTexture3D` (la LUT), sombreadores de píxel, de cómputo y un vértice, muestreadores, mezcla, rasterizador, `CreateQuery`, `CheckFormatSupport`.
- **Formatos:** RGBA16F (intermedio, D-016), NV12/P010/P016 (superficies del decodificador), BGRA8 y RGBA8, R8/RG8/R16/RG16 (planos), RGBA16, RGBA32F, R32 sin tipo, R16_UINT.
- **Sincronización:** un contexto inmediato compartido con el decodificador bajo candado (`GpuDevice.LockContext`), superficies retenidas con cuenta de referencias (`D3D11TexturePayload`).

## Principios

1. **Fina.** Los nombres siguen el modelo de D3D11 que el render ya usa (textura, vista implícita, pasada de pantalla completa, `Dispatch`); el motor Vulkan carga con lo suyo (layouts, descriptores, pases) por dentro.
2. **Sin COM ni punteros nativos en la firma.** Recursos como clases selladas opacas que libera quien las crea (`IDisposable`); nada de reflexión (AOT).
3. **Los registros de HLSL son la dirección.** Se enlaza por `t#`, `s#`, `b#`, `u#`; el motor Vulkan los traduce con los desplazamientos fijos del manifiesto de variantes (los mismos `-fvk-*-shift` con que DXC compiló).
4. **El orden lo da quien graba.** Todo trabajo de GPU se graba en un `IGpuFrame`; no hay estado global de pipeline que sobreviva entre cuadros.
5. **Dos niveles.** *Núcleo* es lo que Rodaje necesita. *Raster* es lo que añade Unicomp (geometría, profundidad, MSAA). Un motor puede declarar que no trae *Raster* (`GpuCaps`), pero los dos previstos lo traen.

## Contrato

Pseudocódigo C# (los nombres pueden cambiar en la fase 3; la forma no debería).

```csharp
public interface IGpu : IDisposable
{
    GpuCaps Caps { get; }                 // límites, formatos, MSAA máximo, cómputo, tipo de interop con el decodificador
    bool IsLost { get; }                  // TDR, VK_ERROR_DEVICE_LOST: todo recurso deja de valer; se crea otro IGpu
    event Action? Lost;

    GpuTexture CreateTexture(in GpuTextureDesc desc);            // 2D o 3D; mips; Samples para MSAA
    GpuBuffer CreateBuffer(in GpuBufferDesc desc);               // constantes, vértices, índices, estructurado
    GpuSampler CreateSampler(in GpuSamplerDesc desc);
    GpuShader CreateShader(ShaderVariantId variant);             // del manifiesto: D3DCompile en Windows, SPIR-V incrustado en Vulkan
    GpuPipeline CreatePipeline(in GpuPipelineDesc desc);         // sombreadores + mezcla + (Raster) entrada de vértices, profundidad, muestras
    IGpuSwapchain CreateSwapchain(in GpuSurfaceDesc surface);    // SwapChainPanel, HWND, ventana X11 o CAMetalLayer
    GpuTimer CreateTimer();

    IGpuFrame BeginFrame();                                      // ver «Sincronización»
}

public interface IGpuFrame : IDisposable
{
    // Subida y copia
    void Write<T>(GpuBuffer buffer, ReadOnlySpan<T> data) where T : unmanaged;
    void Write(GpuTexture texture, int mip, ReadOnlySpan<byte> pixels, int rowPitch);
    void Copy(GpuTexture source, GpuTexture destination);
    void Copy(GpuTexture source, in GpuBox region, GpuTexture destination, int x, int y);
    void GenerateMips(GpuTexture texture);
    void Clear(GpuTexture target, in GpuColor color);
    void ClearUint(GpuTexture target, uint value);

    // Superficies del decodificador (ver «Sincronización»)
    GpuDecoderPlanes Acquire(IDecoderSurface surface);           // planos muestreables válidos hasta que el cuadro termina

    // Núcleo: pantalla completa y cómputo
    void DrawFullscreen(GpuPipeline pipeline, in GpuTargets targets, in GpuBindings bindings, in GpuViewport viewport);
    void Dispatch(GpuPipeline pipeline, in GpuBindings bindings, int groupsX, int groupsY, int groupsZ);

    // Raster (Unicomp)
    void Draw(GpuPipeline pipeline, in GpuTargets targets, in GpuBindings bindings, in GpuViewport viewport, in GpuDrawCall call);
    void Resolve(GpuTexture multisampled, GpuTexture destination);

    // Lectura y medida
    GpuReadback Readback(GpuTexture texture, in GpuBox region);  // asíncrona: los bytes llegan cuando la valla termina
    void BeginTimer(GpuTimer timer);
    void EndTimer(GpuTimer timer);

    GpuFence Submit();                                           // envía lo grabado; Dispose sin Submit lo descarta
}

public interface IGpuSwapchain : IDisposable
{
    void Resize(int width, int height, double scale);
    GpuTexture AcquireBackBuffer(IGpuFrame frame);
    void Present(GpuFence after, bool vsync);
}

public interface IDecoderSurface            // Rodaje.Media: D3D11VA, AVVkFrame o CVPixelBuffer
{
    FramePixelFormat PixelFormat { get; }   // NV12, P010…
    int Width { get; }
    int Height { get; }
    VideoColor Color { get; }
    bool AddRef();                          // falso si ya se liberó (como D3D11TexturePayload hoy)
    void Release();                         // a cero vuelve a su pool
}
```

Tipos de datos (sin comportamiento): `GpuTextureDesc` (ancho, alto, profundidad, formato, mips, `Samples`, usos `Sampled | RenderTarget | Storage | DepthStencil | CopySource | CopyDestination`), `GpuFormat` (los de arriba más `R32Uint`, `R32Float` y `D32Float`), `GpuBindings` (hasta 16 texturas, 4 muestreadores, 4 búferes de constantes, 8 de almacenamiento, por número de registro), `GpuTargets` (hasta 4 colores y profundidad opcional), `GpuPipelineDesc` (`Blend`: opaco, alfa premultiplicado, aditivo; en *Raster*: disposición de vértices, prueba de profundidad, descarte de caras, `Samples`), `GpuDrawCall` (vértices, índices, instancias).

## Sincronización

Es la parte que D3D11 regala y Vulkan cobra. El contrato es el mismo para los dos motores; cambia quién hace el trabajo.

| Regla | D3D11 | Vulkan |
|---|---|---|
| **1. Un cuadro, un dueño.** `BeginFrame` da el único sitio donde se graba; lo usa un hilo. Dos cuadros a la vez (visor y exportación) se serializan. | `BeginFrame` toma el candado del contexto inmediato (el de hoy, compartido con el decodificador) y `Dispose`/`Submit` lo suelta. | Un búfer de órdenes por cuadro; `Submit` entra a la cola bajo el candado de la cola, **el mismo que usa FFmpeg** (`lock_queue`/`unlock_queue` de `AVVulkanDeviceContext`). |
| **2. Adquirir una superficie del decodificador.** `Acquire` la retiene (`AddRef`) hasta que termina la valla del cuadro; devuelve los planos listos para muestrear. Nunca se copia. | SRV por plano sobre la porción del arreglo de texturas (como `PlaneViewCache`). | `lock_frame`; se lee de `AVVkFrame` el layout, el semáforo de línea de tiempo con su valor y la familia de cola de cada imagen; el cuadro espera ese semáforo, pasa la imagen a `SHADER_READ_ONLY`, y al enviar señala el valor siguiente y **escribe de vuelta** layout, valor y familia; `unlock_frame`. (El prototipo de la fase 0 hace esto en un solo hilo y devuelve la imagen a su layout original; el candado de cola con dos hilos queda por probar en la fase 4.) En macOS: `MTLTexture` por plano del `IOSurface` importado como `VkImage` (`VK_EXT_metal_objects`), sin semáforo. |
| **3. Saber cuándo terminó.** `Submit` devuelve una `GpuFence`: `IsComplete`, `Wait(timeout)`. | Consulta de evento (o `Flush` + la espera de `Map`). | Valla o semáforo de línea de tiempo propio. |
| **4. Reciclar.** `Dispose` de un recurso, o `Release` de una superficie, **no lo destruye mientras una valla pendiente lo use**: va a una cola de destrucción diferida que se vacía al completarse la valla. Los pools (`RenderTargetPool`, `DecoderSurfacePool`) reutilizan solo lo ya completado. | Inmediato: COM cuenta las referencias que el contexto retiene. | Cola diferida por valla; sin ella hay uso tras liberar. |
| **5. Lectura.** `Readback` graba la copia; los bytes se piden a `GpuReadback.TryRead(out span)` (falso si la valla no ha terminado) o `Read(timeout)`. Filas de arriba abajo, `RowPitch` declarado, sin relleno al final. | Textura de staging + `Map` (con `DO_NOT_WAIT` para `TryRead`). | Copia a un búfer visible desde la CPU. |
| **6. Presentar.** `Present(after)` espera a la valla del cuadro que dibujó el búfer trasero. | `IDXGISwapChain::Present`. | Semáforos de adquisición y de presentación de la cadena. |
| **7. Pérdida del dispositivo.** Cualquier llamada puede fallar con `GpuLostException`; `IsLost` queda fijo; el dueño destruye todo y crea otro `IGpu`. Las superficies del decodificador de ese dispositivo se descartan. | `DXGI_ERROR_DEVICE_REMOVED`. | `VK_ERROR_DEVICE_LOST`. |
| **8. Tiempos.** `BeginTimer`/`EndTimer` dentro de un cuadro; `GpuTimer.TryGetMilliseconds` cuando la valla termina. | Consultas `TIMESTAMP` + `TIMESTAMP_DISJOINT` (como `GpuTimer` hoy). | Pool de consultas de marca de tiempo y `timestampPeriod`. |

Reglas que valen para los dos: los hilos de decodificación **no llaman a `IGpu`**, solo entregan `IDecoderSurface`; un recurso creado en un `IGpu` no se usa en otro; `Submit` vacío es válido y da una valla ya completa.

## Convenciones que el motor Vulkan iguala a D3D11

- Origen arriba a la izquierda en texturas y objetivos: viewport con alto negativo (o `VK_KHR_maintenance1`), para no tocar el HLSL.
- Profundidad de recorte 0 a 1 (ya es así en Vulkan); orden de las caras como en D3D11.
- Matrices y `cbuffer` con el empaquetado de HLSL tal como lo emite DXC; tamaño del búfer de constantes del manifiesto.
- Sin formatos sRGB: todo es UNORM o flotante y la gamma la hacen los sombreadores (D-016).
- Lectura con filas de arriba abajo y canales en el orden del formato (RGBA8 = R, G, B, A).

## Lo que pide Unicomp

| Necesidad | Cómo queda |
|---|---|
| **Selección por id:** leer un píxel de un objetivo `R32_UINT` | `GpuFormat.R32Uint` como objetivo de color (`ClearUint`), y `Readback` con una caja de 1×1. Atajo: `uint ReadPixelUint(GpuTexture, x, y, timeout)` en una clase de ayuda sobre el contrato. La selección puede esperar un cuadro (`TryRead`) o bloquear unos milisegundos (`Read`). |
| **Captura fuera de pantalla a PNG** (`--capturar`, autoprueba) | Un objetivo RGBA8 sin cadena de intercambio, `Readback` completo y codificar el PNG fuera de `IGpu` (la capa no sabe de archivos). `IGpu` funciona sin ventana ni `IGpuSwapchain`: es requisito del contrato (lo usan las pruebas y la exportación). |
| **Objetivos con MSAA** | `GpuTextureDesc.Samples` (1, 2, 4, 8; `Caps.MaxSamples(format)`), `GpuPipelineDesc.Samples` igual al del objetivo, y `Resolve` a una textura de una muestra. Un objetivo `R32Uint` con MSAA no se resuelve: el pase de ids se dibuja sin MSAA. |
| **Acumulación** (más adelante, «Render final») | Objetivo `RGBA32F` (o `RGBA16F`) que persiste entre cuadros, mezcla aditiva (`Blend.Additive`, si `Caps` dice que el formato se puede mezclar) o ping-pong de dos texturas, y el número de muestra en un búfer de constantes. No pide nada nuevo al contrato. |
| Geometría, profundidad, instancias | Nivel *Raster*: `Draw`, `D32Float`, disposición de vértices. Rodaje no lo usa, pero vive en el mismo contrato para no tener dos. |

## Preguntas abiertas para la fase 3

1. ¿`IGpuFrame` como interfaz (llamada virtual por orden) o como `ref struct` sobre el motor concreto? Con ~40 órdenes por cuadro la llamada virtual no pesa; se mide en la fase 3 con el límite del 2 %.
2. ¿Las vistas (mip o porción concreta de una textura) se piden explícitas o se quedan implícitas? Hoy solo las usan los planos del decodificador y `GenerateMips`.
3. ¿`Readback` de formatos de más de 8 bits con conversión, o siempre en el formato del objetivo? Se propone siempre el del objetivo.
4. Unicomp: ¿necesita más de 4 objetivos de color o lectura de profundidad? Si sí, sube el límite de `GpuTargets`.

## Revisión de Unicomp (16-nov) y qué cambia

Unicomp revisó el borrador: el contrato vale y no sobra nada. Entra en la fase 3:

- **Contrato (obligatorio en *Raster*):**
  1. topología `LineList` y `LineStrip` (aristas del CAD);
  2. `DepthBias` y `SlopeScaledDepthBias` en el estado de *Raster*;
  3. profundidad con `Test`, `Write` y `Compare` por separado (probar sin escribir);
  4. índices de 16 o de 32 bits.
- **Opcional, por `GpuCaps`:**
  5. `Readback` de una caja pequeña de `D32Float` (lectura de profundidad para la selección). Más de 4 objetivos de color no hace falta: `GpuTargets` se queda en 4.
  6. Plantilla `D32FloatS8` para los cortes con tapa, sin prisa.
  7. Opción «preferir software» (WARP en Windows, lavapipe en Linux) para las autopruebas.
- **Preguntas abiertas resueltas:**
  - 1: indiferente para Unicomp, así que decide la medida de la fase 3;
  - 2: vistas implícitas;
  - 3: `Readback` siempre en el formato del objetivo;
  - 4: queda respondida por el punto 5.
