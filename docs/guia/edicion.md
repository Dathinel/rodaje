# Edición

Todo lo que pasa en la línea de tiempo. Los atajos son los del estilo Rodaje; los de Ágil y Clásico están en [Atajos](atajos.md). Cada operación es un paso del historial: **Ctrl+Z** la deshace, también las que hace la IA.

## La línea de tiempo

- **Barra de herramientas** (de izquierda a derecha): herramienta normal (V), envolvente (E), selección y zoom (Z); los interruptores **pista principal magnética**, **ajuste** (N), **ripple automático** (Ctrl+L) con su ámbito («Ripple: pistas afectadas», «todas las pistas» o «pistas, marcadores y regiones»), **fundido cruzado automático**, **bloquear envolventes** e **ignorar grupos**; las acciones cortar (S), recortar inicio (Q) y final (W), borrar (Supr), marcador (M), región (R), deshacer y rehacer; y el zoom.
- **Pistas.** Cada cabecera tiene el nombre, ocultar o silenciar y bloquear. La primera pista de video es la **principal**; las de audio muestran su volumen («Volumen 0,0 dB»). Clic derecho en una cabecera: añadir pista de subtítulos, usar una pista de video para subtítulos, grabar voz en off, ecualizador y compresor de la pista.
- **Clips.** Color por tipo, nombre, tira de película o forma de onda, rombos de keyframes, envolvente de volumen. Delante del nombre, un indicador de velocidad cuando no es 1× («2,0×», «◀ 0,5×», «❚❚ Congelado» o la mini curva).
- **Zoom y desplazamiento.** Rueda desplaza en el tiempo, Mayús+rueda cambia de pistas, Ctrl+rueda hace zoom alrededor del puntero. Ctrl++ y Ctrl+- acercan y alejan; Mayús+Z ajusta todo a la ventana; Ctrl+Alt+Z hace zoom a la selección. El botón central arrastra la vista.

## Seleccionar

Clic selecciona; Ctrl+clic suma o quita; arrastrar en vacío dibuja un recuadro y marca un rango. Ctrl+A selecciona todo. Un clic en una transición la selecciona (y vacía la selección de clips).

## Cortar y borrar

| Qué | Cómo |
|---|---|
| Cortar en el cursor | S (o Ctrl+B): corta los clips seleccionados o el de la principal bajo el cursor |
| Cortar en todas las pistas | Ctrl+Mayús+B |
| Recortar hasta el cursor | Q borra lo que queda a la izquierda del cursor; W, lo de la derecha |
| Borrar | Supr o Retroceso. En la pista principal magnética o con ripple automático, lo de después se corre para cerrar el hueco; en las demás pistas, queda el hueco |

## Mover, recortar, slip

