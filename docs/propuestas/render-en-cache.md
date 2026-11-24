# Render en caché de la línea de tiempo

Propuesta del 15-nov-2026 para el bloque que va **después de la 1.0.3** (aprobado por Daniel el 15-nov). Decisión propuesta: **D-060**. Solo diseño: nada de esto está en el código.

## 1. Qué problema resuelve

Quien edita apila color, máscaras, títulos por letra, desenfoque de movimiento y varias pistas 4K, y en algún tramo la vista previa empieza a perder cuadros. Hoy la única salida son los proxies, que bajan la resolución del **origen** pero no quitan el coste de componer ni de abrir decodificadores. El render en caché precalcula la **composición final** de los tramos pesados a un archivo intermedio y la reproduce como si fuera un solo clip: el tramo rojo pasa a verde y suena y se ve a tiempo real, con la calidad del original (no la del proxy).

### Dónde se cae hoy (medido en las pruebas del repositorio, RTX 3050 Laptop, Ryzen 7 7435HS)

La GPU compone barato; lo que se cae es lo que rodea a la composición:

| Escena | Cifra de hoy | Fuente | ¿Lo arregla el caché? |
|---|---|---|---|
| 8 capas 1080p sin efectos | 0,53 ms (p95 0,86) | `render.md` | No hace falta |
| 1 capa 4K con 6 efectos (color, calificador, curvas, lente, resplandor, grano) | 4,8 ms (**p95 9,1**), pool 165 MB | `render.md` | Sí: tres capas así suman 14,4 ms de mediana y 27 ms en p95 frente a 16,7 ms (60 fps); queda por medir junto con decodificar, subir y presentar |
| Transición 4K con desenfoque de movimiento (16 muestras) | 7,5–10,5 ms | `render.md` | Sí |
| Capa 4K con desenfoque de movimiento | 2,8 ms (**p95 6,5**), 63 MB | `render.md` | Sí, sumado a lo demás |
| Título por letra 4K: atlas la primera vez | 120 ms (cuenta en `LateTitles`) | `render.md` | Sí: el cuadro ya está hecho |
| Cortes con PiP: abrir un decodificador mientras suena | refrescos de **59–370 ms** en la aceptación | `motor.md` («Pendiente») | Sí: el tramo en caché abre un decodificador, no uno por capa |
| 4 pistas HEVC 4K con cortes cada 3 s | dentro del presupuesto de VRAM, `Tick` hasta 5,5 ms en máquina libre | `motor.md` | Sí: una sola lectura en lugar de cuatro |
| Sin GPU (WARP) | sin cifra de reproducción en los documentos | — | Es donde más ayuda: falta medirlo (fase 0) |

**Lo que no se midió en esta sesión:** no se compiló ni se corrió nada (otros agentes compilan en el repositorio). La fase 0 mide las cinco escenas de arriba y los códecs candidatos con una sola prueba, antes de escribir el resto.

### Cómo lo resuelven otros (solo ideas de producto)

- **Resolve.** Barra sobre la regla: roja = falta, azul = en caché. Modo inteligente (decide qué tramos) o de usuario (lo que se marca), caché por salida de la línea de tiempo o por clip, se calcula tras unos segundos sin tocar nada, códec elegible (ProRes, DNxHR, sin comprimir) y opción de usarlo al exportar.
- **Premiere.** Barra roja / amarilla / verde: rojo = necesita render, amarillo = quizá va a tiempo real, verde = render hecho. «Renderizar de entrada a salida». Opción de usar las previsualizaciones al exportar, con aviso de pérdida de calidad.
- **Final Cut.** Render en segundo plano tras unos segundos de inactividad, archivos ProRes 422, línea discontinua sobre lo que falta y un menú para borrar los archivos de render del proyecto o de los no usados.
- **Kdenlive** (GPLv3, solo la lógica). Previsualización por trozos de duración fija en una carpeta de caché; editar marca sucios los trozos del intervalo tocado y la regla los pinta. Invalida por **intervalo de tiempo**: mover un clip invalida aunque los píxeles no cambien.
- **Olive** (GPLv3, solo la lógica). Caché de disco **por huella del cuadro**: la huella sale de los parámetros del grafo en ese instante y el cuadro se guarda bajo ella. Mover sin cambiar el contenido no invalida; deshacer recupera lo calculado.
- **OpenTimelineIO** (Apache 2.0): intercambio de líneas de tiempo, sin caché. No aporta aquí.

