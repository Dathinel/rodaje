# Pruebas a mano

Lo que no cubre la suite automática y conviene repasar en la app antes de unir o publicar: cada apartado es una función con sus pasos y lo que debe verse. Las pruebas automáticas se corren con `Probar.bat`; estas, con `Rodaje.bat` y un proyecto con uno o dos videos con sonido.

## Exportar entre la entrada y la salida

1. En la secuencia, lleva el cursor a los 2 s y pulsa **I**; a los 6 s, **O**. La barra de contexto muestra las marcas.
2. **Ctrl+E**. El cuadro «Rango» debe estar en «Entre la entrada y la salida (0:02 – 0:06)» y la estimación de duración, en 4 s.
3. Cambia a «Toda la secuencia» y vuelve: el resumen y la estimación siguen al rango.
4. Exporta y comprueba que el archivo dura 4 s.
5. Quita las marcas (**Alt+X**) y abre Exportar otra vez: la opción desaparece y se propone «Toda la secuencia».
6. Con solo **I** (a los 4 s), la opción va «(0:04 – final)»: desde la marca hasta el final de la secuencia.

## Solo desde la cabecera de pista

Hace falta un proyecto con dos pistas de audio con sonido (por ejemplo, el audio de un video y una música).

1. Pulsa la **S** de la cabecera de la música: queda con el acento, los clips de la otra pista de audio se ven velados y al reproducir solo se oye la música. Abre el mezclador (**Alt+M**): su botón S está igual.
2. Otra vez la S: todo vuelve a sonar y el velo desaparece.
3. **Ctrl+clic** en la S de la voz con la música ya en solo: la voz queda como única con solo. Otro Ctrl+clic en la misma: ningún solo.
4. Clic derecho en la cabecera: **Solo** marca y desmarca lo mismo; **Solo exclusivo** hace lo de Ctrl+clic.
5. **Ctrl+Z** deshace cada cambio. Guarda, cierra y abre: el solo sigue puesto.
6. En **Archivo › Atajos de teclado…**, asigna una tecla a «Solo de la pista elegida»; haz clic en una cabecera de audio y pulsa la tecla: alterna el solo. Con una cabecera de video elegida no hace nada (la acción solo vale para audio).

## Asas de fundido de audio

Con un video con sonido en la línea de tiempo y la herramienta normal (V).

1. Pasa el puntero por la esquina superior izquierda del clip de audio: el cursor cambia a flechas y se ve un cuadrito. Arrástralo hacia la derecha: aparece el globo «Entrada 0,5 s» y la esquina del clip se sombrea con la curva. Suelta y reproduce desde el principio: el audio entra fundiendo.
2. Lo mismo con el cuadrito de la derecha, hacia la izquierda: «Salida N s». El asa no pasa de la mitad del clip.
3. **Ctrl+Z** una vez deshace el arrastre entero (no paso a paso); Ctrl+Y lo rehace.
4. Propiedades › Audio: «Entrada» y «Salida» muestran los mismos segundos; cambia **Curva** a Lineal y a Exponencial: el sombreado del clip cambia de forma y el sonido se apaga antes (lineal) o más tarde (exponencial). Vuelve a Potencia constante y guarda: el `.rodaje` no lleva `fadeShape` (se omite por defecto).
5. Clic derecho en el clip de video con el cursor a mitad del clip: **Fundido de entrada hasta el cursor** pone en su audio enlazado un fundido que llega justo al cursor; **Fundido de salida desde el cursor**, uno que empieza ahí. Con dos clips de audio seleccionados y el cursor dentro de uno solo, cambia solo ese.
6. Un clip de una pista bloqueada no muestra asas; con el zoom muy alejado (clip de menos de 24 px) tampoco, y el sombreado sigue viéndose.
7. En un clip con transición de audio en un extremo, el fundido de ese lado no se aplica (la transición manda), como ya pasaba desde Propiedades.
