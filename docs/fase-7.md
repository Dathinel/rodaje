# Fase 7: IA local y descarga de complementos

Plan del 11 de octubre de 2026, sobre la [propuesta de módulos](propuesta-modulos.md) aprobada (IA en C#, protocolo compartible, distribución base y completa) y las funciones «7 · L» de [pendientes.md](pendientes.md). Arquitectura en D-031. El paso 0 (protocolo, módulo de ejemplo y cifras) ya está hecho.

## Arquitectura

```
Rodaje.exe ─ ModuleManager (App) ─ ModuleProcess ─ named pipe (JSON) ─ ModuleHost ─ modulo-*.exe (ONNX Runtime, Whisper.net…)
           │                     └ SharedMemoryRegion (cuadros RGBA, máscaras, audio f32) ─────────────┘
           ├ Rodaje.Analysis: silencios, ritmo, ducking, estabilizar, color automático (sin modelos, en la app)
           └ GitHubReleaseDownloader (IPackageDownloader) ─ índice firmado ─ releases de Dathinel y Hugging Face fijado
```

- **Protocolo v1** (`src/Rodaje.Modules.Protocol`, sin referencias a Rodaje, sale a su repo con el segundo programa):
  - **Marco:** 4 bytes little-endian con el largo y un objeto JSON UTF-8 con `type`; máximo 1 MiB por mensaje. Cuadros y audio nunca van por la tubería.
  - **Mensajes:** `hello` → `welcome` (o `error` `version-incompatible`), `invoke`, `progress`, `result`, `error`, `cancel`, `ping`/`pong`, `log`, `shutdown`. Serialización con `JsonSerializerContext` (AOT), `type` en cualquier posición, campos desconocidos ignorados y tipos de mensaje desconocidos saltados (compatibilidad hacia delante).
  - **Versión:** el editor manda su rango `minProtocol`–`maxProtocol`; el módulo elige la mayor común o rechaza con un mensaje que nombra los dos rangos.
  - **Capacidades:** `welcome.module` trae id, versión, nombre, dispositivo y las funciones con sus entradas y salidas (`video`, `audio`, `bytes`), si se cancelan y si informan avance.
  - **Progreso:** fracción 0–1 y etapa; el anfitrión manda como mucho uno cada 50 ms (siempre el de 1 y los cambios de etapa).
  - **Cancelación:** el editor manda `cancel` y espera la confirmación (`error` `cancelled`) hasta 2 s; si no llega, da el módulo por colgado, corta y mata el proceso.
  - **Errores:** códigos estables (`unknown-function`, `invalid-parameters`, `busy`, `out-of-memory`, `buffer-out-of-range`, `cancelled`, `internal`…). Un error de función no cierra la conexión; un mensaje malformado, sí.
  - **Desconfianza:** el editor trata al módulo como no confiable: límites de largo, profundidad (32), cantidad y rango en cada campo, UTF-8 estricto, ids con las reglas de los paquetes, respuestas a llamadas inexistentes ignoradas, búferes fuera de las regiones del editor rechazados. Cualquier violación cierra la conexión, falla las llamadas con `ModuleException` y mata el proceso; el editor y el proyecto siguen.
  - **Memoria compartida:** las regiones las crea siempre el editor (`Local\Rodaje.Mod.<guid>`); el módulo las abre por nombre y solo escribe en los búferes de la llamada; cada acceso comprueba los límites contra la región real. Video `rgba8`/`bgra8`/`rgba16f`/`gray8` con ancho, alto y paso; audio `f32` intercalado.
- **Ciclo de vida y aislamiento** (ronda A): un proceso por módulo, abierto al primer uso y cerrado tras 2 min sin llamadas; tubería solo del usuario actual (`CurrentUserOnly` en los dos lados); **Job Object** con «matar al cerrar» para que ningún módulo sobreviva al editor; `ping` cada 5 s con una llamada en curso (sin respuesta en 10 s = colgado). Un modelo pesado a la vez: cada función declara `vramMb` y el `ModuleManager` reparte el presupuesto (la vista previa usa 3/8 de la VRAM; mientras corre un módulo de GPU pasa a proxies y Mitad, como al exportar).
- **La IA no edita** (regla de la propuesta): un módulo devuelve datos (segmentos, marcadores, rangos, keyframes, máscaras, audio) y la app los convierte en comandos normales del núcleo con vista previa antes de aplicar y un solo Ctrl+Z.
- **El editor decodifica:** los módulos no abren archivos del usuario ni traen FFmpeg. La app baja el audio (f32, 16 o 48 kHz) o los cuadros a la resolución de análisis (512–1024 px) a un anillo de regiones; el módulo devuelve resultados. Las máscaras se guardan en la caché del proyecto como video FFV1 gris y las usa la primitiva de máscara.
- **Sin modelo, en la app:** silencios, ritmo, ducking, estabilizar y color automático no necesitan IA ni licencias ajenas; van en un proyecto `Rodaje.Analysis` (y en shaders de `Rodaje.Render`), no como módulos (pregunta 1).
- **Descarga** (ronda B): `GitHubReleaseDownloader` implementa `IPackageDownloader`. Un **índice firmado** (`indice.json` + `indice.json.sig`, ECDSA P-256 con la clave pública dentro del exe; .NET 10 no trae Ed25519) en las releases de `Dathinel/rodaje-catalogo` lista cada paquete, módulo y modelo con versión, URL, SHA-256, tamaño y versión mínima de Rodaje. Las URL son `github.com/Dathinel/<repo>/releases/download/<tag>/<archivo>` (no gastan la cuota de 60/h de la API; se siguen las redirecciones sin fijar el host) y, para modelos grandes, `huggingface.co/<repo>/resolve/<commit>/<archivo>` fijado por commit. Se verifica el hash al vuelo, se reanuda con `Range`, se instala de forma atómica (temporal y renombrar, con la recuperación tras apagón que ya tiene el instalador) y un hash o firma que no coincide borra el archivo. «Instalar desde archivo» sigue. Construido en la ronda B: detalle, pasos para crear la clave y publicar en [descarga.md](descarga.md).
- **Distribución:** **base** (Rodaje.exe + FFmpeg LGPL, ~60 MB, sin módulos) y **completa** (base + paquetes + módulos + modelos por defecto, ~1,3 GB sin el modelo de traducción, que se baja aparte: un asset de GitHub no pasa de 2 GiB). Los dos zip los arma el mismo script que usa el gestor (`herramientas\distribuir.ps1`).

## Cifras del paso 0 (11-oct)

RTX 3050 Laptop / Ryzen 7 7435HS, `ProcessTests.Latency_and_shared_memory_throughput` (`TiempoReal`) con `Rodaje.Module.Echo` en otro proceso, tres corridas. Con el módulo publicado con **Native AOT** (`RODAJE_MODULO_ECO`, 3,3 MB, 0 advertencias):

| Medida | Resultado |
|---|---|
| Arranque del proceso y saludo | 31–33 ms (168–197 ms con JIT) |
| Ping de ida y vuelta | p50 83–118 µs, p99 127–246 µs |
| Llamada con JSON pequeño | p50 114–140 µs, p99 166–191 µs |
| Cuadro 1080p RGBA: escribir, copiar en el módulo y leer | p50 1,3–1,4 ms, p99 1,6–1,8 ms; **691–750 cuadros/s** (17–19 GB/s movidos) |
| Cuadro 1080p por referencia (sin tocar los datos) | p50 0,1 ms |
| Audio 48 kHz estéreo f32 en bloques de 1 s | 5300–7300× tiempo real |
| Cancelación confirmada (20 al azar) | mediana 0,6 ms, máximo 4–19 ms |
| Módulo que muere a mitad de llamada | la llamada falla en <0,25 s con el código de salida |
| Módulo que ignora la cancelación | cortado y muerto tras el plazo (0,3 s en la prueba) |

**Conclusiones.** El protocolo no limita a ninguna función: un cuadro 1080p por referencia cuesta 0,1 ms y copiado 1,4 ms, frente a los 15–250 ms por cuadro de los modelos. No hace falta otro transporte. Las tuberías se crean con 64 KB de búfer: con el valor por defecto (0) una escritura espera a que el otro lado lea, y un editor que no lee colgaba al módulo (lo encontró una prueba).

## Ronda A: `ModuleManager` en la app (11-oct)

Hecho en `src/Rodaje.App/Services/Modules` (API en [app.md](arquitectura/app.md#módulos-de-ia-servicesmodules)). Pruebas en `tests/Rodaje.App.Tests/Modules` con el módulo eco como proceso real (22, estables en 5 corridas seguidas):

| Medida | Resultado |
|---|---|
| Módulo matado a mitad de «contar» | solo falla su trabajo; historial y proyecto (serializado) idénticos; la próxima llamada abre otro proceso |
| Módulo que muere solo (`morir`) | falla en <1 s con su código de salida |
| Módulo congelado (proceso suspendido, sin pong) | detectado y muerto en <2 s con plazos de prueba (en la app: ping 5 s, 10 s sin respuesta) |
| Cerrar el `ModuleManager` con una llamada en curso | 0 procesos de módulo vivos; cerrar el Job Object mata los que queden |
| Dos módulos pesados (1500 y 1200 MB, VRAM de 4 GB) | el segundo espera o cierra al ocioso; vista previa baja solo durante la llamada de GPU |
| Autoprueba del exe AOT | eco publicado con AOT, «contar» en Trabajos, propuesta aplicada en un paso y deshecha; 0 procesos al cerrar |
## Resultados del agente C (silencios, ritmo y ducking)

`src/Rodaje.Analysis` (sin modelos, AOT, solo depende del núcleo): analizadores por bloques que no guardan el audio (`LevelEnvelope`, `OnsetEnvelope`, `IVoiceActivityDetector`); las ediciones son `AnalysisEdits` con sus fábricas `Edits.RemoveSilences`, `AddBeatMarkers`, `CutToBeats` y `Duck`, cada una un paso del historial. Interfaz en `Views/Analysis` (diálogos con vista previa), menú del clip y Editar. Cifras en Release con señales sintéticas:

| Criterio | Resultado |
|---|---|
| Silencios ±20 ms | error máximo de borde 4,5–4,9 ms (silencio digital y con ruido a −60 dBFS) |
| 1 h en ≤10 s | 0,8 s (4500× tiempo real), sin contar la decodificación |
| Ritmo F ≥0,9 y ±1 BPM | clics a 90–160 BPM: F 0,991–1,000, tempo ±0,01; batería a 100–128: F 0,976–1,000, ±0,14 BPM. A 174 BPM sale 87 (la mitad) salvo que se suba la preferencia |
| Ritmo ≥100× | 291–348× tiempo real |
| Ducking: anticipación ≥30 ms | música abajo ≥30 ms antes de cada frase (bordes de voz con 0 ms de error sobre música filtrada a −30 dB); keyframes deshacibles en un paso |
| Voz ≥100× | 481–643× tiempo real |
## Subtítulos: cifras (agente E, 11-oct)

`modulo-subtitulos` (`modules/Rodaje.Module.Subtitles`, Native AOT 3,7 MB, sin JIT) con whisper large-v3-turbo q5_0 sobre 10,1 min de FLEURS es_419 dev (CC-BY-4.0, una voz por frase, 49 frases con 0,8 s de silencio entre ellas), RTX 3050 Laptop / Ryzen 7 7435HS. `SubtitleModuleTests` con `RODAJE_PRUEBAS_LARGAS=1`; corpus y modelo con `herramientas\preparar-modelos.ps1 -Corpus`.

| Medida | Resultado | Criterio |
|---|---|---|
| Velocidad en GPU (Vulkan) | 10,1 min en 31–56 s, **11–19× tiempo real** (varía con la temperatura del portátil) | ≤1,5 min ✅ |
| WER | **4,1 %** (1178 palabras) | ≤12 % ✅ |
| Inicio de la primera palabra de cada frase, solo DTW | mediana **120 ms** tarde | ≤150 ms ✅ |
| Lo mismo con el ajuste por energía del módulo | mediana 0 ms, p90 30 ms (la verdad sale de un detector parecido: la cifra independiente es la del DTW) | — |
| VRAM dedicada del proceso, pico | **1098 MB** | `vramMb` 1500 |
| CPU de respaldo (8 hilos) | **~1,1× tiempo real** (2,1 min en 117 s) | ≤8 min por 10 ❌ (daría ~9) |
| Cancelación a mitad | confirmada en 49–86 ms | ≤1 s ✅ |
| Proceso muerto a mitad | la llamada falla en 77–172 ms; un proceso nuevo transcribe | ≤1 s ✅ |

- **Tramos de ≤28 s** cortados en el silencio más hondo: con DTW, Whisper.net 1.9.1 deja de devolver segmentos a partir de la 2.ª o 3.ª ventana de 30 s de una misma llamada (sin error; igual en GPU y CPU). Cada tramo lleva como contexto el texto de los anteriores.
- **El DTW llega tarde a la primera palabra tras una pausa** (en la frase acierta). El módulo busca el arranque de la voz por energía entre el inicio por marcas y el DTW (`onsets: false` lo desactiva).
- **Flash attention** rompe el DTW (palabras a −1,3 s) y no acelera en CPU: queda apagada.
- **CPU:** para cumplir los 8 min habría que bajar a un modelo menor en CPU (medium o small q5) o aceptar ~9 min; decisión pendiente.

## Funciones, modelos y licencias

Velocidades estimadas a partir de cifras publicadas; las mide cada agente en el equipo de Daniel. ✅ viable con licencia limpia, ⚠️ viable con condiciones, ❌ sin opción limpia.

| Función | Dónde | Runtime | Modelo y licencia | Tamaño / VRAM | Velocidad esperada | |
|---|---|---|---|---|---|---|
| Subtítulos automáticos con palabras | `modulo-subtitulos` | Whisper.net (MIT) con Vulkan (solo el `vulkan-1.dll` del driver), CPU de respaldo | **whisper large-v3-turbo q5_0** (MIT, [openai/whisper](https://github.com/openai/whisper/blob/main/LICENSE)); palabras con DTW de whisper.cpp. Alternativa: Parakeet-TDT-0.6B-v3 (CC-BY-4.0, es/pt/en con palabras nativas) | ~550 MB / ~1–1,5 GB | 10–20× tiempo real en GPU, 1–3× en CPU | ✅ |
| Traducir subtítulos | `modulo-traducir` | LLamaSharp (MIT) con Vulkan y descarga parcial a CPU | **Qwen3-4B-Instruct-2507 Q4_K_M** (Apache-2.0). Alternativas: Opus-MT por par (CC-BY-4.0, ~300 MB, CPU), MADLAD-400-3B (Apache-2.0). NLLB-200 es NC ❌ | ~2,5 GB / no cabe entero con el editor: capas repartidas | 30–50 tok/s entero; ~10–20 con reparto | ✅ |
| Texto a voz (es, en, pt) | `modulo-voz` | ONNX Runtime (MIT) en CPU o DirectML | **Kokoro-82M** (Apache-2.0): es `ef_dora`, `em_alex`, `em_santa`; pt-BR `pf_dora`, `pm_alex`, `pm_santa`; en ~20 voces. Extra: Piper `es_ES-davefx` (CC0), `pt_BR-faber` (CC0), `es_MX-claude` (Apache-2.0), `en_US-libritts_r` (CC-BY-4.0). **Fonemizador propio en C#** (es, pt) y léxico misaki (Apache-2.0) para en: espeak-ng es GPL y no entra | ~90 MB (q8) + ~60 MB por voz Piper | >5× tiempo real en CPU | ⚠️ pt-PT sin voz limpia; Piper `lessac` y sus derivadas fuera (licencia Blizzard) |
| Reducción de ruido de voz | `modulo-audio-ia` | ONNX Runtime CPU | **DeepFilterNet3** (MIT/Apache-2.0), 48 kHz; RNNoise (BSD-3) como modo rápido | ~10 MB | tiempo real holgado en CPU | ✅ |
| Detección de voz (mejora silencios y ducking) | `modulo-audio-ia` | ONNX Runtime CPU | **Silero VAD v5** (MIT) | ~2 MB | >100× tiempo real | ✅ |
| Quitar silencios | App (`Rodaje.Analysis`) | DSP propio: RMS con histéresis, umbral y margen | — (con VAD si el módulo está) | — | 1 h en ≤10 s | ✅ |
| Ajuste al ritmo | App | Onsets por flujo espectral log-mel, tempograma y seguimiento por programación dinámica (Ellis 2007) | — (Beat This!, MIT según el repo original, como mejora si se confirma su LICENSE) | — | ≥100× tiempo real | ✅ |
| Ducking automático | App | Envolvente con ataque, liberación y anticipación sobre la voz (RMS o VAD) → keyframes de volumen en la música | — | — | inmediato | ✅ |
| Quitar fondo | `modulo-vision` | ONNX Runtime con DirectML | **MODNet** (Apache-2.0) para personas, por defecto; **BiRefNet_lite** (MIT) para calidad y objetos; estabilidad temporal propia (filtro guiado, EMA con movimiento, histéresis). RMBG (NC), RVM (GPL) ❌ | 25 MB / <0,5 GB; 180 MB / 1,5–2 GB | 60–100 fps (512 px); 4–8 fps (1024 px) | ⚠️ BiRefNet se entrenó con DIS5K: uso comercial con términos firmados |
| Reencuadre automático 9:16 / 1:1 | `modulo-vision` | ONNX Runtime con DirectML | **YuNet** (MIT) para rostros + **RT-DETRv2-R18** (Apache-2.0) para personas; seguimiento Kalman + IoU propio y trayectoria L1 sobre el clip entero. YOLOv8/11 (AGPL), SCRFD (NC) ❌ | <1 MB + ~80 MB / <1 GB | 30–60 fps | ⚠️ pesos COCO (CC-BY-4.0 en anotaciones, práctica habitual; se anota) |
| Estabilizar | App (`Rodaje.Render`) | Compute shader propio: Shi-Tomasi + Lucas-Kanade piramidal, RANSAC de similitud en CPU, suavizado L1, warp y recorte | — (Gyroflow GPL ❌; RAFT con pesos de datos de investigación, no) | — | análisis ≥60 fps 1080p | ✅ |
| Color y balance automáticos | App (`Rodaje.Render`) | Shades-of-gray (p≈6), gray-edge, white-patch al percentil 99 y niveles por percentiles en luz lineal, suavizado en el tiempo | — (Deep White-Balance NC ❌) | — | <1 ms por cuadro | ✅ |
| RAW | `modulo-raw` | P/Invoke a DLL nativas | **CinemaDNG con LibRaw** (LGPL-2.1, DLL dinámica) primero; BRAW (SDK gratuito, licencia del SDK por leer, API COM → capa C); R3D (DLL redistribuibles con condiciones de RED); ARRIRAW (programa de socios de ARRI); **ProRes RAW ❌** (sin SDK público para Windows) | — | — | ⚠️ |
| Codificadores GPL | `rodaje-codificadores-gpl` | `ffmpeg.exe` GPL por la tubería de la fase 6 (frontera de proceso, «mere aggregation») | BtbN FFmpeg-Builds fijado a un commit, recompilado, con COPYING y la fuente completa en el mismo release (GPL-3 §6d); nunca la variante `nonfree` | ~130 MB | — | ⚠️ patentes de H.264/HEVC en software: riesgo de quien distribuye |

**Runtimes.** ONNX Runtime con DirectML (MIT, ~37 MB) en todas las GPU sin instalar nada; DirectML está en mantenimiento (opset ≤20) y Windows ML es el sucesor, pero descarga sus proveedores desde Microsoft y pide Windows 11 24H2 (pregunta 4). CUDA no se redistribuye (cublasLt y cuDNN pasan de 1 GB). Whisper.net con Vulkan no necesita el CUDA Toolkit.

## Formatos

- **`paquete.json` de un módulo** (formato 1, campos nuevos compatibles: un programa viejo los ignora):

```json
"module": { "entry": "bin/modulo-vision.exe", "protocol": 1, "functions": ["quitar-fondo", "reencuadre"],
  "runtime": "directml", "vramMb": 1200,
  "models": [ { "id": "modnet", "file": "modelos/modnet.onnx", "size": 25888640, "sha256": "…", "license": "Apache-2.0",
                "url": "https://huggingface.co/<repo>/resolve/<commit>/modnet.onnx" } ] }
```

  `protocol: 0` marca una herramienta de línea de comandos sin este protocolo (el `ffmpeg.exe` GPL). Los modelos se guardan una vez en `complementos\modelos\<sha256>` y los comparten los módulos.
- **Índice firmado:** `{ "formatVersion": 1, "generated": "…", "entries": [ { "id", "version", "kind", "url", "sha256", "size", "minApp" } ] }` más `indice.json.sig` (ECDSA P-256 sobre SHA-256, DER en base64).
- **Resultados por función** (JSON en `result.value`; tiempos en segundos del medio, la app los lleva a ticks y los ajusta al cuadro):
  - `subtitulos` → `{ "language", "segments": [ { "start", "end", "text", "words": [ { "start", "end", "text", "p" } ] } ] }`
  - `traducir` → `{ "segments": [ { "id", "text" } ] }` (mismos ids y tiempos)
  - `voz` → audio f32 en el búfer de salida + `{ "sampleRate", "words": [ … ] }`
  - `quitar-fondo` → máscaras `gray8` en los búferes de salida, una por cuadro del lote
  - `reencuadre` → `{ "keyframes": [ { "t", "x", "y", "scale" } ] }` sobre la transformación del clip
  - `ruido` → audio f32 en el búfer de salida

## Interfaz

Panel IA de la biblioteca: cada función con su estado (no instalada, descargando, lista), «Instalar» descarga de verdad. Al aplicar: diálogo con opciones (idioma, voz, umbral…), progreso en Trabajos con etapa, tiempo restante y cancelar, y vista previa del resultado (subtítulos en la línea de tiempo atenuados, rangos de silencio marcados, trayectoria del reencuadre en el visor) antes de «Aplicar». Edición de subtítulos por palabras en el inspector (sobre los clips de texto de la 5b). Los errores del módulo se muestran en lenguaje simple, sin trazas.

## Reparto

Paso 0 hecho (protocolo, eco, D-031). Luego, en paralelo y cada uno en su worktree; mientras Daniel no apruebe los repos (pregunta 2), los módulos viven en `modules/` de este repo y salen después:

| Agente | Trabajo |
|---|---|
| A | `ModuleManager` en la app: procesos, Job Object, ocioso, `ping`, presupuesto de VRAM (`vramMb`), Trabajos, conversión de resultados en ediciones con vista previa |
| B | Descarga: `GitHubReleaseDownloader`, índice firmado, modelos con hash y reanudación, `herramientas\distribuir.ps1` (base y completa), gestor en Complementos |
| C | `Rodaje.Analysis`: silencios, ritmo y ducking, con su interfaz |
| D | Estabilizar y color/balance automáticos en `Rodaje.Render` y el inspector |
| E | `modulo-subtitulos` y la edición por palabras |
| F | `modulo-traducir` |
| G | `modulo-voz` con el fonemizador propio (es, pt-BR) y misaki (en) |
| H | `modulo-vision` (quitar fondo y reencuadre) y la máscara en el motor |
| I | `modulo-audio-ia` (ruido y VAD) |
| J | `modulo-raw` (CinemaDNG con LibRaw) y el empaquetado de `rodaje-codificadores-gpl` |

Unión 0 → A → B → C → D → E → F → G → H → I → J, suite completa y verificación por un agente de otro modelo (Sonnet). **Para la nube:** fuzz del protocolo (mensajes y marcos al azar en los dos sentidos), servidor HTTP local para probar la descarga (cortes, hashes y firmas malas, redirecciones), corpus de prueba propio (voz CC0 de Common Voice y ruido sintético), `LICENCIAS.md` de cada módulo con la fuente de cada modelo y voz, y `docs/protocolo-modulos.md` para terceros.

## Criterios de terminado

- 0 fallos, AOT sin advertencias en la app y en los módulos que lo admitan, `Rodaje.Modules.Protocol` ≥90 % de cobertura.
- **Protocolo** (cumplido en el paso 0): ping p50 ≤0,5 ms, cuadro 1080p copiado p50 ≤3 ms, cancelación confirmada ≤1 s, muerte del módulo detectada ≤1 s. Mensajes hostiles: 5000 mutados en el paso 0 y 10 000 con el fuzz de la nube, sin que el editor caiga con otra cosa que `ModuleException`.
- **Aislamiento:** matar o colgar un módulo a mitad de cualquier función deja la app y el proyecto intactos; al cerrar Rodaje no queda ningún proceso de módulo; la VRAM vuelve a ±50 MB tras cerrar el módulo.
- **Descarga:** 100 % de los archivos con hash o firma mala rechazados y borrados; reanuda tras cortar la red; un apagón simulado a mitad de la instalación no deja un complemento a medias.
- **Subtítulos:** 10 min de voz en español en ≤1,5 min con GPU (≤8 min en CPU); WER ≤12 % en el corpus; inicio de palabra con error mediano ≤150 ms.
- **Traducir:** 100 subtítulos es→en en ≤90 s, mismos ids y tiempos, líneas ≤42 caracteres.
- **Voz:** es, en y pt-BR con ≥2 voces cada uno; ≥5× tiempo real en CPU; el audio entra como clip y vuelve a generarse al cambiar texto o voz.
- **Ruido:** +10 dB de SNR en voz con ruido sintético; ≥10× tiempo real en CPU.
- **Silencios:** silencios sintéticos detectados con ±20 ms; 1 h analizada en ≤10 s.
- **Ritmo:** pulsos de pistas sintéticas a tempo conocido con F ≥0,9 y ±1 BPM.
- **Ducking:** la música baja antes de que empiece la voz (anticipación ≥30 ms) y queda como keyframes deshacibles.
- **Quitar fondo:** IoU ≥0,9 en el corpus de personas; parpadeo del borde entre cuadros a la mitad que sin el filtro temporal; vista previa ≥30 fps con MODNet.
- **Reencuadre:** el sujeto dentro del recorte en ≥98 % de los cuadros de los clips de prueba; ≥4× tiempo real.
- **Estabilizar:** movimiento residual −70 % en un clip con temblor sintético conocido; análisis ≥60 fps 1080p.
- **Color automático:** dominante sintética corregida a ΔE ≤3.
- **Todo resultado de IA** se ve antes de aplicarse y se deshace con un solo Ctrl+Z.

**Daniel, a mano:** subtítulos y traducción en un video propio, las voces en los tres idiomas, quitar fondo y reencuadre en un clip suyo, estabilizar un clip con temblor, y la descarga del gestor desde cero.

## Riesgos

- **VRAM de 4 GB:** la vista previa (3/8) más un modelo de 1,5–2 GB deja poco margen; BiRefNet_lite y el traductor no caben enteros con el editor. Por eso: un modelo a la vez, vista previa a proxies mientras corre y reparto de capas del traductor.
- **DirectML en mantenimiento:** se aísla tras una interfaz de sesión para pasar a Windows ML sin tocar los módulos.
- **ONNX Runtime y LLamaSharp con AOT:** sin soporte declarado; prueba de humo temprana y, si falla, el módulo sale autocontenido con JIT (pregunta 3).
- **Fonemizador propio:** el español es regular, el portugués medio; un error se oye. Corpus de palabras difíciles en las pruebas.
- **Licencias heredadas de datos:** COCO, DIS5K y los datos de Kokoro no son del todo públicos; se anotan por modelo y se revisan antes de publicar.
- **Patentes y GPL de `rodaje-codificadores-gpl`** y licencias de los SDK RAW: revisar antes de publicar (`pendientes-legales.md`).
- **Whisper.net y el DTW por palabra:** resuelto (ver «Subtítulos: cifras»): lo expone, pero con DTW deja de devolver segmentos tras la 2.ª o 3.ª ventana de una llamada; el módulo parte el audio en tramos de ≤28 s.

## Preguntas para Daniel (se avanza con la recomendada, provisional)

1. **Análisis sin IA en la app o como módulos.** Silencios, ritmo, ducking, estabilizar y color automático no usan modelos. **Recomendado: en la app** (pequeños, sin licencias ajenas, rápidos). El catálogo deja de ofrecer `modulo-ritmo` y `modulo-silencios` como descargas. Desventaja: la base crece unos cientos de KB.
2. **Repositorios de los módulos:** `modulo-subtitulos`, `modulo-traducir`, `modulo-voz`, `modulo-audio-ia`, `modulo-vision`, `modulo-raw`, `rodaje-codificadores-gpl` y `rodaje-catalogo`. **Recomendado: privados hasta publicar, con la licencia de la app** (GPLv3 con términos adicionales, D-032; antes se proponía la PolyForm Noncommercial; el permiso para complementos deja elegir otra) (los modelos conservan su licencia y van en `LICENCIAS.md`). Hasta que respondas se trabajan dentro de `modules/` de este repo; crearlos es el único paso que espera tu respuesta.
3. **Módulos con JIT si una biblioteca no admite AOT** (probable en LLamaSharp). **Recomendado: sí**, autocontenidos y recortados, solo esos; la app sigue AOT. Desventaja: arranque de ~200 ms en vez de ~30 ms.
4. **ONNX Runtime con DirectML ahora y Windows ML después.** **Recomendado: DirectML** (sin conexión, cualquier GPU, cabe en «sin instalador»). Windows ML sería ~50 % más rápido en NVIDIA, pero baja sus proveedores desde Microsoft al ejecutarse.
5. **Traductor por defecto:** Qwen3-4B (mejor, usa el contexto, muchos idiomas, 2,5 GB) u Opus-MT (300 MB por par, CPU, más literal). **Recomendado: Qwen3-4B**, descargado aparte (no entra en el zip completo); Opus-MT como opción ligera.
6. **Voz en portugués de Portugal:** no hay voz con licencia limpia. **Recomendado: solo pt-BR** en la v1 y buscar una voz CC0/CC-BY después.
7. **BiRefNet_lite (datos DIS5K):** su uso no comercial es libre; el comercial pide firmar términos. **Recomendado: incluirlo** como «Calidad alta» opcional, con MODNet por defecto, y anotarlo en `pendientes-legales.md`.
8. **Clave de firma del índice:** **recomendado:** ECDSA P-256 generada en tu PC, guardada fuera del repo con una copia de respaldo; se firma en local con un script. Si se pierde, la próxima versión de Rodaje trae otra clave pública.
