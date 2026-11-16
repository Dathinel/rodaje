# Render en caché de la línea de tiempo

Los tramos pesados de la secuencia se precalculan a fragmentos intermedios y se reproducen como un solo clip (D-060). Diseño completo en [propuestas/render-en-cache.md](propuestas/render-en-cache.md).

## Medición antes de construir (F0)

`RenderCacheBaselineTests` (en `Rodaje.Export.Tests`, solo con `RODAJE_PRUEBAS_LARGAS=1`). RTX 3050 Laptop, Ryzen 7 7435HS, 15-nov-2026, a prioridad baja y con el equipo compartido con otras compilaciones: las cifras son un suelo, no un techo. Fuente `testsrc2` de 5 s a 30 fps.

### Decodificación de los códecs candidatos

| Códec | Ruta | 1080p fps (× 30) | 4K fps (× 30) | Abrir + primer cuadro 1080p / 4K (ms) | MiB por 5 s 1080p / 4K |
|---|---|---:|---:|---:|---:|
| **ProRes 422 HQ** | CPU | 497 (16,6×) | 49,6 (**1,65×**) | 44 / 183 | 47 / 161 |
| ProRes 4444 | CPU | 144 (4,8×) | 43,2 (1,44×) | 108 / 300 | 72 / 244 |
| DNxHR HQX | CPU | 518 (17,3×) | 75,4 (2,51×) | 72 / 406 | 131 / 521 |
| FFV1 | CPU | 54 (1,8×) | 12,2 (0,41×) | 168 / 519 | 49 / 175 |
| HEVC Main 10 intra (NVENC) | D3D11VA | 418 (13,9×) | 173 (5,75×) | 88 / 124 | 20 / 68 |

### Fidelidad (PSNR frente al original 4:4:4 de 10 bits; barras saturadas y contornos de 1 px)

| Imagen | ProRes 422 HQ 1080p / 4K | ProRes 4444 1080p / 4K |
|---|---:|---:|
| SDR BT.709 | 45,9 / 51,4 dB (error máximo 446 de 1023, en las líneas de croma de 1 px) | 81,0 / 84,3 dB (máx. 3) |
| HDR PQ BT.2020 | 56,9 / 60,8 dB | 81,2 / 84,4 dB |

### Composición

| Escena | Dispositivo | Componer mediana / p95 (ms) | Decodificar en CPU (ms por cuadro) |
|---|---|---:|---:|
| A: 4 capas 4K, 6 efectos por capa | RTX 3050 | 23,5 / 25,2 | 85 (las cuatro capas) |
| C: 2 capas 1080p con color | RTX 3050 | 0,7 / 11,4 | 29 |
| C: 2 capas 1080p con color | WARP | 38,6 / 59,6 | 29 |

### Decisiones

- **ProRes 422 HQ en SDR y HDR, también en 4K**: la CPU lo decodifica a 1,65× la tasa en 4K30 (umbral 1,5×), con poco margen; HEVC intra por la GPU queda como respaldo si el uso real lo pide (`RenderCacheFragmentMetadata.PlaybackOnly`).
- **422 y no 4444**: 4444 no llega a 1,5× en 4K y ocupa un 50 % más. La pérdida de 422 se concentra en el croma de detalles de 1 px (error máximo 446 de 1023 en líneas rojas y azules de un píxel); el promedio cumple ≥45 dB. Se dice en la ayuda de la franja.
- **Coste por apertura** para `RenderCostModel`: unos 45 ms en 1080p y 180 ms en 4K (abrir y primer cuadro de ProRes); un fragmento abre un decodificador cada 2 s.
- **Factor sin GPU**: componer en WARP cuesta unas 54 veces lo de la GPU (38,6 frente a 0,7 ms en la escena C); es donde el caché más ayuda.
- Escenas B, D y E (desenfoque de movimiento, compuestos anidados, títulos por letra) y la medida con audio y presentación quedan para la prueba de hardware de F4.

## Cómo está construido

