# Audio: `Rodaje.Audio`

Salida de audio WASAPI en modo compartido, por eventos, que además da el reloj maestro de la reproducción. Es `net10.0-windows`, `IsAotCompatible`, y todo su COM es interop generado en compilación (`[GeneratedComInterface]`, `[LibraryImport]`), sin reflexión. Depende de [Reproducción](reproduccion.md) y la usa el [Motor](motor.md). El mezclador de pistas no vive aquí: es `AudioMixer` del motor, que implementa la fuente de muestras.

## Contratos (`AudioTypes.cs`, `AudioOutput.cs`)

- **`IAudioSampleSource.Read(Span<float>)`** entrega float32 estéreo intercalado a la frecuencia de salida y devuelve cuántos cuadros escribió. Se llama siempre desde el hilo de audio y no debe bloquear: lo que falte se rellena con silencio. `Seek(Time)` avisa de un salto.
- **`IAudioOutput`** ofrece:
  - `Clock`: el transporte se maneja con `Start`, `Pause` y `Seek` del reloj;
  - `HasDevice` y `FallbackReason`;
  - `Device`;
  - `Volume`, con rampa;
  - `Stats`: cuadros, underruns, errores de la fuente, reinicios del dispositivo y tiempos de espera de control;
  - el evento `DeviceChanged`, que llega por el ThreadPool, nunca desde el hilo de audio.
- **`AudioOutputOptions`** trae estos valores por omisión: 48 kHz, búfer de 20 ms, baja latencia preferida (periodo de 5 ms), `ExtraLatency` para Bluetooth y volumen.
- **`AudioOutput.Open(source, options)`** es la fábrica. Intenta [`WasapiOutput`](../../src/Rodaje.Audio/WasapiOutput.cs) y, si no hay dispositivo usable (`AudioDeviceUnavailableException`), devuelve una salida muda con `SystemClock` y el motivo en `FallbackReason`. Así un equipo sin audio reproduce video igual. Los demás errores no se tapan.

## Cómo funciona `WasapiOutput`

```mermaid
flowchart LR
    Src["IAudioSampleSource<br/>(AudioMixer)"] --> R["FrameRenderer<br/>bloques, silencio,<br/>rampa, historial"]
    R --> W["WasapiSession<br/>IAudioClient(3)"]
    W -->|"GetPosition + QPC"| M["DevicePositionModel<br/>cuadros oídos, monótono"]
    M --> C["DeviceAudioClock<br/>Position"]
    C --> V["PlaybackScheduler<br/>(lee, nunca empuja)"]
```

- **Un solo hilo de audio** ("Rodaje audio", prioridad alta, COM MTA, MMCSS "Pro Audio"). Todo el COM vive ahí. El hilo espera el evento del dispositivo o una orden y luego aplica controles, atiende avisos de Windows y llena el búfer.
- **Control sin candados compartidos.** `Start`, `Pause` y `Flush` del reloj llegan como órdenes numeradas que el hilo aplica y confirma (con un tope de 2 s, contado en `ControlTimeouts`). El hilo de alta prioridad nunca espera un candado del llamador.
- **Llenado.** `GetCurrentPadding`, `GetBuffer`, `FrameRenderer.Render` y `ReleaseBuffer`. [`FrameRenderer`](../../src/Rodaje.Audio/FrameRenderer.cs) lee en bloques de 1024 cuadros. Si la fuente se queda corta rellena con silencio (cuenta underruns), y si lanza da silencio sin tumbar el hilo. También aplica una rampa de volumen de unos 10 ms, adapta estéreo a mono o a más canales y guarda unos 0,5 s de historial.
- **Baja latencia.** Si la frecuencia del mezclador coincide y existe `IAudioClient3`, [`WasapiSession`](../../src/Rodaje.Audio/Interop/WasapiSession.cs) usa `InitializeSharedAudioStream` con el periodo mínimo. Si no, usa `Initialize` compartido con conversión automática cuando hace falta remuestrear. Los HRESULT se traducen a mensajes en español: Audiosrv detenido (pasa en AtlasOS), modo exclusivo, dispositivo desconectado...

## El reloj maestro

[`DeviceAudioClock`](../../src/Rodaje.Audio/DeviceAudioClock.cs) es un `SampleCountingClock`. Su posición es el destino del último seek más los cuadros **oídos**, que da [`DevicePositionModel`](../../src/Rodaje.Audio/DevicePositionModel.cs):

