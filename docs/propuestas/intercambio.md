# Intercambio con otros editores: EDL, FCPXML y OpenTimelineIO

Propuesta del 15-nov-2026 para después de la 1.0.3 (bloque aprobado por Daniel). Solo diseño: nada de esto está en `main`. Decisión propuesta: **D-062** (provisional: si otro diseño del mismo lote se une antes, toma el número siguiente libre al unirse).

## 1. Qué resuelve

Quien edita en Rodaje hoy no puede **llevar el montaje a otro programa** (etalonaje en Resolve, mezcla de sonido, un montador con Premiere) ni **traer uno hecho fuera**. Solo sale un video renderizado. Lo que se pide en la práctica:

- **Etalonaje y conformado:** mandar el corte a color con los medios originales (no el render), a la misma tasa y con el código de tiempo de cámara. Es el uso clásico de la EDL y del XML.
- **Sonido:** mandar las pistas de audio con sus niveles y fundidos (lo de verdad se hace con AAF; ver más abajo).
- **Traer un corte:** abrir en Rodaje un montaje hecho en otro editor y seguir editando, con los medios reenlazados.

**Cómo lo resuelven otros (solo producto y experiencia de uso):**

| Programa | Qué ofrece | Idea que tomamos |
|---|---|---|
| Resolve | Importa y exporta EDL, FCPXML, FCP 7 XML, AAF y OTIO; al importar pregunta si crear los medios o buscarlos, y si ajustar al código de tiempo de cámara | Diálogo de importación con «buscar medios en la carpeta…» y una lista de lo que no entendió |
| Premiere | Exporta EDL, FCP 7 XML (xmeml), AAF y OMF; el diálogo de EDL elige pista de video, canales de audio y si incluir transiciones; al exportar avisa de lo que no se lleva | Un informe de pérdidas antes y después de exportar |
| Final Cut | Solo FCPXML; los títulos y efectos no reconocidos se cambian por un hueco con aviso al importar | Un elemento que no se entiende se convierte en hueco o clip sin medio y entra en el informe, nunca rompe la importación |
| Kdenlive (GPLv3) | Importa y exporta OTIO (a través de la librería OTIO) y lee EDL | Lógica: convierte a su modelo y anota lo perdido en el registro. No se copia código |
| Shotcut / MLT (GPLv3/LGPL) | Exporta EDL por un script; lee XML de MLT | Nada que copiar; solo confirma que una EDL de una pista ya es útil |
| Olive (GPLv3) | Lee y escribe OTIO con la librería C++ | Igual que Kdenlive |
| OpenTimelineIO (Apache-2.0) | Modelo neutro (`Timeline`, `Stack`, `Track`, `Clip`, `Gap`, `Transition`, `Marker`, `ExternalReference`, `LinearTimeWarp`, `FreezeFrame`, `metadata` libre) y adaptadores en Python; AAF va en un adaptador aparte (`otio-aaf-adapter`, con `pyaaf2`) | Se lee y escribe su **esquema JSON** (`.otio`) en C# propio. Apache-2.0 permite usar el esquema y sus archivos de ejemplo con atribución; no se usa su código |

**AAF.** AAF es binario (Structured Storage de Microsoft), enorme y con variantes por fabricante; ni Kdenlive ni Shotcut lo escriben por su cuenta. **No entra en este bloque.** Para Avid o Pro Tools, el camino recomendado es `.otio` → adaptador AAF de OTIO (fuera de Rodaje) o Resolve como puente. Se documenta en la guía y queda como pregunta.

**Premiere.** Premiere no lee FCPXML moderno; sí lee FCP 7 XML (xmeml) y, según versión, OTIO. xmeml queda como fase opcional (F7).

## 2. Qué se conserva y qué se pierde

Leyenda: **sí** (ida y vuelta exacta), **aprox.** (se lleva con redondeo o aproximación y va al informe como aviso), **no** (se pierde y va al informe), **meta** (va en `metadata.rodaje` y solo lo recupera Rodaje).

