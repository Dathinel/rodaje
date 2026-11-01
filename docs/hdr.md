# HDR, 10 bits y RAW

Estado medido el 24-oct-2026 (fase B incluida) y plan por fases. Decisión de camino: [D-053](decisiones.md) (propuesta, pendiente de Daniel); la fase C se partió en tres ([D-054](decisiones.md), [D-055](decisiones.md), [D-058](decisiones.md)) y la D en dos ([D-059](decisiones.md): primero el revelado en la GPU, después el intermedio).

## Lo que hay hoy (medido)

Pruebas: `BitDepthChainTests` (Render), `ExportBitDepthTests` (Export, larga: `RODAJE_PRUEBAS_LARGAS=1`), `BitDepthTests` (Module.Raw) y `CinemaDngImportTests.A_12_bit_ramp…` (App). Equipo: RTX 3050 portátil.

**Una rampa P010 de 1024 códigos (rango completo) por la cadena, sin archivo de por medio:**

| Punto | Niveles que quedan | Dónde |
|---|---|---|
| Decodificación (P010 de D3D11VA o FFV1/HEVC 10 bits por software) | 1024 | `FrameUploader.Upload` sube P010 con 10 bits y la palabra entera de 16 (`FramePixelFormat.P016`) con más; `PlaneViewCache.PlaneFormats` lee los dos como R16 |
| Decodificación de 12 bits o 4:4:4 (yuv444p12 FFV1 por software, 4096 códigos) | 3072 de 4096 (techo 3073) | swscale a P016 y, si la GPU no muestrea `DXGI_FORMAT_P016` (la RTX 3050 no), a una textura P010 con la palabra completa; `YuvRange.For(P016)` |
| Imagen fija de 16 bits (PNG rgb48, 4096 códigos) | > 2000 (techo 3073) | `FrameUploader.UploadImage` sube RGBA64 premultiplicado como `R16G16B16A16_UNORM`; las de 8 bits siguen en BGRA8 |
| Intermedio del compositor (RGBA16F, D-016) | 1024 | `Compositor.cs:75`, `LayerRenderer.cs:78`; LUT y curvas también RGBA16F |
| Conversor de exportación a P010 / yuv420p10 (BT.709 limitado, techo 877) | 877 / 877 | `ExportLayout.cs`, tramado solo en 8 bits |
| Conversor a NV12 (techo 220) | 220 | — |
| Lectura de 8 bits (miniaturas, comparación) | 256 | `Compositor.ReadbackRgba` |
| Visor | 8 bits, o 10 (`R10G10B10A2`) con monitor de ≥ 10 bits por color o Preferencias › Rendimiento › «Visor de 10 bits» | `CompositionSwapChain`, `ViewerBitDepth.UsesTenBits`; el presentador acepta los dos formatos (probado en WARP) |

**Archivo exportado y vuelto a leer con el ffmpeg del entorno** (exportación entera desde un FFV1 de 10 bits de rango completo con los 1024 códigos; techo 877 en 10 bits y 220 en 8, por el rango limitado):

| Códec | Píxeles del archivo | Niveles | Salto máx. |
|---|---|---|---|
| H.264 NVENC / HEVC NVENC 8 bits / VP9 (WebM) | yuv420p | 220 | 4 |
| H.264 MF | yuv420p | 219 (Y 68..940) | 4 |
| HEVC NVENC Main 10 | yuv420p10le | 870 | 2 |
| AV1 SVT 10 bits (MKV) | yuv420p10le | 875 | 2 |
| x265 Main 10 / x264 High 10 (complemento GPL) | yuv420p10le | 877 / 872 con la rampa antigua de 877 códigos; sin medir con la corregida | 1–2 |
| ProRes 422 HQ / DNxHR HQX (MOV) | yuv422p10le | 877 | 1 |
| FFV1 sin pérdida (MKV) | yuv420p10le | 877 | 1 |
| AV1 NVENC | — | no hay en la RTX 3050 | — |

Todos salen con curva, primarios y matriz BT.709 de rango limitado y **sin metadatos HDR**. Los tres códecs sin pérdida visible (FFV1, ProRes HQ, DNxHR HQX) dan **877 de 877**: la cadena no pierde nada. Las primeras medidas (812, «65 códigos perdidos») eran culpa de la rampa de la prueba: ffmpeg tomaba el crudo sin etiquetar por BT.601 limitado y, al pedir `-color_range pc`, lo estiraba, así que el FFV1 fuente nacía con 877 códigos y 877 códigos de rango completo caben en 812 huecos del rango limitado. Con rango y matriz también en la entrada la fuente tiene 1024 (`ExportBitDepthTests.RampAsync` lo comprueba). Los códecs con pérdida quedan en 870–875 por su cuantización.