- Toma cada lectura de `IAudioClock::GetPosition` con su marca QPC e interpola con el reloj de pared entre lecturas. El resultado es suave y monótono.
- La interpolación tiene dos topes: lo ya escrito y un máximo de extrapolación (búfer + periodo + 5 ms), por si el dispositivo se atasca.
- Una lectura que retrocede sostiene el valor, y tras `Start` ignora la posición repetida hasta que el dispositivo se mueve de verdad.
- Descuenta `ExtraLatency`.

Un seek (`DoFlush`) detiene y vacía el cliente, avisa a la fuente y abre un segmento nuevo con discontinuidad.

## Pérdida o cambio de dispositivo

Un `IMMNotificationClient` marca el aviso y despierta el hilo, y cualquier HRESULT de error al renderizar lleva a `Reopen`:

1. Cierra la sesión, suma `DeviceRestarts` y pasa a **modo libre**: el reloj sigue con el reloj de pared, sin parar ni saltar, y la fuente se consume a ese ritmo.
2. Intenta abrir el dispositivo predeterminado (como mucho 3 reaperturas anidadas) y, sin dispositivo, reintenta cada segundo o con cada aviso de Windows.
3. Al volver, repite desde el historial lo que estaba en cola sin oírse y continúa el segmento sin discontinuidad.
4. Avisa con `DeviceChanged`. Un cambio del dispositivo predeterminado también reabre.

`Dispose` detiene el hilo con 4 s de tope; si el controlador lo dejó colgado, no cierra los handles a propósito.

## Volumen y fundidos

El volumen lo aplica `MixRenderer` del motor, antes del limitador y de los medidores, con el `GainPlan` de cada clip:

- **Ganancia en dB:** la del `rodaje.audio` del clip (keyframes en tiempo del origen) más la de la pista (en tiempo de la secuencia). −60 dB o menos es silencio.
- **Bloques de 64 muestras** alineados a la línea de tiempo: la ganancia se evalúa en los bordes y se interpola en línea recta; los fundidos de potencia constante (sin(πt/2) al entrar, cos(πt/2) al salir) van exactos por muestra.
- **Transiciones de audio:** los dos clips suenan a la vez, el que sale alargado media duración y el que entra adelantado otra media, con fundidos complementarios; una transición reemplaza el fundido propio de ese lado. Lo que no existe antes del principio del medio es silencio.
- **Cambios sin saltos.** Tras una edición la mezcla se rehace desde 30 ms por delante de lo que ya se leyó; cada clip que sonaba parte de la ganancia que tenía y llega a la nueva a 4 dB por bloque (60 dB en 20 ms).
- **Medido** (`AudioGainTests`): −6 dB = 0,5012; error de los fundidos 3·10⁻⁸; un cruce de ruido rosa conserva la potencia dentro de 0,33 dB (ventanas de 0,5 s); el mayor salto entre muestras al cambiar el volumen es 0,079 dB; el cambio se oye a los 50,7 ms de pedirlo, más el búfer del dispositivo.

## Velocidad (`Timeline/Retime/`, fase 5c)

Un clip con velocidad llega a la mezcla con `AudioClipPlan.Retime` (`AudioRetime`: su `ClipTimeMap` y si mantiene el tono); a 1× sin reversa ni congelado es nulo y el clip va por el camino rápido de siempre. `MixRenderer` lo lee con [`RetimedReader`](../../src/Rodaje.Engine/Timeline/Retime/RetimedReader.cs), que vive en el lector del medio mientras muestre el mismo origen en cada instante (`ClipTimeMap.SameCourse`, y el mismo paso de WSOLA): una edición en otra parte o dividir el clip, con velocidad constante o con curva, no cambian ni una muestra. Al pasar a otro trozo, `Adopt` cambia el recorrido sin rehacer la cadena. Los trozos seguidos de un clip dividido, sin transición entre medias, comparten en el plan el mapeo de todos juntos (`AudioRetime.Joined`, con `ClipTimeMap.Join`): la lectura adelantada sigue la curva y no la velocidad del borde de cada trozo. Todo el código es propio (nada de rubberband ni GPL).