| Pieza | Dónde | Qué hace |
|---|---|---|
| `FrameEvaluator` | `Rodaje.Engine/Timeline` | Las capas de un instante sobre `TimelinePlan`; lo usan la exportación y la receta |
| `FrameRecipe`, `RenderCacheKey` | `Rodaje.Playback/Composition` | La huella de 128 bits y la clasificación de cada propiedad (incluida o excluida) |
| `RecipeBuilder` | `Rodaje.Engine/RenderCache` | La huella de cada cuadro sin decodificar; `null` donde falta contenido o es un respaldo |
| `ContentFingerprint` | `Rodaje.Media/RenderCache` | Huella de contenido del medio, sin ruta (también la usa `MediaDuplicates`) |
| `RenderCacheStore` | `Rodaje.Media/RenderCache` | Fragmentos `.mov` + `.json`, índice huella → cuadro, tope y limpieza |
| `CacheCoverage` | `Rodaje.Engine/RenderCache` | Tramos que se ven desde el caché; los aplica al plan con `TimelineSession.ApplyCoverage` |
| `RenderCacheJob`, `ExportPauseToken` | `Rodaje.Export/Running` | Un tramo → un fragmento, con pausa cooperativa |
| `RenderCacheService`, `RenderCostModel` | `Rodaje.App/Services/RenderCache` | Qué calcular y cuándo; qué tramo va lento |
| `RenderCacheHost`, `RenderCacheScan`, `AppRecipeSources` | `Rodaje.App/Services/RenderCache` | La conexión en la app: huellas y previsión de cada plan del reproductor en otro hilo, cobertura, franja y trabajos sobre `Project.Clone` |
| `RenderChain` | `Rodaje.Render` | Huella de los shaders del compositor, los efectos, la exportación y el revelado RAW (`RecipeFormat.Chain` con el preajuste y la versión de FFmpeg) |
| Franja, menú, Preferencias, Trabajos | `TimelineRenderer.CacheStrip`, `CacheStripLayout`, `TitleBar`, `PreferencesView`, `JobsPanel.RenderCache`, casilla en `ExportView` | Lo que se ve; la autoprueba captura `regla-cache` con los cuatro estados |

**Por qué la cobertura va en el plan.** Poner una capa encima no basta: el planificador seguiría pidiendo y recorriendo las capas tapadas. Recortar los clips vivos fuera de lo cubierto hace que la demanda, la presentación y el pre-roll vean el borde de un tramo en caché como un corte más, sin tocar `TimelineScheduler`.

**En la app (F5b, 16-nov).** Cada plan que abre o recarga el reproductor (`PlayerEngine.PlanChanged`, con su generación de sesión y su `PlanRevision`) se recorre 150 ms después en otro hilo: huella y previsión de cada cuadro, cobertura en el almacén, `ApplyCoverage` en el hilo de render (solo si la sesión y la revisión siguen siendo esas) y franja. Los tramos rojos y amarillos con huella van a `RenderCacheService`; cada trozo es un `RenderCacheJob` sobre una copia del proyecto **del plan recorrido**, nunca de uno más nuevo. El servicio se pausa al reproducir o mover el cursor (mirando la instantánea del reproductor), limpia cada 10 min con las huellas vigentes protegidas y respeta el tope (por omisión, el menor entre 50 GB y el 10 % libre, mínimo 5 GB). En la autoprueba y las medidas el modo es Apagado: la franja y la cobertura sí se calculan. La previsión estima decodificar por la resolución del medio (1,1 ms por megapíxel, de F0) más abrir el decodificador en el primer cuadro de cada clip; si se equivoca con medios reales, medir cada medio con `DecodeSpeedProbe`.

**Calcular un fragmento, por fase (16-nov, ChatGPT).** Medianas de tres fragmentos de 60 cuadros 1080p30 en Release, a prioridad baja y con el equipo compartido (`ExportRunnerOptions.Measure` da los tiempos):

| Fase, ms por fragmento | GPU antes | GPU ahora | WARP antes | WARP ahora |
|---|---:|---:|---:|---:|
| Crear el `GpuDevice` | 158,0 | 0,0 | 3,7 | 0,0 |
| Abrir decodificadores y pedir cuadros | 67,4 | 48,6 | 66,1 | 61,1 |
| Leer la GPU y copiar a la CPU | 55,4 | 70,5 | 1.304,9 | 1.157,4 |
| Entregar a `ffmpeg.exe` (con la espera por codificar) | 1.554,6 | 1.721,6 | 3.442,2 | 2.155,9 |
| Vaciar el codificador y cerrar | 937,6 | 1.010,6 | 1.471,4 | 1.118,7 |
| Verificar con ffprobe | 138,6 | 82,1 | 189,8 | 78,1 |
| **Total** | **3.110** | **3.113** | **6.666** | **4.626** |
| **Cuadros/s** | **19,3** | **19,3** | **9,0** | **13,0** |

