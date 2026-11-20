# HDR, fase D, parte 2: el intermedio del RAW sin recortar

Diseño del 15-nov-2026 para después de la 1.0.3. Concreta la segunda parte de la fase D de `D:\Programas de uso libre\Rodaje\docs\hdr.md` («intermedio de 12 bits»). Decisión propuesta: **D-065** (o el siguiente número libre al unir, si otra propuesta lo toma antes).

## Resumen

- **El plan de `hdr.md` no se puede cumplir con ProRes.** Los codificadores ProRes del ffmpeg del entorno (`prores_ks`, `prores_aw`) solo aceptan `yuv422p10le`, `yuv444p10le` y `yuva444p10le` (comprobado con `-h encoder=`): un «ProRes 4444 XQ de 12 bits» hecho con ffmpeg es de 10 bits.
- **Y 12 bits tampoco bastan para el criterio** («más de 2000 niveles de los 3840 de la rampa»). Calculado con la rampa de 12 bits, el blanco del sensor en 203 nits y PQ: 10 bits dan 433 niveles, 12 bits dan 1294 y 16 bits dan 3840. Cualquier curva que guarde altas luces gasta códigos arriba y deja pocos para la rampa SDR. Una logarítmica no lo arregla: la octava alta de la rampa tiene 1920 códigos y una log de 12 bits le da unos 256.
- **Propuesta:** un segundo intermedio, **FFV1 `yuv444p16le` sin pérdida en MKV, PQ BT.2020 con 1,0 lineal = 203 nits**, a elegir en el diálogo frente al ProRes 422 HQ Rec.709 de hoy, que no cambia ni un byte. PQ y BT.2020 ya los entiende toda la cadena de lectura (`VideoColor`, fases A y C), así que **no cambian el decodificador, los shaders ni el formato del proyecto**: el RAW entra en lineal a una secuencia HDR y una secuencia SDR lo convierte con el mapeo de la fase A, como cualquier PQ.
- **La conversión a PQ la hace `zscale` (zimg, WTFPL, ya está en el ffmpeg LGPL del entorno)** con el cuadro en media precisión lineal **en planos** (`gbrpf16le`). Medido en el entorno: con `rgbaf16le` a la entrada, swscale **recorta a 1,0** antes de `zscale` y el parche de +2 EV sale igual que el blanco; con `gbrpf16le` llega entero (blanco → 0,58068 = PQ(203 nits), +2 EV → 0,72915 = PQ(812 nits)). Con la tubería en media precisión lineal la rampa conserva **2945 niveles de 3840** (los mismos que mide el módulo en rgba16f lineal); con el PQ ya calculado y pasado en media precisión, solo 1129, por eso la curva no va en el shader.

## 1. Qué resuelve y cómo lo hacen otros

**Para quien edita.** Hoy un CinemaDNG importado sale como ProRes 422 HQ Rec.709 de 10 bits con todo lo que pasa del blanco recortado: si se importó con +2 EV para levantar las sombras, las nubes y las ventanas quedan blancas para siempre y bajar la exposición en el editor solo las pone grises. Con el intermedio nuevo las luces quedan guardadas hasta 10 000 nits (+5,6 EV sobre el blanco SDR) y se recuperan después en el editor, que es lo que se espera de un RAW. En una secuencia HDR el material entra en su luz real sin pasar por SDR.

**Cómo lo resuelven otros (solo ideas de producto):**
- **Resolve y Premiere** no hacen intermedio: revelan el RAW al vuelo en la línea de tiempo con ajustes por clip (balance, exposición, recuperación de luces) que se cambian cuando se quiera. Para reproducir con fluidez ofrecen medios optimizados o caché de render en un 4444 de 10–12 bits o en float sin comprimir.
- **Final Cut** lee ProRes RAW de forma nativa y propone «medios optimizados» en ProRes para lo demás. Sus bibliotecas HDR trabajan en gama amplia.
- **Shotcut** (GPLv3, sobre MLT) tiene «convertir a formato apto para edición» con tres calidades, la última sin pérdida (FFV1/Ut Video). La idea útil es que el sin pérdida es una opción explícita, porque pesa mucho. No lee CinemaDNG.
- **Kdenlive** (GPLv3, MLT): sin RAW de cine y con HDR limitado. Nada que tomar.
- **Olive** (GPLv3) trabaja internamente en media precisión con OpenColorIO y guarda su caché en OpenEXR (float). La idea es la misma de aquí: el intermedio guarda luz lineal o una curva que la represente sin recortar.
- **OpenTimelineIO** (Apache 2.0) es solo intercambio de líneas de tiempo y no aplica.