| Elemento | EDL CMX 3600 | FCPXML 1.10/1.11 | OTIO |
|---|---|---|---|
| Cortes de una pista de video | sí (una EDL por pista) | sí (`spine`, `asset-clip`, `gap`) | sí |
| Varias pistas de video | una EDL por pista (`V1.edl`, `V2.edl`) | sí (`lane` positivos) | sí (`Stack` de `Track`) |
| Audio | hasta 4 canales (`A`, `A2`, `AA`…) en la EDL de V1; nivel no | sí, pistas en `lane` negativos; `adjust-volume` en dB y fundidos | pistas sí; nivel y fundidos **meta** |
| Fundido cruzado (`rodaje.crossfade`) | `D` | `transition` (fundido estándar) | `Transition` `SMPTE_Dissolve` |
| Alineación, curva y borde congelado de la transición | alineación sí (el evento `D` se parte en el cuadro de corte); curva y congelado no | alineación sí; curva no | `in_offset`/`out_offset` sí; curva **meta** |
| Otras transiciones | se sustituyen por `W001` (barrido horizontal SMPTE): aprox. | fundido: aprox. | `Transition` con tipo propio: **meta** |
| Velocidad constante | `M2` en cuadros por segundo (2× a 25 fps es `050.0`; negativa en reversa) | `timeMap` | `LinearTimeWarp` (no cambia la duración del clip: el escritor la fija aparte) |
| Curva de velocidad | no (se exporta la media, aprox.) | `timeMap` muestreado: aprox. (la curva de Rodaje es velocidad lineal a tramos, su integral es cuadrática; se muestrea con error ≤ ½ cuadro) | aprox. (media) + **meta** |
| Congelado | `M2 000.0` | `timeMap` de dos puntos iguales | `FreezeFrame` |
| Transformación (posición, escala, giro, opacidad) | no | `adjust-transform`, `adjust-blend` con keyframes lineales: aprox. (las curvas Bézier y el rebote se muestrean) | **meta** |
| Recorte, máscaras, inclinación, desenfoque de movimiento | no | recorte: `adjust-crop`; lo demás no | **meta** |
| Color, LUT, curvas, HDR | no (fase posterior: `*ASC_SOP`/`*ASC_SAT` aprox.) | no | **meta** |
| Títulos y generadores | no (hueco + comentario con el texto) | hueco + marcador con el texto | `Clip` con `GeneratorReference` y **meta** |
| Clips compuestos | se aplanan (aprox.) | `ref-clip` a una `sequence` de `resources`: sí | `Stack` anidado: sí |
| Multicámara | ángulo activo (aprox.) | ángulo activo (aprox.); `mc-clip` en fase posterior | ángulo activo + **meta** |
| Marcadores de secuencia | `* LOC: <TC de la secuencia> <COLOR> <texto>` tras el evento que lo cubre (convención de Resolve) | `marker` dentro del elemento del `spine` que lo cubre, en tiempo local de ese elemento (también sobre un `gap`); sobre un clip con velocidad, su tiempo local se calcula con el mapa inverso; fuera del montaje se pierde | `Marker.2` en la pila |
| Regiones | no | `chapter-marker` (solo inicio): aprox. | `Marker` con duración |
| Colores de clip, grupos, enlaces | no | enlaces aprox. (un `lane` es un clip conectado a otro, no una pista global ni un enlace de Rodaje; se reconstruyen pistas por número de `lane`); lo demás no | **meta** |
| Subtítulos | archivo `.srt` aparte (`SubtitleFiles`) | `caption` (fase posterior) o `.srt` aparte | `.srt` aparte + **meta** |
| Nombre y ruta del medio | `* FROM CLIP NAME:` y `* SOURCE FILE:` | `asset`/`media-rep src` (URL `file://`) | `ExternalReference.target_url` |
| Código de tiempo de cámara | sí (carrete y TC de origen) | `start` del `asset` y `tcFormat` | `available_range.start_time` |

**Ida y vuelta Rodaje → OTIO → Rodaje sin pérdida, con garantía acotada.** El `Timeline` lleva en `metadata.rodaje` un bloque versionado (`{"v":1, "sequence":…, "sequences":[compuestos], "media":[…], "huella":"…"}`) con la secuencia completa (ajustes, buses, mezclador), las secuencias de las que dependen sus compuestos y sus medios, serializados con `ProjectJsonContext` (sin reflexión). `huella` es un SHA-256 de la parte estándar que escribió Rodaje (pistas, clips, rangos, medios, velocidades, transiciones, marcadores, en forma canónica). Al leer:
- si la huella de la parte estándar leída coincide, se usa el bloque entero: ida y vuelta exacta;
- si no (otro programa editó), manda lo estándar de OTIO; cada clip lleva además su propia huella (medio, rango de origen, velocidad, activación) y solo se le recuperan efectos y transformaciones de Rodaje si la suya coincide. El resto va al informe como «Rodaje descartó sus ajustes porque el clip cambió fuera».
La garantía «sin pérdida» se limita a eso: un archivo que nadie tocó, o los clips que nadie tocó. La `metadata` ajena de otros programas no tiene sitio en el modelo de Rodaje: se pierde al importar y va al informe.

## 3. Tasas, código de tiempo y drop frame

