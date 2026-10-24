# HDR, 10 bits y RAW

Estado medido el 24-oct-2026 y plan por fases. Decisión de camino: [D-053](decisiones.md) (propuesta, pendiente de Daniel).

## Lo que hay hoy (medido)

Pruebas: `BitDepthChainTests` (Render), `ExportBitDepthTests` (Export, larga: `RODAJE_PRUEBAS_LARGAS=1`), `BitDepthTests` (Module.Raw) y `CinemaDngImportTests.A_12_bit_ramp…` (App). Equipo: RTX 3050 portátil.

**Una rampa P010 de 1024 códigos (rango completo) por la cadena, sin archivo de por medio:**

| Punto | Niveles que quedan | Dónde |
|---|---|---|
| Decodificación (P010 de D3D11VA o FFV1/HEVC 10 bits por software) | 1024 | `FrameUploader.cs:56` sube P010 si la fuente tiene más de 8 bits; `PlaneViewCache.cs:120` lo lee como R16 |
| Intermedio del compositor (RGBA16F, D-016) | 1024 | `Compositor.cs:75`, `LayerRenderer.cs:78`; LUT y curvas también RGBA16F |
| Conversor de exportación a P010 / yuv420p10 (BT.709 limitado, techo 877) | 877 / 877 | `ExportLayout.cs`, tramado solo en 8 bits |
| Conversor a NV12 (techo 220) | 220 | — |
| Lectura de 8 bits (miniaturas, comparación) y visor | 256 | `Compositor.ReadbackRgba`; cadena de intercambio `B8G8R8A8` en `CompositionSwapChain.cs:180` |

**Archivo exportado y vuelto a leer con el ffmpeg del entorno** (exportación entera desde un FFV1 de 10 bits de rango completo; techo 877 en 10 bits y 220 en 8, por el rango limitado):

| Códec | Píxeles del archivo | Niveles | Salto máx. |
|---|---|---|---|
| H.264 NVENC / H.264 MF / HEVC NVENC 8 bits / VP9 (WebM) | yuv420p | 220 | 4 |
| HEVC NVENC Main 10 | yuv420p10le | 866 | 2 |
| AV1 SVT 10 bits (MKV) | yuv420p10le | 877 | 1 |
| x265 Main 10 / x264 High 10 (complemento GPL) | yuv420p10le | 877 / 872 | 1–2 |
| ProRes 422 HQ / DNxHR HQX (MOV) | yuv422p10le | 812 | 2 |
| FFV1 sin pérdida (MKV) | yuv420p10le | 812 | 2 |
| AV1 NVENC | — | no hay en la RTX 3050 | — |

Todos salen con curva, primarios y matriz BT.709 de rango limitado y **sin metadatos HDR**. El FFV1 sin pérdida da la cifra real de la cadena: **812 de 877** (faltan 65 códigos sueltos, saltos de 2). Los códecs con pérdida llegan a 866–877 porque su ruido rellena los huecos, no porque guarden más. La prueba directa del conversor con una textura P010 da 877, así que los 65 se pierden entre la decodificación por software (`FrameUploader.PackP010`) y el compositor con una fuente de rango completo; queda por aislar en la fase B.

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
- 12 bits y 4:4:4 de 10 bits se bajan a P010 4:2:0 (`FrameUploader.cs:61`); las imágenes fijas de 16 bits suben en BGRA8 (`FrameUploader.cs:43`).

**Conclusión.** La cadena interna ya es de 10 bits en el compositor y en la exportación de 10 bits (877 de 877 desde una textura P010; 812 de 877 desde un archivo de rango completo). Lo que no es de 10 bits: el visor (8), las imágenes fijas (8), las fuentes de 12 bits (bajan a 10), el RAW (llega a 10 bits con las altas luces recortadas) y el color, que no sabe de BT.2020, PQ ni HLG.

## Fases

| Fase | Qué | Tamaño | Quién |
|---|---|---|---|
| A | HDR → SDR (D-052) | M | Codex, unida en #444 |
| B | Cerrar los bordes de 8 bits | M | Codex |
| C | Trabajar en HDR, SDR → HDR, exportar HDR10/HLG | L | Claude (1 y 2 a Codex) |
| D | RAW sin perder profundidad | M | agy la parte del módulo, Claude la integración |

### A. HDR → SDR
PQ/HLG → lineal → BT.2020 a BT.709 → Hable → curva BT.709, en `RenderShaders.SampleRgb`, común a vista previa y exportación, con «Brillo de referencia HDR» por clip. Unida en #444.

### B. Cadena de 10 bits o más de punta a punta
- **Archivos:** `FrameUploader.cs`, `FramePixelFormat.cs`, `PlaneViewCache.cs` (camino de 16 bits para 12 bits y 4:4:4), imágenes fijas de 16 bits en RGBA16F, `CompositionSwapChain.cs` (R10G10B10A2 con monitor de 10 bits).
- **No cambia:** el intermedio RGBA16F ya basta para 10 bits en gamma; pasar a RGBA32F duplicaría la memoria (8 → 16 B/px: 1080p 16,6 → 33,2 MB por intermedio, 4K 66 → 133 MB) para ganar algo que solo pide el RAW de más de 10 bits en gamma.
- **Coste:** una textura P016/R16 de 4K pesa 25 MB frente a 12,4 MB en 8 bits; la cadena de intercambio de 10 bits ocupa lo mismo que la de 8.
- **Aceptación:** el FFV1 sin pérdida de la rampa sube de 812 a 877 niveles; un yuv444p12 de 4096 códigos conserva más de 2000 en el intermedio; las imágenes de 16 bits más de 1024; nada cambia en 8 bits (comparación de bytes).
- **Riesgo:** bajo. El 12 bits por hardware no existe en D3D11VA de consumo: va por software.
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
