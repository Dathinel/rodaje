# Enlazar y desenlazar, separar audio, y copiar/pegar atributos

Tarea 18 de la cola de la nube (funciones 3 y 7 de `docs/pendientes.md`). Rama `claude/qdv-enlaces-atributos`, PR #28.

## Hecho

- **Núcleo, separar audio** (`src/Rodaje.Core/Editing/TimelineEdits.Links.cs`, fábrica `Edits.DetachAudio` en `Commands/Edits.Attributes.cs`). Toma un clip de video cuyo medio tiene sonido y crea su clip de audio con el mismo tramo, el mismo lugar y la misma velocidad, enlazado con él. Si el clip ya estaba enlazado con otro de video, conserva ese enlace. Va a la primera pista de audio sin bloquear que esté libre en ese tramo; si no hay ninguna, crea «Audio N» al final, o usa la pista que se le indique. Los efectos de audio del clip de video (`rodaje.audio`, que en una pista de video no suena) pasan al clip nuevo. Rechaza con un mensaje en español los títulos, los clips que ya están en audio, los medios sin sonido, el audio que ya está separado y las pistas bloqueadas.
- **Núcleo, atributos** (`src/Rodaje.Core/Editing/ClipAttributes.cs`, fábrica `Edits.PasteAttributes`):
  - `ClipAttributes.Capture(sequence, clipId)` copia el clip y sus enlazados. La imagen sale del clip de video del grupo, el sonido del de audio y la velocidad es la que comparte el grupo.
  - Los keyframes se guardan como distancia al inicio del clip en la línea de tiempo.
  - `ClipAttributeKinds` tiene cuatro categorías: `Effects` (la pila de filtros), `Transform` (`rodaje.transform` y `rodaje.blend`), `Speed` (velocidad o curva, reversa, tono y muestreo) y `Volume` (`rodaje.audio`).
  - `ClipAttributeEdits.Paste` reemplaza cada categoría elegida en el destino y deja el resto de su pila como estaba. Lo de imagen va a los clips de video y el volumen a los de audio, también a los enlazados de lo seleccionado. La velocidad se pega una vez por grupo, con el `RippleScope` de la sesión y el magnetismo de D-026; en títulos y congelados no se pega.
  - Todo queda en un paso del historial.
- **App, sesión** (`src/Rodaje.App/Services/EditorSession.ClipLinks.cs`): `LinkActionForSelection` y `ToggleLinkSelection` (enlaza la selección o desenlaza sus grupos con un clic), `CanDetachAudio` y `DetachAudioOfSelection` (añade los clips nuevos a la selección), y `CopyAttributes` y `PasteAttributes(kinds)`.
- **App, menú** (`src/Rodaje.App/Views/Timeline/TimelineView.ClipMenu.cs`, con 4 líneas en `TimelineView.Input.cs`): el clic derecho sobre un clip, que ya lo seleccionaba, abre ahora un menú con Enlazar/Desenlazar, Separar audio, Copiar atributos y «Pegar atributos ▸» (Todo, Efectos, Transformación, Velocidad o Volumen). Cada opción solo se activa si se puede usar.
- **Pruebas** (archivos nuevos): `Editing/Links/DetachAudioTests` (8), `Editing/Links/PasteAttributesTests` (11) y `Properties/LinkAttributePropertiesTests`. Esta última es de propiedades: 3000 proyectos aleatorios de `RandomTimeline` con hasta 25 operaciones de enlazar, desenlazar, separar, pegar y deshacer. Comprueba que un rechazo no cambia nada, que el proyecto queda válido, que las pistas bloqueadas no cambian y que deshacer vuelve a cada estado.

## Resultado de las pruebas en Linux

Con el filtro de la CI:
- Core: 1245 pasan (antes 1225), 0 omitidas.
- Paquetes: 335 pasan, 0 omitidas.
- Playback: 65 pasan, 0 omitidas.

No se omitió nada nuevo. La App (WinUI) no compila en Linux. El parcial de la sesión se compiló aparte contra `Rodaje.Core`, con stubs y las mismas reglas (advertencias como errores, `latest-recommended`). El menú queda para la CI de Windows.

**CI:** las corridas del PR terminaron en unos 3 s sin runner asignado, igual que todas las del repo desde las 19:31 UTC (PR #23 a #28). Lo más probable es que se agotaran los minutos o el límite de gasto de Actions. Es un problema de infraestructura, no del código. La verificación queda en las pruebas locales de Linux y en la CI cuando se restablezca.

## Decisiones

- **Separar audio** crea el clip de audio enlazado (no lo desenlaza). Si el audio ya está separado, la orden no se activa; para soltarlo está «Desenlazar».
- **Pegar es reemplazar.** Si el origen no tiene transformación, pegar «Transformación» deja el destino como recién importado. Así el destino queda igual que el origen en esa categoría.
- **El modo de mezcla va con «Transformación»**, porque el inspector lo muestra en «Opacidad y mezcla», junto a la opacidad.
- **Keyframes por distancia al inicio del clip.** Sin velocidad, o en un congelado, la cuenta es exacta y sin acotar. Con velocidad se usa `ClipTimeMap`, acotado al clip; dos keyframes que caigan en el mismo instante se quedan en uno.
- **Velocidad:** si el destino ya tiene la misma, no se rehace el mapeo, que podría mover su final. Al pegar una curva, el núcleo la ajusta a cada destino (`SetCurve`) y el final del material se alinea al cuadro, como en `SetCurve`.
- **Sin atajos de teclado:** el registro de atajos (`Services/Shortcuts`) está fuera de los caminos de la tarea. Se propone Ctrl+Alt+C y Ctrl+Alt+V para copiar y pegar atributos, y Ctrl+L para enlazar.

## Pendiente

- Atajos de teclado y entradas en el menú Editar (fuera de los caminos).
- Pruebas de la sesión en `tests/Rodaje.App.Tests` (fuera de los caminos).
- Un diálogo para pegar varias categorías a la vez. Hoy el submenú ofrece «Todo» o una sola; el núcleo ya admite cualquier combinación.
- Actualizar `docs/arquitectura/nucleo.md` y `app-vistas.md` (fuera de los caminos).

## Posibles choques

- `TimelineView.Input.cs`: 4 líneas en la rama del botón derecho de `OnLanesPointerPressed`. Ninguna rama `claude/*` abierta toca ese archivo al entregar.
- `Edits` y `TimelineEdits` crecen con parciales nuevos (`Edits.Attributes.cs`, `TimelineEdits.Links.cs`). Si otra tarea (por ejemplo la 19, reemplazar medio) añade una fábrica con el mismo nombre, chocaría al compilar.

## Qué debe comprobar Daniel en Windows

1. Un clic derecho sobre un clip abre el menú; el clic derecho sobre la envolvente o una transición sigue como antes.
2. Con un clip de video sin audio (por ejemplo, de una pista que no trae su audio), «Separar audio» crea el clip de audio debajo, enlazado: se mueven juntos. Ctrl+Z lo deshace en un paso.
3. Selecciona un video y un audio sueltos y elige «Enlazar»; vuelve a abrir el menú y elige «Desenlazar».
4. Copiar atributos de un clip con color, posición animada y 2×. Luego, «Pegar atributos ▸ Todo» sobre dos clips seleccionados: un solo paso en el historial, la animación cae en el mismo lugar de cada clip y lo que sigue se une.
5. «Pegar atributos ▸ Volumen» sobre un clip de video con su audio enlazado cambia el volumen del audio.