El arranque ya no domina: `RenderCacheHost` presta un dispositivo de fondo a cada trabajo (`ExportDevicePool`) y la verificación lee informe, paquetes y cuadros clave con un solo ffprobe, comprobando además las cajas del MOV. Lo que queda es codificar ProRes 422 HQ en `ffmpeg.exe` con la mitad de los hilos: llegar a 60 cuadros/s (2× tiempo real) pide bajar entregar y cerrar unas 4,4 veces, con un codificador persistente que segmente o con más hilos cuando no se edita. La primera vez sin caché de shaders cuesta 20 s (16 s en la primera composición). La prueba con hardware de la reproducción (escenas A y C) y las escenas B, D y E de F0 quedan en `pruebas-a-mano.md`.

## Pendiente de la revisión del bloque (Gemini, 15-nov)

Arreglados: el trozo cortado por una pausa larga vuelve a la cola (#522), los títulos bajo el caché no retienen el cuadro y el tope sigue el último uso tras reabrir el almacén (#524). Y en F5b (16-nov), cada uno con su prueba: `RecipeFormat.Chain` lleva la huella de los shaders (`RenderChain`), el preajuste y la versión de FFmpeg; un fragmento que falla al decodificar sale del índice (`RenderCacheStore.Forget(ruta)`) y la cobertura se rehace sin él; el trozo en curso se adopta si sus huellas siguen en los mismos cuadros; `LateFrames` apunta una vez el cuadro aunque lleguen tarde varias capas; la exportación recibe el mismo almacén y las mismas huellas que los fragmentos.

## Revisión independiente (Claude y ChatGPT, 16-nov)

Arreglados, cada uno con su prueba que antes fallaba:

- **Trozos que nunca se veían.** El servicio partía cada tramo en trozos de 2 s y dejaba un resto corto, o un cuadro tarde suelto, que `CacheCoverage` descarta por durar menos de un cuarto de segundo. Ahora los trozos se reparten por igual y un tramo corto se ensancha dentro de la secuencia (`CacheCoverage.ShortestSpan`).
- **Fallos sin fin.** Un trozo que fallaba volvía delante de la cola en cada reposo. Ahora solo vuelve el que corta una pausa larga; los fallos cuentan en `Failed`.
- **Apagar con un trozo en pausa** lo reanudaba al volver el reposo. Ahora pasar a `Off` lo corta y vacía la cola.
- **`Touch` en cada cuadro.** `TryGet` escribía la fecha de acceso del `.mov` cada vez, y `CacheCoverage.Find` lo llama por cada cuadro de la secuencia. Ahora la escribe como mucho una vez por minuto y fragmento.
- **Subtítulos aparte al exportar con la casilla.** Las huellas se calculaban con el proyecto sin ocultar las pistas de subtítulos (`SubtitleExport.Embedded`), así que podían acertar cuadros con los subtítulos quemados. Ahora se calculan con `plan.Request`, como la composición.
- **Tasas con un periodo que no es entero en ticks** (27 o 33 fps, por ejemplo). El carril del caché podía leer el cuadro anterior del fragmento por el redondeo; el ancla lleva ahora un tick de más.
- La prueba de mutaciones ahora cambia por separado cada uno de los 16 parámetros de un efecto y de una transición.

Arreglados después (16-nov, cada uno con su prueba): `Cleanup` solo reconcilia lo que estaba en el índice antes de enumerar (un `Commit` de otro hilo ya no se olvida); sin acierto anterior, `TryGet` prefiere el fragmento que deja más cuadros por delante (uno viejo de pocos cuadros ya no tapa al continuo); `IsCovered` y los recortes de `Apply` buscan el tramo por búsqueda binaria; la prueba de entrelazado usa 60 fps exportados entrelazados a 30 y falla sin la condición.

Quedan como tareas:

- **Composición retenida tras editar durante un seek.** Puede mostrar un instante el cuadro del caché de la revisión anterior, igual que hoy muestra lo vivo de antes de la edición. Hay que decidir si eso basta o si se retira.
- **Cifras del diseño:** las recetas tardan 152,7 ms por 1800 cuadros con 8 capas, medido a prioridad baja con el equipo cargado. El objetivo es de 150 ms y el tope de la prueba, de 600 ms; la prueba es de `Categoria=TiempoReal` porque en la CI de Windows dio 645 ms. La exportación con la casilla se midió con `TwoTracks` en WARP, no en la escena A.