**RAW** (CinemaDNG sintético de 12 bits, 3840 códigos):

| Punto | Niveles | Dónde |
|---|---|---|
| LibRaw | 16 bits lineales | `RawDeveloper.cs:123` |
| Módulo, rgba16f lineal | 2945 | `ToneTable.cs:21` |
| Módulo, rgba16f con curva Rec.709 (lo que pide la importación) | 2022 | `CinemaDngImport.cs:257` («espacio» y «curva» fijos en rec709) |
| Módulo, bgra8 | 256 | — |
| Intermedio ProRes 422 HQ yuv422p10le, Rec.709 limitado | 878 (Y 64..943) | `CinemaDngImport.cs:281`; recorta todo lo que pasa de 1,0 |

Techo de media precisión para rampas uniformes en [0, 1]: 10 bits → 1024 de 1024; 12 → 3073 de 4096; 14 → 5121; 16 → 7169 (en la mitad alta, de 0,5 a 1, solo caben 1025 valores).

**Color y HDR:**
- Desde D-052, `VideoColor` lleva la curva (PQ/HLG) y si los primarios son BT.2020 (`VideoColor.cs:24`), solo para convertir a SDR al entrar.
- La exportación declara siempre primarios BT.709 (`ExportColor.cs:21`); no hay SEI de mastering display ni MaxCLL/MaxFALL en ningún códec (comprobado con ffprobe en cada archivo de la tabla). La copia directa se niega con HDR (`StreamCopySource.cs:60`).
- Mezcla en gamma (D-016) y 38 `saturate` en los shaders (31 en `EffectShaders.cs`): todo lo que pase de 1,0 se recorta en los efectos. (Medido antes de la parte 2 de la fase C: en una secuencia HDR la mezcla va en lineal y los efectos recortan solo por abajo, D-055.)
- Las fuentes de más de 10 bits conservan la profundidad pero la croma baja a 4:2:0 al subirlas (swscale a P016); 4:4:4 de 10 bits sigue en P010 4:2:0. Un EXR sube en 16 bits pero recortado a [0, 1] y sin curva, como antes.

**Conclusión.** La cadena es de 10 bits o más de punta a punta: 877 de 877 en la exportación de 10 bits desde un archivo de rango completo, 3072 de 4096 en el intermedio desde 12 bits, más de 2000 desde una imagen fija de 16 bits, y el visor en 10 bits con monitor de 10 bits. Lo que falta: el RAW (llega a 10 bits con las altas luces recortadas), la croma 4:4:4 y el color, que no sabe de BT.2020, PQ ni HLG.

## Fases

| Fase | Qué | Tamaño | Quién |
|---|---|---|---|
| A | HDR → SDR (D-052) | M | Codex, unida en #444 |
| B | Cerrar los bordes de 8 bits | M | Claude, hecha (rama `claude/hdr-b`) |
| C | Trabajar en HDR, SDR → HDR, exportar HDR10/HLG | L | Claude; partes 1 y 2 hechas (D-054, D-055), parte 3 hecha para la 1.1 (rama `claude/hdr-c3`, D-058) |
| D | RAW sin perder profundidad | M | Claude; primera parte hecha (revelado en la GPU, D-059, para la 1.1); el intermedio, pendiente |

### A. HDR → SDR
PQ/HLG → lineal → BT.2020 a BT.709 → Hable → curva BT.709, en `RenderShaders.SampleRgb`, común a vista previa y exportación, con «Brillo de referencia HDR» por clip. Unida en #444.

