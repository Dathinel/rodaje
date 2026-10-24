# HDR, 10 bits y RAW

Estado medido el 24-oct-2026 (fase B incluida) y plan por fases. Decisión de camino: [D-053](decisiones.md) (propuesta, pendiente de Daniel).

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
- Mezcla en gamma (D-016) y 38 `saturate` en los shaders (31 en `EffectShaders.cs`): todo lo que pase de 1,0 se recorta en los efectos.
- Las fuentes de más de 10 bits conservan la profundidad pero la croma baja a 4:2:0 al subirlas (swscale a P016); 4:4:4 de 10 bits sigue en P010 4:2:0. Un EXR sube en 16 bits pero recortado a [0, 1] y sin curva, como antes.

**Conclusión.** La cadena es de 10 bits o más de punta a punta: 877 de 877 en la exportación de 10 bits desde un archivo de rango completo, 3072 de 4096 en el intermedio desde 12 bits, más de 2000 desde una imagen fija de 16 bits, y el visor en 10 bits con monitor de 10 bits. Lo que falta: el RAW (llega a 10 bits con las altas luces recortadas), la croma 4:4:4 y el color, que no sabe de BT.2020, PQ ni HLG.

## Fases

| Fase | Qué | Tamaño | Quién |
|---|---|---|---|
| A | HDR → SDR (D-052) | M | Codex, unida en #444 |
| B | Cerrar los bordes de 8 bits | M | Claude, hecha (rama `claude/hdr-b`) |
| C | Trabajar en HDR, SDR → HDR, exportar HDR10/HLG | L | Claude (1 y 2 a Codex) |
| D | RAW sin perder profundidad | M | agy la parte del módulo, Claude la integración |

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
- **Exportación:** HEVC Main 10 (NVENC/AMF/QSV, o x265 del complemento GPL), AV1 10 bits y ProRes, con `bt2020nc`/`bt2020`/`smpte2084` o `arib-std-b67`; en PQ, mastering display y MaxCLL/MaxFALL medidos en la exportación.
- **Efectos a revisar:** color, curvas, LUT (entrada en log o PQ), resplandor, mezclas y transiciones (los 38 `saturate`).
- **Aceptación:** una capa SDR blanca sale en una secuencia PQ con el código de 203 nits; un parche PQ de 1000 nits llega sin recorte al archivo; ffprobe ve los metadatos HDR; una secuencia SDR no cambia ni un bit.
- **Riesgo:** alto. Toca el formato del proyecto, todos los shaders y el visor, y en el portátil no hay pantalla HDR para la prueba a mano.
- Encargo: `.proceso/diseno/encargos/hdr-fase-c.md`.

### D. RAW de 12–16 bits
- **Archivos:** `CinemaDngImport.cs` (parámetros y argumentos de ffmpeg), `ToneTable.cs`.
- **Cambio:** el intermedio pasa de ProRes 422 HQ Rec.709 a ProRes 4444 XQ de 12 bits (o FFV1 yuv444p16 sin pérdida, si el tamaño es aceptable) con una curva logarítmica (la del módulo, documentada) para no recortar las altas luces; el espacio se elige (Rec.709 o BT.2020). Con la fase B el 12 bits se lee sin bajar a 10; con la C, el RAW entra en lineal a una secuencia HDR.
- **Aceptación:** los 3840 códigos de la rampa conservan más de 2000 niveles en el intermedio; un parche a +2 EV sobre el blanco sobrevive y se recupera bajando la exposición.
- **Riesgo:** medio. ProRes 4444 XQ 4K ocupa unas 2,5 veces el HQ.
