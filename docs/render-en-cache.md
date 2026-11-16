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

**Por qué la cobertura va en el plan.** Poner una capa encima no basta: el planificador seguiría pidiendo y recorriendo las capas tapadas. Recortar los clips vivos fuera de lo cubierto hace que la demanda, la presentación y el pre-roll vean el borde de un tramo en caché como un corte más, sin tocar `TimelineScheduler`.

**Lo que falta medir.** Calcular va a 10,2 cuadros/s en 1080p contando preparar y verificar cada fragmento de 2 s (objetivo: 2× tiempo real); la prueba con hardware de la reproducción (escenas A y C) espera a que la app calcule fragmentos reales (F5b).

## Pendiente de la revisión del bloque (Gemini, 15-nov)

Arreglados: el trozo cortado por una pausa larga vuelve a la cola (#522), los títulos bajo el caché no retienen el cuadro y el tope sigue el último uso tras reabrir el almacén (#524). Quedan como tareas, para cuando la app conecte el servicio (F5b):

- **`RecipeFormat.Chain` sin calcular.** Hay que pasarle la huella de los shaders de render, efectos y exportación, el preajuste del codificador y la versión de FFmpeg; sin ella, una versión nueva del render daría por buenos fragmentos viejos.
- **Fragmento ilegible.** Si un `.mov` del caché falla al decodificar, la app debe llamar a `Forget` y volver a aplicar la cobertura (en vivo) en lugar de dejar el tramo en negro.
- **Trozo en curso al cambiar de plan.** Si sigue vigente, adoptarlo en vez de encolar un duplicado.
- **`LateFrames` por capa.** Varias capas tarde en el mismo cuadro lo apuntan varias veces y acortan la ventana de 256.
- **Exportar con la casilla.** La app tiene que pasar a la exportación el mismo almacén y el mismo `RecipeBuilder` con que calcula los fragmentos (`ExportRunnerOptions.RenderCache` y `RecipeBuilder`); sin ellos la casilla no hace nada.

## Revisión independiente (Claude y ChatGPT, 16-nov)

Arreglados, cada uno con su prueba que antes fallaba:

- **Trozos que nunca se veían.** El servicio partía cada tramo en trozos de 2 s y dejaba un resto corto, o un cuadro tarde suelto, que `CacheCoverage` descarta por durar menos de un cuarto de segundo. Ahora los trozos se reparten por igual y un tramo corto se ensancha dentro de la secuencia (`CacheCoverage.ShortestSpan`).
- **Fallos sin fin.** Un trozo que fallaba volvía delante de la cola en cada reposo. Ahora solo vuelve el que corta una pausa larga; los fallos cuentan en `Failed`.
- **Apagar con un trozo en pausa** lo reanudaba al volver el reposo. Ahora pasar a `Off` lo corta y vacía la cola.
- **`Touch` en cada cuadro.** `TryGet` escribía la fecha de acceso del `.mov` cada vez, y `CacheCoverage.Find` lo llama por cada cuadro de la secuencia. Ahora la escribe como mucho una vez por minuto y fragmento.
- **Subtítulos aparte al exportar con la casilla.** Las huellas se calculaban con el proyecto sin ocultar las pistas de subtítulos (`SubtitleExport.Embedded`), así que podían acertar cuadros con los subtítulos quemados. Ahora se calculan con `plan.Request`, como la composición.
- **Tasas con un periodo que no es entero en ticks** (27 o 33 fps, por ejemplo). El carril del caché podía leer el cuadro anterior del fragmento por el redondeo; el ancla lleva ahora un tick de más.
- La prueba de mutaciones ahora cambia por separado cada uno de los 16 parámetros de un efecto y de una transición.

Quedan como tareas:

- **Limpieza y publicación a la vez.** `Cleanup` olvida del índice los fragmentos que no vio al enumerar la carpeta, aunque un `Commit` en otro hilo los acabe de publicar. Hasta reabrir el almacén, esos fragmentos no aciertan. La app debe llamar a los dos desde el mismo hilo, o el almacén debe reconciliar solo lo que existía al enumerar.
- **Aciertos viejos sueltos.** Si una huella está en un fragmento de pocos cuadros y en otro continuo, `TryGet` puede elegir el suelto, y `Find` acaba descartando el tramo entero. Habría que preferir el candidato que da el tramo más largo.
- **Coste con secuencias largas.** `Apply` recorre todos los tramos por cada clip y `IsCovered` es lineal en cada refresco: con horas de secuencia y miles de tramos conviene una búsqueda binaria.
- **Composición retenida tras editar durante un seek.** Puede mostrar un instante el cuadro del caché de la revisión anterior, igual que hoy muestra lo vivo de antes de la edición. Hay que decidir si eso basta o si se retira.
- **Prueba de entrelazado.** `Cuadros_fuera_de_rejilla_entrelazados_y_con_alfa_se_componen_siempre` exporta 30 fps entrelazado a 25: la conversión de tasa ya excluye el caché, así que no prueba la condición de entrelazado. Debe usar una secuencia de 60 fps exportada entrelazada a 30.
- **Cifras del diseño:** las recetas tardan 152,7 ms por 1800 cuadros con 8 capas, medido a prioridad baja con el equipo cargado. El objetivo es de 150 ms y el tope de la prueba, de 600 ms. La exportación con la casilla se midió con `TwoTracks` en WARP, no en la escena A.
