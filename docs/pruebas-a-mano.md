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
