# Interpolación de fotogramas por flujo óptico

Diseño del bloque aprobado por Daniel el 15-nov para después de la 1.0.3. Decisión propuesta: **D-063** (si otro bloque de la 1.0.3 la toma antes, el siguiente número libre). Solo diseño: nada de esto está en el código.

## 1. Qué resuelve

Quien pone un clip de 30 cuadros/s a 0,25× ve cada cuadro repetido cuatro veces: la cámara lenta salta. Lo mismo pasa con una rampa de velocidad (5c.2) y al poner un clip de 24 en una secuencia de 30 (sin velocidad, el motor repite un cuadro de cada cuatro). Hoy `ClipSpeed.FrameSampling` guarda `nearest`, `blend` y `opticalFlow`, pero los dos últimos están reservados y se ven como `nearest` (`ClipSpeed.cs:64-73`, `formato-proyecto.md` § Velocidad).

**Cómo lo resuelven otros (solo producto y uso):**

| Programa | Qué ofrece | Lo que se toma |
|---|---|---|
| Resolve | «Proceso de reajuste» por clip: cercano, mezcla de cuadros, flujo óptico; calidad del cálculo (normal/mejorada) y un modo de IA aparte, más lento | Tres modos por clip y la IA como calidad aparte, no como cuarto modo |
| Premiere | «Interpolación de tiempo» por clip: muestreo, mezcla, flujo óptico; el flujo pide renderizar (barra roja/verde) | El modo se aplica también al ajustar la tasa de un clip de otra cadencia |
| Final Cut | «Calidad de video» de la velocidad: normal, mezcla, flujo óptico, calculado en segundo plano | El cálculo pesado en segundo plano con aviso en el clip |
| Shotcut (GPL) | Filtro de reasignación de tiempo con modo de imagen cercano o mezcla (MLT) | Nada más: no tiene flujo |
| Kdenlive (GPL) | Velocidad por clip con el cuadro más cercano (MLT `timewarp`) | — |
| FFmpeg `minterpolate` | Interpolación por compensación de movimiento (bloques) en la CPU | Se descarta: 1–3 cuadros/s en 1080p, fuera de la cadena de la GPU y sin control de oclusiones |

No se estudia ni se copia código GPL. La referencia del algoritmo clásico es el artículo de DIS (Kroeger et al., 2016, «Fast Optical Flow using Dense Inverse Search»), del que OpenCV tiene una implementación Apache-2.0: se lee la lógica, se escribe desde cero.

## 2. Clásico en la GPU frente a modelo de IA

| | Flujo clásico en compute shader (D3D11) | Modelo de IA por named pipe (ONNX con DirectML) |
|---|---|---|
| Algoritmo / modelo | DIS de dos sentidos (ida y vuelta) en pirámide, comprobación de consistencia, flujos al instante t y deformación de los dos cuadros | **RIFE 4.x** (Practical-RIFE: código MIT; pesos publicados en el mismo repositorio, entrenados con Vimeo-90K) es el único con t arbitrario, ligero y con exportaciones a ONNX probadas. FILM (código Apache-2.0) es 5–10× más lento y su exportación a ONNX no es directa. IFRNet, EMA-VFI y AMT: licencias de pesos por confirmar |
| Calidad | Buena en paneos, objetos rígidos y movimientos medios (< ~40 px por cuadro en 1080p); falla con desenfoque fuerte, transparencias y movimientos grandes (bordes con «goma») | Mejor en movimientos grandes y oclusiones; también falla en cortes de escena y texto fino |
| RTX 3050 (4 GB), 1080p | objetivo **≤ 4 ms** por cuadro intermedio (flujo a ¼ de resolución) y **≤ 12 ms** en 4K: entra en la vista previa en vivo | estimado 10–20 cuadros/s en fp16 con DirectML (a medir): no entra en vivo; se calcula y se guarda |
| Sin GPU (WARP) | objetivo ≥ 3 cuadros/s en 1080p al exportar; en la vista previa reproduciendo cae a «Mezcla» | 0,3–1 cuadro/s en la CPU: solo para clips cortos |
| Tamaño y VRAM | 0 MB de modelo; ≤ 64 MB de VRAM en 1080p (pirámides, dos flujos RG16F, dos RGBA16F), ≤ 200 MB en 4K | modelo de 10–25 MB; 0,6–1 GB de VRAM en 1080p, 4K a media escala (como hace RIFE) |
| Licencia | Código propio, GPLv3 de Rodaje; sin riesgo | Código MIT; pesos con datos de investigación (Vimeo-90K), el mismo aviso que BiRefNet en la fase 7 |
| Vista previa = exportación | Sí: el mismo shader con los mismos parámetros (con proxies, sobre el proxy) | Solo si el tramo ya está calculado; si no, la vista previa usa el clásico |