Rodaje toma de Premiere los tres colores, de Final Cut el render en reposo y la limpieza, de Resolve el modo inteligente y el uso al exportar, y de Olive la invalidación por huella, que es la única exacta. No se copia código de ninguno.

## 2. Diseño

### La idea: huella por cuadro (contenido direccionado)

Cada cuadro de la secuencia tiene una **receta**: todo lo que decide sus píxeles, ya evaluado en ese instante. Su huella (128 bits) es la clave del caché. Así la invalidación es exacta por construcción:

- Si un cambio altera algún píxel del cuadro, cambia su receta y deja de haber acierto. No hay que listar a mano «qué edición invalida qué».
- Si no lo altera (mover un tramo entero con lo que tiene debajo, deshacer, renombrar, marcadores, audio), la huella es la misma y el cuadro sigue en verde, **también en su nueva posición**.
- Deshacer devuelve el verde al instante: los fragmentos viejos siguen en disco hasta la limpieza.

**Qué entra en la receta**: un descriptor explícito (`FrameRecipe`), no un volcado de `CompositionFrame`. Cada campo de los tipos de la composición está clasificado como **incluido** o **excluido a propósito** (los de tiempo absoluto, como el instante de la secuencia, se excluyen: si no, mover un tramo invalidaría).

| Parte | Cómo entra |
|---|---|
| Formato de la secuencia | ancho, alto, tasa, espacio de color (SDR / HDR PQ / HLG), blanco SDR |
| Cada capa, de abajo arriba | pista, `LayerTransform` completo (con `Shape`, `Pin`, `Stabilization`), aspecto de píxel, rotación, `Blend`, `Effects` evaluados (`LayerEffect` con los valores de `EffectValues` leídos por su indexador, no por propiedades), `MotionSamples`, máscaras, `TextMotion` de los títulos (letras y karaoke, `GeneratedPlan`) |
| Transición | código, avance, valores, obturador y la receta recursiva de `Transition.Incoming` |
| Origen de una capa de video | huella de **contenido** del medio, independiente de la ruta (tamaño + SHA-256 de principio, medio y final, la regla de `MediaDuplicates`), más lo que el compositor lee del cuadro: formato de píxel, profundidad, matriz, rango, primarios y curva, y tamaño original; e índice del cuadro del origen. Nunca el proxy: el caché se calcula siempre del original. `SourceFingerprint` de los proxies **no sirve**: mete la ruta en la huella |
| Capa generada (título) | el descriptor de `GeneratedContent` + huella de los archivos de fuente que usa |
| Capa compuesta | la receta de sus capas, recursiva (`LayerFrame.Nested`) |
| Contenido externo | LUT, transición de paquete, máscara de imagen (FFV1 de `<proyecto>.medios`), análisis de estabilizar: `paquete@versión/elemento` o huella del archivo, **con su estado** (pedido, listo, falta) tomado antes de evaluar |
| Versión del render | constante `RenderCacheVersion` + huella de **toda** la cadena que produce el fragmento: `RenderShaders`, `EffectShaders`, `ExportShaders`, el preajuste del codificador y la versión de FFmpeg |

La huella es probabilística en dos sitios y se documenta así: el muestreo del archivo de origen (un cambio solo en el centro con la misma fecha y tamaño no se ve, igual que hoy en los proxies) y la colisión de 128 bits (despreciable).

**Lo que no se guarda nunca:** un cuadro con contenido que falta o que aún carga (LUT sin cargar, transición de paquete caída a fundido, fuente ausente, máscara o análisis sin leer, medio sin conexión) no tiene receta válida: se queda sin caché. Como una LUT que falta llega al plan como recurso nulo (`EffectPlan`), el estado se toma de lo **pedido**, no de lo evaluado. Así el caché nunca congela un respaldo.

**Fuera de la primera versión: la reducción de ruido temporal.** Su historia es el resultado acumulado del clip (`LayerRenderer.Fx`, por clip, efecto, PTS y duración), no el cuadro anterior de la secuencia: un fragmento empezado en frío daría otra imagen que la exportación continua, y pasar de caché a vivo perdería la historia. Un cuadro con ese efecto activo no se guarda (queda en rojo con la explicación en la ayuda). Más adelante: calentar la historia con N cuadros antes de cada fragmento y medir la diferencia.

**Garantía contra el peor fallo** (mostrar un caché viejo):

