# Linux y macOS, fases 5, 6 y 7: títulos, audio y módulos

Resultado de las fases 5 (títulos), 6 (audio) y 7 (módulos e IA) de D-066 ([propuestas/mac-y-linux.md](propuestas/mac-y-linux.md) §5), 16 y 17 de noviembre de 2026. En el equipo de Daniel no se instaló nada ni se corrieron pruebas en local (Daniel pidió no cargar el PC): todo se probó en GitHub Actions (Ubuntu 24.04 x64, macOS 15 arm64 y Windows).

## Fase 5: títulos

**Qué quedó.** `src/Rodaje.Text` arma el texto de un título fuera de Windows igual que el layout de DirectWrite que arma `TitleRasterizer` (mayúsculas, tamaño, ajuste a la caja, saltos por palabra y de emergencia, alineación, interlineado, espaciado entre letras, kerning y rasgos, respaldo de fuentes por familia y por letra) y lo parte en letras, palabras y líneas como `Segment`. Usa solo **HarfBuzzSharp 14.4.0.100** (la versión que trae Uno): una cara por archivo de fuente, sin fontconfig ni CoreText (las del sistema se buscan recorriendo sus carpetas). No toca `src/Rodaje.Render`, que estaba cerrando la fase 3.

**Cómo se comprueba.** Dieciséis casos de aceptación compartidos ([`tests/Rodaje.Text.Tests/TitleLayoutCases.cs`](../tests/Rodaje.Text.Tests/TitleLayoutCases.cs)) con dos fuentes OFL de prueba (Anton y JetBrains Mono, copiadas de `rodaje-fuentes`):

- en Windows, `Rodaje.Render.Tests` los rasteriza con el `TitleRasterizer` real y escribe cada unidad (inicio, fin, línea, palabra y rectángulo) en [`Maquetacion/`](../tests/Rodaje.Text.Tests/Maquetacion); esa es la referencia, y la prueba falla si DirectWrite deja de coincidir con ella;
- en los tres sistemas, `Rodaje.Text.Tests` maqueta con HarfBuzz y exige las mismas unidades, líneas y palabras y cada borde a ±1 px (±0,5 px en vertical).

