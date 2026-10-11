# Entrega: propuesta del formato de estilos de texto (tarea 8)

Rama `claude/propuesta-texto`. Solo documento: `docs/propuestas/texto.md` (nuevo) y esta entrega. Sin código; no se compiló ni se corrieron pruebas, y no se omitió ninguna (omitidas antes: 0; después: 0).

## Hecho
Propuesta de la fase 5b con los seis puntos pedidos: clips generadores y formato 5, contenido de `text-style` (`rodaje.textstyle` v1), render con DirectWrite/Direct2D, fuentes y licencias, edición sobre el lienzo y base de los subtítulos, más plan de trabajo, ventajas y desventajas, y 3 preguntas para Daniel.

## Pendiente
Que Daniel responda las 3 preguntas. Las cifras de costo del render son hipótesis sin medir. La lista de fuentes candidatas necesita verificar licencia y nombre reservado una por una antes de publicar nada.

## Decisiones
Recomendaciones (no decisiones): formato 5 aparte del 4, estilos como primitivas sobre el bloque entero, textura RGBA compuesta como capa, clips de texto como unidad de subtítulo.

## Posibles choques
La 5c cambia `Clip` y el formato 4 (ya unido en `main`): la 5b debe partir de `main` después de la 5c. La rama `claude/pistas-por-defecto` (tarea 7) no toca estos archivos.

## Qué debe comprobar Daniel en Windows
Nada: no hay código. Solo leer la propuesta y responder.

## Cambios tras la revisión de Daniel (comentario del PR #11)
Se atendieron los 7 puntos en `docs/propuestas/texto.md`: tabla de lo que cambia en el código (puntos reales de `Edits`, deshacer, transiciones, plan y esquema), regla de `sourceIn` con rebase, corrección de que el color/LUT/viñeta no se reutilizan (variantes RGBA o limitarlos en la v1), `LayerFrame` con descriptor abstracto, dibujo en el pre-roll, tamaño nativo con ancla, AOT de `IDWriteTextRenderer` como riesgo real con un prototipo previo, riesgos nuevos (RTL, accesibilidad, unidades, keyframes, referencia/copia) y la sección de fuentes (no modificar, `LICENCIAS.md`, `docs/legal.md`, Roboto/Open Sans por verificar) y la coordinación del formato 5 con la fase 6. Las líneas citadas por Daniel vienen de su copia de `main`: aquí se verificaron los nombres, no los números de línea.