### B. Cadena de 10 bits o más de punta a punta
- **Archivos:** `FrameUploader.cs`, `FramePixelFormat.cs`, `PlaneViewCache.cs` (camino de 16 bits para 12 bits y 4:4:4), imágenes fijas de 16 bits en RGBA16F, `CompositionSwapChain.cs` (R10G10B10A2 con monitor de 10 bits).
- **No cambia:** el intermedio RGBA16F ya basta para 10 bits en gamma; pasar a RGBA32F duplicaría la memoria (8 → 16 B/px: 1080p 16,6 → 33,2 MB por intermedio, 4K 66 → 133 MB) para ganar algo que solo pide el RAW de más de 10 bits en gamma.
- **Coste:** una textura P016/R16 de 4K pesa 25 MB frente a 12,4 MB en 8 bits; la cadena de intercambio de 10 bits ocupa lo mismo que la de 8.
- **Aceptación (medida):** el FFV1 sin pérdida de la rampa da 877 (la pérdida a 812 estaba en la rampa, no en la cadena); un yuv444p12 de 4096 códigos conserva 3072 en el intermedio; un PNG de 16 bits más de 2000; las de 8 bits siguen en BGRA8 con los mismos colores (`StillImageTests`).
- **Riesgo:** bajo. El 12 bits por hardware no existe en D3D11VA de consumo: va por software. La RTX 3050 no muestrea `DXGI_FORMAT_P016`: los 16 bits viajan en la palabra de una textura P010.
- **Fuera:** la croma 4:4:4 (sigue bajando a 4:2:0 al subir) y el visor de 10 bits se decide por el monitor con más bits del adaptador, no por el que tiene la ventana; el cambio de la preferencia se aplica al volver a abrir el reproductor.
- Encargo: `.proceso/diseno/encargos/hdr-fase-b.md`.

### C. Trabajar en HDR y SDR → HDR
- **Modelo:** la secuencia elige `sdr-709`, `hdr-pq` o `hdr-hlg`. En HDR el compositor trabaja en luz lineal BT.2020 en RGBA16F con 1,0 = 203 nits (BT.2408); media precisión llega a 65 504, de sobra para 10 000 nits.
- **Entrada:** cada capa por su curva; una SDR entra a 203 nits sin expandir.
- **SDR → HDR:** opcional por clip, mapeo inverso de BT.2446 método A con una perilla de pico (203–1000 nits). Límite honesto: realza las altas luces, no recupera lo recortado ni añade gama; un SDR de 8 bits estirado a 1000 nits enseña bandas.
- **Visor:** con Windows en HDR, cadena RGBA16F en scRGB (`DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709`); sin HDR, el visor muestra el mapeo de la fase A y lo dice.
- **Exportación:** HEVC Main 10 (NVENC/AMF/QSV, o x265 del complemento GPL), AV1 10 bits y ProRes, con `bt2020nc`/`bt2020`/`smpte2084` o `arib-std-b67`; en PQ, mastering display y MaxCLL/MaxFALL medidos en la exportación (parte 3, opcional).
- **Efectos a revisar:** color, curvas, LUT (entrada en PQ desde la parte 3), resplandor, mezclas y transiciones (los 38 `saturate`).
- **Aceptación:** una capa SDR blanca sale en una secuencia PQ con el código de 203 nits; un parche PQ de 1000 nits llega sin recorte al archivo; ffprobe ve los metadatos HDR; una secuencia SDR no cambia ni un bit.
- **Riesgo:** alto. Toca el formato del proyecto, todos los shaders y el visor, y en el portátil no hay pantalla HDR para la prueba a mano.
- Encargo: `.proceso/diseno/encargos/hdr-fase-c.md` (parte 1) y `hdr-fase-c-2.md` (parte 2).

**Parte 1, hecha (24-oct, D-054):** `Sequence.ColorSpace` en el formato 15 (solo se escribe con una secuencia HDR); `ExportColor` con BT.2020 + PQ/HLG y `MaxCll`/`MaxFall` declarados; el conversor de exportación (`ExportShaders`, variante `HDR`) codifica la composición SDR a BT.2020 con PQ o HLG con el blanco en 203 nits, y la variante SDR compila al mismo bytecode de antes (`ExportHdrEncodeTests`); `ffmpeg.exe` recibe `-mastering_display` y `-content_light` como opciones de entrada en PQ; las miniaturas convierten el HDR en la CPU (`HdrToneMap`). Medido con la rampa SDR de `ExportBitDepthTests` en una secuencia HDR (RTX 3050): HEVC NVENC PQ Y 63..572 con SEI de masterizado y MaxCLL/MaxFALL 1000/400; NVENC HLG Y 64..720 sin SEI; SVT-AV1 PQ Y 65..573 y ProRes PQ Y 64..573 con metadatos en el flujo y el contenedor (ffprobe). El rango SDR cabe en 509 códigos PQ (419 niveles de los 877 de la rampa), que es lo esperado: el resto del rango queda para las altas luces de la parte 2.

