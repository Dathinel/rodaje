# Intercambio con otros editores: EDL, FCPXML y OpenTimelineIO

Rodaje importa y exporta líneas de tiempo en **EDL CMX 3600**, **FCPXML 1.10** y **OpenTimelineIO** (`.otio`) para llevar un montaje a etalonaje, sonido u otro editor y traer uno hecho fuera (D-062). Todo vive en `src/Rodaje.Core/Interchange`, en C# propio, sin la librería de OTIO ni dependencias nuevas (`System.Text.Json` y un lector de XML propio, compatibles con Native AOT). Diseño completo y su revisión: [`propuestas/intercambio.md`](propuestas/intercambio.md).

## Piezas

| Pieza | Qué hace |
|---|---|
| `InterchangeFiles` | Fachada: `FormatOf`, `Read`/`Parse` (archivo → `InterchangeResult` listo para añadir al proyecto), `Serialize`/`Write` (secuencia → textos o archivos), `Preview` (solo el informe) |
| `TimelineDraft` | Lo que entrega un lector y lo que `Fidelity.Project` dice que debe entregar: un `Project` con medios y secuencias (compuestos y principal) con identificadores locales |
| `Fidelity` | Qué sobrevive en cada formato (la tabla de abajo). De ella salen el informe y la prueba de ida y vuelta: `Read(Write(s))` debe dar `Project(s)` (`Difference` las compara sin mirar identificadores) |
| `TimelineWalk` | Vista plana para escribir: por pista, clips y huecos seguidos con su medio, su compuesto y sus transiciones; aplana compuestos con presupuesto y detección de ciclos |
| `SequenceBuilder` | Fusión con el proyecto abierto: identificadores nuevos para todo, medios deduplicados por ruta y reutilizados, solapes sin transición resueltos con aviso, validación |
| `InterchangeReport` | Avisos `Info`, `Approximate` (aproximado) y `Lost` (perdido), con su clase, clip y tiempo; `Summary` agrupa y `ToText` lista con el TC |
| `InterchangeLimits` | Presupuestos de lectura: 256 MB, 200 000 objetos, 1 MB por texto, anidado ≤ 32, ≤ 200 000 clips al aplanar |
| `Edl/`, `Fcpxml/`, `Otio/` | Un lector y un escritor por formato. Los escritores no informan: el informe sale de `Fidelity` |

Importar crea **una secuencia nueva** en el proyecto abierto, en un solo paso del historial, y nunca reemplaza nada.

## Qué se conserva y qué se pierde

**sí**: ida y vuelta exacta; **aprox.**: con redondeo, va al informe como aproximado; **no**: se pierde y va al informe; **meta**: va en `metadata.rodaje` y solo lo recupera Rodaje.

| Elemento | EDL CMX 3600 | FCPXML 1.10 | OTIO |
|---|---|---|---|
| Cortes de una pista de video | sí (una EDL por pista) | sí (`spine`, `asset-clip`, `gap`) | sí |
| Varias pistas de video | una EDL por pista (`nombre.edl`, `nombre_V2.edl`…) | sí (`lane` positivos) | sí (`Stack` de `Track`) |
| Audio | hasta 4 canales (`A`, `A2`, `AA`…) en la EDL de V1; nivel no | sí, `lane` negativos; `adjust-volume` en dB y fundidos | pistas sí; nivel y fundidos **meta** |
| Fundido cruzado (`rodaje.crossfade`) | `D` | `transition` | `Transition` `SMPTE_Dissolve` |
| Alineación de la transición | sí (el evento `D` se parte en el cuadro de corte) | sí | `in_offset`/`out_offset` |
| Curva y borde congelado de la transición | no | no | **meta** |
| Otras transiciones | `W001` (barrido SMPTE): aprox. | fundido: aprox. | tipo propio: **meta** |
| Velocidad constante | `M2` en cuadros por segundo (2× a 25 fps es `050.0`; negativa en reversa) | `timeMap` | `LinearTimeWarp` |
| Curva de velocidad | media: aprox. | `timeMap` muestreado (error ≤ ½ cuadro): aprox. | media: aprox. + **meta** |
| Congelado | `M2 000.0` | `timeMap` de dos puntos iguales | `FreezeFrame` |
| Transformación | no | `adjust-transform`, `adjust-blend`: aprox. | **meta** |
| Recorte | no | `adjust-crop` | **meta** |
| Máscaras, inclinación, desenfoque de movimiento, color, LUT, curvas, HDR | no | no | **meta** |
| Títulos y generadores | hueco + comentario con el texto | hueco + marcador con el texto | `GeneratorReference` + **meta** |
| Clips compuestos | se aplanan: aprox. | `ref-clip` a una `sequence` de `resources` | `Stack` anidado |
| Multicámara | ángulo activo: aprox. | ángulo activo: aprox. | ángulo activo + **meta** |
| Marcadores de secuencia | `* LOC:` tras el evento que lo cubre | `marker` en el elemento del `spine` que lo cubre, en su tiempo local | `Marker.2` |
| Regiones | no | `chapter-marker` (solo inicio): aprox. | `Marker` con duración |
| Colores de clip, grupos, enlaces | no | enlaces aprox.; lo demás no | **meta** |
| Subtítulos | `.srt` aparte (`SubtitleFiles`) | `.srt` aparte (`caption`, más adelante) | `.srt` aparte + **meta** |
| Nombre y ruta del medio | `* FROM CLIP NAME:` y `* SOURCE FILE:` | `asset`/`media-rep src` (`file://`) | `ExternalReference.target_url` |
| Código de tiempo de cámara | carrete y TC de origen | `start` del `asset` y `tcFormat` | `available_range.start_time` |

