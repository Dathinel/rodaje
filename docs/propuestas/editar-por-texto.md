# Editar por el texto de la transcripción

Propuesta de diseño para Rodaje (bloque aprobado por Daniel el 15-nov, **después de la 1.0.3**). Solo diseño: no hay código. Base leída: `main` en `77f2bd1c`. Decisión propuesta: **D-061** (si otra propuesta de esta misma tanda se registra antes, toma el siguiente número libre; el formato de proyecto igual: **16** o el siguiente libre).

## 1. Qué problema resuelve

En entrevistas, clases, pódcast con video y tutoriales, el primer corte es quitar lo que sobra de lo que se *dice*: frases repetidas, arranques en falso, muletillas, pausas largas. Hoy en Rodaje eso se hace escuchando y cortando con la cuchilla. Con el texto a la vista, quien edita lee una hora de material en diez minutos, selecciona la frase que sobra, pulsa Supr y la línea de tiempo se cierra sola con el video y el audio enlazados. Lo que se gana es tiempo en el primer corte, no precisión: el retoque fino sigue en la línea de tiempo.

**Cómo lo hacen otros (solo ideas de producto, sin código ni recursos):**

| Programa | Idea que tomamos | Idea que no |
|---|---|---|
| Premiere Pro (edición basada en texto) | Panel de transcripción ligado a la secuencia; seleccionar texto y borrar, extraer o insertar; buscar; detectar pausas y muletillas; hablantes con nombre | Escribir texto libre para «editar»: no lo permite, y hace bien |
| DaVinci Resolve (transcripción en la página Editar) | Transcribir por medio una vez y reutilizarlo en todas las secuencias; quitar silencios desde el texto; los subtítulos salen de la misma transcripción | — |
| Final Cut Pro | Transcribir a subtítulos sin otra pasada | No trae edición por texto propia (depende de extensiones) |
| Descript y similares | Lo descartado se ve tachado y se puede recuperar | Editar texto libre que sintetiza voz: fuera de alcance |

**Proyectos abiertos estudiados (solo su lógica):**

- **Kdenlive** (GPLv3): voz a texto con Vosk o Whisper en scripts de Python; el texto sirve para crear subtítulos y para marcar zonas que se insertan en la línea de tiempo desde el medio de origen. Lógica útil: la transcripción es *del medio*, no del clip. No se copia nada.
- **Shotcut** (GPLv3): voz a texto con whisper.cpp que solo genera subtítulos; no edita por texto.
- **OpenTimelineIO** (Apache-2.0): no tiene transcripciones; los metadatos libres por clip podrían llevarla en un intercambio futuro. Fuera de este bloque.
- **Olive** (GPLv3): sin transcripción.

## 2. Cómo encaja en Rodaje

### 2.1 Lo que ya existe y se reutiliza

| Pieza | Dónde | Para qué sirve aquí |
|---|---|---|
| Transcripción por palabra (Whisper, DTW, `OnsetRefiner`) | `modules/Rodaje.Module.Subtitles` (`Transcript.cs`, `WordBuilder`) | Sin cambios de protocolo: `subtitulos` ya devuelve palabras con tiempo y probabilidad |
| Bajar el audio y llamar al módulo | `src/Rodaje.App/Views/Subtitles/Logic/AutoSubtitles.cs` (`ModuleMedia`, `ModuleManager`) | Se separa la parte «transcribir un medio» de la de «poner subtítulos en la secuencia» |
| Quitar tramos con ripple y enlazados | `AnalysisEdits.RemoveSpans` (`src/Rodaje.Core/Editing/AnalysisEdits.cs`) y `Edits.RemoveSilences` | Es el borrado por texto entero: corta en los bordes ajustados al cuadro hacia dentro, borra con ripple o deja hueco, respeta `RippleScope` |
| Mapa de tiempo del clip | `ClipTimeMap.TimelineOf` / `Clip.ToSourceTime` | Lleva cada palabra (en tiempo del medio) a la secuencia, con velocidad, curva y reversa |
| Pegar insertando | `TimelineEdits.Paste(..., PlacementMode.Insert)`, `ClipClipboard` | Mover una frase = cortar + pegar insertando |
| Silencios y voz | `Rodaje.Analysis/Silence` (`LevelEnvelope`, `SilenceDetector`), `Ducking/VoiceActivity` | Ajustar cada corte al punto más bajo del hueco; encontrar sonidos sin palabra (muletillas que Whisper omite) |
| Palabra activa | `SubtitleEdits.ActiveWord` | Mismo criterio («la última que ya empezó»), pero con búsqueda binaria |
| Historial | `UndoStack`, `SequenceEditCommand` (instantáneas), `RelinkMediaCommand` como modelo de comando de proyecto | Un solo Ctrl+Z para cortes y para correcciones de texto |
| Multicámara | `MulticamEdits.SwitchAngle`, `MulticamAngle(MediaId, Offset)` | El texto sale del audio guía; los ángulos siguen por sus desfases |