- **Posición atada al mapeo.** [`SampleMap`](../../src/Rodaje.Engine/Timeline/Retime/SampleMap.cs) da el origen de cada muestra con `ClipTimeMap.ExtendedSource`, exacto cada 64 muestras e interpolado entre ellas: la deriva es 0 por construcción y una curva no necesita nada aparte.
- **Mantener tono: WSOLA** ([`Wsola`](../../src/Rodaje.Engine/Timeline/Retime/Wsola.cs)). Trozos de 1024 muestras (2048 por encima de 4×) con Hann, solapados a la mitad; el trozo `k` sale en `k · 512` de la línea de tiempo y su centro se busca a ±256 muestras de `map(k · 512 + 512)`. Búsqueda gruesa sobre la mezcla mono diezmada ×4 con `Vector<float>`, afinada a la muestra y con el pico interpolado (parábola): el trozo se lee en esa posición con fracción con un sinc de 32 coeficientes. Sin la fracción cada empalme de un tono redondea la fase hacia el mismo lado y el tono se desafina (439,6 Hz en lugar de 440 a 2×). El fundido conserva la potencia según la correlación de los dos trozos. **Ataques tras silencio:** si el trozo anterior acaba en silencio (por debajo de −60 dBFS) no hay nada que continuar, así que el trozo se coloca para que el primer sonido a su alcance suene justo donde manda el mapeo, entra sin fundido y los trozos siguientes lo continúan tal cual durante 25 ms; si el ataque toca a un trozo posterior, este termina antes. Antes el ataque salía primero en la cola de un trozo y la búsqueda lo recolocaba: a 0,5× llegaba 5 ms tarde y a 0,25× 16 ms. Tras un salto la cadena arranca 250 ms antes y recuerda sus últimos 128 trozos, así que volver a mezclar lo recién oído repite exactamente lo mismo. Por encima de 8× sigue sonando, granuloso (decisión de Daniel; el inspector avisa).
- **Sin mantener tono: cinta** ([`VarispeedResampler`](../../src/Rodaje.Engine/Timeline/Retime/VarispeedResampler.cs)). Sinc enventanado con Kaiser (β 8,6), 32 coeficientes y 256 fases, sin estado. Por encima de 1× el corte baja con la velocidad y el filtro se estira hasta ×4; las tablas se calculan una vez por velocidad (1/16 de octava) y se comparten.
- **Lectura del medio** ([`SourceWindow`](../../src/Rodaje.Engine/Timeline/Retime/SourceWindow.cs)): planos L/R; decodifica seguido mientras el salto sea menor de 250 ms y busca si es mayor; solo mueve memoria cuando se llena.
- **Ganancia y congelado.** Los keyframes de volumen del clip se evalúan en el origen del mapeo (van con el material); un congelado es silencio. `AudioPlan.SoundsLike` compara también la velocidad y «mantener tono».
- **Reversa (5c.3).** `SampleMap` da la posición en el sentido de reproducción y `SourceWindow` lee hacia atrás por bloques de 0,5 s. Medido con un AAC decodificado por FFmpeg ([`AudioReverseMediaTests`](../../tests/Rodaje.Engine.Tests/Timeline/AudioReverseMediaTests.cs)): a 1× la salida es la decodificación seguida leída al revés muestra a muestra (error máximo 3,7·10⁻⁶, igual en el último segundo: deriva 0); manteniendo el tono a 0,5× y 2× el tono queda en 440,000 ± 0,001 Hz, y como cinta se mueve con la velocidad (220 y 660 Hz); el mayor salto entre muestras es 1,004–1,007 veces el del seno ideal, sin chasquidos en las uniones de bloque.

Medido (`AudioRetimeTests`, 11-oct; el tiempo en Release, CPU del hilo con la máquina cargada):