**Ida y vuelta por OTIO.** El `Timeline` lleva en `metadata.rodaje` un bloque versionado (`{"v":1, "sequence", "sequences", "media", "huella"}`) con la secuencia completa, sus compuestos y sus medios, serializados con `ProjectJsonContext`. `huella` es el SHA-256 de la parte estándar en forma canónica. Si al leer coincide, se usa el bloque entero; si otro programa editó, manda lo estándar y cada clip recupera sus ajustes de Rodaje solo si su propia huella (medio, rango de origen, velocidad, activación) coincide. La `metadata` de otros programas se pierde y va al informe.

## Tiempo, tasas y código de tiempo

- Todo se convierte a `Time` (705 600 000 ticks/s). FCPXML (`"1001/30000s"`) se lee como fracción exacta. OTIO guarda `RationalTime{value, rate}` en coma flotante: 23,976, 29,97 y 59,94 (tolerancia 1e-3) se normalizan a `24000/1001`, `30000/1001` y `60000/1001` con un aviso `Info`; un redondeo que mueva un cuadro de video o más de una muestra de audio es aproximado.
- **`SmpteTimecode`** (`Rodaje.Core.Timing`; `Rodaje.Engine.Presentation.Timecode` es otra cosa: el texto del reloj en pantalla): cuadro + base nominal (24, 25, 30, 48, 50, 60) + drop frame. Drop frame solo a 29,97 (salta 2 números por minuto salvo cada 10) y 59,94 (salta 4), escrito con `;`; 23,976 siempre sin drop frame con base 24. Cifras de referencia: `00:01:00;02` = 1800 y `00:10:00;00` = 17 982 a 29,97; `00:01:00;04` = 3600 a 59,94; `23:59:59;29` = 2 589 407.
- **TC de inicio** de la secuencia: `Sequence.StartTimecode` (nulo = `00:00:00:00`); al exportar sin él se propone `01:00:00:00`.
- **TC de los medios:** `MediaRef.Timecode` y `MediaRef.Reel` (formato 16, solo escritos si tienen valor), leídos de `timecode` y `reel_name` de FFmpeg. Sin TC, el origen empieza en `00:00:00:00` y el carrete es el nombre del archivo.
- **Solo la EDL cuantiza:** un clip de audio entre cuadros se redondea y va al informe. Un medio a otra tasa: en EDL su TC se expresa a la tasa de la secuencia con aviso; en FCPXML y OTIO va con la suya.
- **Al leer**, el punto de entrada es `TC del evento − TC inicial del medio`; mientras el medio no se resuelve se guarda el TC y al reenlazarlo se recalcula el rango.

## Perfil de la EDL

Escritura: `TITLE:`, `FCM: DROP FRAME` o `NON-DROP FRAME`, eventos de 3 cifras (más de 999: 4, como Resolve, con aviso), carretes de 8 caracteres en mayúsculas sin espacios y sin colisiones (`TOMA01`, `TOMA01_2`…) con el nombre completo en `* FROM CLIP NAME:` y la ruta en `* SOURCE FILE:`, negro como `BL`, canales `V`, `A`, `A2`, `AA`, `AA/V`, `NONE`. Velocidad con `M2 <carrete> <cuadros por segundo> <TC de origen>`.

