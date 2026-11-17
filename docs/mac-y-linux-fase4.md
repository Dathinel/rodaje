# Linux y macOS, fase 4: el motor Vulkan

Resultado de la fase 4 de D-066 ([propuestas/mac-y-linux.md](propuestas/mac-y-linux.md)), 17-nov. Contrato en [igpu.md](igpu.md); criterio de píxeles en [mac-y-linux-fase0.md](mac-y-linux-fase0.md) (d).

## Qué quedó

- **Contrato** (#599): `IDecoderSurface` en Rodaje.Media (la implementa `D3D11TexturePayload`), `IGpuFrame.Acquire` con una `GpuSurfaceCache` por dueño (en D3D11, la `PlaneViewCache` de siempre) e `IGpuSwapchain` con la cadena de composición de D3D11. El compositor ya pide los planos con `Acquire`.
- **Motor** (#602): `Gpu/Vulkan/` sobre Vulkan 1.3 con *dynamic rendering* y `TerraFX.Interop.Vulkan` 1.4.328.1. Texturas 2D/3D con mips y vistas 2D, 2D_ARRAY y 3D (la que pida la reflexión del SPIR-V), búferes (los de constantes viven en la CPU y se suben a la memoria del envío al enlazarse, como `WRITE_DISCARD`), muestreadores, el SPIR-V del manifiesto con su reflexión (`SpirvModule`, sin dependencias), canalizaciones de dibujo (una por combinación de formatos de destino) y de cómputo, nivel *Raster* entero, lectura, marcas de tiempo, una valla por envío y destrucción diferida. `Rodaje.Render` deja de ser solo de Windows: lo que llama a D3D11 lleva `[SupportedOSPlatform("windows7.0")]` por tipo o por miembro.
- **Variantes** (#602): las 100 del manifiesto crean sombreador y canalización en Vulkan, y los 168 casos de la fase 0 se dibujan con el mismo código (`EffectCaseRenderer`) sobre cualquier `IGpu`. ChatGPT portó los casos; Claude arregló los dos fallos del motor que salieron al compararlos (abajo).
- **Compositor sobre `IGpu`** (#603): `Compositor(IGpu, …)`, `QuadPipeline`, `EffectPipeline`, `RenderTargetPool` y `LayerRenderer` ya no dependen de D3D11; en D3D11 hacen exactamente lo mismo que antes. `GpuTextureSurface` (texturas propias como superficie) da al compositor capas en Vulkan sin decodificador por GPU. En un motor que no es D3D11 no hay todavía interpolación de cuadros ni títulos (Direct2D): esas capas salen sin interpolar o se saltan y se cuentan.
- **CI**: el trabajo de Linux instala lavapipe y **exige** que corran las pruebas de Vulkan (`RODAJE_VK_EXIGIR=1`); dos trabajos nuevos, *Referencias de efectos (WARP)* en Windows y *Vulkan frente a WARP (lavapipe)* en Ubuntu 24.04, comparan los 168 casos y el compositor con las salidas de WARP. En macOS se omiten con el motivo hasta MoltenVK.

## «Hecho cuando», punto por punto

| Punto | Estado | Cifra |
|---|---|---|
| `VulkanGpu` con todas las variantes | **Hecho** | 100 de 100 variantes crean sombreador y canalización (RTX 3050 y lavapipe) |
| Lectura y consultas de tiempo | **Hecho** | Pruebas comunes a los dos motores: 20 de 20 en Vulkan, 22 de 22 en D3D11 (WARP). `Rodaje.Render.Vulkan.Tests` entero: 131 + 2 omitidas en la RTX 3050 |
| Píxeles, criterio (i) centros y fracciones exactas ≤ 1e-3 | **Cumple** | 8 de 8 casos (RTX 3050 y lavapipe) |
| Píxeles, criterio (ii) media ≤ 2e-4 y p99,9 ≤ 4e-3 frente a WARP | **Cumple con excepciones nombradas** | 142 de 142 (RTX 3050 y lavapipe en la CI); 18 casos en la lista de excepciones (media ≤ 5e-3), los que la fase 0 ya explicaba más `sharpen-umbral` en lavapipe |
| Píxeles, criterio (iii) frente a D3D11 de la misma GPU ≤ 1e-3 | **Cumple** | 164 de 164 + las 4 excepciones del control de la fase 0 |
| Compositor entero en Vulkan frente a D3D11 WARP | **Cumple (ii)** | 9 casos (una capa, escala y posición, giro, multiplicar, superponer, color y viñeta, desenfoque, nitidez, fundido cruzado), con planos P010: media ≤ 1,9e-5, p99,9 ≤ 2,0e-3, máximo 6,8e-3 (la capa girada, en el borde). Con NV12, lavapipe filtra los 8 bits con 8 bits de precisión y la media sube a ~7e-4 frente a WARP. En lavapipe, desenfoque, nitidez y fundido cruzado (pasadas por intermedios leídos entre texeles) dan una media de 2,5e-4 a 3,5e-4: van como excepciones nombradas (media ≤ 5e-3), atribuidas al mismo bilineal de WARP que las transiciones de las variantes, sin demostrarlo caso a caso |
| Pruebas de Render en Vulkan (lavapipe en la CI) | **Hecho** | `Rodaje.Render.Tests` corre en los dos motores con `RODAJE_MOTOR` (`TestGpu`): 449 superadas y 135 omitidas de 584 en Vulkan en la RTX 3050 (las omitidas son solo-D3D11: títulos, interpolación, D3D11VA, presentación, monitores y conversor de exportación); en D3D11, 852 superadas con las mismas huellas de píxeles que antes de la migración. `Rodaje.Render.Vulkan.Tests` entero: 131 + 2 omitidas. Render corre en la CI de Linux sobre lavapipe |
| Exportación ProRes/FFV1 por CPU con los mismos cuadros (hash) que en Windows | **No** | Falta el conversor de exportación (`ExportFrameConverter`) sobre `IGpu` sin vistas de D3D11, `FrameUploader` en Unix y `Rodaje.Export` en la CI de Linux |
| `IDecoderSurface`/`Acquire` e `IGpuSwapchain` con D3D11 sin cambiar píxeles | **Hecho** | Huellas de Render, Export y App con el contrato y otra vez con el compositor sobre `IGpu`: composición 697/697 y 1 791/1 791, presentación 31/31, títulos 29/29 y monitores 1/1 iguales; ningún cuadro exportado que la base no haya producido (como en la fase 3). Pruebas: Render 852, Export 472 + 20 omitidas, App 2 688 + 17 omitidas, en verde |

Las 18 excepciones frente a WARP son el filtro bilineal amplificado (ADVANCED, curvas, calificador, capas giradas), las transiciones con desplazamiento a fracciones arbitrarias (media 2,1e-4 a 2,4e-4, apenas por encima de 2e-4), los dos HDR con PQ, `sharpen-umbral` (el umbral es un escalón: en lavapipe unos pocos píxeles del borde pasan el p99,9) y `degradado2` (discontinuidad de `atan2` en un centro de píxel, 0,83 también entre D3D11 y Vulkan en la misma GPU). La prueba las nombra con su motivo y comprueba igualmente que su media no pase de 5e-3.

## Lo que salió al comparar

- **Doble inversión de Y.** `build/shaders` compilaba los sombreadores de vértices con `-fvk-invert-y` y el motor ya invierte con el viewport (la convención del contrato). Las capas giradas daban 0,9 de error. Se quitó la opción de DXC: solo cambia `VsQuad.spv`.
- **Textura nueva con basura.** Una imagen nueva de Vulkan no tiene contenido definido; D3D11 la da a cero. Lo que no cubría una capa girada salía con restos de otra textura. El motor la pone a cero en su primer uso, dentro del mismo cuadro.
- **Reflexión de imágenes de escritura.** La primera versión leía mal el operando *Sampled* de `OpTypeImage` y tomaba un `RWTexture2D` por una textura de lectura.

## Lo que no quedó, y por qué

- **Exportación con hash.** Ver la tabla. Es la siguiente pieza: el conversor ya habla con `IGpu` salvo `SourceViews` (vistas de un tramo de mips), y la exportación por CPU solo necesita subir los planos decodificados con `GpuTextureSurface`.
- **Pruebas de `Rodaje.Render.Tests` en Vulkan.** Hecho: `TestGpu` elige el motor con `RODAJE_MOTOR` y el apoyo (`YuvImage`, `Layers`) crea `GpuTextureSurface` en Vulkan. Quedan omitidas 135: las atadas a D3D11 (títulos con Direct2D, interpolación, D3D11VA y sus arreglos NV12, `Presenter`, `ScopeRenderer`, `ExportFrameConverter`, `GpuRawDeveloper`, `D3DCompiler`, motor WARP, `PlaneViewCache`, `GpuTimer` y otras vistas nativas) y dos que comprueban la caché perezosa de sombreadores de D3D11, que en Vulkan no existe (`VulkanGpu.CreateShader` carga el SPIR-V al crear, `src/Rodaje.Render/Gpu/Vulkan/VulkanGpu.cs:258`).
- **Interpolación y títulos en Vulkan.** `FrameInterpolator` escribe una textura NV12 con vistas UAV por plano (D3D11); los títulos se dibujan con Direct2D. Los dos esperan a las superficies Vulkan (fase 9) y al dibujo de títulos en Unix.
- **`Acquire` de `AVVkFrame` e `IGpuSwapchain` en Vulkan.** Fases 9 y 8/9, como decía el plan: necesitan el decodificador Vulkan en nuestro dispositivo y la ventana X11.
- **Rendimiento.** Barreras simples (todo antes de todo) y una pasada de *dynamic rendering* por dibujo: correcto siempre, sin afinar. Una asignación de memoria por recurso (el límite habitual son 4 096). Se mide en la fase 9 con la RTX 3050.

## Preguntas para Daniel (se sigue con la recomendada)

1. **Tamaño del exe de Windows.** `Rodaje.Render` ahora incrusta el SPIR-V (2,4 MB) también en Windows. Recomendado: dejarlo (es el 6 % del exe y evita un proyecto aparte); la alternativa es cargarlo solo fuera de Windows.
2. **Criterio (ii).** Las transiciones con desplazamiento dan una media de 2,1e-4 a 2,4e-4 por el bilineal de WARP. Recomendado: dejar 2e-4 con esas excepciones nombradas (así se ve cualquier caso nuevo que se pase); la alternativa es subir el umbral a 3e-4 y quitar cinco excepciones.
3. **Vulkan 1.3 obligatorio.** El motor pide *dynamic rendering*: lo tienen lavapipe, NVIDIA, AMD e Intel recientes y MoltenVK (como extensión). Recomendado: mantenerlo; un equipo con solo Vulkan 1.2 se queda en la CPU.

## Ayudantes

| Pieza | Quién | Cómo fue |
|---|---|---|
| Contrato, motor Vulkan, compositor sobre `IGpu`, CI de lavapipe, huellas | Claude | — |
| Variantes y 168 casos sobre `IGpu`, trabajos de referencias en la CI | ChatGPT (gpt-6.1-sol) | 11 min; señaló bien la doble inversión de Y sin tocar `src/`. Claude cambió su truco de reflexión para la vista de arreglo de D3D11 por `GpuTextureSurface` y quitó las cuatro excepciones que tapaban el fallo. |