- **Tiempo interno exacto.** Todo se convierte a `Time` (705 600 000 ticks/s, `Rational` exacto). FCPXML (`"1001/30000s"`) se lee como fracción y cae en tick exacto a las tasas de `formato-proyecto.md`. OTIO guarda `RationalTime{value, rate}` en coma flotante: `rate` ≈ 23,976, 29,97 o 59,94 (tolerancia 1e-3) se normaliza a `24000/1001`, `30000/1001` o `60000/1001`, como hacen los programas que escriben OTIO, y la normalización va al informe como «Info»; cualquier redondeo a tick que mueva un cuadro de video o más de una muestra de audio va como «Aproximado».
- **Esquema OTIO fijado.** Se leen y escriben estas versiones de clase: `Timeline.1`, `Stack.1`, `Track.1`, `Clip.2` (`media_references` + `active_media_reference_key`; se lee también `Clip.1` con `media_reference`), `Gap.1`, `Transition.1`, `Marker.2`, `ExternalReference.1`, `MissingReference.1`, `GeneratorReference.1`, `LinearTimeWarp.1`, `FreezeFrame.1`, `RationalTime.1`, `TimeRange.1`. Una clase o versión desconocida se convierte en hueco con aviso. La tabla de campos va en `docs/intercambio.md`.
- **EDL: perfil de escritura.** Título, `FCM: DROP FRAME` o `NON-DROP FRAME`, eventos de 3 cifras (más de 999: 4 cifras, como Resolve, con aviso), carretes de 8 caracteres en mayúsculas sin espacios y sin colisiones (`TOMA01`, `TOMA01_2`…; la correspondencia completa va en `* FROM CLIP NAME:` y `* SOURCE FILE:`), negro como `BL`, canales `V`, `A`, `A2`, `AA`, `AA/V`, `NONE`. Al **leer**, la EDL casi nunca dice su tasa: el diálogo la pide (por defecto, la de la secuencia activa; con `FCM: DROP FRAME`, 29,97) y cada evento guarda su carrete hasta que el reenlazado decide a qué archivo corresponde.
- **Código de tiempo SMPTE** en un tipo nuevo `Rodaje.Core.Timing.Timecode` (cuadros + base nominal 24/25/30/48/50/60 + drop frame). Drop frame solo a 29,97 (salta 2 números por minuto salvo cada 10) y 59,94 (salta 4); se escribe con `;`. 23,976 es siempre sin drop frame con base 24. Cifras de referencia de las pruebas: `00:01:00;02` = cuadro 1800 y `00:10:00;00` = 17 982 a 29,97 DF; `00:01:00;04` = 3600 a 59,94 DF; `23:59:59;29` = 2 589 407.
- **TC de inicio de la secuencia.** Campo nuevo opcional `Sequence.StartTimecode` (texto `HH:MM:SS:FF` o `HH:MM:SS;FF`; nulo = `00:00:00:00`). Al exportar, si es nulo, el diálogo propone `01:00:00:00`, lo habitual en los otros editores.
- **TC de los medios.** `MediaTags` lee además `timecode` (del flujo o del contenedor; pista `tmcd` de MOV y MXF los traen así en FFmpeg) y `reel_name`. Campos nuevos opcionales `MediaRef.Timecode` y `MediaRef.Reel`. Sin TC, el origen empieza en `00:00:00:00` y el carrete es el nombre del archivo.
- **Cuadros enteros solo en EDL.** La EDL trabaja en cuadros: un clip de audio entre cuadros se redondea y va al informe. FCPXML (tiempos racionales, el audio puede ir en muestras) y OTIO no se cuantizan. Un medio a otra tasa que la secuencia: en EDL el TC de origen se expresa a la tasa de la secuencia (como hace Resolve) y se avisa; en FCPXML y OTIO va con su propia tasa.
- **Del TC al tiempo dentro del archivo.** Al leer, el punto de entrada en el medio es `TC del evento − TC inicial del medio` (de `MediaRef.Timecode`, o 0 si no tiene). Mientras el medio no esté resuelto se guarda el TC; al reenlazarlo se recalcula el rango con el TC real del archivo.

**Transformación en FCPXML (fórmulas, F0 las verifica contra la documentación de Apple y una muestra real).** `position` va en porcentaje del alto de la secuencia con y hacia arriba: `x_px = posX · alto/100`, `y_px = −posY · alto/100` (Rodaje: píxeles de la secuencia con y hacia abajo). `scale` es un factor por eje (identidad `1 1`) = `scale · scaleX`, `scale · scaleY`. `rotation` en grados con signo contrario. `anchor` también en porcentaje del alto. El conformado (cómo encaja un origen de otro tamaño) se fija igual en los dos lados (`conform-fit`) y la prueba usa un origen de 4000×3000 en una secuencia de 1920×1080.

## 4. Reenlazado de medios al importar