| Criterio | Pedido | Medido |
|---|---|---|
| Tono de 440 Hz a 0,5×, 0,75×, 1,5×, 2× y 4× | ± 0,5 Hz | 440,000 Hz en todos |
| THD+N de 997 Hz, 0,5×–2× | ≤ −35 dB | −75,5 a −82,0 dB (`atempo`: −29,1 a −38,5 dB) |
| Cinta: 1 kHz a 2× | 2 kHz ± 1 Hz, espurias ≤ −60 dB | 2000,000 Hz, −147 dB (0,5×: −87,8; 1,5×: −92,8) |
| Cinta: 18 kHz a 2× (se plegaría a 12 kHz) | — | −89,7 dB |
| Ruido rosa a 0,5×, 1,5× y 2× | RMS ± 0,5 dB | +0,28, +0,01 y −0,09 dB |
| Chasquidos (seno de 440 Hz, 0,5×–8×, curva 1×→3×→0,5×) | salto ≤ 1,1× el ideal | 1,000–1,001× |
| Tren de clics a 1,5× durante 60 s | número y posición ± 5 ms | 900 de 900, error máximo 0,00 ms, sin deriva |
| Chirp de cada segundo (`AudioCode`), 8 fases | error medio ≤ 1 ms | 0,25×: +0,31; 0,5×: +0,10; 0,75×: +0,04; 1,5×: −0,04 ms (cinta: 0) |
| Tras un seek (voz sintética con pausas) | correlación ≥ 0,999 pasados 250 ms | 1,00000 (también en el mismo instante del salto) |
| Dividir un clip a 1,5× | — | idéntico muestra a muestra |
| Dividir en tres un clip con «Montaje», «Bala» (con tono) o «Entrada rápida» (sin tono) | — | idéntico muestra a muestra |
| Bloque de 1024 cuadros estéreo | ≤ 0,15 ms | 0,036–0,10 ms (WSOLA 0,06–0,10; cinta 0,04–0,09) |
| Cambio de velocidad reproduciendo (1× → 2×), hasta que se oye | ≤ 60 ms | 30,0 ms de la mezcla + 20 del búfer = 50 ms, con y sin mantener tono: es el margen de 30 ms que la mezcla conserva tras cada edición |

Con un tono puro sostenido, un seek en frío puede dejar la salida con otra fase que la reproducción continua (WSOLA encadena cada trozo con el anterior); no se oye y se corrige sola en la primera pausa (tras silencio el trozo no depende de los anteriores).

## Medidores de nivel (`Metering/`)

La mezcla se calcula hasta 500 ms por delante de lo que suena, así que medirla al producirse daría los picos medio segundo antes de oírlos. Por eso cada bloque medido lleva la muestra de la línea de tiempo donde empieza, y quien dibuja pide los niveles de la posición que da el reloj del dispositivo.

```mermaid
flowchart LR
    M["MixRenderer<br/>(hilo de mezcla)"] -->|"mezcla limitada + sin limitar"| A["AudioLevelAnalyzer<br/>bloques de 128 cuadros"]
    A -->|"LevelBlock(Start, picos, pico real, Σx², saturó)"| R["AudioLevelRing<br/>4096 bloques, sin candados"]
    C["Reloj del dispositivo<br/>(posición que suena)"] --> P["PlayerEngine.TakeAudioLevels<br/>(hilo de la interfaz)"]
    R --> P --> B["MeterBallistics → MetersView"]
```

- **`AudioLevelAnalyzer`** mide por bloques de 128 cuadros (2,7 ms): pico por canal, suma de cuadrados para el RMS, pico real con [`TruePeakDetector`](../../src/Rodaje.Audio/Metering/TruePeakDetector.cs) (sobremuestreo ×4 con sinc enventanado de 16 coeficientes por fase, 48 multiplicaciones por muestra) y si la suma pasó de 0 dBFS antes del limitador suave. Un salto de posición (seek o una edición que se vuelve a mezclar) cierra el bloque y reinicia el pico real.
- **`AudioLevelRing`** tiene un escritor (el hilo de la mezcla) y lectores en cualquier hilo, sin candados ni reservas: cada entrada lleva un número de secuencia que el escritor invalida antes de escribirla y publica después, y el lector descarta lo que cambió mientras lo copiaba. Como tras una edición o un seek la mezcla vuelve a escribir posiciones, el lector recorre de lo más nuevo a lo más viejo y de cada bloque toma solo lo que no tapan los más nuevos.
- **`TimelineSession.MeasureAudio(desde, hasta, ventanaRms)`** da los picos de `(desde, hasta]` y el RMS de la ventana; se puede llamar desde cualquier hilo. `PlayerEngine` publica en cada vuelta del render la posición del reloj con su marca de tiempo, y la interfaz la extrapola al instante en que dibuja.
- **`MeterBallistics`** (subida instantánea, caída configurable de 20 dB/s, pico retenido 1,5 s, saturación que queda encendida) y **`MeterScale`** (deflexión IEC 60268-18) son lógica pura para la vista.

Con un dispositivo simulado (búfer de 20 ms) y los chirps de `AudioCode`, el pico aparece entre 1,4 ms antes (un bloque) y 0,6 ms después de que la señal pasa el umbral en la posición del reloj, también con un AAC real; la mezcla iba unos 300 ms por delante (`AudioLevelSyncTests`).

## Sonoridad y «Normalizar» (EBU R128)

