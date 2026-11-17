# Linux y macOS, fase 4: el motor Vulkan

Resultado de la fase 4 de D-066 ([propuestas/mac-y-linux.md](propuestas/mac-y-linux.md)), 17-nov. Contrato en [igpu.md](igpu.md); criterio de píxeles en [mac-y-linux-fase0.md](mac-y-linux-fase0.md) (d).

## Qué quedó

- **Contrato** (#599): `IDecoderSurface` en Rodaje.Media (la implementa `D3D11TexturePayload`), `IGpuFrame.Acquire` con una `GpuSurfaceCache` por dueño (en D3D11, la `PlaneViewCache` de siempre) e `IGpuSwapchain` con la cadena de composición de D3D11. El compositor ya pide los planos con `Acquire`.
- **Motor** (#602): `Gpu/Vulkan/` sobre Vulkan 1.3 con *dynamic rendering* y `TerraFX.Interop.Vulkan` 1.4.328.1. Texturas 2D/3D con mips y vistas 2D, 2D_ARRAY y 3D (la que pida la reflexión del SPIR-V), búferes (los de constantes viven en la CPU y se suben a la memoria del envío al enlazarse, como `WRITE_DISCARD`), muestreadores, el SPIR-V del manifiesto con su reflexión (`SpirvModule`, sin dependencias), canalizaciones de dibujo (una por combinación de formatos de destino) y de cómputo, nivel *Raster* entero, lectura, marcas de tiempo, una valla por envío y destrucción diferida. `Rodaje.Render` deja de ser solo de Windows: lo que llama a D3D11 lleva `[SupportedOSPlatform("windows7.0")]` por tipo o por miembro.
- **Variantes** (#602): las 100 del manifiesto crean sombreador y canalización en Vulkan, y los 168 casos de la fase 0 se dibujan con el mismo código (`EffectCaseRenderer`) sobre cualquier `IGpu`. ChatGPT portó los casos; Claude arregló los dos fallos del motor que salieron al compararlos (abajo).
- **Compositor sobre `IGpu`** (#603): `Compositor(IGpu, …)`, `QuadPipeline`, `EffectPipeline`, `RenderTargetPool` y `LayerRenderer` ya no dependen de D3D11; en D3D11 hacen exactamente lo mismo que antes. `GpuTextureSurface` (texturas propias como superficie) da al compositor capas en Vulkan sin decodificador por GPU. Los títulos y la interpolación llegaron después (abajo).
- **Exportación por CPU en Vulkan** (#606): sin dispositivo D3D11 los decodificadores dan cuadros de CPU y el compositor los sube (`CpuFrameUploads`: NV12, P010, P016, yuv420p y yuv420p10 a planos de 16 bits, BGRA/RGBA16 premultiplicado, y lo demás convertido antes con swscale; ver la revisión de bloque); `ExportFrameConverter` trabaja sobre `IGpu` (fuera de D3D11 escala sin vistas de mips: atajo anotado) y `ExportJob` abre `VulkanGpu` fuera de Windows. `Rodaje.Export` y sus pruebas pasan a `net10.0` y entran en la CI de Linux.
- **Pruebas de Render en los dos motores** (#607): `TestGpu` elige D3D11 o Vulkan con `RODAJE_MOTOR`; en Linux corren con lavapipe. Hecho por Devin (Fusión) y revisado por Claude.
- **Interpolación de cuadros en Vulkan** (#608): `SurfaceInterpolator` hace la mezcla y el flujo óptico sobre `GpuTextureSurface` (planos R8/Rg8 o R16/Rg16 escritos desde cómputo con el SPIR-V de `FlowShaders`); `FlowInterpolator` pasa a `IGpu` y lo comparten los dos motores. Si el formato no admite escritura desde un sombreador, la capa sale sin interpolar; si fallan los intermedios del flujo, se mezcla. En D3D11, mismas órdenes y mismas huellas (2 680 lecturas iguales). Pruebas de `Interpolation/`: 41 en Vulkan (RTX 3050) y 46 en D3D11, sin ensanchar tolerancias; solo en D3D11 quedan los arreglos de D3D11VA, la compilación con D3DCompiler, el fallo inyectado en `ShaderCache.Compile` y la medida de rendimiento.
- **Títulos con SkiaSharp**: `ITitleRasterizer` con dos implementaciones, Direct2D en D3D11 (el código de dibujo de siempre: solo cambian tipos y anotaciones, mismas pruebas y píxeles) y `SkiaTitleRasterizer` en los demás motores, que dibuja con SkiaSharp 4.156.0 los glifos colocados por la maquetación de HarfBuzz de `Rodaje.Text` (ahora con tramos de peso, cursiva y escala) y sube el resultado a una textura del `IGpu`. Compositor, `ExportJob` (con su comprobación de fuentes que faltan) e imagen fija dibujan títulos con Vulkan. 26 pruebas de títulos pasan de solo-D3D11 a los dos motores. Hecho por ChatGPT; Claude arregló la subida con mips en D3D11 y lo verificó.
- **Imagen fija y MaxCLL/MaxFALL fuera de Windows**: `StillExporter` abre el motor como `ExportJob`; `ContentLightMeter` pasa a `IGpu` y la exportación PQ mide la luz en cualquier motor (#609). La caché de render ya iba por `ExportJob`; solo el reparto de dispositivos (`ExportDevicePool`) sigue siendo de D3D11.
- **Autoprueba «Antes/después» y «Waveform»** (#609): al abrir la pestaña de filtros cambia el tamaño del visor y `PlayerView` vuelve a aplicar al motor el estado del botón de comparar (apagado), deshaciendo el `SetCompare` de la autoprueba. La autoprueba ahora pulsa el botón y espera al cuadro con las ruedas; Waveform espera hasta 15 s. No era un fallo de la app. Exe de la rama: 5 de 5 en verde (50–53 s).
- **CI de macOS estable** (#605): las pruebas de reloj de CoreAudio y la de CPU llena van en `TiempoReal` (Mac está aparcado: Daniel, 17-nov). Ninguna pasada programada las corre (la nocturna es de Linux y Windows): son **manuales**, en un Mac con BlackHole.
- **CI**: el trabajo de Linux instala lavapipe y **exige** que corran las pruebas de Vulkan (`RODAJE_VK_EXIGIR=1`); dos trabajos nuevos, *Referencias de efectos (WARP)* en Windows y *Vulkan frente a WARP (lavapipe)* en Ubuntu 24.04, comparan los 168 casos y el compositor con las salidas de WARP. En macOS se omiten con el motivo hasta MoltenVK.

## Revisión de bloque (17-nov)

Revisión independiente de Devin (Fusión) sobre `7aa4ddbb~1..75f7d5f5`: ocho hallazgos y cuatro sobre las pruebas. Cada arreglo lleva una prueba que falla sin él.

| Hallazgo | Arreglo | Prueba |
|---|---|---|
| 1. Formatos del SPIR-V (`CsScale` Rgba32f, `CsBlend+CHROMA` Rg32f) frente a las vistas; `StorageImageExtendedFormats` sin la característica | DXC con `-fspv-use-unknown-image-format`: las imágenes de escritura se declaran `Unknown` y el formato lo pone la vista. `VulkanShader` exige `shaderStorageImageRead/WriteWithoutFormat` (y `ExtendedFormats` si el módulo lo pide; se habilita si existe), las texturas de escritura piden `STORAGE_*_WITHOUT_FORMAT` y `Bind` rechaza un formato declarado distinto del de la textura (#612) | `Manifest_declares_storage_images_without_a_format`, `Storage_image_with_another_declared_format_is_rejected_when_bound` |
| 2. Exportación en Vulkan que omitía 4:2:2, 4:4:4, más de 10 bits y RGBA | `CpuFramePayload.ToUploadable` (portátil, swscale) lleva todo a NV12/P010/P016 o BGRA/RGBA16 con alfa premultiplicado; lo que no se sabe convertir lanza y la exportación falla con el error (#613) | `CpuUploadTests`: yuv422p10le, yuv444p, yuv420p12le, yuv422p y PNG con alfa de 8 y 16 bits, sin capas omitidas |
| 3. `VK_ERROR_DEVICE_LOST` en `vkGetFenceStatus` como «sin terminar»; `MapWhenReady` sin plazo | `Poll` lanza y marca la pérdida; la lectura de la exportación tiene `ReadTimeout` (1 min) y cancelación (#612) | `Lost_device_after_an_accepted_submit_throws_instead_of_waiting`, `Export_read_gives_up_after_its_timeout_and_on_cancel` |
| 4. `Wait` sobre un `FrameContext` reciclado | Contador de esperas: `Poll` no devuelve a la reserva un contexto con esperas; lo hace la última (#612) | `Wait_keeps_its_submission_out_of_reuse_until_it_returns` |
| 5. `Acquire` sin retener la superficie | `AddRef` al adquirir; D3D11 suelta al cerrar el cuadro y Vulkan cuando termina su último envío (#612) | `Acquired_surface_outlives_its_owner_until_the_frame_completes` (los dos motores) |
| 6. 8 → 16 bits rompía el rango completo (255 → 1020/1023) | ×257 y la superficie sigue siendo NV12, también en `YuvImage.CreateSurface` (#613) | `Full_range_8_bit_samples_keep_their_normalization`: rampa 0..255 en coma flotante, < 1e-5 |
| 7. Fugas en fallos de creación | Limpieza en cada etapa de `VulkanGpu`, `FrameContext`, `DefaultResources`, búferes, texturas y canalizaciones; fallos inyectados sin llamar a Vulkan (#612) | `Failed_creation_leaves_nothing_behind` (5 etapas), `Failed_device_setup_releases_the_device`; la fuga la delata la pasada con validación |
| 8. CoreAudio sin pasada que lo corra | Declaradas manuales en la prueba, en `ci.yml` y aquí (Mac aparcado) | — |

**Pruebas.** La de mips escribe cuadrantes de color y lee cada nivel (falla con `GenerateMips` vacío). Cada excepción frente a WARP, en las variantes y en el compositor, tiene sus límites de media, p99,9 y máximo (lo medido en lavapipe y la RTX 3050 por ~1,5) en vez de una media común de 5e-3. Con `RODAJE_REFERENCIAS_WARP` la falta de una referencia o de FFmpeg es un fallo; sin la variable, se omiten. Hecho por Devin (SWE-2) y revisado por Claude (#615).

**Validación.** Trabajo nuevo *Vulkan con validación (lavapipe)*: `vulkan-validationlayers` con la validación de sincronización, `Rodaje.Render.Vulkan.Tests` y `Rodaje.Render.Tests` en Vulkan; un mensajero escribe cada error en `RODAJE_VK_VALIDACION_INFORME` y el trabajo falla si hay alguno. `Validation_layers_are_active_when_requested` comprueba que las capas cargan. Resultado: sin errores.

**Excepción abierta: reducción sin mips.** Fuera de D3D11 el conversor escala sin vistas de mips y ensancha el núcleo como mucho 8 veces. `ExportDownscaleTests` reduce 12 veces una composición 4K de ruido gris frente a la media exacta de cada bloque: Vulkan (RTX 3050) da un error medio de 2,50 códigos de 8 bits y un máximo de 13,0; D3D11 WARP con mips, 2,48 y 15,1. Con este núcleo (Catmull-Rom) la diferencia no se ve en la media; queda como excepción con límite (3,5 y 18) hasta que el contrato tenga vistas de mips.

**Huellas de D3D11.** `build\huellas-pixeles.ps1` con Render (878 pruebas) y Export (485) en `main` con #612, #613 y #615 frente a `75f7d5f5`: composición 700, composición en coma flotante 1 791, presentación 31, títulos 32 y monitores 1, **iguales**; las 6 lecturas de composición de más son las de `CpuUploadTests` en Vulkan. Exportación: ningún cuadro que la base no haya producido (solo cambia cuántas veces se repite alguno, por el reloj); las miniaturas no son deterministas, como en la fase 3. App no se repitió: en D3D11 solo cambian la retención de `Acquire` y el plazo de la lectura, sin tocar sombreadores ni órdenes de dibujo. El filtro del script (`Categoria!=…&…`) lo rompe `cmd` al pasar por `dotnet-local.cmd`, igual en las dos corridas.

**Tareas que quedan.** Compartir lo puro duplicado entre `FrameInterpolator` y `SurfaceInterpolator` (selección de salidas, presupuesto, reciclado) y entre los rasterizadores de títulos (atlas, geometría de unidades, arco, márgenes), sin unificar los motores: no es corto y no cambia el comportamiento.

## «Hecho cuando», punto por punto

| Punto | Estado | Cifra |
|---|---|---|
| `VulkanGpu` con todas las variantes | **Hecho** | 100 de 100 variantes crean sombreador y canalización (RTX 3050 y lavapipe) |
| Lectura y consultas de tiempo | **Hecho** | Pruebas comunes a los dos motores: 20 de 20 en Vulkan, 22 de 22 en D3D11 (WARP). `Rodaje.Render.Vulkan.Tests` entero: 131 + 2 omitidas en la RTX 3050 |
| Píxeles, criterio (i) centros y fracciones exactas ≤ 1e-3 | **Cumple** | 8 de 8 casos (RTX 3050 y lavapipe) |
| Píxeles, criterio (ii) media ≤ 2e-4 y p99,9 ≤ 4e-3 frente a WARP | **Cumple con excepciones nombradas** | 142 de 142 (RTX 3050 y lavapipe en la CI); 18 casos en la lista de excepciones, cada uno con sus límites de media, p99,9 y máximo, los que la fase 0 ya explicaba más `sharpen-umbral` en lavapipe |
| Píxeles, criterio (iii) frente a D3D11 de la misma GPU ≤ 1e-3 | **Cumple** | 164 de 164 + las 4 excepciones del control de la fase 0 |
| Compositor entero en Vulkan frente a D3D11 WARP | **Cumple (ii)** | 9 casos (una capa, escala y posición, giro, multiplicar, superponer, color y viñeta, desenfoque, nitidez, fundido cruzado), con planos P010: media ≤ 1,9e-5, p99,9 ≤ 2,0e-3, máximo 6,8e-3 (la capa girada, en el borde). Con NV12, lavapipe filtra los 8 bits con 8 bits de precisión y la media sube a ~7e-4 frente a WARP. En lavapipe, desenfoque, nitidez y fundido cruzado (pasadas por intermedios leídos entre texeles) dan una media de 2,5e-4 a 3,5e-4: van como excepciones nombradas con sus límites (media ≤ 4,3e-4 a 5,4e-4, máximo ≤ 2,2e-3 a 1,4e-2), atribuidas al mismo bilineal de WARP que las transiciones de las variantes, sin demostrarlo caso a caso |
| Pruebas de Render en Vulkan (lavapipe en la CI) | **Hecho** | `Rodaje.Render.Tests` corre en los dos motores con `RODAJE_MOTOR` (`TestGpu`): en Vulkan 485 correctas y 114 omitidas de 599 con títulos e interpolación (las omitidas son solo-D3D11: D3D11VA, presentación, monitores, RAW, las del conversor de exportación, que aún usan texturas de D3D11, y los detalles de Direct2D), en la RTX 3050 y en lavapipe; en D3D11, 870. Las capas de prueba suben NV12 a 16 bits en Vulkan (abajo). Con Vulkan, unas 25 pruebas admiten un código más (las que en D3D11 piden igualdad exacta entre dos caminos o ±1/±2 frente a la referencia de CPU: transiciones en 0 y 1, paso por el intermedio, eco temporal, efectos espaciales) y la nitidez, 2 + cantidad (amplifica la diferencia del desenfoque); en lavapipe salían a 1–4 códigos |
| Exportación ProRes/FFV1 por CPU con los mismos cuadros (hash) que en Windows | **Cumple con otra cifra** | El mismo hash cuadro a cuadro **no es alcanzable** entre rasterizadores (tampoco entre D3D11 de dos GPU): el bilineal y el redondeo a 10 bits difieren en las fronteras. `ExportHashTests` exporta un proyecto de 150 cuadros (color, fundido, imagen en imagen) en Vulkan y con D3D11 WARP: FFV1 (sin pérdidas) como mucho **2 códigos de 10 bits** en el **0,64 %** de las muestras con lavapipe (0,20 % en la RTX 3050; 38 de 150 cuadros idénticos), 82 dB; ProRes 70 dB. `Rodaje.Export` corre en la CI de Linux |
| Títulos: misma maquetación que en Windows | **Cumple** | Skia frente a Direct2D en los 16 casos de aceptación de `Rodaje.Text.Tests`: mismas unidades (inicio, fin, línea y palabra) y tinta a ≤ 0,005 px (se pide ±1 px) |
| Títulos: píxeles dentro de una tolerancia global | **Cumple** | Composición 960×540 RGBA8 frente a Direct2D (WARP en la CI, cada caso abajo): media ≤ 0,0015 y máximo ≤ 144/255 (borde antialias); el caso «respaldo por letra» usa fuentes distintas en cada sistema y solo se le pide media ≤ 0,005 |
| Interpolación en Vulkan | **Hecho** | Mezcla a ±1 código y flujo óptico en los dos motores; 41 pruebas de interpolación en Vulkan, 46 en D3D11, huellas de D3D11 iguales (2 680) |
| `IDecoderSurface`/`Acquire` e `IGpuSwapchain` con D3D11 sin cambiar píxeles | **Hecho** | Huellas de Render, Export y App con el contrato y otra vez con el compositor sobre `IGpu`: composición 697/697 y 1 791/1 791, presentación 31/31, títulos 29/29 y monitores 1/1 iguales; ningún cuadro exportado que la base no haya producido (como en la fase 3). Pruebas: Render 852, Export 472 + 20 omitidas, App 2 688 + 17 omitidas, en verde |

**Títulos, Skia frente a Direct2D** (RTX 3050 frente a D3D11; error medio sobre 1, máximo en códigos de 8 bits, error de la tinta de cada unidad):

| Caso | Media | Máximo | Maquetación |
|---|---|---|---|
| una-linea | 0,00019 | 127 | 0,005 px |
| saltos-forzados | 0,00050 | 87 | 0,005 px |
| salto-por-ancho | 0,00123 | 128 | 0,005 px |
| palabra-larga | 0,00063 | 121 | 0,005 px |
| por-palabras | 0,00121 | 131 | 0,004 px |
| por-lineas | 0,00041 | 85 | 0,004 px |
| derecha | 0,00042 | 59 | 0,005 px |
| interlineado | 0,00013 | 56 | 0,005 px |
| espaciado | 0,00018 | 111 | 0,004 px |
| con-kerning | 0,00016 | 85 | 0,004 px |
| sin-kerning | 0,00016 | 81 | 0,004 px |
| mayusculas | 0,00020 | 42 | 0,005 px |
| acentos | 0,00038 | 118 | 0,004 px |
| monoespaciada | 0,00008 | 36 | 0 px |
| respaldo-de-familia | 0,00013 | 33 | 0 px |
| respaldo-por-letra | 0,00369 | 255 | — (fuentes distintas en cada sistema) |

Umbral: media ≤ 0,0015 y máximo ≤ 144 (el peor medido, 0,00123 y 131, más margen para el antialias de otro rasterizador); en Linux se compara con las referencias de WARP del trabajo de Windows.

**lavapipe y las texturas de 8 bits.** lavapipe filtra las texturas UNORM8 con 8 bits de precisión: una capa NV12 se alejaba hasta 6 códigos de 10 bits de WARP al exportar y uno en las pruebas de Render. Por eso los cuadros de CPU (y las capas de prueba) se suben a planos de 16 bits con el mismo código en la parte alta, que da el mismo color. Las superficies NV12 de D3D11VA no cambian.

Las 18 excepciones frente a WARP son el filtro bilineal amplificado (ADVANCED, curvas, calificador, capas giradas), las transiciones con desplazamiento a fracciones arbitrarias (media 2,1e-4 a 2,4e-4, apenas por encima de 2e-4), los dos HDR con PQ, `sharpen-umbral` (el umbral es un escalón: en lavapipe unos pocos píxeles del borde pasan el p99,9) y `degradado2` (discontinuidad de `atan2` en un centro de píxel, 0,83 también entre D3D11 y Vulkan en la misma GPU). La prueba las nombra con su motivo y les pone límites propios de media, p99,9 y máximo (revisión de bloque).

## Lo que salió al comparar

- **Doble inversión de Y.** `build/shaders` compilaba los sombreadores de vértices con `-fvk-invert-y` y el motor ya invierte con el viewport (la convención del contrato). Las capas giradas daban 0,9 de error. Se quitó la opción de DXC: solo cambia `VsQuad.spv`.
- **Textura nueva con basura.** Una imagen nueva de Vulkan no tiene contenido definido; D3D11 la da a cero. Lo que no cubría una capa girada salía con restos de otra textura. El motor la pone a cero en su primer uso, dentro del mismo cuadro.
- **Reflexión de imágenes de escritura.** La primera versión leía mal el operando *Sampled* de `OpTypeImage` y tomaba un `RWTexture2D` por una textura de lectura.

## Lo que no quedó, y por qué

- **Mismo hash exacto en la exportación.** No es alcanzable entre rasterizadores (tabla); se pide la cifra de arriba.
- **Las 35 pruebas del conversor de exportación en Vulkan.** El conversor ya es portátil, pero su apoyo de pruebas (`Export/ExportGpu.cs`) sube texturas de D3D11 por puntero: falta pasarlo a `IGpu`. La exportación entera en Vulkan sí está cubierta por `ExportHashTests`.
- **Pruebas de `Rodaje.Render.Tests` en Vulkan.** Hecho: `TestGpu` elige el motor con `RODAJE_MOTOR` y el apoyo (`YuvImage`, `Layers`) crea `GpuTextureSurface` en Vulkan. Quedan omitidas 114: las atadas a D3D11 (detalles de Direct2D, arreglos de D3D11VA en la interpolación, D3D11VA y sus arreglos NV12, `Presenter`, `ScopeRenderer`, `ExportFrameConverter`, `GpuRawDeveloper`, `D3DCompiler`, motor WARP, `PlaneViewCache`, `GpuTimer` y otras vistas nativas) y dos que comprueban la caché perezosa de sombreadores de D3D11, que en Vulkan no existe (`VulkanGpu.CreateShader` carga el SPIR-V al crear, `src/Rodaje.Render/Gpu/Vulkan/VulkanGpu.cs:258`).
- **Títulos en la vista previa de Linux.** El dibujo existe; lo usará la interfaz Uno (fase 8). Los efectos de título exclusivos de Direct2D siguen probándose solo en D3D11 (6 pruebas, con su motivo).
- **`Acquire` de `AVVkFrame` e `IGpuSwapchain` en Vulkan.** Fases 9 y 8/9, como decía el plan: necesitan el decodificador Vulkan en nuestro dispositivo y la ventana X11.
- **Rendimiento.** Barreras simples (todo antes de todo) y una pasada de *dynamic rendering* por dibujo: correcto siempre, sin afinar. Una asignación de memoria por recurso (el límite habitual son 4 096). Se mide en la fase 9 con la RTX 3050.

## Decisiones de Daniel (17-nov)

Aceptadas las tres recomendadas: el SPIR-V va dentro del exe de Windows, el criterio (ii) queda en 2e-4 con las excepciones con nombre y el motor exige Vulkan 1.3. **De momento solo Linux**: Mac queda aparcado (pide el registro en Apple); lo que hay de macOS en la CI se queda. Después aceptó la cifra medida en lugar del mismo hash, arreglar aparte la autoprueba y **SkiaSharp** (FreeType con la FTL, el código GIF con MPL 1.1/LGPL 2.1 y el DNG SDK de Adobe con su cláusula; anotado en `LICENCIAS.md` y `docs/legal.md`).

## Preguntas ya resueltas

1. **Tamaño del exe de Windows.** `Rodaje.Render` ahora incrusta el SPIR-V (2,4 MB) también en Windows. Recomendado: dejarlo (es el 6 % del exe y evita un proyecto aparte); la alternativa es cargarlo solo fuera de Windows.
2. **Criterio (ii).** Las transiciones con desplazamiento dan una media de 2,1e-4 a 2,4e-4 por el bilineal de WARP. Recomendado: dejar 2e-4 con esas excepciones nombradas (así se ve cualquier caso nuevo que se pase); la alternativa es subir el umbral a 3e-4 y quitar cinco excepciones.
3. **Vulkan 1.3 obligatorio.** El motor pide *dynamic rendering*: lo tienen lavapipe, NVIDIA, AMD e Intel recientes y MoltenVK (como extensión). Recomendado: mantenerlo; un equipo con solo Vulkan 1.2 se queda en la CPU.

## Ayudantes

| Pieza | Quién | Cómo fue |
|---|---|---|
| Contrato, motor Vulkan, compositor sobre `IGpu`, CI de lavapipe, huellas | Claude | — |
| Títulos con SkiaSharp, licencias | ChatGPT (gpt-6-astra) | 21 min y se quedó sin cuota con todo escrito y la exportación con títulos probada en Vulkan; Claude arregló la subida de títulos con mips en D3D11 (el contenido inicial solo va con un mip), quitó un enlace a un documento que no existía y añadió la imagen fija |
| Interpolación en Vulkan | Devin (Fusión: Claude Opus 5.5 + SWE-2) | 45 min; huellas de D3D11 iguales y sin ensanchar tolerancias; revisado por Claude sin cambios |
| Exportación en Vulkan, `Rodaje.Export` en Linux, CI de macOS, autoprueba | Claude | — |
| `Rodaje.Render.Tests` en los dos motores | Devin (Fusión: Claude Opus 5.5 + SWE-2) | 56 min; sin ensanchar tolerancias. Claude subió las capas de prueba a 16 bits y pasó el RAW a solo-D3D11 tras la primera corrida en lavapipe (30 fallos por el filtro de 8 bits y por el RAW). ChatGPT estaba sin cuota. |
| Variantes y 168 casos sobre `IGpu`, trabajos de referencias en la CI | ChatGPT (gpt-6.1-sol) | 11 min; señaló bien la doble inversión de Y sin tocar `src/`. Claude cambió su truco de reflexión para la vista de arreglo de D3D11 por `GpuTextureSurface` y quitó las cuatro excepciones que tapaban el fallo. |