- **Mover:** arrastra el cuerpo. No pisa nada en pistas libres; con **ajuste** (N) se pega a bordes, cursor, marcadores y regiones. Alt al arrastrar ignora el ajuste. En la principal magnética lo **reordena**: sacarlo cierra el hueco y meterlo lo abre, también en las pistas de sus enlazados.
- **Recortar:** arrastra un borde. Un globo muestra el resultado. En la principal magnética o con ripple automático es un **recorte ripple**: lo de después se corre.
- **Slip:** Alt + arrastrar el cuerpo mueve el contenido dentro del clip sin cambiar su sitio ni su duración.
- **Estirar (velocidad):** Alt + arrastrar un borde cambia la velocidad para que el mismo material dure lo arrastrado (de 0,1× a 100×). Ver [Velocidad](#velocidad-y-reversa).
- **Esc** cancela cualquier arrastre en curso.

Roll y slide no tienen gesto en esta versión.

## Pista principal magnética

El interruptor del imán (de fábrica, encendido) mantiene los clips de la pista principal pegados unos a otros: cortar, borrar y mover nunca dejan huecos. La cabecera dice «Principal · magnética» o «Principal · libre». Al soltar un medio en ella se **inserta** en el corte más cercano; en las demás pistas **sobrescribe** lo que haya (con Ctrl, inserta).

## Audio y video enlazados

Un video con sonido entra como dos clips enlazados que se mueven, cortan y recortan juntos. En el menú del clip: **Desenlazar** / **Enlazar**, **Separar audio**, **Copiar atributos** y **Pegar atributos** (todo, efectos, transformación, velocidad o volumen). El interruptor **ignorar grupos** edita solo el clip agarrado sin romper el enlace. En Ver › «Pistas de video que traen su audio» eliges qué pistas de video reciben su audio al soltar un medio.

## Edición de tres y cuatro puntos

1. En la biblioteca, clic derecho en el medio › **Abrir en el visor de origen**. El reproductor pasa a mostrar solo ese medio; **Mayús+2** alterna entre origen y programa.
2. **I** marca la entrada y **O** la salida (en el visor que se ve). **Alt+X** las quita. La barra de contexto bajo las herramientas muestra las marcas, los selectores V/A con su pista de destino y las marcas de la secuencia.
3. **,** inserta y **.** sobrescribe el tramo en el cursor (o entre las marcas de la secuencia).
4. Con cuatro puntos de distinta duración pregunta **Ajustar velocidad** o **Recortar**.

## Clips compuestos

Selecciona varios clips y pulsa **Alt+G** (Crear clip compuesto): quedan como uno solo. **Doble clic** entra a editar su secuencia; las migas de pan en la barra de contexto vuelven al proyecto. **Alt+Mayús+G** descompone. Los efectos propios del compuesto como un todo son una limitación conocida de esta versión.

## Velocidad y reversa

Pestaña **Velocidad** del panel derecho (Ctrl+R la abre con el clip seleccionado):

![Pestaña Velocidad con el clip invertido](img/velocidad-invertido.png)

- Deslizador de 0,1× a 100× con imanes en 0,25, 0,5, 1, 2, 4 y 10; la velocidad escrita («1,5», «150 %», «x2»); botones rápidos; la duración resultante («5,0 s → 3,3 s»), que también se puede escribir para obtener la velocidad exacta.
- **Mantener tono**, activo de fábrica: el audio no se agudiza ni se agrava. Por encima de 8× se oye granuloso y por debajo de 0,25× metálico; sigue sonando.
- **Congelar** (Mayús+R) mete 3 s del cuadro del cursor. **Invertir** reproduce al revés, cuadro exacto; con videos de más de 1080p necesita un proxy, que se genera solo.
- **Curva:** Ninguna, Personalizada, Montaje, Héroe, Bala, Entrada rápida y Salida rápida. En Personalizada, arrastra los puntos (doble clic añade o quita, de 2 a 64) sobre el editor: altura logarítmica de 0,1× a 10×.
- Desde el teclado, sobre la selección: **Ctrl+Alt+.** más rápido y **Ctrl+Alt+,** más lento.
- Con velocidad distinta de 1×, los keyframes y la envolvente del clip siguen al material, no a la línea de tiempo.

## Transiciones

- Categoría **Transiciones** de la biblioteca: de fábrica, **Fundido cruzado**, **Fundido a color**, **Barrido** (ángulo y borde suave), **Empujar** (dirección), **Fundido con zoom** y **Fundido con desenfoque**; más las de los paquetes instalados (deslizar, empujar, acercar, alejar, girar).
- Arrástrala al **corte** entre dos clips pegados (cae en el más cercano). Dura 0,5 s; arrastra sus manijas para cambiar la duración, siempre centrada en el corte. Si el corte ya tenía una, cambia el tipo y conserva la duración.
- Con **fundido cruzado automático** encendido, cada corte sin transición de la pista principal muestra un símbolo: un clic crea un fundido de 0,5 s.
- Con la transición seleccionada, Propiedades muestra tipo, duración, parámetros y «Quitar transición» (deja el corte seco). Supr también la quita.
- **Guardar como transición…** (menú ⋯ de Propiedades) convierte los keyframes de transformación de un clip en una transición propia.

## Marcadores y regiones

- **M** deja un marcador en el cursor («Marcador N»). En la regla, arrastrarlo lo mueve y clic derecho lo borra. La categoría **Marcadores** de la biblioteca los lista: ir al anterior o al siguiente, Añadir, doble clic o Intro para ir, F2 renombra, Supr quita. Los marcadores se convierten en capítulos al exportar.
- **R** crea una región («Región N») sobre el rango marcado, la selección o el clip bajo el cursor. Las regiones sirven como rangos de exportación.
- **Detectar ritmo** deja un marcador «Pulso» en cada tiempo de la música (ver [Audio](audio.md#detectar-ritmo)).

## Medios: reemplazar, volver a enlazar, carpetas vigiladas

- **Reemplazar el clip seleccionado** (menú del medio en la biblioteca) cambia el medio de un clip conservando cortes, efectos y keyframes. Si el nuevo es más corto, avisa y pregunta «Reemplazar y acortar».
- Si al abrir un proyecto faltan archivos, el diálogo **Falta un medio** ofrece «Buscar en carpeta…» (reconoce el archivo aunque haya cambiado de nombre: «¿Es este?») o «Elegir…». «Más tarde» deja el proyecto abierto con esas capas vacías y un aviso con «Volver a enlazar».
- **Archivo › Carpetas vigiladas...**: lo nuevo que llegue a esas carpetas (hasta tres niveles) aparece al pie de la biblioteca y en la barra de avisos con «Importar».
- Clic derecho en un medio: añadir, **Generar proxy**, mostrar en el Explorador, quitar si no se usa. Medios tiene además carpetas, búsqueda, filtro por tipo, favoritos y vista de lista con resolución, duración, fps y códec.

## Proyecto, guardado y recuperación

- **Ctrl+N** nuevo (pregunta «¿Guardar los cambios?» si hay), **Ctrl+O** abrir, **Ctrl+S** guardar, **Ctrl+Mayús+S** guardar como. El `.rodaje` es JSON legible; los medios se referencian por ruta.
- **Plantillas:** Archivo › Proyecto nuevo desde plantilla (Horizontal 16:9 1080p y 4K, Vertical 9:16, Cuadrado 1:1, Retrato 4:5, más las tuyas) y Guardar como plantilla...
- **Autoguardado:** copias en `%LOCALAPPDATA%\Rodaje\Autoguardado` tras 5 s sin cambios y como mucho cada 2 min (10 por sesión). Tras un cierre inesperado, al abrir Rodaje avisa «Hay una copia de «X» del … con cambios sin guardar» con **Recuperar**, que la guarda como «X (recuperado).rodaje».
- **Formato del proyecto:** botón de la cabecera del reproductor (16:9, 9:16, 1:1, 4:5, 4:3, 21:9 o Personalizado), cuadros por segundo (23,976 a 60) y «Resolución y más opciones…» (720p a 4K o ancho y alto propios de 16 a 8192, pares).
- **Espacios de trabajo:** Ver › Espacio de trabajo: Edición, Color (panel derecho ancho en Filtros), Audio (línea de tiempo alta) y los tuyos («Guardar la distribución actual como...»).
- **Pantalla completa:** F o doble clic en el visor; mover el ratón muestra los controles; Esc sale. **Visor en otra pantalla** (Ver) abre el visor a pantalla completa en el monitor más grande donde no está el editor.