**Parte 2, hecha (24-oct, D-055):** `Compositor.ColorSpace` pone el compositor en luz lineal BT.2020 con 1,0 = 203 nits y sin recorte (`hdr.w` desde `QuadPipeline.Draw`): PQ y HLG entran por su curva sin Hable, SDR, títulos e imágenes a 203 nits; la mezcla y los efectos van en lineal, con `Clip` (solo por abajo) en color, LUT, ruedas, curvas, nitidez, grano y resplandor y los demás `saturate` intactos (índices de tablas, rampas, máscaras y mezclas por shader son [0, 1] por definición). La exportación codifica el lineal a PQ/HLG (variante `HDR`, bandera 64 para una fuente SDR) o lo recorta a SDR BT.709 (variante `TONEMAP`) si el códec no da HDR; el presentador recorta igual en una pantalla SDR y, con Windows en HDR y la preferencia «Visor HDR» (Auto/Sí/No), usa una cadena `R16G16B16A16_FLOAT` en scRGB. «Formato del proyecto…» ofrece SDR BT.709, HDR10 (PQ) y HLG; el aviso de importación calla en una secuencia HDR; la barra del visor y el panel de exportación dicen cómo se ve y cómo sale. Medido (RTX 3050): blanco SDR → 573 (PQ) y 721 (HLG); parche PQ de 1000 nits → 723 (antes ≤ 575); presentador SDR byte a byte igual al shader congelado; vista previa HDR → SDR = exportación SDR (±1); el parche de 1000 nits llega al archivo con NVENC y ProRes (`ExportBitDepthTests.A_1000_nit_pq_patch…`).

**Parte 3, hecha (31-oct, D-058; para la 1.1):**
- **MaxCLL/MaxFALL medidos.** «Medir MaxCLL/MaxFALL antes de codificar» en el panel de exportación (`VideoExportSettings.MeasureLightLevels`, solo con salida HDR10 desde una secuencia HDR) añade una pasada previa (`ExportJobState.MeasuringLight`): `ExportJob` compone cada cuadro de la salida y `ContentLightMeter` reduce en la GPU cada tesela de 16×16 de la composición lineal entera (sin recorte ni escala) al máximo y la suma de max(R, G, B); la CPU junta las teselas (8160 pares de floats en 1080p). MaxCLL es el mayor máximo y MaxFALL la mayor media por cuadro, × 203 nits, redondeados; con mezcla de cuadros se miden los dos (cota superior). Llenan `ExportColor.MaxCll`/`MaxFall` antes de arrancar `ffmpeg.exe`, salen en el estado del trabajo y en la notificación («MaxCLL 1004 nits · MaxFALL 401 nits (medidos)») y en `ExportJob.LightLevels`. Sin la pasada siguen los declarados (1000/400 o los de los ajustes). El código PQ 723 son 1004 nits, no 1000: por eso lo medido no es redondo. Prueba: `ExportBitDepthTests.Measured_light_levels_reach_the_file` (larga) y `HdrPart3Tests.The_light_meter…` (WARP).
- **SDR → HDR por clip.** «Expandir a HDR» en Propiedades › Opacidad y mezcla (`rodaje.transform` / `hdrExpand`, apagado; solo se ve en una secuencia HDR) con el pico en «Brillo de referencia HDR» (203–1000 nits; 0 usa 1000). En la rama SDR de `ToLinear2020` (`hdr.y` = pico; una capa SDR sin expandir lleva 0) la luminancia BT.709 lineal pasa por la curva inversa de BT.2446-1 método A normalizada al pico y **anclada en el gris del 18 %**: por debajo no cambia nada y por encima la ganancia sigue la forma de la curva hasta llevar el blanco al pico, conservando las proporciones RGB (`SdrExpansion` en `Rodaje.Core.Effects` es la referencia en la CPU). El método literal multiplica el gris por 2,4 y un clip SDR dejaría de verse como en una secuencia SDR. Límite honesto: realza las luces, no recupera lo recortado ni añade gama; un SDR de 8 bits estirado a 1000 nits enseña bandas. En una secuencia SDR no actúa (`hdr.w` = 0: los bytes son los de antes, `HdrPart3Tests.An_sdr_sequence_ignores_the_expansion`). Pruebas: monotonía y gris del 18 % a ±1 % (`SdrExpansionTests`, Linux), el shader contra la referencia a ±3 códigos PQ y el blanco en el código de 1000 nits (`HdrPart3Tests`).
- **LUT y curvas en HDR.** Con `hdr.w > 0`, `ApplyLut` y `Curves` codifican la luz lineal a PQ (BT.2020; 203 nits ≈ 0,58), consultan la tabla y vuelven a lineal (`LinearToPq`/`PqToLinear` en el preludio). **Una LUT se interpreta con entrada y salida en PQ BT.2020**, como las LUT de HDR10 (una LUT pensada para SDR en gamma BT.709 da otro resultado en una secuencia HDR; es la misma convención que DaVinci con la línea de tiempo en PQ). Las curvas se dibujan sobre la señal PQ: el 50 % de la curva es ≈ 92 nits. Antes, lo que pasaba de 203 nits tomaba el borde de la tabla; ahora un parche de 1000 nits atraviesa una LUT y unas curvas identidad entero. El camino SDR es el mismo (`[branch]` sobre `hdr.w`; el bytecode del conversor SDR sigue congelado).
- **Miniatura y osciloscopios.** `ExportShaders.Thumbnail` tiene la variante `TONEMAP` (lineal BT.2020 → SDR BT.709 recortando lo que pasa de 203 nits, como `PresentHdr`) que el conversor elige con una composición HDR; sin ella la miniatura es la de siempre. `ScopeShaders` recibe `space.x` (1 en HDR) y recorta igual antes de contar: los monitores miden lo que se ve en una pantalla SDR y lo que sale en una exportación SDR; 1000 y 203 nits caen en 255, 50 nits en 124.
- **HDR del monitor de la ventana** (hallazgo 79 del Reproductor, opción b). `DisplayHdr.IsWindowMonitorInHdr(hwnd)` pregunta por DisplayConfig (`MonitorFromWindow` → `\\.\DISPLAYn` → ruta activa → `DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO.advancedColorEnabled`); si no contesta (WARP, sin ventana, error) se cae a DXGI (algún monitor del adaptador en HDR) y, si tampoco, queda sin saber. «Visor HDR»: Automático usa scRGB solo si se sabe que el monitor de la ventana está en HDR; **Siempre** lo usa salvo que se sepa que está en SDR (antes quemaba la imagen en un monitor SDR); Nunca, SDR. Se evalúa al crear la cadena y al cambiar la preferencia (`CommandKind.ViewerFormat`), no al mover la ventana de monitor.
- **Fuera de la parte 3:** el blanco SDR del sistema en scRGB sigue fijo en 203 nits (Windows deja elegirlo en Configuración › Pantalla › HDR; leerlo pide `DISPLAYCONFIG_SDR_WHITE_LEVEL`, que encaja en `DisplayHdr` cuando haga falta); MaxCLL/MaxFALL se miden sobre la composición entera, no sobre el recorte ni la escala de la salida; la pasada previa compone cada cuadro dos veces.