**Recomendación.** El modo `opticalFlow` es el clásico en la GPU: va en vivo, igual en la vista previa y en la exportación, sin descargas, y cumple el formato ya publicado. La IA es una **calidad aparte** («Flujo óptico IA»), opcional y posterior (fase 7 del bloque), como el modo de IA de Resolve: se calcula en segundo plano por el módulo y se guarda en la caché; mientras no esté, el clip se ve con el clásico.

## 3. Cómo encaja en Rodaje

### Comportamiento

- **Cuándo interpola:** clip de medio (no imagen fija, título ni compuesto), con `FrameSampling` distinto de `Nearest`, sin `Freeze`, y si el instante del origen cae entre dos cuadros (fracción t entre 0,002 y 0,998). También a 1× si la cadencia del clip no es la de la secuencia (24 en 30): para eso un clip a 1× tiene que poder guardar el modo, y hoy no puede: `TimelineEdits.Normalize` (`TimelineEdits.Speed.cs:709`) borra la velocidad cuando `IsIdentity` y `Offset` es 0, y copiar atributos desde un clip a 1× vuelve el muestreo a `Nearest` (`ClipAttributes.cs:365-373`). Se cambian esos dos sitios (`Normalize` conserva la velocidad si `FrameSampling` no es `Nearest`; copiar lleva el modo también a 1×); `IsIdentity` no se toca, porque lo usan el mapeo de tiempos, la sincronía de enlazados y las miniaturas.
- **Reversa:** el par va del cuadro i al i − 1 con t medido hacia atrás.
- **Por encima de 4×** el flujo no aporta y el desplazamiento se sale del rango de búsqueda: `opticalFlow` cae a `blend`.
- **Cortes de escena dentro del medio:** por cada par se mide la diferencia de histogramas de luma (64 cajas) y el residuo tras deformar en el nivel más bajo de la pirámide; si los dos pasan su umbral, el par es un corte y sale el cuadro más cercano (nunca la mezcla: un fundido de dos planos es peor que el salto). Se decide en la GPU (reducción a un valor en un búfer estructurado que lee la última pasada): sin lecturas de vuelta a la CPU.
- **Oclusiones:** comprobación de ida y vuelta (|F₀₁(x) + F₁₀(x + F₀₁(x))|² > α(|F₀₁|² + |F₁₀|²) + β, los valores de Sundaram et al.); el píxel tapado en un cuadro se toma solo del otro; donde fallan los dos, se mezclan con (1 − t, t). Lo que apunta fuera de la imagen cuenta como tapado de ese lado. Flujos al instante t con la aproximación lineal de Super SloMo (F_t0 = −(1 − t)t·F₀₁ + t²·F₁₀; F_t1 = (1 − t)²·F₀₁ − t(1 − t)·F₁₀).
- **Dónde se interpola: en la señal del origen, no en RGB.** El interpolador recibe los planos de A y B (NV12, P010 o P016) y devuelve un cuadro con **el mismo formato de planos y el mismo tamaño**: el flujo se calcula sobre la luma (el plano Y, sin convertir nada) y la misma deformación se aplica al plano de croma con el flujo a media escala. Así la capa interpolada es una `D3D11TexturePayload` como cualquier cuadro decodificado: `LayerRenderer.Usable` y el camino rápido `DrawPlain` la aceptan, la forma del píxel y el giro de la `LayerFrame` no cambian (`SourceGeometry`), y la conversión de color, el HDR (D-055) y «Expandir a HDR» se aplican después exactamente como hoy. La mezcla de oclusiones queda en la señal codificada (gamma en SDR, PQ o HLG en HDR), la misma regla que D-016 para SDR; en HDR la diferencia con mezclar en lineal solo se ve donde fallan los dos lados, y se acepta.
- **Transiciones y capas compañeras** interpolan igual (mismo plan). **Proxies:** se interpola lo decodificado (el proxy en la vista previa, el original al exportar), como con el resto de la imagen.