Lectura: la tasa la da el diálogo (por defecto, la de la secuencia activa; con `FCM: DROP FRAME`, 29,97). Cada evento guarda su carrete hasta que el reenlazado decide su archivo: se busca por `* SOURCE FILE`, luego `* FROM CLIP NAME`, luego carrete = nombre de archivo sin extensión, y el TC desempata.

## OTIO: clases y versiones

Se leen y escriben `Timeline.1`, `Stack.1`, `Track.1`, `Clip.2` (`media_references` + `active_media_reference_key`; se lee también `Clip.1` con `media_reference`), `Gap.1`, `Transition.1`, `Marker.2`, `ExternalReference.1`, `MissingReference.1`, `GeneratorReference.1`, `LinearTimeWarp.1`, `FreezeFrame.1`, `RationalTime.1` y `TimeRange.1`. Una clase o versión desconocida se convierte en hueco con aviso. El esquema de OTIO es Apache-2.0: se usa con atribución en `LICENCIAS.md`, sin su código.

## Transformación en FCPXML

`position` va en porcentaje del alto de la secuencia con y hacia arriba: `x_px = posX · alto/100`, `y_px = −posY · alto/100` (Rodaje: píxeles de la secuencia, y hacia abajo). `scale` es un factor por eje (identidad `1 1`): `scale · scaleX`, `scale · scaleY`. `rotation` en grados con el signo contrario. `anchor` también en porcentaje del alto. El conformado se fija con `conform-fit` en los dos lados. Hasta compararla con una muestra real de Final Cut o Resolve, la transformación sale marcada como aproximada.

## XML propio, no `System.Xml`

Medido en F0 con `Rodaje.AotSmoke` publicado con AOT: `XmlReader` suma 3,5 MB al exe y `XmlWriter` 0,35 MB más (5,57 → 9,47 MB), por encima del objetivo de 3 MB. El FCPXML se lee y se escribe con un lector y un escritor mínimos propios (`Interchange/Xml`): elementos, atributos, texto, comentarios, `CDATA` y las cinco entidades predefinidas más las numéricas; la DTD se salta sin leerla y cualquier otra entidad es un error. Es la mitigación prevista en la sección 9 del diseño.

## Seguridad

El archivo importado no es de confianza. El lector de XML propio salta la DTD sin interpretarla (un FCPXML legítimo empieza con `<!DOCTYPE fcpxml>`), no resuelve nada externo ni entidades declaradas y lee 256 MB como mucho. Los presupuestos de `InterchangeLimits` se comprueban al leer; aritmética `checked`; tasas fuera de 1/5–1000, rangos invertidos, duraciones negativas y NaN dan un error claro o un aviso, nunca una excepción sin capturar ni un bucle. Las rutas importadas solo se sondean, y solo las locales: una UNC o de red queda como medio que falta.

## Estado

| Fase | Qué | Estado |
|---|---|---|
| F0 | Firmas, este documento y D-062 | hecho |
| F1 | `SmpteTimecode` | hecho |
| F2 | Formato 16 (`startTimecode`, `timecode`, `reel`), TC de los medios, reenlazado con datos desconocidos | hecho |
| F3 | `TimelineWalk`, `SequenceBuilder`, `Fidelity.Difference`, XML propio, límites | hecho |
| F4 | EDL, OTIO, FCPXML | pendiente |
| F5 | Servicio, diálogos, menú y autoprueba | pendiente |

**Cifras de F3** (`InterchangeCommonTests`, categoría `Rendimiento`; corpus de la semilla 1: 10 000 clips en 8 pistas y 400 medios; Release, prioridad baja, con el equipo ocupado por otras compilaciones): recorrer la secuencia 63 a 265 ms, fusionarla con un proyecto (identificadores nuevos, medios y validación) 255 a 650 ms, de los que validar es 157 a 576 ms; 16 a 18 MB asignados. Los umbrales de la prueba son los objetivos del diseño con el margen ×3.

**Reglas al fusionar** (`SequenceBuilder`): un clip que pisa a otro sin transición pasa a una pista nueva justo encima («V1 (solapes)»), sin perder nada; un medio provisional dura lo que usan sus clips; un clip que se sale de un medio conocido se recorta (o no se importa si empieza después de su final). Al aplanar compuestos (`TimelineWalk`), en el video manda la pista más alta del compuesto, en el audio solo entra su primera pista, un clip con velocidad tapado en parte se deja fuera y las transiciones de dentro se pierden; todo va al informe.