### D. RAW de 12–16 bits

**Primera parte, hecha (1-nov, D-059; para la 1.1): demosaico y revelado en la GPU.**

- **Qué cambia.** Al importar CinemaDNG con una GPU, `modulo-raw` ya no revela: su función nueva **`mosaico`** lee cada DNG con LibRaw (`libraw_unpack` → `libraw_raw2image` → `libraw_subtract_black`) y entrega el mosaico de Bayer de 16 bits sin interpolar y con el negro restado (2 bytes por píxel en lugar de los 8 del rgba16f), más lo que hace falta para revelarlo: el patrón (`RGGB`…), el blanco, los multiplicadores del balance pedido (de la toma, luz de día o propios) y la matriz 3×3 de la cámara a Rec.709 (`rgb_cam`). **`GpuRawDeveloper`** (`Rodaje.Render/Raw`) sube el mosaico a una textura `R16_UINT`, un compute shader (`RawShaders`) hace el balance con recorte a 1, el **interpolado de Malvar-He-Cutler** (5×5, bordes reflejados sin repetir el píxel del borde para conservar el patrón), la matriz recortada a [0, 1], la exposición y la curva Rec.709, y el cuadro rgba16f vuelve por una textura `Staging` al `ffmpeg.exe` que escribe el ProRes. Los pasos y el orden son los de `dcraw_process` sin recuperar las luces, así que la salida es la misma que la de LibRaw salvo por el interpolado.
- **Respaldo en la CPU.** Sin GPU por hardware (WARP no cuenta: Direct3D por software es más lento que LibRaw), con un módulo anterior a la 1.1 (sin `mosaico`), con un cuadro que no es un mosaico de Bayer RGB (`info` lo dice en `bayer`: X-Trans, Foveon, DNG lineal, cuadros girados o de píxeles anchos) o si Direct3D no puede crear el revelador, la importación sigue por `revelar` (LibRaw, AHD o lineal) como en la 1.0.2, sin avisos ni cambios. El diálogo dice cuál de los dos va a usar y solo ofrece «Calidad del revelado» en la CPU; la notificación del final dice «Revelado en la GPU (…): N ms por fotograma».
- **Referencia en la CPU.** `MosaicDeveloper` (`Rodaje.Core/Raw`, con `BayerPattern` y `RawDevelopParameters`, el contrato entre el módulo y los reveladores) es la misma receta escrita sin GPU: las pruebas comparan el shader con ella píxel a píxel en WARP (`RawGpuDeveloperTests`: la carta, ruido por píxel con una matriz de color y los cuatro patrones, cuatro curvas y exposiciones, a una unidad de media precisión en rgba16f) y la referencia con LibRaw sobre el mosaico que entrega `mosaico` (`MosaicTests`: los parches de la carta a menos de 0,006 en lineal, Rec.709 y sRGB con exposición; la rampa de 12 bits llega con sus 3840 códigos); `CinemaDngImportTests.The_gpu_path…` importa la carta por los dos caminos (WARP y LibRaw) y compara el ProRes. Las de `Rodaje.Core.Tests/Raw` corren en Linux.
- **Qué no cambia todavía.** El intermedio sigue siendo ProRes 422 HQ Rec.709 de 10 bits con las luces recortadas en el blanco; lo de abajo (ProRes 4444 XQ o FFV1 con curva logarítmica, el espacio a elegir y la entrada en lineal a una secuencia HDR) es la segunda parte. El revelado en la GPU no recupera luces (como `highlight = 0` de LibRaw) ni hace reducción de ruido; `espacio` solo puede ser `rec709` en `mosaico` (los demás primarios siguen por `revelar`).