1. Las rutas se normalizan: `file://` con `%20` y tildes, `/Volumes/…` de macOS y relativas al archivo importado. Solo se sondean rutas locales: una ruta UNC o de red no se toca sola (podría abrir una conexión) y queda como medio que falta; quien edita la elige en el reenlazado.
2. Los medios que existen se sondean con `IMediaProber` (síncrono): un `Task.Run` por archivo, 4 a la vez, con cancelación entre archivos, fuera del hilo de la interfaz, y se crean con `MediaInfo.ApplyTo`.
3. Los que no existen se crean **sin archivo** (como un proyecto con medios movidos) con lo que se sepa: en FCPXML y OTIO, duración y tasa; en EDL solo el rango usado (la duración provisional es el mayor punto de salida y el tamaño se marca como desconocido). Se abre el diálogo de reenlazado que ya existe (`MediaRelinkService`, `MediaRelinkSearch`, `Views/Relink/RelinkDialog.cs`) con dos cambios: `MediaRelink` no exige coincidencia de duración ni tamaño cuando son desconocidos (solo que el archivo cubra el rango usado), y `RelinkProbe` lleva el TC y el carrete del archivo. En EDL se busca por `* SOURCE FILE`, luego `* FROM CLIP NAME`, luego carrete = nombre de archivo sin extensión, y el TC desempata. Al resolver un medio se recalculan los rangos de sus clips (sección 3).
4. El medio se deduplica por ruta normalizada (o por carrete en EDL) y **se reutiliza el del proyecto abierto** si ya está: el mismo archivo usado en 300 clips es un solo `MediaRef`.

**Fusión con el proyecto abierto.** La importación crea **una secuencia nueva** y nunca reemplaza nada. `SequenceBuilder` devuelve medios, secuencias de compuestos y la secuencia principal con **identificadores nuevos** (todos, también los recuperados de `metadata.rodaje`, remapeando `mediaId`, `sequenceId`, `linkId`, `groupId` y las transiciones `From`/`To`), así importar dos veces el mismo archivo no choca. Se aplican en un `CompositeCommand` (un paso del historial): primero `AddMediaCommand` de los medios nuevos, después `AddSequenceCommand` de las secuencias de los compuestos (de las más internas hacia fuera) y al final la principal. Se valida el conjunto antes de aplicar; si algo falla, no se aplica nada.

## 5. Informe de pérdidas

`InterchangeReport`: lista de `InterchangeIssue(Severity, Kind, ClipId?, Name, Time?, Message)`, con `Severity` = `Info | Aproximado | Perdido` y `Kind` un `enum` cerrado (`Color`, `Transform`, `Transition`, `Speed`, `Title`, `Compound`, `Audio`, `Marker`, `Rounding`, `Media`, `Unknown`…).

- **Antes de exportar** el diálogo muestra el resumen agrupado («12 clips pierden el color; 3 transiciones se vuelven fundido») calculado sin escribir nada; se puede cambiar de formato y ver el resumen del otro.
- **Después** se muestra la lista completa con TC de la secuencia; botones «Copiar» y «Guardar como .txt».
- **Al importar**, lo mismo: lo que no se entendió (efectos de otro programa, títulos, elementos desconocidos) con su TC.
- **El informe y la prueba salen de la misma regla.** Cada formato declara `Fidelity.Project(project, sequence, options) → (medios, secuencias)`: lo que sobrevive, con medios y compuestos incluidos. El informe se calcula comparando con esa proyección. La prueba de ida y vuelta exige `Read(Write(s)) ≡ Project(s)`, con una igualdad semántica que ignora los identificadores (los empareja por posición). Si el escritor pierde algo que la proyección no declara, la prueba falla; si lo declara, aparece en el informe. Como lector, escritor y `Fidelity` podrían compartir el mismo error, esto no basta solo: hacen falta **oráculos independientes** (sección 7: muestras escritas a mano con su valor esperado, y muestras aceptadas por los programas de destino).

## 6. Encaje en la arquitectura

**Todo el formato vive en `Rodaje.Core`, sin capas nuevas.** Es transformación del modelo, como `SubtitleFiles`; no toca Engine, Render, Export ni módulos. `Rodaje.Export` no sirve: arrastra Engine, Media y Render.