- La receta sale de un **evaluador puro compartido** sobre `TimelinePlan` (`FrameAt`, `EffectTimeAt`, estabilizar, movimiento: lo que hoy concentra `OfflineTimeline`), con los metadatos de los medios ya leídos, sin lectores ni esperas. `OfflineTimeline` y la receta usan el mismo evaluador.
- Una prueba con reflexión (solo en las pruebas, que no son AOT) exige que **cada campo** de los tipos de la composición esté en la lista de incluidos o en la de excluidos; un campo nuevo sin clasificar rompe la CI.
- Pruebas de mutación dirigidas: cambiar cada campo incluido cambia la huella; cambiar uno excluido no.
- Muestras al azar «huella igual ⇒ píxeles iguales» tras cada edición: son evidencia, no demostración.

### Fragmentos y almacén

- **Fragmento:** un `.mov` con hasta 2 s de cuadros seguidos de la secuencia (`ceil(2 × tasa)`: 48 a 24 fps, 120 a 60), todo intra, sin audio, más un `.json` con el formato, el códec y la lista de huellas de sus cuadros. Escritura atómica como los proxies (`.tmp` y renombrar): un apagón nunca deja un fragmento a medias que parezca bueno.
- **Índice en memoria:** huella → (fragmento, cuadro). Se arma al abrir leyendo los `.json`, que se validan como en `ProxyStore` (existe el `.mov`, tamaño y número de cuadros coinciden). Un cuadro está en caché si su huella está en el índice. Si una misma huella está en varios fragmentos, se elige **favoreciendo la continuidad** (el que sigue al cuadro anterior), para no saltar de archivo en archivo cada cuadro.
- **Lectura que falla** (fragmento truncado o borrado por fuera): el cuadro vuelve a componerse en vivo y el fragmento sale del índice.
- **Carpeta:** `%LOCALAPPDATA%\Rodaje\render`, o `RODAJE_RENDER` (nueva en `variables-entorno.md`), o la que se elija en Preferencias. Es desechable: no va con el proyecto ni en `GeneratedMedia`.
- **El proyecto no cambia.** Nada se guarda en el `.rodaje`: no sube `schemaVersion` (sigue el criterio de compatibilidad de D-050). Modo, carpeta y tope van en `ajustes.json`.

### Códec intermedio (comparación)

Cifras de tasa de `ExportEstimate` (Mb/s a 1080p30; ×4 en 4K). Decodificación: medida en la fase 0 con `DecodeSpeedProbe`.

| Códec | Calidad | Tamaño 1080p30 / 4K30 por minuto | Decodificar | Codificar | Contra |
|---|---|---|---|---|---|
| **ProRes 422 HQ** (`prores_ks`, LGPL) | 10 bits 4:2:2, visualmente sin pérdida; 877 de 877 niveles en una rampa BT.709 (`hdr.md`; no prueba detalle espacial ni HDR), y al subirlo el croma 4:2:2 se promedia a 4:2:0 (`FrameUploader`) | 1,65 GB / 6,6 GB | CPU (FFmpeg en proceso, por cortes, 8 núcleos) | CPU en `ffmpeg.exe` | 4K por CPU puede no llegar a 60 fps: medir |
| DNxHR HQX | Igual que ProRes HQ, 877 de 877 | 1,65 GB / 6,6 GB | CPU, algo más lento que ProRes en FFmpeg | CPU | Ninguna ventaja sobre ProRes aquí |
| HEVC Main 10 todo intra por NVENC/AMF/QSV | Con pérdida (alta tasa, QP bajo), 870 de 877 | ~0,5 GB / 2 GB | **GPU (D3D11VA), sin copias** al compositor | GPU | Sin GPU no hay; no sirve para reutilizar al exportar un máster |
| FFV1 | Sin pérdida | ~4 GB / 16 GB | CPU, lento en 4K | CPU | Demasiado lento para reproducir 4K |
| Propio sin pérdida en la GPU (P010 o RGBA16F + compresión en un compute shader) | Exacto | sin comprimir: 11 GB / 45 GB por minuto; comprimido, quizá la mitad | GPU | GPU | Meses de trabajo, ancho de banda de disco (373 MB/s en 4K P010), formato que nadie más lee |