**Resultado medido** (HarfBuzz en Linux frente a DirectWrite en Windows, CI del #592):

| Casos | Unidades, líneas y palabras | Diferencia horizontal |
|---|---|---|
| 15 sin respaldo por letra (saltos por ancho, saltos forzados y línea vacía, palabra partida, derecha, espaciado, con y sin kerning, mayúsculas, acentos, interlineado 1,6, monoespaciada con ligaduras, respaldo de familia, por palabras, por líneas) | **iguales** | **0,00 px** |
| respaldo por letra («Alfa Ωμέγα beta» con Anton, que no tiene griego) | iguales | los avances coinciden hasta la primera letra de respaldo, medidos desde el borde de la línea (la línea entera se corre 7 px porque el respaldo es otra fuente en cada sistema y cambia el centrado); desde ahí solo se comparan línea y palabra |

El alto de cada unidad, que DirectWrite da como la letra alrededor de la línea base, se igualó con las métricas de la fuente (coincide a ±0,005 px en los 15 casos). Corrida final del #592: `Rodaje.Text.Tests` **16 de 16 en Linux, en macOS y en Windows**, y la referencia de DirectWrite 16 de 16 en Windows. **Hecho cuando: cumplido.**

**Falta y por qué.**

- **Dibujo con Skia: sin hacer, pendiente de una pregunta de licencias** (abajo). La maquetación no lo necesita.
- **Unir con Render:** el `TitleRasterizer` de Windows sigue con su maquetación; el de Linux y macOS (fase 8) usará `Rodaje.Text`. Hasta entonces la lógica de `Segment` y `Arc` está en los dos lados.
- **Atajos** (comentados como `shortcut:` en el código): solo texto de izquierda a derecha; sin tramos con otro peso, cursiva o tamaño (DirectWrite cambia entonces a interlineado proporcional); fuentes variables en su instancia por omisión; sin USE_TYPO_METRICS se toma `hhea` sin comprobar contra Windows; las fuentes del sistema sin fontconfig ni CoreText. Cada uno lleva su caso cuando la interfaz de Linux dibuje esos títulos.

## Fase 6: audio

**Qué quedó.**

- **Linux (#591):** `PulseOutput` y `PulseCapture` sobre la **API asíncrona de libpulse** (`pa_mainloop` + `pa_stream`, también sobre PipeWire), con el contrato de WASAPI: reutilizan `FrameRenderer` y `DevicePositionModel`, el reloj sigue `pa_stream_get_time`, la pausa es `cork`, el salto y la reapertura crean un flujo nuevo, el cambio de sumidero predeterminado se sigue y la captura sella cada paquete en la base del reloj. `AudioOutput.Open` y `AudioCapture` los eligen en Linux.
- **macOS (#594):** `CoreAudioOutput` (la unidad de salida predeterminada de AudioToolbox, que pide el audio desde su hilo) y `CoreAudioCapture` (unidad HAL con la entrada activa), con el mismo contrato. El reloj sale de la marca de tiempo de cada pedido de CoreAudio (lo entregado menos lo que aún no suena), el hilo de control vigila el dispositivo predeterminado cada 50 ms y, al reabrir, el reloj corre libre antes de cerrar la unidad vieja (cerrarla mientras macOS cambia de dispositivo tarda unos 300 ms).

**Sin tarjeta de sonido en la CI.** En Linux, PulseAudio con **dos sumideros nulos** a 48 kHz, sin suspensión y con un flujo de ceros en cada uno (sin flujos, un sumidero nulo duerme y tardaba 1,1–1,6 s en atender uno nuevo); la captura lee el **monitor** del sumidero. En macOS el corredor ya trae «Apple Virtual Sound Device» y «Null Audio Device»; para el bucle salida→entrada se instalan **BlackHole 2ch y 16ch** solo en el corredor (GPL-3.0, ni se enlaza ni se distribuye; anotado en `LICENCIAS.md`).

**Cifras (Linux, sumidero nulo, CI del #594):**

| Prueba | Resultado |
|---|---|
| Reloj, 10 s | arranca a 1,25 ms del `Start`; deriva +283 ppm frente a `Stopwatch` (el sumidero nulo corre con el reloj del sistema); residuo máx. 1,39 ms (RMS 0,37); 0 retrocesos; posición final 10,002 s |
| Pausa y salto | pausa congelada; salto a 10 s y 20 s exactos; 0 órdenes sin confirmar |
| Cambio de sumidero (`pactl set-default-sink`) | aviso a los 6 ms; parada máx. 0,8 ms; salto máx. 0,44 ms; 1 reapertura |
| Pérdida simulada | sin salida el reloj corre libre (300,1 ms en 300 ms) y vuelve solo |
| Captura sincronizada (clic por el monitor) | el clic cae **+1,1 ms** de cuando el reloj marca 1,000 s |
| Latencia | medida de punta a punta (entregar el clic al servidor → capturarlo) **30,8 ms**; la que informa `pa_stream_get_latency`, 39,6–42,3 ms |

**Cifras (macOS 15 arm64, «Apple Virtual Sound Device» y BlackHole; CI del #594):**

| Prueba | Resultado |
|---|---|
| Reloj, 10 s | arranca a 29 ms del `Start`; deriva +0,1 ppm; residuo máx. 0,28 ms (RMS 0,01); 0 retrocesos |
| Captura sincronizada (clic por BlackHole) | el clic cae **+0,0 ms** de cuando el reloj marca 1,000 s |
| Latencia | medida de punta a punta **15,0–18,6 ms** en tres corridas (17,8 en la última); la que informa el dispositivo, 2,1 ms (salida) |
| Pérdida simulada | parada máx. 37 ms; vuelve sola |
| Pausa y salto | pausa congelada; tras el salto, 509,7 ms de reloj en 541,9 ms de pared (se compara con la pared medida: en el corredor `Thread.Sleep` se pasa más de 100 ms) |
| Cambio de dispositivo predeterminado (Apple Virtual → BlackHole 16ch) | parada máx. 40,7 ms; salto máx. 7,1 ms; 0 retrocesos; 1 reapertura |

**Hecho cuando: cumplido en los dos**, con sumidero nulo (Linux) y dispositivos virtuales (macOS); falta medir con hardware real (la RTX/Realtek del PC de Daniel en Linux por USB, un Mac real).

## Fase 7: módulos e IA

**Qué quedó.**

- **Formato del paquete (#590):** `paquete.json` gana `platform` (`win-x64`, `linux-x64`, `osx-arm64`), **solo con `formatVersion` 2**; un módulo del formato 2 lo lleva siempre y en el 1 está prohibido. Un editor ya publicado (entiende hasta el 1) **rechaza el paquete entero** con «actualiza el programa», y además no lo ve porque lee `indice.json`, que no cambia (fase 1). `PackageInstaller` rechaza un módulo de otro sistema. Los de Windows y el contenido siguen en el formato 1: nada cambia en Windows. 12 pruebas nuevas (`PlatformManifestTests`) en los tres sistemas.
- **Módulos por RID (#595):** `modulo-audio-ia`, `-voz`, `-subtitulos` y `-traducir` pasan a `net10.0` con los tres RID en el lockfile y el ejecutable del sistema que compila (como el eco). Sus paquetes de NuGet ya traen lo nativo de Linux x64 y macOS arm64: ONNX Runtime (CPU), Whisper.net (CPU; Metal en macOS) y LLamaSharp (CPU; Metal en macOS). En Linux la IA va en la CPU.
- **`modulo-vision` (#597):** DirectML en Windows como siempre; fuera de Windows el ONNX Runtime de CPU, con **CoreML en macOS** al pedir la GPU y CPU en Linux. Como los paquetes difieren, `VisionUnix` elige el lockfile (`packages.unix.lock.json`). Sus 19 pruebas pasan en Linux y macOS; ninguna corre un modelo (no hay modelos en la CI), así que **CoreML no está probado con un modelo real**.
- **`empaquetar-modulo.ps1` por sistema (#596):** en pwsh de Linux y macOS arma `<id>-<versión>-<rid>.paquete` con `platform` y el formato 2, el ejecutable sin `.exe` y sin los símbolos aparte de Native AOT; salta los módulos que no compilan para ese RID. En Windows arma lo mismo que antes.

**Cifras (CI):**

| | Linux | macOS |
|---|---|---|
| Pruebas de módulos (pasan / omitidas) | audio-ia 26/1, voz 152/6, subtítulos 11/3, traducir 53/2, visión 19/0 | iguales |
| Paquete de `modulo-audio-ia` armado, instalado con `PackageInstaller` y arrancado por el protocolo (`PackagedModuleTests`) | 18,9 MB; saluda con `ruido` y `detectar-voz`; ping 7,3 ms | 20,2 MB; ping 3,7 ms |

Las pruebas omitidas son las que necesitan los modelos o el corpus de `.entorno` (cientos de MB que la CI no baja); las mismas se omiten en la CI de Windows.

**Hecho cuando:** «un editor viejo no ve los paquetes de otro sistema»: **cumplido** (formato 2 + índice por sistema, con pruebas). «Pruebas de cada módulo en Linux y Mac»: **cumplido para audio-ia, voz, subtítulos, traducir y visión, sin modelos**; **RAW no** (abajo). Lo que necesita modelos no se probó fuera de Windows.

**Falta.**

- **`modulo-raw`:** LibRaw no publica binarios de Linux ni de macOS y el módulo lee estructuras de LibRaw 0.22 por desplazamientos medidos en Windows (con `long` de 32 bits); en Linux y macOS `long` es de 64. Hace falta compilar LibRaw 0.22.2 (LGPL) en la CI como el FFmpeg y medir los desplazamientos por sistema. Sin hacer.
- **Pruebas con modelos en Linux y macOS:** un flujo aparte (manual o nocturno) que baje los modelos con caché. `preparar-modelos.ps1` todavía usa `curl.exe`, `tar.exe` y rutas con `\`: hay que portarlo.
- **Índice del catálogo por sistema:** `distribuir.ps1 -SoloIndice` todavía arma solo `indice.json`; los de Linux y macOS van con la fase 10.
- **Firma:** los paquetes de Linux y macOS no se firman todavía (la firma de código es de Windows); los protege el índice firmado, como a los demás.

## Licencias

- **HarfBuzzSharp 14.4.0.100** (MIT) y su biblioteca nativa (HarfBuzz, «Old MIT»): en `LICENCIAS.md` y `docs/legal.md`. Todavía no se distribuye.
- **Fuentes de prueba** Anton y JetBrains Mono: OFL-1.1, con su `OFL-*.txt`; solo pruebas.
- **BlackHole** (GPL-3.0): solo en el corredor de macOS, no se enlaza ni se distribuye.
- Los módulos de Linux y macOS llevan las bibliotecas nativas de esos sistemas de los mismos paquetes (ONNX Runtime, Whisper.net, LLamaSharp: MIT, con whisper.cpp y llama.cpp MIT); las licencias no cambian respecto de Windows.
- **Pregunta abierta (se paró aquí):** la biblioteca nativa de **SkiaSharp** trae código de terceros con licencias propias según su `THIRD-PARTY-NOTICES.txt`: el decodificador gif (MPL 1.1/GPL 2.0/LGPL 2.1), FreeType (FTL o GPL 2.0), el **DNG SDK de Adobe con un acuerdo que incluye indemnización**, ICU, libjpeg-turbo, libpng, libwebp… Hay que confirmar qué partes van de verdad en `libSkiaSharp` de Linux y macOS y que cada una se puede usar bajo una licencia compatible con la GPLv3. Por eso `Rodaje.Text` no depende de SkiaSharp y el dibujo de títulos espera. **Afecta también a la interfaz Uno (fase 8)**, que trae SkiaSharp.

## Revisión de bloque

Se hizo al levantarse la pausa de la Cuadrilla, de solo lectura y con otro modelo: **ChatGPT** (gpt-6-astra) la fase 6 y **Gemini** (gemini-3.8-flash-high) las fases 5 y 7.

**Arreglado** (#600 y #601): reabrir el audio con tope (Resume → Start → Reopen podía entrar sin fin); el reloj se congela al cerrar la salida; la captura no deja salir excepciones del receptor y marca la discontinuidad tras un bloque perdido; el salto vacía la unidad de CoreAudio (`AudioUnitReset`); una operación de Pulse vencida se cancela y solo «hecha» vale; `ThisPlatform` da `linux-x64`/`osx-arm64` por sistema aunque la distribución informe otro RID; un sustituto UTF-16 suelto ya no rompe la maquetación; las fuentes del sistema de más de 20 MB no entran en el respaldo.

**Queda como tarea:**

- Captura de macOS: la unidad HAL no convierte la frecuencia; con una entrada a 44,1 kHz y pedido de 48 kHz puede no abrir. Leer el formato del dispositivo y remuestrear.
- El receptor de la grabación de voz escribe el WAV dentro del hilo de audio (CoreAudio): pasar los paquetes por una cola a otro hilo.
- CoreAudio convierte latencia y búfer del dispositivo con la frecuencia del cliente: con hardware a 96 kHz las duraciones salen dobles. Leer la frecuencia nominal del dispositivo.
- Pulse: `cork` no comprueba que el servidor aceptó la orden (solo que terminó).
- Las pruebas de transporte miden el reloj con silencio: falta una que capture una secuencia identificable a través de pausa, salto y cambio de salida.
- `modulo-traducir` queda en CPU fuera de Windows: el backend Vulkan de LLamaSharp para Linux (y Metal en macOS, que ya trae el de CPU) por probar.
- El bloque de RID se repite en seis `.csproj` de módulos y el paso de empaquetado en dos trabajos de `ci.yml`: unificar (`modules/Directory.Build.props`, acción compuesta).
- Fuentes del sistema: los `name` solo de Macintosh (plataforma 1) se ignoran; rutas registradas sin normalizar.

**No se aplicó:** que `<RuntimeIdentifiers>[^<]*` no cruza saltos de línea (en .NET una clase negada sí los incluye).

## Fallos encontrados por el camino

- Dos pruebas de Engine que comparan vista previa y exportación de audio (`AudioEffectsMixTests`, `CompressorMixTests`) fallaron una vez cada una en Linux con «muestras distintas» y pasaron al repetir: inestables, ajenas a estas fases.
- `AudioBufferList` de CoreAudio tiene 4 bytes de relleno antes del búfer; sin él se entregaban cero cuadros y el reloj de macOS no avanzaba (arreglado en #594).
- `-r win-x64` en el `restore` de un módulo con los tres RID da NU1004: los módulos con los tres RID salen del bucle de la CI que lo pasa.

## Preguntas para Daniel (se siguió con la recomendada)

1. **Licencias de SkiaSharp** (ver «Licencias»): ¿se revisa ya qué trae `libSkiaSharp` de Linux y macOS? Bloquea el dibujo de títulos y toca a Uno (fase 8). Recomendada: sí, antes de la fase 8.
2. **Módulos del formato 1 en Linux y macOS:** hoy el instalador los deja pasar (solo rechaza una `platform` distinta); no llegan por el catálogo porque cada sistema lee su índice. ¿Rechazarlos también a mano? Recomendada: sí, como «módulo de Windows», ajustando las pruebas de Packages que instalan módulos de ejemplo en Linux.
3. **BlackHole (GPL-3.0) en el corredor de macOS:** solo para probar; ¿de acuerdo? El corredor ya trae dos dispositivos sin bucle; sin BlackHole no se puede medir la captura sincronizada.
