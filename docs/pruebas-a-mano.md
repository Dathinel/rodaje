# Pruebas a mano

Lo que no cubren las pruebas automáticas ni la autoprueba del exe y conviene repasar con la app abierta antes de publicar. Cada apartado dice qué hacer y qué tiene que pasar. Las pruebas automáticas se corren con `Probar.bat`; la autoprueba, con `Rodaje.bat -Autoprueba`.

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

## Aviso de versión nueva

La lógica (comparar versiones, 24 h, versión descartada) está probada en `tests/Rodaje.App.Tests/Updates/`. A mano se comprueba lo que depende de la red, de la ventana y del navegador.

1. **Arranque con red.** Borra `lastUpdateCheck` de `%LOCALAPPDATA%\Rodaje\ajustes.json` (o cierra la app y pon una fecha de hace más de un día), abre Rodaje y espera dos segundos. Si en GitHub hay una release más nueva que la del exe (Ayuda › Acerca de Rodaje...), la barra de avisos dice «Rodaje X.Y.Z disponible» con **Ver novedades** y **Más tarde**; si no la hay, no sale nada. En `ajustes.json` queda `lastUpdateCheck` con la hora en UTC. La ventana no se queda congelada mientras consulta.
2. **Ver novedades.** Abre en el navegador `https://github.com/Dathinel/rodaje/releases/tag/vX.Y.Z` y el aviso se cierra.
3. **Más tarde.** El aviso se cierra y `ajustes.json` guarda `dismissedUpdateVersion: "vX.Y.Z"`. Cierra y abre Rodaje con `lastUpdateCheck` borrado: no vuelve a avisar de esa versión. Con una versión más nueva aún publicada sí avisaría.
4. **Una vez al día.** Con `lastUpdateCheck` de hace menos de 24 h, al arrancar no hay consulta (sin tráfico a `api.github.com`, por ejemplo mirando el Monitor de recursos) ni aviso.
5. **Sin red.** Desconecta la red (o apunta `api.github.com` a `127.0.0.1` en `hosts`) y arranca con `lastUpdateCheck` borrado: ningún aviso, nada en `app\rodaje-error.txt` ni en el registro de la sesión, y la app arranca igual de rápido. `lastUpdateCheck` queda anotado igual (no insiste en cada arranque).
6. **Ayuda › Buscar actualizaciones...** Consulta aunque hayan pasado menos de 24 h, el aviso esté apagado o la versión esté descartada. Con una versión nueva, el mismo aviso que al arrancar; con la última instalada, «Tienes la última versión, X.Y.Z» en verde; sin red, «No se pudo comprobar» en ámbar. Pulsarlo dos veces seguidas no abre dos consultas.
7. **Preferencias › General › «Avisar cuando haya una versión nueva».** Desmárcalo y guarda: `checkForUpdates: false` en `ajustes.json` y al arrancar no se consulta (ni con `lastUpdateCheck` borrado). Vuelve a marcarlo y guarda: de fábrica está marcado.
8. **Autoprueba y mediciones.** `Rodaje.bat -Autoprueba` y `-Medir` no consultan nunca (ninguna petición a `api.github.com`).

## Exportar: muestreo de color y línea de ffmpeg

1. Abre un proyecto con un clip, pulsa **Ctrl+E** y despliega **Avanzado**.
2. Con el preajuste **Maestro ProRes 422 HQ**, «Muestreo de color» dice 4:2:2 y la línea «Formato» lleva «4:2:2». Con **Web 1080p** (H.264 por GPU) el combo queda en 4:2:0 y apagado si el codificador no da otra cosa.
3. Cambia a HEVC por CPU (o a un codificador que admita 4:4:4) y elige 4:4:4: la tarjeta de preajuste pasa a «Personalizado» y la estimación no cambia de orden de magnitud.
4. Pulsa **Copiar la línea de ffmpeg** y pégala en el Bloc de notas: empieza por `ffmpeg -hide_banner`, lleva `-f rawvideo`, `-i pipe:0`, el codificador elegido (`-c:v:0 …`), `-pix_fmt:v:0` con el muestreo elegido y termina con la ruta de salida entre comillas si tiene espacios. Con «Solo audio» no hay `pipe:0`.
5. Con un problema en rojo (carpeta inexistente), el enlace avisa que hay que arreglarlo primero y no copia nada.

## Sincronizar por el sonido

1. Importa un video con sonido y, como «grabadora», un recorte de su propio audio (Exportar › Solo audio de un tramo que empiece unos segundos después) o una grabación aparte del mismo momento. Pon el video en V1 y el audio en una pista de audio libre, descolocado a propósito.
2. Selecciona los dos (Ctrl+clic) y pulsa **Herramientas › Audio › Sincronizar por el sonido**. La barra de avisos muestra el avance y, al terminar, «Sincronizados por el sonido» con el desfase en fotogramas; el clip de audio queda alineado con el video (reproduce: una sola voz, sin eco) y **Ctrl+Z** lo devuelve a su sitio en un paso.
3. Con un solo clip elegido, o con el video y su audio enlazado, el menú del clip muestra la herramienta atenuada y al pulsarla explica que hacen falta dos clips con sonido.
4. Pon otro clip donde debería caer el audio sincronizado: la acción avisa «El lugar está ocupado…» y no mueve nada.
5. Pon el video de referencia a 2× (pestaña Velocidad): la acción avisa que la referencia tiene que ir a 1×.