| Archivo | Qué |
|---|---|
| `src/Rodaje.Core/Timing/Timecode.cs` (nuevo) | SMPTE con drop frame, `Parse`, `Format`, `ToTime(Rational)`, `FromTime` |
| `src/Rodaje.Core/Interchange/InterchangeFiles.cs` (nuevo) | Fachada: `FormatOf(path)`, `Read(path, options) → InterchangeResult(Project parcial, Sequence, Report)`, `Write(path, project, sequence, options) → InterchangeReport`, `Preview(...)` (solo el informe) |
| `src/Rodaje.Core/Interchange/InterchangeReport.cs`, `InterchangeOptions.cs` (nuevos) | Informe y opciones (pistas, TC de inicio, carretes largos, incluir audio) |
| `src/Rodaje.Core/Interchange/TimelineWalk.cs` (nuevo) | Vista plana para escribir: por pista, clips, huecos y transiciones en cuadros de la secuencia, con origen, velocidad y TC resueltos; aplana compuestos cuando el formato lo pide |
| `src/Rodaje.Core/Interchange/SequenceBuilder.cs` (nuevo) | Lo común al leer: deduplica medios, crea pistas, clips, transiciones y marcadores con `IdGenerator` y remapeo de identificadores; convierte el solape de dos clips con transición en una `TransitionInstance` (no lo recorta) y solo resuelve solapes sin transición, con aviso; pasa por `ModelValidator` antes de devolver |
| `src/Rodaje.Core/Interchange/InterchangeLimits.cs` (nuevo) | Presupuestos de lectura (sección Seguridad) |
| `src/Rodaje.Core/Interchange/Fidelity.cs` (nuevo) | `Project(sequence, format)` de la sección 5 |
| `src/Rodaje.Core/Interchange/Edl/EdlReader.cs`, `EdlWriter.cs` | CMX 3600 (texto) |
| `src/Rodaje.Core/Interchange/Otio/OtioReader.cs`, `OtioWriter.cs` | JSON con `JsonDocument`/`Utf8JsonWriter` (sin reflexión) |
| `src/Rodaje.Core/Interchange/Fcpxml/FcpxmlReader.cs`, `FcpxmlWriter.cs` | `XmlReader`/`XmlWriter` (sin `XmlSerializer`) |
| `src/Rodaje.Core/Model/MediaRef.cs`, `Sequence.cs`, `Project.cs` | `Timecode`, `Reel`, `StartTimecode` (opcionales); `Project.CurrentSchemaVersion` = 16 |
| `src/Rodaje.Core/Serialization/ProjectSerializer.cs`, `ProjectMigrator.cs`, `ProjectSchema.cs`, `schema/rodaje-proyecto.v16.schema.json` | Formato 16, escrito solo si algún campo nuevo tiene valor; si no, se conserva la elección actual de 13, 14 o 15 (D-050): cuatro salidas posibles, las cuatro probadas |
| `src/Rodaje.Media/Probe/MediaTags.cs`, `MediaInfo.cs`, `MediaProbe.cs` | Leer `timecode` y `reel_name` (`MediaProbe` decide de qué diccionarios: flujo de video, pista `tmcd` y contenedor); `ApplyTo` los copia |
| `src/Rodaje.Core/Editing/MediaRelink.cs`, `MediaRelinkSearch.cs`, `src/Rodaje.App/Services/Relink/MediaRelinkService.cs` | Duración y tamaño desconocidos; TC y carrete en `RelinkProbe`; recálculo de rangos al resolver |
| `src/Rodaje.App/Services/Interchange/InterchangeService.cs` (nuevo) | Sin WinUI: elegir formato, leer fuera del hilo, crear el comando, lanzar el reenlazado; probado en `Rodaje.App.Tests` |
| `src/Rodaje.App/Views/Interchange/InterchangeDialog.cs`, `InterchangeReportDialog.cs` (nuevos) | Diálogos (en código, como `StabilizeDialog`) |
| `src/Rodaje.App/Services/Shortcuts/InterchangeShortcuts.cs` (nuevo), `Shell/TitleBar.xaml` | «Importar línea de tiempo…» y «Exportar línea de tiempo…» en Archivo, junto a los de subtítulos |
| `src/Rodaje.App/Shell/SelfTestRun.Interchange.cs` (nuevo) | Autoprueba: exporta la secuencia de prueba a los tres formatos en `%TEMP%`, la reimporta y compara número de clips y duración |
| `docs/intercambio.md` (nuevo), `docs/formato-proyecto.md`, `docs/arquitectura/nucleo.md`, `docs/decisiones.md`, `LICENCIAS.md`, `docs/guia/` | Tablas de la sección 2, formato 16, atribución del esquema y ejemplos de OTIO |

**Seguridad (el archivo importado no es de confianza).** `XmlReaderSettings { DtdProcessing = Ignore, XmlResolver = null, MaxCharactersFromEntities = 0, MaxCharactersInDocument = 256 M }`: `Ignore` y no `Prohibit`, porque un FCPXML legítimo empieza con `<!DOCTYPE fcpxml>` y `Prohibit` lo rechazaría; sin resolver ni entidades, la DTD no hace nada. `InterchangeLimits` fija presupuestos propios (`TimelineLimits` limita tiempos y tasas, no cantidades): 256 MB por archivo, 200 000 objetos leídos, 1 MB por texto, anidado ≤ 32, y un presupuesto de expansión al aplanar compuestos (≤ 200 000 clips resultantes) con detección de ciclos de `ref-clip`/`Stack`. Aritmética `checked` con `Rational`; tasas fuera de 1/5–1000, rangos invertidos, duraciones negativas y NaN → error claro o aviso, nunca excepción sin capturar ni bucle. Se distingue lo negativo válido (el origen virtual de un generador, un `start` de FCPXML antes del cero de la secuencia) de lo inválido. Las rutas importadas solo se sondean (y solo las locales), nunca se ejecutan ni se escriben. Se suma al corpus hostil de #496.

**Native AOT.** `System.Xml` (`XmlReader`/`XmlWriter`) y `System.Text.Json` (`JsonDocument`, `Utf8JsonWriter`, `ProjectJsonContext`) son compatibles con recorte y AOT. `System.Xml` hoy no está en el exe: se mide su peso (objetivo ≤ 3 MB sobre los 37,5 MB) y `Probar.bat aot` debe seguir sin advertencias.