### Por capas

| Capa | Archivos | Cambio |
|---|---|---|
| Core | `Model/ClipSpeed.cs` | Se quitan los «Reservado» de `Blend` y `OpticalFlow`; propiedad nueva `FlowQuality` (`Standard`, `Ai`), opcional (fase 7) |
| Core | `Editing/TimelineEdits.Speed.cs` (`Normalize`), `Editing/ClipAttributes.cs` (copiar a 1×) | Un clip a 1× conserva un `FrameSampling` distinto de `Nearest` |
| Core | `Model/ModelValidator.cs`, `Serialization/ProjectJsonContext.cs` | Validar y registrar `FlowQuality` con `CamelCaseEnumConverter` (sin reflexión, como `FrameSampling`) |
| Engine | `Timeline/VideoPlan.cs` | `VideoClipPlan.SampleAt(Time) → (long A, long B, float T, FrameSampling Mode)`: el par, la fracción y el modo efectivo (cae a `Nearest`/`Blend` con las reglas de arriba); `FrameAt` sigue igual |
| Engine | `TimelineScheduler.Demand.cs`, `TimelineScheduler.Layers.cs` | Con t > 0 la capa espera los dos cuadros (A y B, ya cercanos en una cámara lenta, la caché por urgencia de D-017 los guarda) y arma la `LayerFrame` con los dos; si B falta al reproducir, muestra A (no se espera) |
| Engine | `Timeline/TimelinePlan.Video.cs` | Pasa el modo y la cadencia al plan |
| Engine | `Offline/OfflineTimeline.cs` (`Layer`, l. ~506-537), `Offline/OfflineReaders.cs` | La exportación arma sus `LayerFrame` por su cuenta: pide y retiene también B y rellena `Next`/`Fraction`/`Sampling`; si no, exportaría siempre el cuadro más cercano |
| Playback | `Composition/CompositionTypes.cs` | `LayerFrame` suma `Next` (`DecodedFrame?`), `Fraction` y `Sampling`. Ciclo de vida: quien la arma hace `Next.TryRetain()` (como con el cuadro principal en `TimelineScheduler.Layers.cs:~274`; si falla, la capa va sin `Next`) y `Dispose` libera `Next`; `HeldLayer` retiene el par en pausa y tras un seek |
| Render | **nuevo** `Interpolation/FrameInterpolator.cs`, `Interpolation/FlowShaders.cs` | De los planos de A y B a un cuadro de planos del mismo formato (ver «Dónde se interpola»), en texturas propias del interpolador (una por capa interpolada y refresco, devueltas al terminar el cuadro, contadas en el presupuesto de VRAM); los shaders pasan por `ShaderCache` como los de `ExportFrameConverter` y `ContentLightMeter` |
| Render | `Compositor.cs` (`DrawLeaf`, l. ~426) | Antes de `_layers.Usable(layer)`: si la capa trae `Next`, el cuadro se sustituye por el interpolado; `LayerRenderer` no cambia (la estabilización, el recorte y los efectos van después, sobre el cuadro interpolado) |
| Analysis | **nuevo** `Video/Interpolation/FlowReference.cs` | Referencia en la CPU del mismo algoritmo (DIS, consistencia, deformación, corte) sobre `GrayImage`/`Pyramid` existentes; corre en Linux y es la vara de las pruebas del shader (el patrón de `MosaicDeveloper` en D-059) |
| App | `Views/Inspector/Speed/SpeedPanel.xaml(.cs)`, `Services/Inspector/SpeedCommands.cs`, `SpeedTabValues.cs` | Desplegable «Cuadros intermedios» (ver Interfaz) |
| App | `Views/Timeline/Logic/SpeedBadge.cs` | Marca «≈» en el texto de velocidad del clip con mezcla o flujo, junto a la «◀» de la reversa |
| Export | — | Nada propio: la exportación compone con el mismo `Compositor` (`OfflineTimeline`) y hereda el interpolador |
| Módulos (fase 7) | `modules/Rodaje.Module.Vision` | Función `interpolar` (RIFE en ONNX, DirectML o CPU), el modelo declarado en `paquete.json` y descargado aparte como los demás |
| App (fase 7) | **nuevo** `Views/Analysis/InterpolationStore.cs`; **nuevo** `IInterpolatedFrames` en Engine | Caché `cache\interpolar\<clave>.mov` (ProRes 422 HQ por `FfmpegPipeSink`) más un índice `.ridx` de posiciones del origen (cuadro A, t cuantizado a 1/256): recortar o dividir el clip sigue encontrando sus cuadros; cambiar la velocidad no. Clave propia `InterpolationStore.KeyFor(huella, primer cuadro, cuadros, modelo)` con prefijo `rint1|`, al estilo de `StabilizationStore`. El motor los pide por un proveedor de cuadros propio que leen `TimelineScheduler` y `OfflineTimeline`, no por `IEffectContent` (que es de efectos y transiciones) |