- **Cuentas comunes** ([`Metering/Loudness.cs`](../../src/Rodaje.Audio/Metering/Loudness.cs)): `KWeightingFilter` (BS.1770-4 a 48 kHz), `Loudness.Gated` (puertas de −70 LUFS y relativa) y `LoudnessIntegrator`. Las usan el medidor de la exportación (`LoudnessMeter`) y los medidores en vivo.
- **En vivo.** `AudioLevelAnalyzer` suma también la potencia ponderada K de cada bloque (`LevelBlock.Weighted`); `AudioLevelRing.WeightedPower` y `TimelineSession.MeasureLoudnessPower` dan la momentánea (400 ms) y la corta (3 s) de lo que suena. `MetersView` muestra la corta y la integrada desde el último clic (un bloque de 400 ms cada 100 ms mientras suena) y en la ayuda la momentánea, la integrada y la ganancia maestra; el pico real ya estaba.
- **Por clip.** «Normalizar» en la sección Audio del inspector (−14, −16, −23 LUFS u otro): `OfflineAudio.MeasureClip` mide el clip solo (su volumen y su velocidad; sin pista, fundidos, transiciones, ganancia maestra ni limitador) y `Edits.ShiftGain` sube o baja su `gainDb` (valor y keyframes, así la envolvente conserva su forma) en un paso, para todos los clips de audio seleccionados.
- **Mezcla.** Menú contextual de los medidores: mide la secuencia con `OfflineAudio.MeasureLoudness` y fija `Sequence.MasterGainDb` (−60 a +24 dB, sin pasar de −1 dBTP) con `Edits.SetMasterGain`. `MixRenderer` la aplica antes del limitador y de los medidores; un cambio llega con la misma rampa que el volumen de los clips (4 dB cada 64 muestras) y tras un seek ya está aplicada, igual que en la exportación.
- **Medido** (`AudioLoudnessTests`, señales de EBU Tech 3341): casos 1–5 integrados a −22,993/−32,993/−23,014/−23,014/−22,979 LUFS (tolerancia ±0,1), iguales fuera de línea y en vivo; momentánea y corta del caso 1, −22,99. Pico verdadero de fs/4 a −6 dBFS con fase 0/45/60/67,5°: −6,021/−5,908/−6,072/−5,997 dBTP (pedido −6,4 a −5,8). Un clip a −20 dBFS con +3 dB mide −16,993 y normalizado a −14 da −14,000; la mezcla a −26 normalizada a −16 da −16,003, y al quitar la maestra el mayor salto entre muestras es 0,983 veces el del seno.

## Ecualizador y compresor (`Processing/`)

- **Efectos** `rodaje.equalizer` (código 20: corte de graves de 12 dB/oct, estante de graves, campanas de medios y presencia, estante de agudos) y `rodaje.compressor` (21: umbral, relación, ataque, relajación, compensación y rodilla), sin keyframes, en clips y en pistas de audio. Preajustes «Voz clara», «Podcast» y «Música suave» en `AudioPresets` (núcleo): en la sección Audio del inspector para el clip y en el menú de la cabecera para la pista, en un paso; cada valor se ajusta en Filtros.
- **DSP propio** ([`Biquad`](../../src/Rodaje.Audio/Processing/Biquad.cs), fórmulas del «Audio EQ Cookbook» en forma directa transpuesta II con estado `double`; [`AudioEffectChain`](../../src/Rodaje.Audio/Processing/AudioEffectChain.cs): ecualizador y luego compresor de rodilla suave de Giannoulis, Massberg y Reiss, estéreo enlazado). Todo el estado está en un arreglo; `Configure` cambia los coeficientes sin tocarlo, así mover un control no reinicia los filtros.
- **En la mezcla.** `AudioClipPlan.Effects` va antes del volumen y los fundidos del clip; `AudioTrackPlan.Effects` sobre la suma de la pista (también en sus huecos, para las colas), antes de la ganancia maestra. `MixRenderer` guarda el estado de cada cadena al empezar cada lectura (64 lecturas); tras una edición `BufferedAudioSource.Rewind(alignToChunk)` vuelve a un borde de lectura y la cadena retoma el estado que tenía ahí: la vista previa sigue siendo la exportación muestra a muestra. Un seek a otro sitio empieza los filtros de cero, igual que una exportación que empieza ahí.
- **Medido** (`AudioEffectChainTests`, `AudioEffectsMixTests`): campana de +6 dB en su centro, pasa altos −3,01 dB en el corte y −12,3 una octava abajo, estantes con la mitad de la ganancia en su frecuencia; respuesta de 5 bandas medida con senos contra la función de transferencia en 25 frecuencias de 30 Hz a 16 kHz: error máximo 0,0001 dB. Curva estática (−20 dB, 4:1, rodilla 6 dB) de −50 a 0 dBFS: error 0,00000 dB; reducción a los 10 ms de ataque 4,747 dB (teórica 4,741) y a los 100 ms de relajar 2,759 (2,759). Vista previa (mezcla adelantada, lecturas de 17 a 2048 cuadros y dos recargas) frente al WAV exportado, 6 s con ecualizador y compresor en el clip y compresor en la pista: 0 muestras distintas de 576 000; sin alinear la recarga, 228 870. Un bloque de 1024 cuadros con 5 bandas y compresor: 0,30–0,45 ms en Release con la máquina cargada.