**Rendimiento.** Es CPU pura: la GPU no cuenta, así que el RTX 3050 y el equipo sin GPU dan lo mismo; se mide en el portátil de Daniel y en la CI.

| Medida | Objetivo |
|---|---|
| Exportar 10 000 clips en 8 pistas (cualquier formato) | < 300 ms, < 100 MB de pico |
| Importar ese mismo archivo (sin sondear medios) | < 1 s |
| Sondear 200 medios existentes al importar | < 10 s, sin bloquear la interfaz, cancelable |
| `Timecode`: todos los cuadros de 24 h a 29,97 DF, ida y vuelta | < 1 s |

Son hipótesis hasta medirlas en F3: corpus fijo (la secuencia generada con semilla 1 de 10 000 clips, 8 pistas y 400 medios, sin `metadata.rodaje` y con ella), memoria medida como incremento de `GC.GetTotalAllocatedBytes` y de `GC.GetGCMemoryInfo().HeapSizeBytes`, en el portátil de Daniel a prioridad baja y en el ejecutor de la CI. El sondeo depende del disco y del códec: se mide con 200 MP4 locales de `Rodaje.TestMedia` y su objetivo es que la interfaz no se congele y se pueda cancelar, no un tiempo. Las de 10 000 clips van en categoría `Rendimiento` (solo `nocturna.yml`), con margen ×3.

## 7. Pruebas

- **Tablas de `Timecode`:** ≥ 40 casos (límites de minuto y de décima de minuto en DF, 23,976, 25, 50, 59,94, 24 h, entradas inválidas) y la ida y vuelta de todos los cuadros de 24 h.
- **Ida y vuelta por propiedad:** secuencias generadas al azar con semilla fija (clips, huecos, fundidos, velocidad, compuestos, marcadores) → `Write` → `Read` → igual a `Fidelity.Project`. 500 semillas por formato en la CI.
- **Archivos de muestra:** `tests/Rodaje.Core.Tests/Interchange/Fixtures/` con EDL y FCPXML escritos a mano según la especificación, los ejemplos `.otio` del repositorio de OTIO (Apache-2.0, con `NOTICE`) y, si Daniel los da, exportaciones de Resolve (sección 9). Cada muestra tiene su secuencia esperada en una prueba.
- **Hostiles:** XML con DTD/entidades, OTIO con 10 000 niveles, tasas 0 y 1/0, TC `99:99:99:99`, EDL con 100 000 eventos, rutas con `..`, UNC y largas.
- **Edición externa simulada:** se escribe, se cambia el archivo como lo haría otro programa (mover un clip, cambiar su velocidad, borrar una transición) y se comprueba que manda lo editado y que Rodaje no resucita ajustes viejos (huellas de la sección 2).
- **Formato 16:** sin campos nuevos se siguen escribiendo 13, 14 o 15 como hoy; con ellos, 16; el esquema v16 valida; un 16 se migra en memoria.
- **App sin WinUI:** `InterchangeServiceTests` (importar crea una sola entrada de historial; deshacer la quita; importar dos veces no choca; medios que faltan abren el reenlazado y al resolverlos se recalculan los rangos).
- **Autoprueba:** exporta la secuencia de prueba a los tres formatos, la reimporta y la compara con `Fidelity.Project` (no solo cuenta clips).

## 8. Fases

Cada fase es una rama y un PR. **Cada archivo tiene un solo dueño por fase** (columna «Archivos»); las fases que no comparten archivos pueden ir a la vez. Los contratos que otras usan (firmas de `InterchangeFiles`, `InterchangeReport`, `Fidelity`, `SequenceBuilder`) los fija F0 y después solo los cambia Claude. «Un encargo por CLI a la vez» manda: las de codex van en serie.