**Fuera de las capas:** `Rodaje.Media` no cambia. En `Rodaje.Modules.Protocol` no hace falta nada nuevo: los cuadros van por memoria compartida.

### Formato del proyecto

- **Sin subir la versión para `blend` y `opticalFlow`:** ya son valores válidos desde el formato 4. Un Rodaje 1.0.x los abre y los ve como `nearest`, que es la degradación que se quería al reservarlos. `formato-proyecto.md` cambia la fila de `frameSampling` («reservados» → qué hace cada uno).
- **`flowQuality` (fase 7):** campo opcional (`standard` si falta; `ai`), añadido al esquema vigente sin subir la versión, como `words` en el formato 5: un Rodaje anterior lo ignora, ve el clásico (o el cercano) y lo pierde al guardar. Se documenta.

### Interfaz

- **Inspector › Velocidad:** desplegable «Cuadros intermedios» con «Cuadro más cercano», «Mezcla de cuadros» y «Flujo óptico» (con `AutomationProperties.Name`), visible también a 1× (para conformar la cadencia) y apagado en un congelado o en una imagen fija. Se aplica a toda la selección en un paso del historial (`ClipAttributes` ya copia `FrameSampling`).
- **Fase 7:** debajo, «Calidad: Normal / IA» y un botón «Calcular» que lanza el trabajo (Trabajos, con progreso y cancelar); el clip lleva una franja como la de Premiere (rayada: sin calcular; lisa: calculada).
- **Sin cambio de fábrica:** los clips nuevos siguen en «Cuadro más cercano» (pregunta 2).

### Rendimiento (cifras medibles)

| Medida | Objetivo | Cómo |
|---|---|---|
| Flujo óptico 1080p, RTX 3050 | ≤ 4 ms por cuadro (GPU) | `GpuTimer` en `FlowInterpolatorTests`, categoría Rendimiento (`nocturna.yml`) |
| Flujo óptico 4K, RTX 3050 | ≤ 12 ms | ídem |
| Reproducción 1080p a 0,25× en secuencia de 60 cuadros/s | 0 cuadros tarde en 10 s (`TimelineStats`) | prueba larga del motor con GPU |
| VRAM extra | ≤ 64 MB en 1080p, ≤ 200 MB en 4K | cuenta del `RenderTargetPool` |
| WARP 1080p (exportación) | ≥ 3 cuadros/s | prueba larga |
| Coste con `Nearest` | 0: los bytes de una secuencia sin interpolación no cambian | las pruebas de bytes existentes del compositor |