## Archivos

| Archivo | Qué hace |
|---|---|
| [`AudioTypes.cs`](../../src/Rodaje.Audio/AudioTypes.cs) | Contratos, opciones, estadísticas y excepciones |
| [`AudioOutput.cs`](../../src/Rodaje.Audio/AudioOutput.cs) | Fábrica con caída a la salida muda |
| [`WasapiOutput.cs`](../../src/Rodaje.Audio/WasapiOutput.cs) | Hilo de audio, control, llenado, reapertura y modo libre; `ProbeDefaultDevice` para decidir si corren las pruebas con hardware |
| [`DeviceAudioClock.cs`](../../src/Rodaje.Audio/DeviceAudioClock.cs) | El reloj sobre `IRenderTransport` |
| [`DevicePositionModel.cs`](../../src/Rodaje.Audio/DevicePositionModel.cs) | Posición oída suave, monótona y con segmentos |
| [`FrameRenderer.cs`](../../src/Rodaje.Audio/FrameRenderer.cs) | Bloques, silencio, rampa, canales e historial |
| [`Interop/Wasapi.cs`](../../src/Rodaje.Audio/Interop/Wasapi.cs) | Interfaces COM generadas, `LibraryImport` y estructuras |
| [`Interop/WasapiSession.cs`](../../src/Rodaje.Audio/Interop/WasapiSession.cs) | Abrir el dispositivo, formato, baja latencia y mensajes de error |

## Pruebas

[`tests/Rodaje.Audio.Tests`](../../tests/Rodaje.Audio.Tests) tiene dos niveles:

- **Sin hardware:** la caída a la salida muda, el reloj con un transporte falso (seek, pausa, reinicio y lecturas concurrentes monótonas), unos 14 casos del modelo de posición (jitter, atasco, retroceso, segmentos, modo libre) y el renderizador.
- **Con el dispositivo real** (`Hardware/WasapiHardwareTests`, en serie). Los atributos `[AudioDeviceFact]` y `[AudioToneFact]` de [`Skip.cs`](../../tests/Rodaje.TestMedia/Skip.cs) las omiten salvo con `RODAJE_PRUEBAS_LARGAS=1` (silencio) o `RODAJE_PRUEBAS_AUDIO=1` (un tono de 3 s). Miden:
  - deriva frente a QPC de 1000 ppm como mucho, residuo de 3 ms como mucho y 0 retrocesos;
  - pausa, seek y volumen;
  - pérdida del dispositivo sin saltos de más de 30 ms.

## Aceptación de la fase 2

[`tests/Rodaje.Acceptance.Tests`](../../tests/Rodaje.Acceptance.Tests) se corre con `Probar.bat aceptacion`, que pone `RODAJE_PRUEBAS_LARGAS=1` y lanza las suites una tras otra.

- `FrameExactSeekTests`: seek exacto al cuadro en H.264 y HEVC, por D3D11VA y por software, con `GetFrame` y con `PlaybackScheduler.SeekToFrame`. El número de cuadro va dibujado en la imagen.
- `AvDriftTests`:
  - 10 minutos con reloj manual (nominal y ±0,1 %), con marcas de audio por segundo;
  - 30 s en tiempo real con el dispositivo a volumen 0;
  - con `-Completa` (`RODAJE_PRUEBAS_COMPLETAS=1`), los 10 minutos en tiempo real.

Las medidas salen como líneas `MEDIDA clave = valor` que [`build/aceptacion.ps1`](../../build/aceptacion.ps1) resume por criterio. El resultado registrado: 2864 de 2864 seeks exactos y 0 cuadros de deriva en 10 minutos.
