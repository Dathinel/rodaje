# Linux y macOS, fase 4: el motor Vulkan

Resultado de la fase 4 de D-066 ([propuestas/mac-y-linux.md](propuestas/mac-y-linux.md)), 17-nov. Contrato en [igpu.md](igpu.md); criterio de píxeles en [mac-y-linux-fase0.md](mac-y-linux-fase0.md) (d).

## Qué quedó

- **Contrato** (#599): `IDecoderSurface` en Rodaje.Media (la implementa `D3D11TexturePayload`), `IGpuFrame.Acquire` con una `GpuSurfaceCache` por dueño (en D3D11, la `PlaneViewCache` de siempre) e `IGpuSwapchain` con la cadena de composición de D3D11. El compositor ya pide los planos con `Acquire`.
- **Motor** (#602): `Gpu/Vulkan/` sobre Vulkan 1.3 con *dynamic rendering* y `TerraFX.Interop.Vulkan` 1.4.328.1. Texturas 2D/3D con mips y vistas 2D, 2D_ARRAY y 3D (la que pida la reflexión del SPIR-V), búferes (los de constantes viven en la CPU y se suben a la memoria del envío al enlazarse, como `WRITE_DISCARD`), muestreadores, el SPIR-V del manifiesto con su reflexión (`SpirvModule`, sin dependencias), canalizaciones de dibujo (una por combinación de formatos de destino) y de cómputo, nivel *Raster* entero, lectura, marcas de tiempo, una valla por envío y destrucción diferida. `Rodaje.Render` deja de ser solo de Windows: lo que llama a D3D11 lleva `[SupportedOSPlatform("windows7.0")]` por tipo o por miembro.
- **Variantes** (#602): las 100 del manifiesto crean sombreador y canalización en Vulkan, y los 168 casos de la fase 0 se dibujan con el mismo código (`EffectCaseRenderer`) sobre cualquier `IGpu`. ChatGPT portó los casos; Claude arregló los dos fallos del motor que salieron al compararlos (abajo).
- **Compositor sobre `IGpu`** (#603): `Compositor(IGpu, …)`, `QuadPipeline`, `EffectPipeline`, `RenderTargetPool` y `LayerRenderer` ya no dependen de D3D11; en D3D11 hacen exactamente lo mismo que antes. `GpuTextureSurface` (texturas propias como superficie) da al compositor capas en Vulkan sin decodificador por GPU. En un motor que no es D3D11 no hay todavía títulos (Direct2D): esas capas se saltan y se cuentan (la interpolación llegó después, abajo).
- **Exportación por CPU en Vulkan** (#606): sin dispositivo D3D11 los decodificadores dan cuadros de CPU y el compositor los sube (`CpuFrameUploads`: NV12, P010, yuv420p y yuv420p10, siempre a planos de 16 bits); `ExportFrameConverter` trabaja sobre `IGpu` (fuera de D3D11 escala sin vistas de mips: atajo anotado) y `ExportJob` abre `VulkanGpu` fuera de Windows. `Rodaje.Export` y sus pruebas pasan a `net10.0` y entran en la CI de Linux.
- **Pruebas de Render en los dos motores** (#607): `TestGpu` elige D3D11 o Vulkan con `RODAJE_MOTOR`; en Linux corren con lavapipe. Hecho por Devin (Fusión) y revisado por Claude.
- **Interpolación de cuadros en Vulkan**: `SurfaceInterpolator` hace la mezcla y el flujo óptico sobre `GpuTextureSurface` (planos R8/Rg8 o R16/Rg16 escritos desde cómputo con el SPIR-V de `FlowShaders`); `FlowInterpolator` pasa a `IGpu` y lo comparten los dos motores. Si el formato no admite escritura desde un sombreador, la capa sale sin interpolar; si fallan los intermedios del flujo, se mezcla. En D3D11, mismas órdenes y mismas huellas (2 680 lecturas iguales). Pruebas de `Interpolation/`: 41 en Vulkan (RTX 3050) y 46 en D3D11, sin ensanchar tolerancias; solo en D3D11 quedan los arreglos de D3D11VA, la compilación con D3DCompiler, el fallo inyectado en `ShaderCache.Compile` y la medida de rendimiento.
- **CI de macOS estable** (#605): las pruebas de reloj de CoreAudio y la de CPU llena van en `TiempoReal` (Mac está aparcado: Daniel, 17-nov).
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
| Pruebas de Render en Vulkan (lavapipe en la CI) | **Hecho** | `Rodaje.Render.Tests` corre en los dos motores con `RODAJE_MOTOR` (`TestGpu`): en Vulkan 441 correctas y 140 omitidas de 581 (las omitidas son solo-D3D11: títulos, interpolación, D3D11VA, presentación, monitores, RAW y las del conversor de exportación, que aún usan texturas de D3D11), en la RTX 3050 y en lavapipe; en D3D11, 852 con las mismas huellas de píxeles. Las capas de prueba suben NV12 a 16 bits en Vulkan (abajo). Con Vulkan, unas 25 pruebas admiten un código más (las que en D3D11 piden igualdad exacta entre dos caminos o ±1/±2 frente a la referencia de CPU: transiciones en 0 y 1, paso por el intermedio, eco temporal, efectos espaciales) y la nitidez, 2 + cantidad (amplifica la diferencia del desenfoque); en lavapipe salían a 1–4 códigos |
| Exportación ProRes/FFV1 por CPU con los mismos cuadros (hash) que en Windows | **Cumple con otra cifra** | El mismo hash cuadro a cuadro **no es alcanzable** entre rasterizadores (tampoco entre D3D11 de dos GPU): el bilineal y el redondeo a 10 bits difieren en las fronteras. `ExportHashTests` exporta un proyecto de 150 cuadros (color, fundido, imagen en imagen) en Vulkan y con D3D11 WARP: FFV1 (sin pérdidas) como mucho **2 códigos de 10 bits** en el **0,64 %** de las muestras con lavapipe (0,20 % en la RTX 3050; 38 de 150 cuadros idénticos), 82 dB; ProRes 70 dB. `Rodaje.Export` corre en la CI de Linux |
| `IDecoderSurface`/`Acquire` e `IGpuSwapchain` con D3D11 sin cambiar píxeles | **Hecho** | Huellas de Render, Export y App con el contrato y otra vez con el compositor sobre `IGpu`: composición 697/697 y 1 791/1 791, presentación 31/31, títulos 29/29 y monitores 1/1 iguales; ningún cuadro exportado que la base no haya producido (como en la fase 3). Pruebas: Render 852, Export 472 + 20 omitidas, App 2 688 + 17 omitidas, en verde |

**lavapipe y las texturas de 8 bits.** lavapipe filtra las texturas UNORM8 con 8 bits de precisión: una capa NV12 se alejaba hasta 6 códigos de 10 bits de WARP al exportar y uno en las pruebas de Render. Por eso los cuadros de CPU (y las capas de prueba) se suben a planos de 16 bits con el mismo código en la parte alta, que da el mismo color. Las superficies NV12 de D3D11VA no cambian.

Las 18 excepciones frente a WARP son el filtro bilineal amplificado (ADVANCED, curvas, calificador, capas giradas), las transiciones con desplazamiento a fracciones arbitrarias (media 2,1e-4 a 2,4e-4, apenas por encima de 2e-4), los dos HDR con PQ, `sharpen-umbral` (el umbral es un escalón: en lavapipe unos pocos píxeles del borde pasan el p99,9) y `degradado2` (discontinuidad de `atan2` en un centro de píxel, 0,83 también entre D3D11 y Vulkan en la misma GPU). La prueba las nombra con su motivo y comprueba igualmente que su media no pase de 5e-3.

## Lo que salió al comparar

- **Doble inversión de Y.** `build/shaders` compilaba los sombreadores de vértices con `-fvk-invert-y` y el motor ya invierte con el viewport (la convención del contrato). Las capas giradas daban 0,9 de error. Se quitó la opción de DXC: solo cambia `VsQuad.spv`.
- **Textura nueva con basura.** Una imagen nueva de Vulkan no tiene contenido definido; D3D11 la da a cero. Lo que no cubría una capa girada salía con restos de otra textura. El motor la pone a cero en su primer uso, dentro del mismo cuadro.
- **Reflexión de imágenes de escritura.** La primera versión leía mal el operando *Sampled* de `OpTypeImage` y tomaba un `RWTexture2D` por una textura de lectura.

## Lo que no quedó, y por qué

- **Mismo hash exacto en la exportación.** No es alcanzable entre rasterizadores (tabla); se pide la cifra de arriba.
- **Las 35 pruebas del conversor de exportación en Vulkan.** El conversor ya es portátil, pero su apoyo de pruebas (`Export/ExportGpu.cs`) sube texturas de D3D11 por puntero: falta pasarlo a `IGpu`. La exportación entera en Vulkan sí está cubierta por `ExportHashTests`.
- **Títulos y medidor de luz HDR al exportar fuera de Windows.** Los títulos esperan a la pregunta de licencias de SkiaSharp de la fase 5 (un proyecto con títulos falla con un mensaje claro); el PQ sale sin MaxCLL/MaxFALL (atajo anotado en `ExportJob`).
- **Exportar una imagen fija (`StillExporter`) y la caché de render fuera de Windows.** Siguen sobre el dispositivo D3D11.
- **Pruebas de `Rodaje.Render.Tests` en Vulkan.** Hecho: `TestGpu` elige el motor con `RODAJE_MOTOR` y el apoyo (`YuvImage`, `Layers`) crea `GpuTextureSurface` en Vulkan. Quedan omitidas 135: las atadas a D3D11 (títulos con Direct2D, interpolación, D3D11VA y sus arreglos NV12, `Presenter`, `ScopeRenderer`, `ExportFrameConverter`, `GpuRawDeveloper`, `D3DCompiler`, motor WARP, `PlaneViewCache`, `GpuTimer` y otras vistas nativas) y dos que comprueban la caché perezosa de sombreadores de D3D11, que en Vulkan no existe (`VulkanGpu.CreateShader` carga el SPIR-V al crear, `src/Rodaje.Render/Gpu/Vulkan/VulkanGpu.cs:258`).
- **Títulos en Vulkan.** Se dibujan con Direct2D y el dibujo en Unix espera a la pregunta de licencias de SkiaSharp (fase 5).
- **Autoprueba del exe.** Publicado desde `main` (0fde1f63) y corrido de fondo 4 veces: 2 en verde (56 y 69 s) y 2 rojas («El monitor Waveform no llegó de la GPU al panel» y «Antes/después no divide la imagen»). El exe de **antes** de la fase 4 (09c516b4) falla igual 2 de 4 veces con «Antes/después…»: es un fallo intermitente previo de la autoprueba (la captura tras `SetCompare` espera 250 ms), no de esta fase. Queda para investigar aparte.
- **`Acquire` de `AVVkFrame` e `IGpuSwapchain` en Vulkan.** Fases 9 y 8/9, como decía el plan: necesitan el decodificador Vulkan en nuestro dispositivo y la ventana X11.
- **Rendimiento.** Barreras simples (todo antes de todo) y una pasada de *dynamic rendering* por dibujo: correcto siempre, sin afinar. Una asignación de memoria por recurso (el límite habitual son 4 096). Se mide en la fase 9 con la RTX 3050.

## Decisiones de Daniel (17-nov)

Aceptadas las tres recomendadas: el SPIR-V va dentro del exe de Windows, el criterio (ii) queda en 2e-4 con las excepciones con nombre y el motor exige Vulkan 1.3. **De momento solo Linux**: Mac queda aparcado (pide el registro en Apple); lo que hay de macOS en la CI se queda.

## Preguntas para Daniel (se sigue con la recomendada)

1. **El «mismo hash» de la exportación.** Recomendado: dar por buena la cifra medida (FFV1 a ±2 códigos en menos del 1 % de las muestras, ProRes ≥ 60 dB); el hash idéntico pediría el mismo rasterizador en los dos sistemas.
2. **Autoprueba intermitente** («Antes/después…», también antes de la fase 4). Recomendado: alargar la espera tras `SetCompare` o esperar al cuadro siguiente; no se tocó aquí por ser de la app.

## Preguntas ya resueltas

1. **Tamaño del exe de Windows.** `Rodaje.Render` ahora incrusta el SPIR-V (2,4 MB) también en Windows. Recomendado: dejarlo (es el 6 % del exe y evita un proyecto aparte); la alternativa es cargarlo solo fuera de Windows.
2. **Criterio (ii).** Las transiciones con desplazamiento dan una media de 2,1e-4 a 2,4e-4 por el bilineal de WARP. Recomendado: dejar 2e-4 con esas excepciones nombradas (así se ve cualquier caso nuevo que se pase); la alternativa es subir el umbral a 3e-4 y quitar cinco excepciones.
3. **Vulkan 1.3 obligatorio.** El motor pide *dynamic rendering*: lo tienen lavapipe, NVIDIA, AMD e Intel recientes y MoltenVK (como extensión). Recomendado: mantenerlo; un equipo con solo Vulkan 1.2 se queda en la CPU.

## Ayudantes

| Pieza | Quién | Cómo fue |
|---|---|---|
| Contrato, motor Vulkan, compositor sobre `IGpu`, CI de lavapipe, huellas | Claude | — |
| Exportación en Vulkan, `Rodaje.Export` en Linux, CI de macOS, autoprueba | Claude | — |
| `Rodaje.Render.Tests` en los dos motores | Devin (Fusión: Claude Opus 5.5 + SWE-2) | 56 min; sin ensanchar tolerancias. Claude subió las capas de prueba a 16 bits y pasó el RAW a solo-D3D11 tras la primera corrida en lavapipe (30 fallos por el filtro de 8 bits y por el RAW). ChatGPT estaba sin cuota. |
| Variantes y 168 casos sobre `IGpu`, trabajos de referencias en la CI | ChatGPT (gpt-6.1-sol) | 11 min; señaló bien la doble inversión de Y sin tocar `src/`. Claude cambió su truco de reflexión para la vista de arreglo de D3D11 por `GpuTextureSurface` y quitó las cuatro excepciones que tapaban el fallo. |