### Native AOT y pruebas sin WinUI

Sin reflexión: los enums por `CamelCaseEnumConverter` y el contexto de JSON generado; las constantes del shader en un `struct` con `StructLayout`. Las pruebas no tocan WinUI: Analysis (referencia, Linux), Engine (par y fracción, sin GPU), Render (WARP frente a la referencia) y Export (larga).

## 4. Fases

Cada fase en su rama y su PR; uniones con CI verde y squash. F1, F3 y F5a no se pisan (Engine/Playback, Analysis y App); F2 empieza cuando F1 deja el contrato de `LayerFrame` unido.

| Fase | Rama | Quién | Qué | Hecho cuando |
|---|---|---|---|---|
| F0 | `diseno/interpolacion` | Claude | D-063 en `decisiones.md` y este diseño recortado en `docs/interpolacion.md`; índice en `docs/README.md` | Unido; enlaces comprobados |
| F1 | `interp/motor` | ChatGPT (codex) | `SampleAt`, demanda de los dos cuadros en la vista previa **y en `OfflineTimeline`**, `LayerFrame.Next/Fraction/Sampling` con su retención; `Normalize` y copiar atributos a 1×; el renderizador aún los ignora | Pruebas de Engine y Core: ningún cuadro retenido de más tras 1000 refrescos con pausa y seek; fracción exacta con curva, reversa, `Offset` tras recortar, 24 → 30 a 1×, congelado sin par, > 4× cae a mezcla; la suite entera sin cambios |
| F2 | `interp/mezcla` | ChatGPT (codex); Claude revisa el color | `FrameInterpolator` con `Blend` sobre planos y la sustitución en `Compositor.DrawLeaf` | WARP: mezcla a t = 0,5 igual a la media de los planos ±1 código en NV12 y P010; un origen anamórfico y uno girado 90° salen con la misma geometría que sin interpolar; bytes con `Nearest` iguales; autoprueba con un clip a 0,5× en mezcla |
| F3 | `interp/referencia` | Claude (algoritmo y umbrales) + ChatGPT (código) | `FlowReference` en Analysis | Linux: textura que se traslada 8 px/cuadro, t = 0,5 con PSNR ≥ 32 dB y ≥ 10 dB sobre la mezcla; cuadrado que tapa un fondo: el halo < 2 % del área; dos imágenes sin relación: salida = cuadro más cercano byte a byte |
| F4 | `interp/gpu` | ChatGPT (codex, esfuerzo alto); revisa Claude | `FlowShaders` y `FlowInterpolator` en compute shader | WARP frente a la referencia: PSNR a menos de 0,5 dB y la decisión de corte igual; tiempos de la tabla en el RTX 3050; autoprueba con flujo óptico a 0,25× |
| F5a | `interp/interfaz` | ChatGPT (codex) | Desplegable, marca «≈», varios clips en un paso | Pruebas de `SpeedCommands`/`SpeedTabValues` sin WinUI; autoprueba que cambia el modo y deshace |
| F5b | `interp/docs` | Devin | Quitar «Reservado» en `ClipSpeed.cs` y `formato-proyecto.md`, `CHANGELOG.md`, una `Theory` nueva en `FormatV4Tests` que guarda y lee `nearest`, `blend` y `opticalFlow` (hoy solo se carga el ejemplo canónico) | CI verde; diff solo de esos archivos |
| F6 | `interp/verificacion` | Gemini (agy) pruebas + revisión de lectura | Exportación larga de un patrón sintético a 0,25× (TestMedia) medido contra la verdad; vista previa en pausa = exportación ±1; guía en `docs/guia/` | Prueba larga verde con el PSNR anotado; revisión de bloque de F1–F5 por otro modelo |
| F7 | `interp/ia` (solo si Daniel la quiere) | Claude (licencias, modelo, `LICENCIAS.md`); ChatGPT (función `interpolar`, `InterpolationStore`, trabajo); Gemini (pruebas); Devin (script de modelos con SHA-256) | RIFE en `modulo-vision`, caché e índice, calidad «IA» | Modelo con SHA-256 fijado y licencia anotada; 1080p medido en el RTX 3050 y en la CPU; matar el módulo a mitad no deja la caché a medias (temporal y renombrar); el clip recortado sigue usando lo calculado |