**Recomendación:** ProRes 422 HQ como único códec del caché en SDR y HDR (PQ o HLG BT.2020 de 10 bits, el mismo camino de la exportación HDR), en `ffmpeg.exe` como la exportación. Si la fase 0 mide que la CPU no decodifica ProRes HQ 4K a ≥1,5× la tasa de la secuencia, en 4K se usa **HEVC Main 10 todo intra por la GPU** solo para reproducir (se marca en el `.json` y la exportación no lo reutiliza). El códec propio se descarta: el beneficio no paga el coste.

**El caché no es idéntico a componer en vivo**, como un proxy no es idéntico al original: ProRes pierde algo y el croma pasa a 4:2:0 al subirlo. La huella dice qué receta se calculó, no que los píxeles decodificados sean los de la composición. La fase 0 mide la diferencia en títulos con borde fino, croma saturado y HDR (PSNR y error máximo); si los títulos o el croma fino se ven peor, se prueba ProRes 4444 (sin submuestreo, va por swscale a P016) a cambio de un 50 % más de disco. Nada GPL entra: `prores_ks`, HEVC por hardware y el decodificador ProRes están en el FFmpeg LGPL del entorno.

### Cómo se reproduce un tramo en caché

El caché entra en el plan como **un carril virtual**, pero no basta con ponerlo encima: hoy una capa tapada solo pierde prioridad en la demanda (`TimelineScheduler.Demand`) y `Tick` sigue recorriéndola. Hace falta una **selección exclusiva caché / vivo por cuadro**, compartida por la demanda y por la presentación: en un cuadro con acierto solo existe la capa del fragmento; en uno sin acierto, solo las vivas. En el borde se sigue pidiendo por delante lo vivo que viene después del acierto (y al revés), y los lectores abiertos a la vez en el borde cuentan en el presupuesto de VRAM.

- **Tramos reproducibles:** los aciertos se agrupan en tramos `VideoClipPlan` (un fragmento, índices seguidos); un acierto suelto o una sucesión de fragmentos inconexos se limita (como mucho una apertura nueva por segundo; lo demás, en vivo).
- **Ediciones sin caché viejo:** `Reload` retira la cobertura **en el acto** y solo la vuelve a poner cuando las recetas del plan nuevo están calculadas. Cada cobertura lleva la revisión del plan del que salió, y se comprueba al publicarla y al usar cada acierto.
- Lo demás ya existe: pre-roll del corte, composición atómica tras un seek, presupuesto de VRAM.
- El caché usa siempre la calidad completa: con «Mitad» en el visor se muestra a escala, no se recalcula.
- En reversa, el fragmento es intra: no hace falta proxy ni pasadas por GOP.

### Indicador sobre la regla

Una franja de 4 px bajo la regla (`TimelineRenderer`, junto a `_timeRangeRuler`), con color y texto en la ayuda (nunca solo color):

| Color | Significado |
|---|---|
| Sin franja | Tramo ligero: coste previsto < 50 % del presupuesto del cuadro |
| Amarillo | Sin caché; previsto a tiempo real, pero con poco margen (50–100 %) |
| Rojo | Sin caché; previsto por encima del presupuesto, o con cuadros perdidos medidos al reproducirlo |
| Verde | En caché |

- **Previsión** (`RenderCostModel`, puro): por cuadro, la suma por capa de decodificar (`DecodeSpeedProbe`, que no cuenta la apertura ni el primer seek: se le suma lo que mida F0 por apertura), la pila de efectos (`EffectCost`, la misma estimación que ve el inspector), los intermedios de compuestos y movimiento, y un factor sin GPU calibrado en la fase 0. Presupuesto: 80 % del periodo del cuadro. Va en `Rodaje.App` (`Services/RenderCache`), porque `EffectCost` está en Render y Engine no lo referencia.
- **Lo medido manda:** si al reproducir un tramo hubo refrescos tarde (`TimelineStats`), ese tramo pasa a rojo hasta que su receta cambie.
- **Tras una edición** se recalculan las recetas de la ventana visible y de 30 s alrededor del cursor en un hilo aparte, 150 ms después de la última edición y con cancelación (franja al día ≤300 ms en total). El resto, en reposo.

### Render en segundo plano