No se copia código de ninguno; todo lo de arriba son ideas de producto.

**Por qué no revelar al vuelo como Resolve.** Es la solución completa, pero exige meter el módulo y el revelado en la cadena de reproducción, guardar los ajustes de revelado por clip (formato 16) y dar la latencia de LibRaw por cuadro. Cabe como fase E, más adelante (pregunta 4). El intermedio de 16 bits deja el mismo resultado visible con un cambio de unas 150 líneas.

## 2. Cómo encaja en Rodaje

**Formato del intermedio (modo HDR):**

| | |
|---|---|
| Contenedor y códec | MKV, FFV1 nivel 3, `-slices 24 -slicecrc 1` (todo intra) |
| Píxeles | `yuv444p16le`, rango completo (el mismo camino de 16 bits que mide `BitDepthChainTests`) |
| Color | `-color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc -color_range pc` |
| Escala de luz | 1,0 lineal = 203 nits (BT.2408, la misma convención del compositor HDR, D-055) |
| Audio | el WAV de la toma, `pcm_s24le`, como hoy |
| Cadena de ffmpeg | `-f rawvideo -pix_fmt gbrpf16le … -i pipe:0 -vf zscale=tin=linear:pin=2020:min=gbr:rin=full:t=smpte2084:p=2020:m=2020_ncl:r=full:npl=203,format=yuv444p16le` |

**Qué se revela distinto en modo HDR.** Solo dos parámetros; la receta de D-059 no cambia:
- **Curva `Linear`** en lugar de `Rec709`. Ya existe en `RawOutputCurve` y en los dos reveladores.
- **Primarios BT.2020:** `ReadMosaicParameters` compone la matriz BT.709→BT.2020 con la `matriz` (rgb_cam a Rec.709) que entrega `mosaico`. `mosaico` sigue aceptando solo `espacio: rec709` y **el módulo no cambia**. En el respaldo de la CPU, `revelar` recibe `espacio: rec2020, curva: lineal`, que ya admite `modulo-raw` 1.0.0 (comprobado en `v1.0.2:DevelopSettings.cs`).

**Dónde queda margen.** Los reveladores recortan en el sensor (balance con `min(…, 1)`) y la matriz a [0, 1], y **después** multiplican por 2^EV. La exposición del diálogo es lo que sube las luces por encima de 1,0, en la GPU y en LibRaw por igual (la `ToneTable` del módulo no recorta arriba en rgba16f). No se quita el recorte de la matriz: daría altas luces rosas o cian con canales del sensor saturados, y el GPU dejaría de coincidir con LibRaw. Recuperar luces quemadas en el sensor queda fuera, como en D-059. El significado de «Exposición» es el mismo en los dos modos: 0 EV pone el blanco del sensor en el blanco SDR. En modo HDR, +2 EV deja dos pasos de luces guardados en lugar de recortados.

**Archivos que se tocan o se crean:**

