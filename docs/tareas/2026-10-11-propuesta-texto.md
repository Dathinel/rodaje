# Entrega: propuesta del formato de estilos de texto (tarea 8)

Rama `claude/propuesta-texto`. Solo documento: `docs/propuestas/texto.md` (nuevo) y esta entrega. Sin código; no se compiló ni se corrieron pruebas, y no se omitió ninguna (omitidas antes: 0; después: 0).

## Hecho
Propuesta de la fase 5b con los seis puntos pedidos: clips generadores y formato 5, contenido de `text-style` (`rodaje.textstyle` v1), render con DirectWrite/Direct2D, fuentes y licencias, edición sobre el lienzo y base de los subtítulos, más plan de trabajo, ventajas y desventajas, y 3 preguntas para Daniel.

## Pendiente
Decidido en D-029 (11-oct). Las cifras de costo del render son hipótesis sin medir. La lista de fuentes candidatas necesita verificar licencia y nombre reservado una por una antes de publicar nada.

## Decisiones
Decisiones (D-029): formato 5 aparte del 4, estilos como primitivas sobre el bloque entero, textura RGBA compuesta como capa, clips de texto como unidad de subtítulo.

## Posibles choques
La 5c cambia `Clip` y el formato 4 (ya unido en `main`): la 5b debe partir de `main` después de la 5c. La rama `claude/pistas-por-defecto` (tarea 7) no toca estos archivos.

## Qué debe comprobar Daniel en Windows
Nada: no hay código. Solo leer la propuesta y responder.

## Cambios tras la revisión de Daniel (comentario del PR #11)
Se atendieron los 7 puntos en `docs/propuestas/texto.md`: tabla de lo que cambia en el código (puntos reales de `Edits`, deshacer, transiciones, plan y esquema), regla de `sourceIn` con rebase, corrección de que el color/LUT/viñeta no se reutilizan (variantes RGBA o limitarlos en la v1), `LayerFrame` con descriptor abstracto, dibujo en el pre-roll, tamaño nativo con ancla, AOT de `IDWriteTextRenderer` como riesgo real con un prototipo previo, riesgos nuevos (RTL, accesibilidad, unidades, keyframes, referencia/copia) y la sección de fuentes (no modificar, `LICENCIAS.md`, `docs/legal.md`, Roboto/Open Sans por verificar) y la coordinación del formato 5 con la fase 6. Las líneas citadas por Daniel vienen de su copia de `main`: aquí se verificaron los nombres, no los números de línea.

## Segunda ronda de cambios (comentario del PR #11)
Nueva sección 7 (exportación y fase 6, formato 5 coordinado, rol de pista); animaciones de entrada y salida ancladas a los bordes con «convertir en keyframes» opcional; costo de rebase frente a `sourceIn` negativo (recomendado el segundo); `LayerFrame` con `DecodedFrame` o descriptor y criterio de reservas en `Tick`; prototipo de AOT que compara `ComWrappers` con la vtable a mano, ya en el plan; frases falsas y meta-comentarios quitados, `PsGraded` en `Effects/EffectShaders.cs`; huecos de fuentes (varios archivos por familia, superficie de ataque, topes, `\n`, escala de `size` entre formas, rol de pista); preguntas 2 y 3 reescritas. El costo del rebase viene de la revisión de Daniel y de leer `TrimmingSupport`/`ModelValidator`, no está medido.

## Tercera ronda de cambios (comentario del PR #11)
El motor solo emite el descriptor (no referencia a Render) y rasteriza la App o `Rodaje.Export`, con API de precarga; sección 7 enganchada a `MissingContentPolicy`, `TimelinePlan.MissingContent`, `ExportErrorCode.ContentMissing`, `IEffectContent.FindTextStyle` y quién evalúa las curvas; bandera `edges` para dividir; `ClipDrag.Room`/`PlanSlip` en el costo de `sourceIn` negativo; tope de textura en bytes; reservas de `Tick`; `widthFraction` y `scale` nativo; AOT con `ComWrappers` y vía de dilatación del alfa; decisiones de producto (estilo por referencia, sin color/LUT/viñeta) en el resumen; licencias de Roboto/Open Sans. Verificado en `origin/main`: los nombres del contrato y las líneas de `ClipDrag`.
## Tercera pasada (verificación contra `src/`)
Se volvieron a comprobar contra el código las afirmaciones de la segunda revisión (`TrimmingSupport:84`, `ModelValidator:139`, `TransitionOps.Continues`, `MediaOf`, `PsGraded` en `Effects/EffectShaders.cs`, BGRA en `GpuDevice`, `LayerFrame`). Se quitó un meta-comentario de la cabecera, se precisó el costo de `sourceIn` negativo (las comprobaciones de `TimelineEdits.Speed` no aplican) y se explicitó el ancla implícita de `in`/`out`. Pruebas omitidas: 0 antes y 0 después (solo documento).

## Cuarta pasada (revisión independiente, 6 puntos menores)
En `docs/propuestas/texto.md`: (1) la tabla de la sección 1 lista las búsquedas `FindMedia(clip.MediaId)` de la interfaz y de `TimelinePlan.Audio.cs` que fallarían sin medio, más la nota de `new Clip` campo por campo en `TimelineEdits.Speed`; las líneas se verificaron contra esta rama y se citan por nombre porque cambian con `main`; (2) se dice que el validador omite `SourceOut > media.Duration` y `hasVideo`/`hasAudio` en generadores; (3) los subtítulos blandos dependen de la fase 7 en las secciones 6 y 7 (como `fase-6.md`); (4) las animaciones in/out se combinan con `rodaje.transform` en forma aditiva (posición, rotación) y multiplicativa (escala, opacidad), y si el clip dura menos que in+out ambas se escalan; (5) los criterios separan el costo de GPU/render (≤0,15 ms por título) del `Tick` del motor (≤0,3 ms con 5 títulos) y citan el criterio redefinido de `fase-5.md` (~2,8 KB por `Tick`); (6) la sección 7 expresa la regla «si falta X, la exportación se detiene y ofrece…». Solo documentos; pruebas omitidas: 0.

## Decisiones de Daniel reflejadas (D-029)
Las preguntas pasan a «Decidido»: bloque entero en la v1, fuentes empaquetadas solo de paquetes instalados, entrada y salida ancladas con «convertir en keyframes», y **el proyecto copia el estilo** (`generator.style` con `source`, sin `overrides`). Cambios derivados: `FindTextStyle` solo al aplicar; lo que puede faltar al dibujar es la fuente; tamaño del estilo copiado acotado por el validador. D-029 está en `docs/decisiones.md` en `docs/decisiones.md` (la rama no lo tiene aún): se siguió lo que dice el comentario del PR.