## Montaje al ritmo

1. Pon tres o cuatro fotos o tomas pegadas en V1 y una música en una pista de audio. Selecciona la música y, con Ctrl+clic, los clips del montaje.
2. **Herramientas › Audio › Detectar ritmo…**: al terminar el análisis aparece «Ajustar al ritmo los otros N clips seleccionados». Márcalo, elige «cada 2 pulsos» y Aplica: cada clip termina en un pulso (los marcadores «Pulso», si los dejaste, caen en cada corte) y los clips siguen pegados. **Ctrl+Z** deshace el montaje y los marcadores de una vez.
3. Repite con un clip corto que no tenga material de sobra: crece solo hasta su final de archivo y el aviso no falla.
4. Con solo la música seleccionada, la casilla no aparece.

## Exportar otros proyectos (lote)

1. Guarda dos o tres proyectos pequeños en una carpeta y abre otro distinto. Pulsa **Ctrl+E**, elige un preajuste y pulsa **Otros proyectos…**.
2. Elige los archivos `.rodaje` guardados (y, a propósito, el del proyecto abierto y un `.rodaje` roto o vacío). El diálogo lista una salida por proyecto («Boda.mp4») y, en «Se saltan», el abierto y el roto con su motivo.
3. **Exportar**: la página se cierra, el aviso dice cuántos entraron y **Trabajos** muestra un trabajo por proyecto, cada uno con el nombre de su archivo; al terminar, «Abrir carpeta» lleva a la carpeta del `.rodaje` correspondiente.
4. Repite con un preajuste de otra forma (Vertical 9:16): cada salida mantiene la forma de su propio proyecto si el preajuste solo fija un lado.

## Rueda de desplazamiento

1. Añade **Ruedas de color** a un clip (Biblioteca › Filtros). En Filtros se ven cuatro ruedas: Sombras, Medios, Luces y Desplazamiento, y debajo las filas de nivel de cada una.
2. Arrastra el punto de Desplazamiento hacia el azul: toda la imagen se tiñe, también el negro puro (compáralo con Sombras, que no toca el blanco, y con Luces, que no toca el negro). El monitor de forma de onda sube entero con el deslizador «Desplazamiento».
3. Doble clic en la rueda la devuelve al centro; cada arrastre es un paso de **Ctrl+Z**.
4. Abre un proyecto de la 1.0.2 con ruedas: se ve igual que antes (el desplazamiento vale 0) y se guarda sin cambiar el formato.

## Portapapeles: pegar insertando, transiciones y otro proyecto

1. Con dos clips seguidos en V1 y una transición entre ellos (**Ctrl+T** en el corte), selecciona los dos y **Ctrl+C**. Lleva el cursor más allá y **Ctrl+Mayús+V**: las copias traen la transición, con su tipo y su duración. **Ctrl+D** sobre los dos originales también la duplica.
2. Copia un solo clip, pon el cursor en medio de otro clip y pulsa **Ctrl+Alt+V** (Editar › Pegar insertando): el clip de debajo se parte en el cursor y su mitad derecha, con todo lo que sigue en esa pista y en la de su audio, se corre lo que dura lo pegado; nada se tapa. **Ctrl+Z** lo deshace en un paso («Pegar insertando»).
3. Bloquea la pista de audio y repite el paso 2 en un punto donde haya que correr audio enlazado: avisa «No se pudo hacer» y no cambia nada.
4. Copia un clip, **Ctrl+N** (descarta los cambios) y **Ctrl+V** en el proyecto vacío: el medio aparece en la biblioteca (paso «Importar medio» en el historial) y el clip se pega con él. Si importas antes ese mismo archivo a mano, no se repite en la biblioteca.
5. Repite el paso 4 después de mover o renombrar el archivo de video: aviso «No se pudo pegar» con la ruta que falta, y la línea de tiempo y la biblioteca quedan como estaban.

## Resultado del repaso del 1-nov (Claude con el ratón, build 1.1 de `dist\Rodaje`)

Pasaron: exportar entre entrada y salida (archivo de 4,1 s; «Toda la secuencia» al quitar las marcas), línea de ffmpeg copiada (empieza por `ffmpeg -hide_banner`, `-f rawvideo`, `-i pipe:0`, `-c:v:0 h264_nvenc`, `-pix_fmt:v:0 nv12`, ruta entre comillas), asas de fundido (globo «Entrada 1,17 s», Propiedades 1,2 s, Ctrl+Z/Ctrl+Y en un paso), Ayuda › Buscar actualizaciones («Tienes la última versión, 1.0.2», `lastUpdateCheck` en UTC, sin `rodaje-error.txt`), ruedas de color (Desplazamiento tiñe también el negro; doble clic centra).
Sin probar: solo de pista, sincronizar por el sonido, montaje al ritmo, lote, «Más tarde» y sin red.
Fallos: (1) tras **Alt+X** la barra de contexto desaparece pero la regla sigue sombreando el tramo entre las marcas viejas hasta el siguiente repintado; (2) el asa de fundido mide unos 5 px con el zoom por defecto y es fácil arrastrar el clip en su lugar (valorar un área de agarre de 8-10 px).