| Fase | Qué | Quién | Hecho cuando | Archivos (dueño único) |
|---|---|---|---|---|
| F0 | D-062; `docs/intercambio.md` con tablas, fórmulas de transformación, perfil de EDL y versiones de OTIO; firmas públicas vacías | Claude | Compila sin advertencias; revisión de lectura de agy sin objeciones abiertas | `Interchange/*.cs` (solo firmas), `docs/intercambio.md`, `docs/decisiones.md` |
| F1 | `Timecode` | codex (código), devin (tabla de casos en la misma rama, después) | ≥ 40 casos y la ida y vuelta de 24 h en < 1 s | `Timing/Timecode.cs`, `TimecodeTests.cs` |
| F2 | Formato 16, TC/carrete de los medios y reenlazado con datos desconocidos | Claude (código y esquema); agy después repasa `formato-proyecto.md` en la misma rama | Cuatro salidas del guardado (13/14/15/16) probadas; esquema v16 valida; un MOV de `Rodaje.TestMedia` con `-timecode 01:00:00;00` sale con su TC; reenlazado de un medio sin duración | `Model/MediaRef.cs`, `Sequence.cs`, `Project.cs`, `Serialization/`, `schema/`, `Rodaje.Media/Probe/`, `Editing/MediaRelink*.cs`, `Services/Relink/`, `docs/formato-proyecto.md` |
| F3 | Implementar `TimelineWalk`, `SequenceBuilder` (remapeo, fusión), `Fidelity`, `InterchangeLimits`, informe | Claude | Pruebas propias: aplanado con presupuesto y ciclos, solapes con transición, remapeo al importar dos veces, cifras de rendimiento medidas | `Interchange/` comunes |
| F4t | Muestras escritas a mano con valor esperado, corpus hostil, pruebas de rendimiento | agy (pruebas); devin (copiar los ejemplos `.otio` de OTIO con `NOTICE`) | Pruebas compilan contra las firmas de F0, marcadas `Skip` hasta que su lector exista | `tests/Rodaje.Core.Tests/Interchange/Fixtures/`, `…/Interchange/Samples*Tests.cs`, `…/Interchange/Hostile*Tests.cs` |
| F4a | EDL | codex | Ida y vuelta por propiedad (500 semillas); quita los `Skip` de EDL en F4t y pasan | `Interchange/Edl/`, `…/Interchange/EdlRoundTripTests.cs` |
| F4b | OTIO con `metadata.rodaje` y huellas | codex (después de F4a) | Ídem; Rodaje → OTIO → Rodaje idéntico en las 500 semillas; edición externa simulada | `Interchange/Otio/`, `…/OtioRoundTripTests.cs` |
| F4c | FCPXML 1.10 | codex, esfuerzo alto (después de F4b) | Ídem; compuestos como `ref-clip` dentro de `media`; transformación con la muestra 4000×3000 | `Interchange/Fcpxml/`, `…/FcpxmlRoundTripTests.cs` |
| F5 | Servicio, diálogos, menú, autoprueba | codex (servicio y sus pruebas) → Claude (diálogos y repaso) | `InterchangeServiceTests` en verde; `--autoprueba` pasa con el paso nuevo; AOT sin advertencias y ≤ 3 MB más; entrada en `docs/pruebas-a-mano.md` | `Rodaje.App/Services/Interchange/`, `Views/Interchange/`, `Shortcuts/InterchangeShortcuts.cs`, `TitleBar.xaml`, `SelfTestRun.Interchange.cs` |
| F6 | Revisión de bloque | agy (lectura) y codex (revisión de otro modelo) | Hallazgos arreglados o como tareas; `CHANGELOG.md`; guía | — |
| F7 (opcional) | FCP 7 XML (xmeml) para Premiere | codex | Igual que F4 | `Interchange/Xmeml/` |

Orden: F0 → (F1 ∥ F2 ∥ F4t) → F3 → F4a → F4b → F4c → F5 → F6. El peso del exe con `System.Xml` se mide ya en F0 (una llamada a `XmlReader` en `Rodaje.AotSmoke`). El plazo (dos o tres noches con la Cuadrilla) es una estimación, no un compromiso.

## 9. Riesgos

- **Variantes de cada programa.** Cada editor escribe la EDL y el FCPXML un poco distinto (carretes largos, comentarios, versiones 1.8 a 1.13). Mitigación: lector tolerante (ignora lo desconocido y lo informa) y escritor conservador (FCPXML 1.10, que leen Final Cut y Resolve actuales).
- **Sin muestras reales.** Sin archivos exportados por Resolve o Final Cut, las pruebas validan contra la especificación, no contra lo que esos programas aceptan. Es el riesgo mayor (ver pregunta 2).
- **Posición en FCPXML.** Un error de signo o escala en las fórmulas de la sección 3 pasa las pruebas de ida y vuelta (es simétrico). Se compara contra una muestra real antes de dar el mapeo por bueno; mientras tanto la transformación sale marcada como aproximada.
- **Ajustes de Rodaje que resucitan.** Si las huellas de `metadata.rodaje` cubrieran poco, un clip cambiado fuera recuperaría efectos que ya no le tocan. Mitigación: la huella incluye todo lo que el formato estándar puede expresar del clip, y hay pruebas de edición externa.
- **Reenlazado con EDL.** Un carrete no identifica un archivo único (varias tarjetas con el mismo nombre de clip). Mitigación: el TC desempata y, si sigue ambiguo, el diálogo pregunta.
- **Peso de `System.Xml` en el exe.** Si pasa de 3 MB, el FCPXML se escribe con `XmlWriter` solo (sin `XDocument`) o se pasa a un lector propio mínimo.
- **Formato 16.** Un Rodaje 1.0.x no abre un proyecto con TC de cámara; por eso solo se escribe 16 si hay TC.

## 10. Preguntas para Daniel (se avanza con la recomendada)