- **Modos** (Preferencias › Rendimiento): **Inteligente** (por omisión): calcula los tramos rojos y luego los amarillos; **Manual**: solo lo que se pide con «Secuencia › Calcular de entrada a salida» (o la selección); **Apagado**.
- Arranca tras **3 s sin editar ni reproducir**, primero lo más cerca del cursor. Se pausa al reproducir o al arrastrar el cursor y sigue después. Mientras se edita sí calcula: cada fragmento terminado comprueba que sus huellas sigan vigentes; si no, se tira y no se publica.
- **Es una exportación de un tramo:** `OfflineTimeline` (exacto, sin proxies) → compositor → `ExportFrameConverter` → `ffmpeg.exe` con un preajuste fijo, en su propio `GpuDevice` con `ExportGpuPriority.Lowest` (si el controlador no lo admite, se sigue igual: la pausa es la que protege), el proceso hijo en `BelowNormal`, un solo trabajo a la vez y como mucho la mitad de los hilos. Sale en el panel de trabajos (`JobsPanel`) como cualquier otro, con cancelar.
- **Pausa cooperativa:** hoy `ExportJob` solo cancela (mata el codificador). El trabajo del caché mira un testigo de pausa entre cuadros y deja de dar cuadros a `ffmpeg.exe` (la tubería simplemente espera); si la pausa dura más de 30 s, cancela y tira el fragmento a medias. Se mide la latencia de la pausa también en WARP y con una decodificación en curso.
- Sin GPU también calcula (es donde más ayuda), en WARP y a prioridad baja.

### Al exportar

- **Decodificar el caché en lugar de componer:** ahorra decodificar y componer muchas capas, pero suma una generación de ProRes. Va tras una casilla, «Usar el render en caché», **apagada por omisión**: hoy Rodaje promete vista previa = exportación cuadro a cuadro, y eso se mantiene salvo que se elija lo contrario. Solo se usa en los cuadros de la rejilla de la secuencia, sin conversión de tasa, sin entrelazado a tasa de campos (`ExportPlan`), sin alfa, con el mismo tamaño, espacio, rango y una profundidad de salida ≤10 bits; lo demás se compone como hoy.
- **Copia directa sin recodificar** (salida ProRes 422 HQ igual al caché): **fuera de la primera versión.** `StreamCopyPlanner` solo copia un tramo seguido de un mismo archivo; unir fragmentos copiados con tramos recodificados (marcas de tiempo, metadatos, cabeceras del MOV) necesita su propio diseño. Que `prores_ks` sea determinista no basta para prometer un archivo igual byte a byte: se compararían los paquetes de video y los píxeles decodificados.
- El caché HEVC de reproducción (si se usa en 4K) nunca se reutiliza al exportar.

### Espacio en disco y limpieza

- **Tope:** el menor entre 50 GB y el 10 % del espacio libre del disco, con un mínimo de 5 GB; se cambia en Preferencias. Además no se escribe si quedan menos de 5 GB libres.
- **Limpieza** (al abrir, cada 10 min en reposo y al pasar el tope), con las reglas de `ProxyStore.Cleanup`: temporales de más de 1 h, `.json` sin `.mov` y al revés, y después los fragmentos usados hace más tiempo. **Protegidos:** los que tienen algún cuadro de las recetas vigentes de las secuencias abiertas y los que se están leyendo.
- **Si los protegidos llenan el tope**, no se borra nada protegido: se deja de calcular (la franja lo dice) hasta que se suba el tope o se borre. Siempre se reserva sitio para el fragmento en curso.
- «Borrar el render en caché» en Preferencias (todo) y en Secuencia (lo de esa secuencia); dice cuántos GB libera.

### Dónde va cada cosa

| Capa | Qué se crea o toca |
|---|---|
| `Rodaje.Playback` | `Composition/FrameRecipe.cs`: el descriptor y su huella de 128 bits (SHA-256 truncado, `IncrementalHash`, sin dependencias nuevas), con las listas de campos incluidos y excluidos. Puro, sin FFmpeg. `RenderCacheKey` (16 bytes) es el único contrato que necesita el almacén |
| `Rodaje.Engine` | `Timeline/FrameEvaluator.cs`: el evaluador puro sobre `TimelinePlan` que hoy vive dentro de `OfflineTimeline` (refactor sin cambio de conducta; `OfflineTimeline` pasa a usarlo). `RenderCache/RecipeBuilder.cs`, `RenderCache/CacheCoverage.cs` (aciertos por cuadro con la revisión del plan, agrupados en tramos), selección exclusiva caché / vivo en `TimelineScheduler.Demand`, `.Presentation` y `Tick`; `TimelineStats` gana tarde por tramo |
| `Rodaje.Media` | `RenderCache/RenderCacheStore.cs` (índice, fragmentos, validación, escritura atómica, limpieza, tope), `RenderCache/RenderCacheFormat.cs` (elige ProRes o HEVC intra según la medida), `RenderCache/ContentFingerprint.cs` (huella sin ruta); `EnvironmentVariables.Render` |
| `Rodaje.Export` | `Running/RenderCacheJob.cs` (un tramo → un fragmento, con `ExportRunner` y pausa cooperativa); `ExportPlan` decodifica el caché donde es equivalente |
| `Rodaje.Render` | La huella de la cadena (`RenderShaders`, `EffectShaders`, `ExportShaders`) |
| `Rodaje.App` | `Services/RenderCache/RenderCacheService.cs` (reposo, cola, pausa al reproducir), `Services/RenderCache/RenderCostModel.cs`, franja en `TimelineRenderer`, menú Secuencia, Preferencias › Rendimiento (`AppSettings.Workflow`), panel de trabajos, captura `regla-cache` en la autoprueba |
| Módulos (named pipe) | Nada: lo que devuelven los módulos ya llega como archivos (máscaras FFV1, análisis) y entra en la receta por su huella |
| Formato del proyecto | Sin cambios (`schemaVersion` igual) |