## 5. Riesgos

- **Ghosting y bordes de goma** del clásico con movimientos grandes o desenfoque: se acota con la caída a mezcla por encima de 4× y con la mezcla donde falla la consistencia; lo que quede es lo esperable de cualquier flujo clásico, y es la razón de la fase 7.
- **Cadencia de la vista previa:** con dos cuadros por capa se dobla la presión sobre la caché en 4K con varias pistas; si `TimelineStats` muestra cuadros tarde, la capa cae a mezcla mientras reproduce y vuelve al flujo en pausa.
- **Color:** al interpolar en los planos del origen el HDR y la conversión no cambian; el riesgo que queda es la croma 4:2:0 deformada con el flujo a media escala (bordes de color en movimientos finos). Se mide en F3 con un patrón de color y lo revisa Claude.
- **Pesos de RIFE** (fase 7): entrenados con Vimeo-90K, un conjunto de investigación; el mismo caso que BiRefNet. Se anota en `LICENCIAS.md` y `legal.md` antes de publicarlo.
- **VRAM de 4 GB** (fase 7): RIFE en DirectML y la vista previa juntos; el `ModuleManager` ya baja la vista previa a proxies mientras corre un módulo de GPU.

## 6. Preguntas para Daniel (se avanza con la recomendada)

1. **¿Entra la IA (fase 7) o basta el flujo clásico?** Recomendada: hacer F0–F6 y dejar F7 para después de verlo en su material; el clásico cubre la mayoría de las cámaras lentas sin descargas.
2. **¿Qué modo llevan los clips nuevos?** Recomendada: «Cuadro más cercano», como hoy; el flujo se elige por clip. Alternativa: una preferencia «Cuadros intermedios por defecto».
3. **¿El modo vale también a 1× para conformar la cadencia (24 en 30)?** Recomendada: sí; sin él el motor repite cuadros sin que se pueda evitar.
4. **¿Marca en el clip?** Recomendada: «≈» discreta junto a «◀»; sin franja de render hasta la fase 7.
5. **Fase 7: ¿módulo nuevo o función en `modulo-vision`?** Recomendada: función en `modulo-vision` (comparte ONNX Runtime y DirectML, ~37 MB, y el sistema de modelos); un módulo aparte solo si el modelo crece.

## Revisión de otro modelo (Gemini, 15-nov, solo lectura)

Diez puntos, todos comprobados en el código. **Incorporados:** la exportación arma sus capas en `OfflineTimeline` y había que llevar el par allí (grave, F1); un clip a 1× no podía guardar el modo por `Normalize` y por copiar atributos (grave); la caché de la IA no va por `IEffectContent` sino por un proveedor de cuadros propio (grave); la retención y liberación de `Next` y `HeldLayer`; la marca va en `SpeedBadge.cs`, no en `ClipDecorations.cs`; la `Theory` de ida y vuelta; la clave propia `rint1|`. **Incorporados de otra forma:** para el choque con `LayerRenderer.Usable`/`DrawPlain`, la forma del píxel y el giro, y el espacio de color de la mezcla (puntos 4, 5 y 7), en vez de un intermedio RGBA16F con reglas por secuencia el interpolador devuelve planos del mismo formato que el origen: los tres problemas desaparecen sin tocar `LayerRenderer`. **Descartado:** cambiar `ClipSpeed.IsIdentity` para que mire `FrameSampling` (punto 1): `IsIdentity` habla del mapeo de tiempos y lo usan Core, Engine y la línea de tiempo; se cambia `Normalize`, que es quien borra la velocidad.
