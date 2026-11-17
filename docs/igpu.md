# `IGpu`: la capa de GPU

Contrato vigente de la capa fina de GPU de D-066 ([propuestas/mac-y-linux.md](propuestas/mac-y-linux.md)), que comparten Rodaje y Unicomp. Sale del [borrador de la fase 0](igpu-borrador.md) con la revisión de Unicomp. Código en `src/Rodaje.Render/Gpu/` (contrato) y `src/Rodaje.Render/Gpu/D3D11/` (motor de Windows); pruebas en `tests/Rodaje.Render.Tests/Gpu/`.

## Forma

- **`IGpu`**: crea recursos (desde cualquier hilo) y abre cuadros. `Caps`, `IsLost`, `CreateTexture`, `CreateBuffer`, `CreateSampler`, `CreateShader(ShaderVariant)`, `CreatePipeline`, `CreateTimer`, `CreateReadback`, `BeginFrame`.
- **`IGpuFrame`**: donde se graba todo el trabajo. `Write`, `Copy`, `GenerateMips`, `Clear`, `ClearUint`, `ClearDepth`, `Draw`, `Dispatch`, `Resolve`, copia a un `GpuReadback`, `BeginTimer`/`EndTimer`, `Submit` (con valla) y `Flush` (sin ella).
- **Recursos**: clases opacas (`GpuTexture`, `GpuBuffer`, `GpuSampler`, `GpuShader`, `GpuPipeline`, `GpuTimerQuery`, `GpuReadback`) que libera quien las crea. Sin COM ni punteros en la firma, sin reflexión (AOT).
- **Enlaces por registro HLSL** (`GpuBindings`): hasta 16 `t#` (texturas o búferes de lectura), 4 `s#`, 4 `b#` y 8 `u#`. El motor Vulkan los traduce con los desplazamientos fijos del manifiesto (`b` +0, `t` +16, `s` +32, `u` +48).
- **Destinos** (`GpuTargets`): hasta 4 de color y uno de profundidad.
- **Canalización** (`GpuPipelineDesc`): sombreadores (vértices y píxeles, o cómputo), mezcla con factores separados para color y alfa (las nueve mezclas de Rodaje caben), y en *Raster* topología, caras, sesgo de profundidad, prueba de profundidad, disposición de vértices y muestras.
- **Un `Draw` para todo**: sin búfer de vértices, los vértices salen de `SV_VertexID` (los cuadriláteros de Rodaje, cuatro vértices en tira); con búfer de vértices o de índices es el dibujo de *Raster*.

### Niveles

| Nivel | Qué trae | D3D11 |
|---|---|---|
| **Núcleo** (Rodaje) | Texturas 2D y 3D, mips, búferes de constantes, estructurados y sin tipo, cómputo, mezcla, lectura, marcas de tiempo, vallas | Sí |
| ***Raster*** (Unicomp, obligatorio) | Búferes de vértices e índices de 16 y 32 bits, `TriangleList`, `LineList` y `LineStrip`, profundidad `D32Float` con `Test`, `Write` y `Compare` por separado, `DepthBias` y `SlopeScaledDepthBias`, MSAA con `Resolve`, destino `R32Uint` con `ClearUint` | Sí (`Caps.Raster`) |
| Opcional por `GpuCaps` | Lectura de profundidad (`DepthReadback`), muestras por formato (`MaxSamples`), mezcla por formato (`CanBlend`), motor de software (`D3D11Gpu.Create(preferSoftware: true)`: WARP) | `DepthReadback` no: D3D11 no copia regiones de un recurso de profundidad |

La plantilla `D32FloatS8` (cortes con tapa) no entra todavía: Unicomp la pidió sin prisa.

## Sincronización

Es la parte que D3D11 regala y Vulkan cobra. El contrato es el mismo para los dos motores; cambia quién hace el trabajo.