**Native AOT:** sin reflexión; los `.json` de fragmentos con el generador de `System.Text.Json`; SHA-256 de la biblioteca base. **Pruebas sin WinUI:** todo menos la franja y Preferencias vive en Playback, Engine, Media y Export, y se prueba con dobles y con WARP.

### Cifras objetivo (medibles)

| Qué | Objetivo | Cómo se mide |
|---|---|---|
| Tramo en caché, secuencia 4K30 con 3 capas 4K y 6 efectos cada una (RTX 3050) | 0 perdidos, 0 tarde en 60 s | Prueba con hardware, escena A de la fase 0 |
| Ídem 1080p30 sin GPU (WARP, decodificación por CPU) | 0 perdidos en 60 s | Escena C, en WARP |
| Refrescos > 50 ms en cortes con PiP dentro de un tramo en caché | 0 | `TimelineCutPauseHardwareTests` con caché |
| `Tick` con el carril virtual | mismos bytes reservados que sin él; mediana ≤0,12 ms con 8 capas | Pruebas de memoria del motor |
| Recetas | ≤150 ms por 1800 cuadros con 8 capas (hilo aparte); franja al día ≤300 ms tras editar | Prueba de rendimiento con dobles |
| Calcular en segundo plano (RTX 3050) | ≥2× tiempo real 1080p30; ≥0,5× 4K30 | Escena A y B |
| Exactitud de la invalidación | cada campo clasificado; mutación de cada incluido cambia la huella; 200 cuadros al azar tras cada una de las ~50 ediciones de `Edits`: huella igual ⇒ composición igual en WARP | Pruebas de la receta |
| Fidelidad del caché | ProRes HQ frente a componer en vivo: PSNR ≥45 dB en la escena A, título con borde de 1 px y croma saturado medidos (y HDR PQ) | F0 |
| Exportar con la casilla | ≥45 dB frente a exportar de cero y más rápido en la escena A | Prueba de exportación |
| Tope de disco | nunca más de un fragmento por encima; protegidos intactos aunque llenen el tope | Pruebas del almacén |
| Pausa | ≤100 ms desde que empieza a reproducir, también en WARP | Prueba del trabajo |

## 3. Fases

Cada fase es una rama propia (`cache/fN-…`) y un PR. **Primero un PR corto de contratos** (Claude): `RenderCacheKey`, la firma de `FrameRecipe` y la interfaz del almacén, sin lógica; después F0, F1, F2 y F5a no comparten archivos y van en paralelo.