### 2.2 Modelo: la transcripción es del medio, en tiempo del medio

La clave del diseño. Una transcripción se guarda **una vez por medio** (`MediaRef`), con tiempos en ticks del origen. Cualquier clip de ese medio, en cualquier secuencia, con cualquier recorte o velocidad, muestra su parte proyectando las palabras con su `ClipTimeMap`. Así:

- cortar, recortar, mover o cambiar la velocidad de un clip **actualiza el texto sin hacer nada** (el documento es una función pura de la secuencia y las transcripciones);
- corregir una palabra o asignar un hablante vale para todos los clips de ese medio;
- los «Subtítulos automáticos» de un clip ya transcrito salen al instante, sin otra pasada de Whisper.

Tipos nuevos en `src/Rodaje.Core/Model/MediaTranscript.cs` (un tipo por archivo si el revisor lo pide, como en #486):

```text
MediaTranscript   Id, MediaId, Language ("es"), Model ("large-v3-turbo-q5_0"), Created (UTC),
                  MediaDuration (para saber si quedó vieja tras reenlazar),
                  Words: List<TranscriptWord>, Speakers: List<TranscriptSpeaker>
TranscriptWord    Start, End (Time, origen del medio), Text, Confidence (0–1),
                  SpeakerId?, ParagraphStart (bool: empieza párrafo)
TranscriptSpeaker Id, Name, Color? (mismo formato que Clip.Color)
```

Clases selladas con `Clone()` y `ContentEquals()` como `GeneratorWord`; nada de reflexión. `Project` gana `List<MediaTranscript> Transcripts`.

### 2.3 Formato de proyecto

- `Project.CurrentSchemaVersion` 15 → **16**; esquema nuevo `schema/rodaje-proyecto.v16.schema.json` (los anteriores se conservan) y sección nueva en `docs/formato-proyecto.md`.
- **Se mantiene la regla de compatibilidad** de `ProjectSerializer.Serialize`, con la condición nueva en cabeza: `project.Transcripts.Count > 0 ? 16 : (HDR ? 15 : imagen fija ? 14 : 13)`. Al quitar la última transcripción, el siguiente guardado vuelve solo a 15, 14 o 13. Un proyecto sin transcripción lo sigue abriendo la 1.0.2.
- Campo de primer nivel `transcripts`: `[{ id, mediaId, language, model, created, mediaDuration, speakers: [{id, name, color}], words: [{s, e, t, c, sp?, p?}] }]`. Las palabras usan claves cortas porque son el 99 % del peso: una hora de habla son unas 9 000 palabras y ~0,5 MB de JSON (el límite de archivo es 256 MB, `MaxProjectFileBytes`).
- `ModelValidator.Transcripts.cs` (nuevo, como `ModelValidator.Words.cs`): `mediaId` existe; palabras ordenadas, `0 ≤ s ≤ e ≤ mediaDuration`; texto sin caracteres de control ni vacío; `speakerId` declarado; ids únicos; como mucho una transcripción por medio y 200 000 palabras. Lo que llega de fuera se valida igual que el resto del `.rodaje`.
- `ProjectJsonContext` (generador de código de System.Text.Json) gana los tipos; `ProjectMigrator` no cambia (las versiones viejas no tienen transcripciones).
- `RemoveMediaCommand` quita también su transcripción (y la devuelve al deshacer). `RelinkMediaCommand` la conserva; si la duración del medio nuevo difiere más de un cuadro de `mediaDuration`, el panel la marca «puede no coincidir» y ofrece transcribir de nuevo.
- Copiar clips a otro proyecto (`ClipClipboard`) **no** lleva la transcripción en este bloque (pregunta 6).

### 2.4 Núcleo sin interfaz (`Rodaje.Core`, probado sin WinUI)

| Archivo nuevo | Qué hace |
|---|---|
| `Editing/TranscriptDocument.cs` | `TranscriptDocument.Build(Project, Sequence, guideTrackId)` → lista ordenada de `DocWord(SeqStart, SeqEnd, Text, ClipId, TranscriptId, WordIndex, SpeakerId)` agrupada en `DocParagraph` (cambio de clip, de hablante, `ParagraphStart` o pausa > 1,5 s). Una palabra entra si su centro (en tiempo del medio) cae en `[SourceIn, SourceOut)`: se filtra en el origen **antes** de `ClipTimeMap.TimelineOf`, que lleva al borde del clip lo que queda fuera. En reversa `TimelineOf(Start) > TimelineOf(End)`: el inicio y el fin proyectados se toman con `Time.Min`/`Time.Max`, como ya hace `AutoSubtitles`, y se ordena por tiempo de la secuencia; los congelados (`Freeze`) y las imágenes fijas no muestran texto. `ActiveWord(Time)` por búsqueda binaria; `Find(string, opciones)` sin distinguir mayúsculas ni tildes, sobre un texto normalizado que se arma una vez con el documento (sin crear cadenas por búsqueda) |
| `Editing/TranscriptEdits.cs` | `SpansOf(doc, selección)` → tramos de la secuencia por clip (de la mitad del hueco anterior a la mitad del siguiente, para no comer sílabas); `DeleteWords` reparte por clip y llama a `AnalysisEdits.RemoveSpans` (que toma un solo clip) **clip a clip, del que empieza más tarde al que empieza antes**: el ripple de un clip solo corre lo que va a su derecha, así que los tramos ya calculados de los clips anteriores siguen valiendo; y dentro de cada clip `RemoveSpans` ya va de derecha a izquierda y conserva el id del trozo izquierdo. `MoveWords(selección, destino)` corta en los bordes (`AnalysisEdits.SplitAt`), copia a `ClipClipboard` **el grupo enlazado entero** (`TrackOps.ExpandSelection`: video y audio juntos, porque `Paste` renueva los `LinkId` con `DerivedLinkId` y solo mantiene unidos los clips copiados juntos), borra con ripple y pega con `PlacementMode.Insert` en el borde de la palabra destino (el destino se recalcula tras el borrado); `FillerCandidates(doc, idioma)`; `Pauses(doc, mínimo)` |
| `Editing/FillerWords.cs` | Listas por idioma (es, en, pt, los de `AutoSubtitles.Languages`): **seguras** (eh, em, mm, ehm; uh, um, hmm; é, hã) que vienen marcadas, y **dudosas** (o sea, este, bueno, pues; like, you know; tipo, né) que se ofrecen sin marcar porque también son palabras de verdad |
| `Commands/Edits.Transcript.cs` | `Edits.DeleteWords`, `MoveWords`, `RemoveFillers`, `RemovePauses`: cada uno un `SequenceEditCommand` («Borrar texto», «Mover frase»…) |
| `Commands/TranscriptCommands.cs` | `SetTranscriptCommand` (poner, reemplazar o quitar la de un medio) y `EditTranscriptCommand` (corregir palabras, partir párrafo, hablantes) con instantánea antes/después de esa transcripción, como `RelinkMediaCommand`. Mismo `UndoStack` que los cortes |

**Pista guía.** El documento se lee de una pista: por omisión, la primera pista de audio con clips transcritos (en multicámara, la del audio de referencia). Los clips de video enlazados se cortan con ella por los enlaces, igual que en «Quitar silencios». Con varias pistas transcritas (dos micrófonos), el panel permite elegir la guía; mezclarlas en un solo documento queda fuera (pregunta 3).

**Alcance del ripple.** El mismo selector que «Quitar silencios» (`RippleScope`), con la misma opción por omisión (`Track`: la pista guía, sus enlazados y los enlazados de lo que se corre). Con `AllTracks`, una música de fondo que atraviesa la frase hace fallar el borrado entero (`TrackOps.EnsureNothingStraddles`, «hay un clip que atraviesa ese tramo») sin dejar nada a medias; el panel lo explica y sugiere bloquear esa pista o usar `Track`. Partir la música sola queda fuera (pregunta 8).

**Multicámara.** El documento sale de la pista guía de audio; cambiar de ángulo (`SwitchAngle`) solo cambia clips de video, así que el texto y sus párrafos no se mueven aunque el ángulo nuevo no tenga transcripción.

**Puntos de corte.** Los tiempos de Whisper con DTW fallan unos ±40–80 ms. `SpansOf` coloca el corte en la mitad del hueco entre palabras; si hay audio analizado, la app pasa un `Func<Time, Time, Time>` («el punto más bajo entre a y b») calculado con `LevelEnvelope` (bloques de 5 ms), y `RemoveSpans` lo ajusta al cuadro como hoy. El núcleo no depende de `Rodaje.Analysis`: recibe los puntos ya elegidos.

### 2.5 App (`Rodaje.App`)

| Archivo | Cambio |
|---|---|
| `Services/Transcripts/MediaTranscriber.cs` (nuevo) | Sale de `AutoSubtitles`: baja el audio **del medio entero** (no del clip), llama a `subtitulos` con el perfil de IA (D-037) y devuelve un `MediaTranscript` en ticks del origen. Pasa a `prompt` las muletillas seguras del idioma para que Whisper no las limpie |
| `Views/Subtitles/Logic/AutoSubtitles.cs` | Si el medio ya tiene transcripción, la proyecta por `ClipTimeMap` y pone los subtítulos sin llamar al módulo; si no, transcribe con `MediaTranscriber` y la guarda en el mismo paso del historial (`CompositeCommand`) |
| `Views/Transcript/TranscriptPanel.xaml(.cs)` (nuevo) | Panel acoplable como `MarkersPanel`: `ItemsRepeater` virtualizado de párrafos; cada párrafo un `TextBlock` con un `Run` por palabra. **No es un cuadro de texto libre**: se selecciona por palabras (clic, Mayús+clic, arrastre, Ctrl+flechas) y se actúa (Supr borra con ripple, Retroceso deja hueco, Ctrl+X/Ctrl+V mueve, F2 corrige la palabra, Alt+1…9 asigna hablante). Doble clic lleva el cursor a la palabra |
| `Views/Transcript/Logic/TranscriptPanelModel.cs` (nuevo, sin WinUI) | Selección, búsqueda, palabra activa, lista de muletillas y pausas con casillas; reconstruye el documento solo cuando cambia la secuencia (`EditorSession`) y resalta solo cuando cambia la palabra activa |
| `Services/EditorSession.Transcript.cs` (nuevo) | Ejecuta los comandos y mantiene la selección de la línea de tiempo y la del panel coherentes (seleccionar palabras selecciona sus clips) |
| `Shell/SelfTestRun.Transcript.cs` (nuevo) | La autoprueba carga una transcripción sintética del clip de prueba, abre el panel, borra una palabra, deshace y comprueba duración y texto; sin módulo ni GPU |
| Herramientas, menú del clip y atajos | «Transcribir», «Transcripción» y «Quitar muletillas…» entran en `QuickTools` y en el menú del clip **con la misma regla de estado** (atenuado si falta el módulo), que la revisión de `interfaz-flujos-ia.md` pide unificar |

**Sincronía en los dos sentidos.** Cursor → texto: `ActiveWord` en cada cambio de posición, sin tocar la interfaz si la palabra no cambió; el panel se desplaza solo si la palabra sale de la vista y el usuario no está desplazando. Texto → línea de tiempo: el documento se vuelve a construir tras cada edición del historial, sea del panel o de la línea de tiempo, así que una cuchillada en la línea de tiempo parte el párrafo y un recorte esconde sus palabras.

**Lo descartado.** Las palabras del medio que quedaron fuera de los clips no se ven por omisión. Con «Mostrar lo quitado» aparecen tachadas y en gris entre las vecinas, y «Recuperar» las devuelve con un pegado insertando desde el medio (fase 6).

### 2.6 Rendimiento (cifras objetivo, medidas en pruebas con reloj y `-Medir`)

| Qué | Objetivo | Cómo se mide |
|---|---|---|
| `TranscriptDocument.Build`, 1 h de habla (9 000 palabras) en 200 clips | ≤ 15 ms (CPU; igual con o sin GPU) | Prueba de categoría Rendimiento (solo `nocturna.yml`) |
| `ActiveWord` | ≤ 2 µs por llamada | Misma prueba |
| `Find` en 1 h | ≤ 5 ms | Misma prueba |
| Borrar 50 frases sueltas con ripple y enlazados (un paso) | ≤ 150 ms incluido el historial | Misma prueba |
| Abrir el panel con 1 h transcrita | ≤ 250 ms hasta el primer párrafo visible | Autoprueba con `-Medir` |
| Reproducción con el panel abierto | Sin cuadros perdidos de más frente a panel cerrado (RTX 3050 y WARP) | `-Medir` en los dos |
| Guardar y abrir 1 h transcrita | +≤ 30 ms y +≤ 0,6 MB | Prueba de serialización |
| Transcribir | Lo del módulo (D-034): turbo en la GPU; small en CPU a 3,9× tiempo real. Falta la cifra del RTX 3050 con medio entero y `parallel` 4: se apunta en `docs/guia` | Medición a mano, una vez |

### 2.7 Native AOT

Todo con tipos concretos y el contexto de System.Text.Json generado; las listas de muletillas son arrays estáticos, no recursos con reflexión. `Rodaje.AotSmoke` gana un caso que abre y guarda un proyecto v16.

## 3. Fases

Cada fase en su rama y worktree; se unen por PR con CI verde y squash. Las que no comparten archivos van en paralelo (columna «Depende»).

| Fase | Qué | Quién | Depende | Terminado cuando |
|---|---|---|---|---|
| 0 | D-061 en `decisiones.md`, sección v16 en `formato-proyecto.md`, esquema v16 y **el contrato de tipos**: `MediaTranscript`, `TranscriptWord`, `TranscriptSpeaker`, `DocWord`, `DocParagraph` y las firmas públicas de `TranscriptDocument` y `TranscriptEdits` con cuerpos que lanzan `NotImplementedException` | Claude (decisión, formato y contrato); Devin copia el esquema v15 a v16 y añade `transcripts` siguiendo la sección | — | El esquema valida el ejemplo de la sección; `ProjectSchemaTests` pasa con v13–v16; el contrato compila sin advertencias. Desde aquí las fases 1, 2 y 3 van en paralelo sin tocar los mismos archivos |
| 1 | Tipos `MediaTranscript`, serialización, validador, regla de versión mínima, `RemoveMediaCommand`/`RelinkMediaCommand` | ChatGPT (codex) | 0 | Ida y vuelta byte a byte; proyecto sin transcripción sigue en 15/14/13; validador rechaza los casos hostiles (desorden, fuera de duración, inicio después del fin, hablante sin declarar, medio inexistente, texto vacío o con control, duplicados, >200 000); AOT sin advertencias |
| 2 | `TranscriptDocument` (proyección, párrafos, palabra activa, búsqueda) | ChatGPT (codex) | 0 | Pruebas de propiedades: con clips en velocidad 0,5×/2×, curva y reversa, cada palabra proyectada vuelve a su origen con `ToSourceTime` a ±1 tick; cortar un clip no cambia el texto del documento; prueba de rendimiento con las cifras de 2.6 |
| 3 | `TranscriptEdits` + comandos: borrar, mover, muletillas, pausas | **Claude** (lo delicado: tramos, orden de cortes, mover con ripple) y ChatGPT para el resto | 0 (las pruebas construyen el documento a mano hasta que la 2 esté unida) | Invariantes con datos aleatorios (Gemini escribe esta batería): tras borrar, el documento es el de antes menos lo borrado; la duración baja exactamente lo quitado (ajustado al cuadro); video y audio enlazados siguen en sincronía (`LinkSync`); deshacer devuelve la secuencia idéntica; mover una frase y moverla de vuelta deja el mismo texto |
| 4 | `MediaTranscriber`, `AutoSubtitles` desde la transcripción guardada, comandos de transcripción | ChatGPT (codex); Claude revisa el reparto con `AutoSubtitles` | 1 | `AutoSubtitlesTests` siguen verdes sin tocarlas; con transcripción guardada no se llama al módulo (doble falso del `ModuleManager` que cuenta llamadas); prueba del módulo fuera del árbol de procesos (`Invoke-CimMethod`) con el corpus existente |
| 5 | Panel, modelo del panel, sesión, atajos, autoprueba | ChatGPT (codex) la interfaz; Gemini las pruebas de `TranscriptPanelModel` | 2, 3 | `TranscriptPanelModelTests` (selección, búsqueda, palabra activa, muletillas) sin WinUI; `--autoprueba` pasa con el paso nuevo; cifras de abrir el panel y reproducir de 2.6 medidas en el RTX 3050 y en WARP |
| 6 | Hablantes (asignar, renombrar, color en el panel), «Mostrar lo quitado» y «Recuperar» | ChatGPT; Devin las listas de muletillas por idioma y los renombrados | 5 | Asignar hablante en un clip lo muestra en todos los clips del mismo medio; recuperar una frase deja el documento como antes de borrarla |
| 7 | Guía (`docs/guia/editar-por-texto.md`), `pruebas-a-mano.md`, `CHANGELOG.md` | Gemini (agy) la guía; Devin el changelog | 5 | Revisión de bloque de todo el bloque por otro modelo (regla 4 de la raíz) |

Cada archivo tiene una sola fase dueña: `Project.cs`, `ProjectJsonContext.cs`, `ProjectSerializer.cs` y los comandos de medios son de la 1; `TranscriptDocument.cs`, de la 2; `TranscriptEdits.cs`, `FillerWords.cs` y `Edits.Transcript.cs`, de la 3; `AutoSubtitles.cs`, de la 4. El contrato de la fase 0 es lo único compartido, y no cambia sin avisar a las ramas abiertas.

## 4. Riesgos

1. **Whisper limpia las muletillas.** Los modelos grandes tienden a omitir «eh», «em» y los arranques en falso. Mitigación: las muletillas en `prompt` y, en la lista de «Quitar muletillas», los tramos con voz (`VoiceActivity`) sin ninguna palabra encima, mostrados en el texto como «[…]». Si aun así se escapan, se dice en la guía.
2. **Cortes que comen sílabas o dejan chasquidos.** Mitad del hueco + punto más bajo del nivel; si el hueco entre palabras es menor de un cuadro, el corte cae en el cuadro más cercano y el panel lo marca «corte justo». Fundido corto de audio en cada corte: pregunta 2.
3. **Transcripción vieja.** Reenlazar a otro archivo, o un medio de duración libre (imagen fija), invalida tiempos: la imagen fija no se transcribe; reenlazar con otra duración avisa.
4. **Rendimiento del panel.** Miles de `Run` en WinUI cuestan: virtualización por párrafo obligatoria; si un párrafo pasa de 400 palabras, se parte al construir el documento.
5. **Peso del historial.** `SequenceEditCommand` guarda la secuencia entera dos veces por paso; las transcripciones no van en esas instantáneas (son del proyecto), así que un borrado por texto pesa lo mismo que un borrado normal.
6. **Mover frases entre clips de medios distintos** con enlaces cruzados puede dejar el audio y el video desfasados si una pista está bloqueada: `RemoveSpans` ya falla con `EditException` en pistas bloqueadas; mover hace lo mismo y no deja nada a medias (instantánea de antes).

## 5. Preguntas para Daniel (se avanza con la recomendada)

1. **¿Dónde vive la transcripción?** Recomendada: **dentro del `.rodaje`** (formato 16), porque es trabajo de quien edita (correcciones, hablantes) y debe viajar con el proyecto. Alternativa: archivo aparte en `%LOCALAPPDATA%\Rodaje` como las carpetas de la biblioteca: no sube el formato, pero se pierde al copiar el proyecto.
2. **¿Fundido de audio en cada corte hecho desde el texto?** Recomendada: **no en este bloque**; corte en el punto más bajo y medir si se oyen chasquidos en material real. Si se oyen, un fundido de 2 cuadros como opción.
3. **¿Varias pistas transcritas en un solo documento (dos micrófonos)?** Recomendada: **no**, una pista guía elegible. Mezclarlas obliga a decidir qué se corta cuando dos personas hablan a la vez.
4. **¿Hablantes automáticos (diarización)?** Recomendada: **manuales en este bloque**. Lo automático necesita otro modelo (de huellas de voz) con licencia y peso a revisar, en un módulo: decisión aparte.
5. **¿Editar texto libre?** Recomendada: **no**; solo seleccionar y actuar, y F2 para corregir una palabra. Escribir texto nuevo no puede crear audio sin voz sintética.
6. **¿Copiar clips a otro proyecto lleva la transcripción?** Recomendada: **no por ahora**; se transcribe de nuevo o se reutiliza si el medio ya la tiene allí.
7. **¿Entra en la 1.1 o en la 1.2?** Recomendada: **1.2**, porque la 1.1 ya está entera en `main` y espera su puerta; esto sube el formato de proyecto.
8. **Con «todas las pistas», ¿partir la música que atraviesa una frase borrada?** Recomendada: **no**; falla entera con un aviso claro, como hoy el ripple de todas las pistas. Partirla cambia la música sin que se haya pedido.

## 7. Revisión de otro modelo (Gemini, solo lectura, 15-nov)

Encargo: `mesa-agentes\datos\encargos\rodaje-editar-por-texto-revision.md`. Lo que se incorporó:

- **Música que atraviesa con `AllTracks`** (`TrackOps.EnsureNothingStraddles`): confirmado; ahora el diseño lo dice, el panel lo explica y hay pregunta 8.
- **Mover rompe enlaces si no se copia el grupo**: confirmado (`Paste` renueva `LinkId` con `DerivedLinkId`); `MoveWords` copia el grupo enlazado entero con `TrackOps.ExpandSelection`. Se mantiene el portapapeles en vez de una primitiva nueva: ya mantiene unidos los enlaces de lo que se copia junto.
- **Reversa en la proyección**: se pide `Time.Min`/`Time.Max` explícito, como en `AutoSubtitles`.
- **Expresión de la versión**: escrita tal cual en 2.3.
- **Fases 2 y 3 dependientes**: el contrato de tipos pasa a la fase 0, y cada archivo tiene una fase dueña.
- **`Find`**: texto normalizado una vez por documento.
- **Congelados y multicámara**: dicho en 2.4.

Lo que se descartó:

- **«`RemoveSpans` de varios clips desplaza los tramos de los anteriores»**: no ocurre si se va del clip que empieza más tarde al que empieza antes, porque el ripple solo corre lo que va a la derecha. No hace falta una primitiva nueva; el diseño ahora lo dice explícito y la fase 3 lo prueba (la duración baja exactamente lo quitado).

## 6. Decisión propuesta

**D-061. Editar por el texto: la transcripción es del medio y la línea de tiempo manda (15 de noviembre de 2026; propuesta, pendiente de Daniel; después de la 1.0.3).** Una transcripción por medio, en tiempo del origen, guardada en el proyecto (formato 16, solo si hay alguna); el panel es una vista calculada de la secuencia, no un segundo modelo; borrar y mover texto son las ediciones del núcleo de siempre (`AnalysisEdits.RemoveSpans`, pegar insertando) en un solo paso del historial; sin texto libre ni hablantes automáticos en este bloque.