| Regla | D3D11 (hecho) | Vulkan (fase 4) |
|---|---|---|
| **1. Un cuadro, un dueño.** `BeginFrame` da el único sitio donde se graba; lo usa un hilo. Dos cuadros a la vez (visor y exportación) se serializan. | `BeginFrame` toma el candado del contexto inmediato (el de `GpuDevice`, compartido con D3D11VA) y `Dispose` lo suelta. Es recursivo: un cuadro dentro de otro en el mismo hilo vale. | Un búfer de órdenes por cuadro; el envío entra a la cola bajo el candado de la cola, el mismo que usa FFmpeg (`lock_queue`/`unlock_queue`). |
| **2. Superficies del decodificador.** Se retienen (`AddRef`) hasta que el cuadro que las lee termina; nunca se copian. | Vistas por plano sobre la porción del arreglo (`PlaneViewCache`); COM retiene lo enlazado. | `lock_frame`; esperar el semáforo de `AVVkFrame`, pasar a `SHADER_READ_ONLY`, señalar el valor siguiente y escribir de vuelta layout, valor y familia; `unlock_frame`. |
| **3. Saber cuándo terminó.** `Submit` devuelve una `GpuFence` (`IsComplete`, `Wait`); se puede seguir grabando después. `Dispose` sin `Submit` también envía lo grabado, sin valla. | Consulta de evento; la primera espera vacía la cola, las siguientes no. | Valla o semáforo de línea de tiempo propio. |
| **4. Reciclar.** `Dispose` de un recurso no lo destruye mientras una valla pendiente lo use. | Inmediato: COM cuenta las referencias que el contexto retiene. | Cola de destrucción diferida por valla. |
| **5. Lectura.** `GpuReadback` es memoria visible desde la CPU, reutilizable: el cuadro graba la copia, `TryMap` no espera (falso si la GPU no terminó), `Map` sí. Filas de arriba abajo, `RowPitch` declarado, siempre en el formato del origen. | Textura o búfer `STAGING`; `Map` con `DO_NOT_WAIT` en `TryMap`. Las filas abiertas retienen el candado del contexto. | Búfer visible desde la CPU y la valla del cuadro. |
| **6. Presentar.** La cadena presenta tras la valla del cuadro que dibujó el búfer trasero. | `IDXGISwapChain::Present` en la app; el búfer trasero entra con `D3D11Gpu.Wrap`. | Semáforos de adquisición y de presentación. |
| **7. Pérdida del dispositivo.** Un fallo es una `RenderException`; si `IsLost` es verdadero, el dueño destruye todo y crea otro `IGpu`. | `GetDeviceRemovedReason`. | `VK_ERROR_DEVICE_LOST`. |
| **8. Tiempos.** `BeginTimer`/`EndTimer` dentro de un cuadro; `TryGetMilliseconds` o `WaitMilliseconds` después. | `TIMESTAMP` + `TIMESTAMP_DISJOINT`, como el `GpuTimer` de siempre. | Pool de marcas de tiempo y `timestampPeriod`. |

Valen para los dos: los hilos de decodificación no llaman a `IGpu`; un recurso de un `IGpu` no se usa en otro; crear recursos (también sombreadores, en segundo plano como hacen los efectos) se puede desde cualquier hilo.

### Enlaces y peligros (D3D11)

D3D11 anula en silencio una entrada que sigue enlazada como salida. El motor pone siempre las salidas antes que las entradas: en `Draw`, los destinos y después las texturas; en `Dispatch`, desenlaza los destinos de dibujo y pone las `u#` antes que las `t#`; los registros que una pasada deja sin usar se vacían si la anterior los usaba. Así una textura puede ser destino en una pasada y entrada en la siguiente sin más.

## Convenciones que el motor Vulkan iguala a D3D11

- Origen arriba a la izquierda en texturas y destinos (viewport con alto negativo), profundidad de recorte de 0 a 1, caras delanteras en el sentido de las agujas del reloj.
- `cbuffer` con el empaquetado de HLSL tal como lo emite DXC; tamaño del manifiesto.
- Sin formatos sRGB (D-016); lectura con filas de arriba abajo y canales en el orden del formato.

## Decisiones de la fase 3

- **`IGpuFrame` es una interfaz** (llamada virtual por orden). El coste está en el estado, no en la llamada: el motor D3D11 no repite lo que el dibujo anterior del mismo cuadro dejó puesto (canalización, destinos, viewport, constantes, muestreadores y el tramo de `t#` que no cambia) y lo olvida al abrir o cerrar un cuadro de fuera, al cambiar de destino y al despachar cómputo. Cifras en [mac-y-linux-fase3.md](mac-y-linux-fase3.md).
- **Vistas implícitas**: una textura trae las que pide su uso (lectura de todos los mips, escritura del mip 0). Los planos del decodificador las traen del lado de Media.
- **Lectura siempre en el formato del origen**, como pidió Unicomp.
- **`GpuTargets` se queda en 4** destinos de color.
- **Sin `GpuLostException`**: la pérdida es una `RenderException` con `IsLost` (lo que ya captura la app).
- **`CreateShader` recibe la `ShaderVariant`**: D3D11 la compila con `D3DCompile` y la caché de siempre (D-015); Vulkan toma el SPIR-V incrustado por su id.
- **La cadena de intercambio queda en la app**: su búfer trasero entra con `D3D11Gpu.Wrap`. `IGpuSwapchain` llega con el motor Vulkan, que es quien la necesita.
- **Paso por partes**: `D3D11Gpu.For(GpuDevice)` da el motor del dispositivo de siempre y `Wrap` y las propiedades nativas de `D3D11Texture` dejan mezclar código viejo y nuevo mientras dura la migración.