1. **¿AAF?** Recomendada: no en Rodaje; `.otio` y la guía explican cómo llegar a AAF con el adaptador de OTIO o con Resolve.
2. **¿Puedes exportar desde Resolve (gratis) una línea de tiempo corta a EDL, FCPXML y OTIO para usarla como muestra?** Recomendada: sí cuando puedas; mientras, muestras escritas a mano según la especificación y la transformación de FCPXML marcada como aproximada.
3. **¿FCP 7 XML para Premiere (F7) entra en este bloque?** Recomendada: después, como fase opcional, cuando el resto esté unido.
4. **¿Se nombran los otros programas en la interfaz?** Recomendada: el menú solo nombra formatos («EDL CMX 3600», «FCPXML», «OpenTimelineIO»); la guía dice con qué programas se probó, como uso descriptivo de compatibilidad, sin logos.
5. **¿Importar crea una secuencia nueva o un proyecto nuevo?** Recomendada: secuencia nueva en el proyecto abierto (un paso del historial; con el proyecto vacío equivale a uno nuevo).
6. **TC de inicio por defecto al exportar.** Recomendada: `01:00:00:00` si la secuencia no tiene uno propio.
7. **¿El color va como CDL en la EDL (`*ASC_SOP`)?** Recomendada: no en este bloque; solo si el etalonaje en Resolve pide conservar el primario.
8. **Al importar una EDL, ¿qué tasa por defecto?** Recomendada: la de la secuencia activa; 29,97 si la EDL dice `DROP FRAME`.

## 11. Decisión propuesta (para `docs/decisiones.md`)

## D-062. Intercambio de líneas de tiempo con EDL, FCPXML y OpenTimelineIO, en C# propio dentro del núcleo (15 de noviembre de 2026; propuesta, pendiente de Daniel; después de la 1.0.3)

- **Contexto.** Rodaje solo entregaba renders; no podía llevar un montaje a etalonaje o sonido ni abrir uno hecho en otro editor.
- **Decisión.** Importar y exportar EDL CMX 3600, FCPXML 1.10/1.11 y OpenTimelineIO en `Rodaje.Core/Interchange`, sin la librería de OTIO ni dependencias nuevas (`System.Xml` y `System.Text.Json`, compatibles con AOT). La importación crea una secuencia nueva y reutiliza el reenlazado existente. Cada formato declara qué conserva (`Fidelity`), y de esa misma regla salen el informe de pérdidas y las pruebas de ida y vuelta. Rodaje → OTIO → Rodaje no pierde nada gracias a `metadata.rodaje`. Formato de proyecto 16 con `startTimecode` en la secuencia y `timecode`/`reel` en el medio, escrito solo si se usan. AAF y FCP 7 XML quedan fuera.
- **Verificación.** Tablas de `Timecode`, ida y vuelta por propiedad con 500 semillas por formato, muestras con valor esperado, edición externa simulada, corpus hostil, rendimiento con 10 000 clips y paso nuevo de la autoprueba.

## 12. Revisión de otro modelo (ChatGPT, 15-nov, solo lectura)

Encargo: `D:\Programas de uso libre\mesa-agentes\datos\encargos\rodaje-intercambio-revision.md`; respuesta completa en `mesa-agentes\datos\trabajos\codex-rodaje-intercambio-revision-20261115-160826\final.txt`. Dio por buenas las cuatro cifras de drop frame, dejar AAF fuera y los conversores en Core. Trajo 15 hallazgos:

**Incorporados (ya en este documento):** garantía de `metadata.rodaje` acotada con bloque versionado y huellas (1); fusión con remapeo de identificadores y orden de dependencias (2); reenlazado con duración y tamaño desconocidos, TC en `RelinkProbe` y `TC − TC inicial` (3); curva de velocidad como aproximada y `LinearTimeWarp` sin cambio de duración (4); alineación de transiciones y solapes con transición que no se recortan (5); `DtdProcessing.Ignore` en vez de `Prohibit` (6); perfil de escritura de EDL, `M2` en cuadros por segundo, carretes sin colisiones, `BL` (7); tasa pedida al leer EDL y sin cuantizar el audio en FCPXML ni OTIO (8); marcadores en tiempo local y `lane` como aproximado (9); fórmulas de `adjust-transform` (10); versiones de clase de OTIO fijadas y normalización de tasas informada (11); `Fidelity` con proyecto y opciones, igualdad semántica, oráculos independientes y autoprueba que compara de verdad (12); `InterchangeLimits` con presupuesto de expansión y ciclos, rutas de red sin sondeo automático (13); dueño único por archivo, `Project.cs` y `MediaProbe.cs` en la lista y las cuatro salidas del guardado (14); cifras de rendimiento como hipótesis con corpus y medida de memoria definidos, sondeo sin cambiar `IMediaProber` (15).

**Descartado o recortado:** conservar la `metadata` ajena de otros programas (11): el modelo de Rodaje no tiene dónde guardarla y añadir un campo libre por clip solo para eso no compensa; se pierde y se informa. Cambiar `IMediaProber` a asíncrono con cancelación (15): basta un `Task.Run` por archivo con cancelación entre archivos, sin tocar la interfaz que usan otros. No normalizar `23.976` (11): se mantiene la normalización porque así escriben los programas que generan OTIO, pero ahora va al informe.
