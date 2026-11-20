# Editar por el texto

En entrevistas, clases, pódcast y tutoriales, gran parte del primer corte es quitar lo que sobra de lo que se *dice*: frases repetidas, arranques en falso, muletillas y pausas largas. Con la transcripción a la vista se hace leyendo: se selecciona la frase en el panel **Transcripción** y la línea de tiempo se corta y se cierra sola, con el video y el audio enlazados. Lo que se gana es tiempo en el primer corte, no precisión: el retoque fino sigue en la línea de tiempo.

La transcripción la hace el módulo «Subtítulos automáticos» ([IA local](ia-local.md)) y se guarda **por medio**, no por clip: cada clip del mismo archivo muestra su parte, con cualquier recorte o velocidad.

## Transcribir el medio

1. Selecciona un clip con voz y abre **Subtítulos automáticos…** (menú del clip o **Herramientas › Texto y voz › Subtítulos automáticos…**). Si el medio aún no tiene transcripción, Rodaje lo transcribe **entero**; si la tiene, los subtítulos salen al instante «de la transcripción guardada» (con «Detectar el idioma» vale la que haya; con otro idioma se vuelve a transcribir).
2. Al **aplicar**, los subtítulos van a su pista y la transcripción queda guardada en el proyecto: un solo paso del historial (un **Ctrl+Z** los quita a los dos). Si solo te interesa el texto, borra los subtítulos después: la transcripción se queda.
3. **Transcribir de nuevo** repite la pasada y, al aplicar, reemplaza la transcripción guardada.

Un medio de **más de tres horas** no cabe en una pasada: entonces se transcribe solo el tramo del clip y no se guarda (un clip que pase de tres horas hay que cortarlo y transcribir cada parte).

## El panel Transcripción

Se abre con **Ver › Transcripción** o con el botón **Transcripción** del carril de la biblioteca. Sin nada transcrito, el panel solo dice cómo empezar.

- **Pista guía.** El texto se lee de una pista: por omisión, la primera de audio con clips transcritos (si no hay, la primera de video). Con varias pistas transcritas —dos micrófonos— aparece el selector **Pista guía** para elegir cuál se lee.
- **Palabra activa.** Al reproducir, la palabra que suena se resalta y el panel se desplaza solo cuando sale de la vista. **Doble clic** en una palabra lleva el cursor ahí; elegir palabras selecciona sus clips en la línea de tiempo.
- **Buscar (Ctrl+F** o la caja de arriba**):** sin distinguir mayúsculas, tildes ni signos («camion» encuentra «camión»). **Intro** va al siguiente resultado y **Mayús+Intro** al anterior; también los botones **↑** y **↓**. El contador del panel dice palabras, seleccionadas y resultados.
- El texto sigue a la línea de tiempo: cortar, recortar o poner velocidad a un clip actualiza sus palabras solo.

## Editar desde el texto

Las teclas valen con el foco en el panel (un clic en una palabra se lo da; dentro de la caja de búsqueda, solo **Ctrl+F**) y no pisan las de la línea de tiempo. Cada acción es un paso del historial: **Ctrl+Z** la deshace.

- **Seleccionar:** clic en una palabra; **Mayús+clic** de la primera a la última; **Ctrl+Mayús+←** y **Ctrl+Mayús+→** recorren la selección palabra a palabra; **Ctrl+A** la toma entera.
- **Supr** borra lo elegido **con ripple**: se cierra el hueco en la pista guía y en las de los enlazados, con el video junto a su audio. **Retroceso** hace lo mismo pero **dejando el hueco**.
- **Ctrl+X** marca la frase elegida y **Ctrl+V** la mueve delante de la palabra donde esté la selección (sin selección, al final): «Mover frase» la corta, la quita con ripple y la pega insertando, con el grupo enlazado entero.
- Clic derecho en una palabra: **Corregir palabra · F2** (con una sola elegida; escribir varias la parte) y **Empezar párrafo aquí**.

## Muletillas y pausas

- **Quitar muletillas…** (en el panel, o **Herramientas › Texto y voz** con un clip transcrito elegido) lista las que encontró en el idioma de cada transcripción. Las seguras («eh», «mmm») vienen marcadas; las dudosas («o sea», «bueno», «pues»), que también son palabras de verdad, aparecen sin marcar y con «revisar». **Quitar lo marcado** las borra con ripple.
- **Quitar pausas…** lista los silencios de 0,6 s o más entre palabras de un mismo clip, todos marcados: deja 0,15 s de cada pausa, la mitad a cada lado del corte, y cierra con ripple.

## Hablantes

Rodaje no detecta quién habla: los hablantes se crean y se asignan a mano y, como la transcripción, valen para todos los clips del medio.

- **Hablantes…** abre el menú: **Añadir hablante…** (nombre y color) y, por cada uno, **Asignar · Alt+N** y **Renombrar y dar color…**; **Quitar hablante · Alt+0** se lo quita a la selección.
- Con palabras elegidas de un solo medio, **Alt+1** a **Alt+9** les asignan su hablante: el nombre sale en su color abriendo el párrafo.

## Lo quitado

Las palabras del medio que ya no están en la pista guía no se ven por omisión. El interruptor **Mostrar lo quitado** las pinta tachadas y en gris entre sus vecinas; clic derecho en un tramo tachado › **Recuperar** lo devuelve, insertando ese trozo del medio con el enlace y la velocidad del clip vecino.

## Límites

- Los tiempos que da Whisper fallan unas décimas: cada corte cae en la mitad del hueco entre palabras y se ajusta al cuadro. Si un borde suena a chasquido o se come una sílaba, se retoca en la línea de tiempo.
- Whisper a veces limpia las muletillas al transcribir: las que no llegaron al texto no se pueden quitar desde el panel.
- Una frase que cruza un hueco entre clips (o un clip sin texto) no se mueve entera: avisa y hay que moverla por partes.
- El ripple corre solo las pistas afectadas; el resto no se mueve. Si en una pista que se corre un clip atraviesa el tramo que se cierra —una música enlazada con la voz, por ejemplo— la acción avisa y no cambia nada.
- No se escribe texto libre: el panel selecciona y actúa, no edita como un procesador de textos; corregir con **F2** es lo único que cambia las palabras.

## Dónde se guarda

Dentro del `.rodaje`: la transcripción viaja con el proyecto, en tiempo del origen del medio (una hora de habla, unas 9 000 palabras, ocupa ~0,5 MB). El archivo sube al formato 16 solo cuando hay alguna transcripción; sin ella sigue en el formato anterior y lo abre Rodaje 1.0.x. Quitar el medio quita su transcripción (**Ctrl+Z** la devuelve); reenlazarlo la conserva. El detalle está en [El formato del proyecto](../formato-proyecto.md).

*Pendiente medir la transcripción del medio entero en el RTX 3050 con `parallel` 4; se apunta aquí cuando se mida.*