| Fase | Qué | Quién | Terminado cuando |
|---|---|---|---|
| **F0** Medición | `RenderCacheBaselineTests` (`HardwareFact`, `RODAJE_PRUEBAS_LARGAS=1`): escenas A (4 pistas 4K HEVC + 6 efectos), B (1080p con desenfoque de movimiento 16 muestras y transición), C (WARP 1080p 2 capas con color), D (compuesto anidado 3 niveles 4K), E (título por letra 4K al entrar), cada una partida en apertura, decodificar, subir, componer y presentar, con el audio sonando; decodificación de ProRes HQ, ProRes 4444, DNxHR HQX, HEVC intra y FFV1 en 1080p y 4K; fidelidad del caché (PSNR, título fino, croma, HDR) | codex escribe; Claude corre a prioridad baja | Tabla con las cifras en `docs/render-en-cache.md`; decide ProRes o HEVC intra en 4K, 422 o 4444, el coste por apertura y el factor sin GPU |
| **F1** Receta | `FrameEvaluator` (refactor de `OfflineTimeline`), `FrameRecipe`, `RecipeBuilder`, `ContentFingerprint` | **Claude** (delicado) | Todo campo clasificado (reflexión); mutación por campo; determinismo en dos procesos; tabla de las ~50 ediciones de `Edits`; ≤150 ms por 1800 cuadros; suite del motor e igualdad vista previa = exportación sin cambios |
| **F2** Almacén | `RenderCacheStore`, formato y validación del `.json`, limpieza, tope | codex; pruebas sencillas de limpieza, Devin | Apagón simulado (`.tmp` a medias), `.mov` truncado y lectura que falla nunca aciertan; tope respetado; protegidos intactos aunque lo llenen; AOT sin advertencias |
| **F5a** Previsión | `RenderCostModel` puro con costes de entrada (la calibración llega de F0 como constantes) + tarde por tramo en `TimelineStats` | codex | Pruebas con costes conocidos |
| **F3** Calcular | `RenderCacheJob` (pausa cooperativa) + `RenderCacheService` (reposo, cola, descarte si la revisión cambió) | codex; revisión Claude | Cuadros del fragmento decodificados = los de una exportación ProRes HQ del mismo tramo; pausa ≤100 ms también en WARP; cifras de cálculo de la tabla |
| **F4** Reproducir | Selección exclusiva caché / vivo, tramos reproducibles, retirada en `Reload` | **Claude** + codex | Con dobles: 2000 seeks exactos dentro, fuera y en los bordes; 0 perdidos y 0 tarde al cruzar caché ↔ vivo; aciertos fragmentados, cuadros repetidos y reversa; `Reload` al editar mientras suena nunca muestra un acierto de la revisión vieja; con hardware: escenas A y C a 0 tarde |
| **F5b** Interfaz | Franja, menú, Preferencias, panel de trabajos, autoprueba | codex; textos, menú y atajo, Devin | Captura `regla-cache` en la autoprueba con los cuatro estados; sin conflicto de atajos (`ShortcutCatalog`) |
| **F6** Exportar | «Usar el render en caché» con sus condiciones | codex; revisión Claude | ≥45 dB frente a exportar de cero, exportación más rápida en la escena A, y cuadros fuera de la rejilla, entrelazados o con alfa siempre compuestos |
| **F7** Cierre | `docs/render-en-cache.md`, `motor.md`, `variables-entorno.md`, `pruebas-a-mano.md` (qué mirar con el ratón), D-060 en `decisiones.md` | agy (docs y revisión de lectura); renombrados, Devin | Revisión de bloque de otro modelo sin hallazgos abiertos de lógica |

Orden: contratos → F0 ‖ F1 ‖ F2 ‖ F5a → F3 y F4 (en paralelo; F4 solo necesita F1 y un almacén falso) → F5b ‖ F6 → F7. Después, aparte: reducción de ruido temporal con historia calentada y copia directa de fragmentos al exportar.

## 4. Riesgos y preguntas para Daniel

**Riesgos**

- **Caché viejo en pantalla** (una receta incompleta o una cobertura de la revisión anterior): lo cubren el evaluador compartido, la clasificación obligatoria de campos, las mutaciones, la retirada en `Reload` y la revisión del plan en cada acierto.
- **Pérdida visible del caché** frente a lo vivo: se mide en F0 y se dice en la ayuda de la franja, como hoy el aviso «Proxy».
- **ProRes 4K por CPU puede no llegar** en el Ryzen 7 7435HS mientras también decodifica audio y compone: por eso F0 mide y existe el respaldo HEVC intra.
- **Disco:** 6,6 GB por minuto en 4K. El tope y la limpieza lo contienen; en portátiles con SSD pequeño conviene «Manual».
- **Calcular mientras se edita roba CPU y GPU:** prioridad baja, un trabajo a la vez y pausa al reproducir. Si molesta al arrastrar, se pausa también durante el arrastre.
- **Refactor del armado de capas** en `TimelineScheduler.Layers` y `OfflineTimeline`: es la parte que más se usa del motor. Va solo, sin cambio de conducta, con la suite completa y la igualdad vista previa = exportación como red.

**Preguntas** (con lo que se hace mientras no conteste)