**Cifras (tiempo por fotograma, 4K DCI 4096×2160 y 1080p, CinemaDNG sintético de 12 bits):**

| Camino | 1080p | 4K DCI | Dónde se mide |
|---|---|---|---|
| Antes: `revelar` con LibRaw (lineal / AHD) en el módulo | por medir en el RTX 3050 | por medir | `SpeedTests.Develop_4k_speed` (TiempoReal) y el `ms` que devuelve el módulo |
| Después: `mosaico` (LibRaw sin interpolar) + `GpuRawDeveloper` | por medir en el RTX 3050 | por medir | `RawGpuDeveloperTests.Time_per_frame_on_the_gpu_and_the_cpu_reference` (`[GpuFact]`, escribe la mediana de 5 cuadros con subida, shader y bajada) y la notificación de la importación |
| Referencia `MosaicDeveloper` en la CPU, un hilo (Xeon a 2,1 GHz de la nube, Release) | 404 ms | 1654 ms | la misma prueba, en Linux con un programa suelto |

Las dos primeras filas quedan para la máquina con GPU: en la nube no hay Direct3D (el shader se comprueba en la CI de Windows con WARP, que no mide nada útil). Para llenarlas: `Probar.bat` con `RODAJE_PRUEBAS_LARGAS=1` y el filtro `FullyQualifiedName~Time_per_frame_on_the_gpu|FullyQualifiedName~Develop_4k_speed`, o importar una carpeta CinemaDNG y leer la notificación («N ms por fotograma», que suma el `ms` del módulo y el tiempo de la GPU con la bajada). Lo que sí está medido: el mosaico pesa la cuarta parte que el rgba16f en la memoria compartida (17,7 MB frente a 70,8 MB por cuadro 4K), así que caben el doble de cuadros por lote (`BatchSize`).

**Segunda parte (pendiente): el intermedio sin perder profundidad.**
- **Archivos:** `CinemaDngImport.cs` (argumentos de ffmpeg), `RawShaders.cs` y `MosaicDeveloper.cs` (la curva de salida).
- **Cambio:** el intermedio pasa de ProRes 422 HQ Rec.709 a ProRes 4444 XQ de 12 bits (o FFV1 yuv444p16 sin pérdida, si el tamaño es aceptable) con una curva logarítmica (documentada) para no recortar las altas luces; el espacio se elige (Rec.709 o BT.2020). Con la fase B el 12 bits se lee sin bajar a 10; con la C, el RAW entra en lineal a una secuencia HDR.
- **Aceptación:** los 3840 códigos de la rampa conservan más de 2000 niveles en el intermedio; un parche a +2 EV sobre el blanco sobrevive y se recupera bajando la exposición.
- **Riesgo:** medio. ProRes 4444 XQ 4K ocupa unas 2,5 veces el HQ.