| Capa | Archivo | Cambio |
|---|---|---|
| Core | `src/Rodaje.Core/Raw/RawDevelopParameters.cs` | constante `Bt709ToBt2020` (la de `RenderShaders.cs:71`, con el mismo nombre, en C#) y `WithRec2020Primaries()` que compone la matriz |
| Core | `src/Rodaje.Core/Raw/GbrPlanes.cs` (nuevo) | `FromRgbaHalf(ReadOnlySpan<byte> rgba16f, Span<byte> gbr)`: de rgba16f intercalado a tres planos **en el orden G, B, R** de `gbrpf16le`, sin alfa (4K: 53,1 MB en lugar de 70,8) |
| App | `src/Rodaje.App/Views/Raw/Logic/CinemaDngImport.cs` | `CinemaDngOptions.Intermediate` (`Sdr709`, `HdrPq`); `EncoderArguments` con la segunda variante y extensión `.mkv`; `Parameters`/`ReadMosaicParameters` con lineal y BT.2020; `Write` y `DevelopAndWrite` pasan por `GbrPlanes` en HDR; `CinemaDngResult` con MB por fotograma |
| App | `src/Rodaje.App/Views/Raw/CinemaDngDialog.cs` | ComboBox «Intermedio»: «SDR · ProRes 422 HQ, 10 bits» o «HDR sin recortar · FFV1 16 bits, PQ BT.2020 (unas 5 veces más grande)». Por defecto, HDR si la secuencia activa es HDR (`Sequence.ColorSpace`) y SDR si no |
| App | `src/Rodaje.App/Services/Media/AutoProxyRule.cs` | FFV1 de más de 10 bits desde 1080p pide proxy siempre (se decodifica en la CPU: ver cifras) |
| Media | `src/Rodaje.Media/Proxies/ProxyEncoders.cs` | sin cambio: con los mismos argumentos del proxy (OpenH264, `-map_metadata -1`), el proxy de un FFV1 PQ conserva `smpte2084`/`bt2020`/`bt2020nc`/`pc` (medido con ffprobe). Solo se añade una prueba que lo fije |
| — | Render, Engine, Export, `modulo-raw`, esquema | **sin cambios** |

**Formato del proyecto:** sin cambio de versión (sigue en 15). El intermedio es un medio más, con sus etiquetas de color dentro del archivo. La elección del diálogo no se guarda en el proyecto. Un Rodaje 1.0.2 abre el proyecto y lee el MKV como PQ (la fase A ya estaba).

**Native AOT y pruebas sin WinUI:** solo cadenas de argumentos, `Span` y `Half`, sin reflexión ni JSON nuevo. Todo lo nuevo se prueba en `Rodaje.Core.Tests` (Linux) y en `Rodaje.App.Tests`/`Rodaje.Media.Tests`, sin interfaz. El diálogo es la única pieza que solo se ve con ratón.

**Cifras medidas hoy (este PC, ffmpeg del entorno, 24 cuadros 4K DCI sintéticos con grano de 8 bits, todos los hilos, prioridad baja):**

| Intermedio | MB/cuadro | Codificar | Decodificar | Niveles de la rampa (calculados) |
|---|---|---|---|---|
| ProRes 422 HQ 10 bits (hoy) | 3,9 | 353 ms | 20 ms | 878 (con recorte) |
| ProRes 4444 XQ 10 bits | 8,7 | 534 ms | 54 ms | 433 en PQ |
| FFV1 yuv444p12 | 16,7 | 207 ms | 279 ms | 1294 en PQ |
| **FFV1 yuv444p16 (propuesta)** | **18,5** | **273 ms** | **442 ms** | **2945** (tubería en media precisión lineal) |

El grano sintético comprime mejor que el ruido real de 12 bits, así que en material real hay que contar con unos 25–30 MB por cuadro 4K. Eso es 0,6–0,7 GB/s a 24 fps y unos 40 GB por minuto; en 1080p, la cuarta parte. FFV1 se codifica más rápido que `prores_ks`, así que la importación no se alarga por el códec. FFV1 de 16 bits **no se reproduce en tiempo real** (2 fps en 4K, unos 9 en 1080p), así que va con proxy, como ya va el ProRes 4K. El proxy sale de este intermedio, no del DNG.

**Objetivos medibles (fase 4, en el RTX 3050 y sin GPU):**
- La importación HDR tarda como mucho 1,25 veces lo que tarda la SDR del mismo material, por los dos caminos (GPU y LibRaw). Se lee en la notificación («N ms por fotograma»).
- `zscale` + `GbrPlanes` añaden menos de 40 ms por cuadro 4K. Si `zscale` va en un hilo y pasa de eso, se le da `threads`.
- La memoria no crece: el búfer de planos (53 MB en 4K) sustituye al envío de rgba16f y el lote (`BatchSize`) no cambia.
- El proxy de un intermedio 4K se genera y reproduce como el de un ProRes 4K.

**Relación con el render en caché.** **Antes y sin esperarlo.** No comparten intermedio, porque piden cosas opuestas: la caché tiene que decodificarse en tiempo real (ProRes 4444 o 422 HQ, 10 bits; en una secuencia HDR, PQ de 10 bits basta para mirar) y el intermedio del RAW tiene que guardar la señal sin pérdida aunque pese. Sí comparten la receta de ffmpeg para un intermedio etiquetado en PQ BT.2020 (la parte `zscale` + etiquetas). Si la caché la necesita, se saca entonces de `CinemaDngImport.EncoderArguments` a `Rodaje.Media`; antes no, para no crear una abstracción sin segundo uso.

## 3. Fases

Las ramas 1, 2 y 3 son independientes (tocan archivos distintos) y van en paralelo. La 4 empieza cuando la 1 está unida, porque usa `CinemaDngOptions.Intermediate` y `CinemaDngResult`. Las pruebas que arrancan `modulo-raw` como proceso se lanzan fuera del árbol de procesos de Claude Code (`Invoke-CimMethod Win32_Process Create`, salida a un archivo).

| Fase | Rama | Quién | Qué | Terminado cuando |
|---|---|---|---|---|
| 1. Escribir el intermedio | `codex/hdr-d2-intermedio` | ChatGPT (codex), revisa Claude | `GbrPlanes`, `Rec709ToRec2020`/`WithRec2020Primaries`, `CinemaDngOptions.Intermediate`, variante HDR de `EncoderArguments`, `Parameters` y `ReadMosaicParameters` con lineal BT.2020, planos en `Write`/`DevelopAndWrite`, `.mkv` | `GbrPlanesTests` (Core, Linux: orden G, B, R, valores > 1 intactos, entrada de 8 bytes por píxel y salida de 6, y `ArgumentException` si los tamaños no cuadran); `CinemaDngImportTests`: los argumentos HDR exactos; la rampa sintética de 12 bits importada en HDR por la GPU (WARP) y por LibRaw da **≥ 2000 niveles**, con los dos caminos iguales dentro de la tolerancia de D-059 también en luces > 1,0 (LibRaw aplica su matriz cámara → BT.2020 en enteros de 16 bits y la GPU la compuesta en float: tienen que recortar igual) en Y (se esperan unos 2945) y ffprobe ve `smpte2084/bt2020/bt2020nc/pc`; el blanco a exposición 0 da PQ(203) ± 2 códigos de 16 bits; con +2 EV, un parche al 100 % del sensor da PQ(812) ± 2 y no PQ(203); **el modo SDR da el mismo archivo que hoy** (pruebas actuales intactas). Compila sin advertencias y el AOT publica sin advertencias |
| 2. Leer el intermedio | `codex/hdr-d2-lectura` | ChatGPT (codex) escribe las pruebas, Gemini (agy) las revisa en lectura | Pruebas de la cadena de lectura con un FFV1 `yuv444p16le` PQ BT.2020 hecho con el ffmpeg del entorno en la propia prueba, sin depender de la fase 1. Si alguna falla, el arreglo va aquí | `VideoDecoder` da `VideoColor` {BT.2020, completo, PQ, primarios BT.2020} y pasa por P016; en una secuencia HDR el parche de 812 nits llega al compositor a 4,0 ± 1 % y, con «Exposición» −2 EV en Color, sale a 1,0 sin recortar (con el ProRes SDR sale a 0,25: la diferencia es la prueba de la fase); en una secuencia SDR pasa por el mapeo de la fase A sin errores (WARP) |
| 3. Proxy y reproducción | `devin/hdr-d2-proxy` | Devin | FFV1 > 10 bits ≥ 1080p pide proxy; prueba de que el proxy de una fuente PQ conserva sus etiquetas de color | `AutoProxyTests` con FFV1 de 16 y de 8 bits; `ProxyGenerationTests`: ffprobe del proxy de un FFV1 PQ da `smpte2084` y `bt2020` (hoy ya pasa con OpenH264; la prueba lo fija también para NVENC donde haya GPU) |
| 4. Diálogo, cifras y documentos | `claude/hdr-d2-dialogo` | Claude (diálogo y medición), Devin (CHANGELOG y tablas), Gemini (revisión de lectura de los documentos) | ComboBox «Intermedio» con su valor por defecto; MB por fotograma en la notificación; `hdr.md` (parte 2 hecha, tabla de cifras del RTX 3050 y sin GPU), `decisiones.md` (D-065), `CHANGELOG.md`, `pruebas-a-mano.md` (qué mirar: luces de una toma real a +2 EV en una secuencia HDR y en una SDR) | Autoprueba del exe (`--autoprueba`) verde; las cifras de la sección 2 medidas en el RTX 3050 y en una máquina sin GPU (por LibRaw) y escritas en `hdr.md`; revisión de bloque de 1–4 por otro modelo antes de unir el último PR |

Tamaño: fase 1 M (unas 150 líneas más pruebas), fases 2 y 3 S, fase 4 S. Cada fase es un PR con CI verde, unido con squash.

## 4. Riesgos y preguntas

**Riesgos:**
- **Tamaño.** Unas 5–8 veces el ProRes HQ, con el riesgo de llenar el disco a mitad de la importación. Hoy un fallo de ffmpeg ya borra el `.rodaje-tmp`, así que no queda basura. Si la fase 4 mide más de 30 MB por cuadro real, se añade un aviso de espacio libre antes de empezar.
- **swscale recorta el float.** Si una versión futura de ffmpeg o un cambio en la tubería vuelve a `rgbaf16le`, el +2 EV se pierde sin error. Lo protege la prueba del parche de la fase 1.
- **Croma.** El archivo es 4:4:4, pero al subirlo a la GPU baja a 4:2:0 (límite conocido de la fase B, `FrameUploader` → P016). No es nuevo y no se arregla aquí.
- **En una secuencia SDR**, un PQ toma por defecto un blanco de referencia de 100 nits (`hdrReferenceNits` = 0), no de 203: el intermedio HDR se ve un paso más brillante y comprimido arriba que el SDR, hasta que se pone 203 en «Brillo de referencia HDR». Por eso el modo por defecto sigue a la secuencia activa.
- **Proxy de 8 bits en PQ.** Conserva las etiquetas (medido), pero en 8 bits PQ hace bandas en los degradados: sirve para cortar, no para juzgar el color. Es lo mismo que ya pasa con cualquier HEVC PQ de cámara.

**Preguntas para Daniel** (entre paréntesis, lo que se hace mientras no conteste):
1. ¿Vale el intermedio de **16 bits FFV1** (unos 40 GB por minuto en 4K) para cumplir el criterio, o se prefiere **ProRes 4444 XQ de 10 bits en PQ** (unos 12 GB por minuto, se reproduce sin proxy, pero solo 433 niveles en la parte SDR, menos que los 878 de hoy)? *(FFV1 16 bits: es lo único que cumple «más de 2000 niveles» y no pierde nada.)*
2. ¿El modo por defecto sigue a la secuencia activa (HDR si es HDR), o el HDR siempre? *(Sigue a la secuencia: una secuencia SDR no cambia ni un byte.)*
3. ¿Hay CinemaDNG real para medir (de tu cámara o muestras públicas con licencia clara)? Descargarlas pide tu permiso. *(Mientras tanto se usa el sintético de `SyntheticDng`, y las cifras de tamaño se marcan como sintéticas.)*
4. ¿Se plantea después una **fase E: RAW en la línea de tiempo** (revelar al vuelo con ajustes por clip, formato 16), como Resolve? *(Queda anotada en `hdr.md` como idea, sin plan.)*
5. ¿Hace falta elegir otros primarios (P3) o una curva HLG para el intermedio? *(No: PQ BT.2020 cubre las dos secuencias HDR, porque el compositor trabaja en lineal y la salida HLG se codifica al exportar.)*

## 5. Decisión propuesta

**D-065. HDR, fase D, segunda parte: intermedio FFV1 de 16 bits en PQ BT.2020, opcional, en lugar de ProRes 4444 XQ** (15 de noviembre de 2026; propuesta de Claude, pendiente de Daniel; después de la 1.0.3).
- **Contexto.** `hdr.md` pedía ProRes 4444 XQ de 12 bits con curva logarítmica, pero el ProRes de ffmpeg solo codifica 10 bits y ni 12 bits con ninguna curva que guarde luces dan los 2000 niveles del criterio.
- **Decisión.** «Importar CinemaDNG» ofrece un segundo intermedio, FFV1 `yuv444p16le` en MKV, PQ BT.2020 con 1,0 = 203 nits, convertido por `zscale` desde media precisión lineal en planos. Los reveladores (GPU y LibRaw) dan lineal BT.2020 con la exposición sin recortar arriba. El ProRes 422 HQ Rec.709 sigue siendo el de las secuencias SDR, sin cambios. No cambian el módulo, el decodificador, los shaders ni el formato del proyecto.
- **Consecuencia.** Los intermedios HDR pesan 5–8 veces más y se reproducen con proxy. La curva logarítmica propia se descarta (necesitaría una curva nueva en `VideoColor` y en los shaders sin ganar niveles). Revelar al vuelo queda como posible fase E.

## Revisión de otro modelo (Gemini, solo lectura, 15-nov)

Primer intento: agy entregó solo el cierre de cuatro líneas y perdió la lista. Era un defecto de la cabecera de la Cuadrilla, que decía «Termina con…». Arreglado en `mesa-agentes` (`main`, bfd3d76): la respuesta empieza por el entregable. El segundo intento dio seis hallazgos.

**Incorporados:**
- **La matriz se llama `Bt709ToBt2020`**, como en los shaders, no `Rec709ToRec2020`.
- **La paridad GPU/LibRaw en luces > 1,0 se prueba, no se supone.** LibRaw aplica la matriz cámara → BT.2020 en enteros y recorta en 65 535; la GPU usa la matriz compuesta en float con `saturate`. En teoría recortan igual (las dos antes de la ganancia), pero el criterio de la fase 1 ya lo exige con la tolerancia de D-059.
- **La fase 4 depende de la 1** (usa los tipos de `CinemaDngImport`); queda escrito.
- **`GbrPlanes` valida los tamaños:** 8 bytes por píxel a la entrada (rgba16f) y 6 a la salida, con excepción si no cuadran. Gemini decía «múltiplos de 6» a la entrada, que es un error suyo.

**Descartados:**
- *«`zscale` no admite `min=gbr` con `pin=2020`»* (alta). La cadena exacta está medida en el ffmpeg del entorno: blanco → 0,58068 y +2 EV → 0,72915, con las etiquetas correctas en ffprobe. El `format=yuv444p16le` final no sobra: fija la salida de la negociación.
- *«`AutoProxyRule` no mira `ffv1`»* (media). Es justo lo que añade la fase 3; el diseño ya lo decía.

## Revisión independiente (Claude, 16-nov)

Verificado en local: fases 1 a 4 cumplen su «Terminado cuando» (rampa 3030/2954 niveles, blanco y +2 EV en PQ, lectura a 4,0 y 1,0, proxy con PQ, autoprueba del exe). Arreglado en el PR de la revisión: el proxy de un MKV corto y la prueba de color en BT.2020 (ver `hdr.md`).

**Tareas que quedan:**
- **Las pruebas con `modulo-raw` no corren en la CI.** La CI no compila `libraw.dll`, así que `RawModuleAsync` devuelve nulo y las pruebas de la rampa, las luces y la carta de color terminan en verde sin ejecutar nada. Deberían marcarse como omitidas (salto dinámico de xUnit) o la CI debería traer `libraw.dll`, para que un verde signifique que se probaron.
- **WAV más corto que los DNG.** Con `-shortest`, ffmpeg recorta el video al audio (48 DNG y un WAV de 2 s a 23,976 dan 47 cuadros), pero la notificación cuenta los DNG. Viene de D-059 y pasa también con el ProRes.
- **Autoprueba inestable con el equipo cargado:** el paso de máscaras falló una vez («La estrella no recorta el clip») y pasó al repetirlo.