1. ¿Tres colores como Premiere, o también «sin franja» para lo ligero? **Mientras tanto:** sin franja en lo ligero, para que la regla no salga amarilla entera.
2. ¿Modo por omisión Inteligente o Manual? **Mientras tanto:** Inteligente, con tope de disco.
3. ¿Al exportar se usa el caché por omisión (más rápido, una generación más) o se mantiene «vista previa = exportación» exacto? **Mientras tanto:** apagado, con la casilla; la copia directa de fragmentos queda para después.
4. ¿Se acepta que lo que se ve desde el caché pueda diferir algo de lo vivo (como un proxy, pero a resolución completa)? **Mientras tanto:** sí, con la diferencia medida en F0 y dicha en la ayuda; si en títulos finos se nota, ProRes 4444.
5. ¿La primera entrega puede salir sin la reducción de ruido temporal en caché? **Mientras tanto:** sí; esos tramos quedan en rojo con la explicación.
6. ¿El caché en 4K a resolución completa o a la mitad para ahorrar disco? **Mientras tanto:** completa (sirve para exportar y para «Completa» en el visor).
7. ¿La carpeta del caché en `%LOCALAPPDATA%` o junto a `GeneratedMedia` (la que eligió Daniel para lo generado)? **Mientras tanto:** `%LOCALAPPDATA%\Rodaje\render`, aparte, porque se puede borrar sin perder trabajo.

## 5. Decisión propuesta: D-060

**D-060. Render en caché por huella de cuadro, en ProRes 422 HQ, con carril virtual en la vista previa** (15 de noviembre de 2026; propuesta de Claude, provisional; después de la 1.0.3).

- **Contexto.** La composición en la GPU es barata, pero apilar efectos 4K, desenfoque de movimiento, títulos por letra o muchos decodificadores tira cuadros, y sin GPU todo es lento. Los proxies no quitan ese coste.
- **Decisión.** Cada cuadro tiene una receta evaluada por el mismo evaluador que usa la exportación; su huella (con el origen identificado por contenido, no por ruta) es la clave del caché: invalidación exacta y deshacer recupera. Los tramos se calculan como una exportación de un tramo a fragmentos ProRes 422 HQ de hasta 2 s (HEVC intra por GPU solo si la CPU no llega en 4K), en reposo, a prioridad baja y con pausa cooperativa. La vista previa elige por cuadro entre caché y vivo, en exclusiva, y retira la cobertura en cada `Reload` hasta validarla; la regla muestra rojo, amarillo y verde. Al exportar, solo si se pide y en los cuadros equivalentes. Fuera de la primera versión: reducción de ruido temporal y copia directa de fragmentos. Nada en el `.rodaje`.
- **Consecuencia.** Nueva carpeta desechable con tope y limpieza, `RODAJE_RENDER`, una clasificación obligatoria de los campos de la composición (incluido o excluido) comprobada en la CI y un refactor que saca de `OfflineTimeline` un evaluador puro.

## Revisión de otro modelo (ChatGPT, solo lectura, 15-nov)

17 hallazgos, comprobados contra el código los de más peso (`SourceFingerprint` mete la ruta, `FrameUploader` promedia el croma 4:2:2, la reducción temporal guarda historia por clip). **Incorporados:** reducción temporal fuera de la v1; huella de origen sin ruta; descriptor explícito con `TextMotion`, formato del cuadro y `Transition.Incoming`; estado de lo pedido antes de evaluar; huella de toda la cadena de shaders y del codificador; clasificación obligatoria de campos en lugar de «toda propiedad pública»; evaluador puro sacado de `OfflineTimeline`; selección exclusiva caché / vivo (una capa encima no bastaba); tramos reproducibles y continuidad; pérdida del caché declarada y medida; copia directa fuera de la v1 y condiciones para usar el caché al exportar; retirada de la cobertura en `Reload` con revisión del plan; F0 por etapas y la cifra de 3 × 4,8 ms corregida; pausa cooperativa; tope frente a protegidos; `RenderCostModel` en App; fragmentos de 2 s a cualquier tasa; espera de 150 ms para cumplir los 300 ms; PR de contratos antes de las ramas paralelas.

**Descartado:** calcular la huella con el archivo de origen entero (propuesta del hallazgo 2). Leer varios GB por medio al abrir cada proyecto cuesta demasiado; se queda el muestreo de principio, medio y final que ya usan `MediaDuplicates` y los proxies, documentado como probabilístico.
