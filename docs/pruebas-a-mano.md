# Pruebas a mano

Lo que no cubren las pruebas automáticas ni la autoprueba del exe y conviene repasar con la app abierta antes de publicar. Cada apartado dice qué hacer y qué tiene que pasar. Las pruebas automáticas se corren con `Probar.bat`; la autoprueba, con `Rodaje.bat -Autoprueba`.

## Lista corta de la 1.0.4 (lo nuevo desde la 1.0.3, por orden)

Con el exe publicado desde `main` (`Rodaje.bat`). Cada punto remite al apartado de abajo; unos 10 a 20 minutos en total si solo se hace el primer paso de cada uno. Lo que falle va a la 1.0.5 salvo que rompa datos o el arranque.

1. **Arranque y cierre:** abre y cierra la app tres veces seguidas (con un proyecto y sin él). No debe salir ningún error de WinUI al cerrar.
2. **Render en caché**, pasos 1 a 4: la franja sobre la regla, el cálculo solo tras 3 s y reproducir el tramo verde. Es lo que más toca la reproducción.
3. **Editar por el texto**, pasos 1, 3, 4 y 9: transcribir, borrar una frase con Supr, mostrar lo quitado y recuperarlo.
4. **Cuadros intermedios**, pasos 1 a 4: 0,25× con mezcla y con flujo óptico, y 24 en 30 a 1×.
5. **Plantillas de movimiento**, pasos 1 a 4 y 7 (necesita el paquete de plantillas, hoy privado: se salta si no lo tienes).
6. **Intercambio:** exporta a OTIO, FCPXML y EDL un montaje con cortes, un fundido y una velocidad, e importa el OTIO (pasos 1 y 2). Abrir la EDL/FCPXML en DaVinci Resolve es opcional (paso 4).
7. **CinemaDNG en HDR sin recortar**, pasos 1 a 3 (necesita un DNG).
8. **Preferencias:** abre Preferencias › Rendimiento y guarda sin cambiar nada: el cálculo en caché en marcha no se pierde. El «Mapeo de rutas» acepta una fila «origen | destino».
9. **Exportar:** un MP4 H.264 corto, con y sin «Usar el render en caché».
10. **Colaboración** (apartado de abajo; necesita otra persona o un segundo equipo): invitar, entrar, editar a la vez y salir.

## Colaboración (sala con anfitrión)

Hacen falta **dos equipos** (o un equipo y alguien en otro lugar): quien hospeda y quien entra, con **la misma versión de Rodaje** y `dathinel-red.exe` junto a `Rodaje.exe` (en `modulos\red\`). Por internet entra por los relés públicos de n0; en la misma red, directo. Lo que sigue lo hace quien hospeda (A) y quien entra (B), siguiendo solo lo que dice la pantalla: **no hay que leer nada más**.

1. **A guarda un proyecto con 2 o 3 videos** y abre **Colaborar › Invitar a otras personas…**: sale su nombre ya puesto, «Puede editar», «24 horas» y «Solo sirve para una persona». **Crear invitación**: sale el texto, **Copiar invitación** lo copia y dice que ya se puede pegar.
2. **B pega la invitación** en **Colaborar › Unirse a una sala…** (con espacios o saltos de línea de más: da igual), escribe su nombre y pulsa **Unirme**. La conexión y la entrada muestran barra de progreso con fase, porcentaje, tiempo restante, último avance si se pausa y botón **Cancelar** para abortar limpiamente.
3. **A recibe la tarjeta «B quiere entrar»** con las cuatro palabras de comprobación y los botones **Dejar entrar para editar / solo para mirar / Rechazar**. Rechaza una vez (B ve «Quien hospeda la sala no te dejó entrar…»), vuelve a invitar y esta vez deja entrar para editar. La invitación de un solo uso ya no sirve una segunda vez (B lo ve con qué hacer). Si entra otro invitado con un medio ausente o sin archivo en el anfitrión, esto no bloquea ni impide su entrada.
4. **B recibe el proyecto** (barra de progreso con fase, porcentaje y tiempo restante) y llega a «Busca tus archivos»: con los videos en su equipo salen «Encontrado» (aunque estén en otra carpeta o con otro nombre); sin ellos, «No está en tu equipo» y los clips quedan sin conexión con su aviso. Deja uno sin encontrar a propósito y pulsa **Pedir la copia ligera** en ese medio (o **Pedir copia ligera de los que faltan**, que los pide uno a uno): llega del anfitrión y queda «Copia ligera recibida», y si después se encuentra el original en una carpeta, este manda. Si la conexión es por el relé público de pruebas, la petición se rechaza con su explicación. Continúa: tras **Entrar al proyecto** ya no sale un segundo diálogo de «Faltan medios» con lo mismo, y los avisos de medios que faltan enseñan los nombres de los archivos, nunca códigos internos.
5. **Los dos editan a la vez** (cortar, mover, añadir un marcador, cambiar un efecto): lo de uno aparece en el otro en menos de un segundo; el cabezal de cada uno se ve en el del otro con su nombre y color; la pista de B muestra «B edita aquí» en la cabecera de A y la selección de B sale con su contorno.
6. **Los dos tocan la misma pista a la vez:** uno de los dos ve «Un cambio tuyo no se pudo aplicar: B tiene esa pista ahora…» y su cambio se deshace; el otro sigue. Si quien recibe el rechazo hizo más cambios propios detrás en otras zonas válidas, solo se deshace el paso rechazado y lo posterior se reproyecta y conserva (aviso único). Nadie queda con un proyecto distinto (Colaborar › Panel de la sala sigue mostrando a los dos).
7. **Deshacer** (Ctrl+Z) en B solo deshace lo de B, y llega a A. Si A tocó antes lo mismo, B ve «No se puede deshacer: otra persona cambió ese elemento».
8. **A pone a B en «Solo puede mirar»** (panel › … › Cambiar a solo mirar): B ve «Ahora solo puedes mirar» y, si intenta editar, «Solo puedes mirar en esta sala…» y nada cambia.
9. **Corta la conexión de B** (desenchufa el cable o apaga el Wi-Fi 30 s): B ve «Sin conexión con quien hospeda la sala… Rodaje reintenta solo»; al volver, se restablece y sigue igual que A.
10. **A expulsa a B** (panel › … › Expulsar): B ve «La sala terminó» con el motivo y su copia sigue en su equipo. Prueba también que **A cierra Rodaje** (B lo ve) y que **abrir otro proyecto** dentro de una sala pregunta antes «¿Quieres salir/cerrar la sala?».
11. **Versión distinta:** con otra versión de Rodaje, B no entra y el mensaje dice cuál le falta.
12. **Panel de la sala:** quién está y quién no (A ve a quien se fue como «Desconectado»), últimos avisos, y salir o cerrar la sala con su confirmación.
13. **Proxies compartidos (copia ligera):** el invitado sin los originales solicita la copia ligera al anfitrión (ya se ofrece en «Busca tus archivos» al entrar, y luego en el menú del medio en la biblioteca); edita y reproduce con ella en la línea de tiempo. Si intenta exportar desde el invitado con la copia ligera, la interfaz avisa «Exporta en el anfitrión». Conexión por el relé público de pruebas: comprueba que por el relé no se envían proxies.
14. **Pasar anfitrión:** con B como editor, A abre **Colaborar › Panel de la sala › …** junto a B y pulsa **Pasar anfitrión a…**. La confirmación dice que B pasa a hospedar y A sigue como editor. Cancela una vez: nada cambia. Repite y confirma: B ve «Tu sala» y A puede seguir editando; un cambio de cada uno llega al otro. La opción no aparece para quien solo mira ni en el menú de un invitado.
15. **Suplente tras un apagón:** vuelve a una sala de A con B y un tercer equipo C como editores. Comprueba la etiqueta **suplente** en los paneles. Apaga el equipo A de golpe (sin cerrar la sala desde el menú). Tras detectar la desconexión, el suplente ve **El anfitrión se ha desconectado** y **Tomar el anfitrión**; C ve **Esperando a que B tome la sala; tus cambios se guardan aquí** si B es el suplente. Pulsa **Tomar el anfitrión**; si todavía no transcurrió la espera del transporte, lee el aviso y vuelve a pulsar más tarde. B pasa a hospedar, C reconecta y los cambios de ambos llegan al otro. Los avisos de espera desaparecen. Comprueba también que, si solo se corta la red y A vuelve antes de tomar el mando, desaparece la acción y se restablece la sala.
16. **Conservar un cambio que no entró:** en una sala de A y B, deja que B pierda la red y haga dos cambios propios reconocibles sobre clips; mientras tanto A cambia esos mismos clips. Reconecta B y espera la respuesta de la sala. Si los cambios no chocaron, repite sobre el mismo elemento hasta que aparezca **N cambios no entraron · Ver**. Pulsa **Ver**: cada tarjeta muestra la descripción, el motivo en español y **Conservar como nuevo / Descartar**. Con la pista libre, pulsa **Conservar como nuevo** en un cambio de clip: aparece una copia recuperada en una pista nueva, llega a A y el contador baja. Si no se puede recuperar automáticamente, el cambio permanece en la lista y el texto indica que puedes rehacerlo a mano o descartarlo.
17. **Descartar y refrescar:** en el otro cambio, pulsa **Descartar** y después **Conservar en la lista**: sigue ahí. Repite y confirma **Descartar**: desaparece sin alterar el proyecto. Al resolver el último cambio desaparece el contador. Deja abierto **Ver** mientras llega otro rechazo: la lista y el contador se actualizan solos. Si la sala termina con cambios pendientes, el botón de la sala sigue permitiendo consultarlos y descartarlos; conservar pide volver a entrar. Reabre la misma copia de la sala y comprueba que los cambios conservados en la lista siguen disponibles y los descartados no vuelven.

## Editar por el texto

Hace falta el módulo «Subtítulos automáticos» y un video con voz de unos minutos.

1. Pon el video en la línea de tiempo, elige su clip y abre **Subtítulos automáticos…**. Se transcribe el medio entero; al aplicar, los subtítulos y la transcripción entran en un solo paso (un Ctrl+Z quita los dos).
2. Corta el clip en dos, separa las mitades y pide otra vez **Subtítulos automáticos…** en la segunda: los subtítulos salen al instante, con el aviso «de la transcripción guardada». «Transcribir de nuevo» sigue disponible.
3. **Ver › Transcripción**: el texto aparece por párrafos y la palabra que suena se resalta al reproducir; el panel solo se desplaza cuando la palabra sale de la vista.
4. Selecciona una frase (clic y Mayús+clic) y pulsa **Supr**: la frase desaparece del texto, la línea de tiempo se cierra con el video enlazado y al escucharlo no se come sílabas ni deja chasquidos (si se oyen, apúntalo: es la pregunta 2 del diseño, fundido en cada corte).
5. **Retroceso** con otra frase: queda el hueco. **Ctrl+X** en una frase y **Ctrl+V** con el cursor del panel en otra palabra: la frase se mueve ahí con su video.
6. **Ctrl+F**, escribe una palabra con tilde sin ella («camion»): la encuentra.
7. **Quitar muletillas…**: las seguras vienen marcadas y las dudosas no; al quitar, el texto pierde justo esas.
8. **F2** en una palabra, escribe «dos palabras» e Intro: se parte en dos. **Alt+1** en un párrafo: aparece el hablante con su color; pon el mismo medio en otra pista guía o en otro clip y el hablante también sale ahí.
9. Activa «Mostrar lo quitado»: la frase borrada en el paso 4 aparece tachada y en gris; «Recuperar» la devuelve con su video y la línea de tiempo vuelve a durar lo de antes.
10. Guarda, cierra y abre el proyecto: la transcripción sigue. Quita la última transcripción (quitando el medio) y guarda: el archivo vuelve a la versión anterior del formato.

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

## CinemaDNG revelado en la GPU

1. Con el módulo «RAW de cine» 1.1.0 instalado (o el de `modules\` en desarrollo), Archivo › **Importar CinemaDNG…** y elige un DNG de una carpeta. El diálogo debe decir «Se revela en la GPU (RTX 3050 Laptop)» y no mostrar «Calidad del revelado».
2. Importa con exposición +1 EV. Al terminar, la notificación trae «Revelado en la GPU (…): N ms por fotograma»; apunta N en `docs/hdr.md` (fase D, cifras). El clip de la biblioteca se ve como antes (balance de la toma, gris del 18 % en su sitio, un paso más claro).
3. Repite con `RODAJE_FORZAR_WARP=1` (o en una máquina sin GPU): el diálogo dice «Se revela en la CPU con LibRaw», ofrece la calidad y la notificación dice «Revelado en la CPU (LibRaw)». Los dos ProRes deben verse iguales a simple vista (solo cambia el interpolado en los bordes finos).
4. Con el módulo 1.0.0 instalado (sin `mosaico`) todo sigue por la CPU sin ningún aviso.

## CinemaDNG en HDR sin recortar (D-065)

Con una toma real con cielo o ventanas que se quemen (o la carta sintética si no hay ninguna):

1. En una secuencia **HDR10 (PQ)** (Archivo › «Formato del proyecto…»), Archivo › **Importar CinemaDNG…**: «Intermedio» debe proponer «HDR sin recortar · FFV1 16 bits, PQ BT.2020». En una secuencia SDR debe proponer el ProRes.
2. Importa en HDR con **+2 EV**. La notificación dice «N ms y M MB por fotograma»: apunta los dos en `docs/hdr.md` (fase D, segunda parte; hoy solo hay cifras sintéticas). Compara con la misma toma en SDR: el tiempo no debería pasar de 1,25 veces.
3. Pon el clip en la secuencia HDR y baja «Exposición» en Color a −2 EV: las nubes y las ventanas deben recuperar detalle. Con el ProRes SDR importado a +2 EV, lo mismo solo las pone grises.
4. Pon el clip HDR en una secuencia **SDR**: se ve un paso más brillante que el ProRes hasta poner 203 en «Brillo de referencia HDR» (Propiedades); con 203 debe verse como el ProRes, sin bandas.
5. Mira que el clip HDR de 4K reproduce con su proxy con la misma fluidez que un ProRes 4K, y que el proxy se ve con los colores del original (sirve para cortar; en degradados puede tener bandas).

## Intercambio con otros editores (EDL, FCPXML, OpenTimelineIO)

1. Con un montaje de varias pistas (cortes, un fundido **Ctrl+T**, un clip a 2× y un marcador), Archivo › **Exportar línea de tiempo…** y guarda como `.otio`. No debe pedir nada: OTIO lo lleva todo. Repite como `.fcpxml` y `.edl`: antes de escribir aparece «Exportar a …» con el resumen de lo que no llega igual (en EDL, por ejemplo, los efectos y las pistas de audio de la 3 en adelante). Al terminar, «Ver informe» lista cada aviso con su TC de la secuencia; prueba **Copiar** y **Guardar como .txt**. La EDL escribe un archivo por pista de video (`nombre.edl`, `nombre_V2.edl`…).
2. Archivo › **Importar línea de tiempo…** y elige el `.otio`: en un solo paso del historial aparecen dos pistas nuevas con un clip compuesto enlazado en el cursor; doble clic lo abre y dentro está el montaje igual que el original (efectos, color y transiciones incluidos). **Ctrl+Z** lo quita entero. Clic derecho › **Descomponer** deja sus clips en la secuencia.
3. Importa la `.edl`: los medios aparecen; si moviste o renombraste el material antes, se abre «Faltan medios» y «Buscar en carpeta» los encuentra por el nombre del carrete; los clips quedan en su sitio aunque la cámara grabara con TC (por ejemplo, empezando en `01:00:00:00`).
4. Abre en DaVinci Resolve (gratis) la `.edl`, el `.fcpxml` y el `.otio` exportados: el corte, los fundidos y las velocidades deben coincidir. Si puedes, exporta desde Resolve una línea de tiempo corta a los tres formatos y guárdalos para las pruebas (pregunta 2 de D-062).
5. Importa un `.fcpxml` de Final Cut o Resolve con títulos: los títulos quedan como huecos y el informe los nombra con su texto.

## Render en caché (D-060)

1. Arma un tramo pesado: tres pistas con el mismo video 4K (o 1080p si el equipo va justo) y en cada clip seis efectos (color, curvas, lente, resplandor, grano, viñeta). Sobre la regla aparece una franja de 4 px **roja** en ese tramo (y **amarilla** donde va justo); lo ligero no lleva franja. Pasa el ratón por encima: cada color dice qué significa.
2. Suelta el ratón y espera 3 s sin tocar nada. En **Trabajos** aparece «Render en caché · Calculando desde … s (60 cuadros)» con su barra; la franja se vuelve **verde** trozo a trozo. Dale a reproducir mientras calcula: la fila dice «En pausa mientras se reproduce o se mueve el cursor» y la reproducción no debe notar nada. Al parar, sigue sola.
3. Reproduce el tramo verde: debe ir fluido (sin cuadros perdidos en la barra de estado) y verse como lo vivo. Mira de cerca un título con borde fino y un rojo saturado: el caché es ProRes 422 HQ y puede perder algo en líneas de un píxel; di si se nota.
4. Cambia un efecto de un clip del tramo verde: esa parte pasa a roja enseguida. **Ctrl+Z**: vuelve a verde al momento, sin recalcular. Mueve el tramo entero (todas sus pistas) a otro sitio: sigue verde.
5. Preferencias › Rendimiento › «Calcular en segundo plano» = **Manual**. Marca I y O sobre un tramo rojo y usa Secuencia › **Calcular de entrada a salida** (**Ctrl+Mayús+R**): solo calcula ese tramo, aunque no esté en reposo «inteligente». Sin marcas, calcula la secuencia entera. Con **Apagado** no calcula nada y la franja sigue diciendo lo que va lento.
6. Preferencias › Rendimiento: cambia la carpeta (por ejemplo, a otro disco) y el tope a 5 GB; **Borrar el render en caché** dice «Liberados X GB» y la franja deja de estar en verde.
7. Exporta el tramo verde con **«Usar el render en caché»** marcada y sin marcar: con la casilla debe tardar menos y verse igual a simple vista; sin ella, como siempre.
8. Pruebas con hardware pendientes (para Claude, con el equipo libre): escenas B (1080p con desenfoque de movimiento de 16 muestras y transición), D (compuesto anidado de 3 niveles en 4K) y E (título por letra 4K al entrar) de F0, y las escenas A y C de F4 reproduciendo un tramo en caché (0 tarde en 60 s): `RODAJE_PRUEBAS_LARGAS=1`, `RenderCacheBaselineTests` y `TimelineCutPauseHardwareTests`.

## Plantillas de movimiento (D-064)

Hace falta al menos un paquete con plantillas instalado (el `.paquete` de `Complementos
odaje-plantillas`, soltándolo en la biblioteca) o una plantilla propia del paso 7.

1. Biblioteca › **Plantillas**: las miniaturas aparecen solas, se animan al pasar el ratón y vuelven al cuadro quieto al salir. ¿Se entiende el cuadro quieto (es el del medio)? ¿Sirve el ícono del carril?
2. Doble clic pone la plantilla en el cursor; arrastrarla a una pista concreta la deja ahí. La etiqueta del clip es el nombre de la plantilla.
3. Inspector con la plantilla seleccionada: escribe en un campo de texto (el cursor no debe saltar), cambia un color con el selector y con el código hexadecimal, mueve un deslizador y teclea el número, escribe una fuente a mano, prueba una casilla y un desplegable. **Ctrl+Z** deshace una tanda de tecleo o un arrastre entero, no letra a letra.
4. Fluidez: ¿el visor sigue al deslizador y al tecleo sin retraso visible? (objetivo: un cuadro, 33 ms).
5. Recorta el clip: se para en la duración mínima de la plantilla. Divídelo en plena entrada, copia, pega y duplica: las dos mitades se ven como antes y las copias no comparten valores.
6. **Convertir en títulos** desde el inspector y desde el menú del clip: se ve igual y quedan seleccionados los títulos; **Ctrl+Z** vuelve a la plantilla. En un trozo donde no se ve ninguna capa, avisa y no borra nada.
7. **Guardar como plantilla…** con un título y con tres en pistas distintas y con desfase: marca «Expuesto», cambia nombres y guarda. Aparece en la biblioteca sin reiniciar y al usarla cae en el mismo sitio, también en un proyecto 9:16 y en 4K.
8. **Exportar…** una plantilla propia e instala ese `.paquete` soltándolo en la biblioteca y desde Complementos.
9. **Actualizar a X**: instala la 1.0.0 de un paquete, usa una plantilla y cambia valores; instala la 1.1.0. Sale el botón, conserva los valores, el aviso nombra los que volvieron a su defecto y **Ctrl+Z** regresa.
10. Una plantilla de transición sobre un corte y una de efecto sobre un clip: desde su menú se aplican con sus valores por defecto (sus parámetros aún no se editan en el inspector y arrastrar la de transición a un corte aún no hace nada).
11. Selecciona a la vez un título y una plantilla: di qué panel sale y si confunde. Con una plantilla seleccionada, «Aplicar al texto seleccionado» de un estilo de texto no debe ofrecerse.
12. Abre un proyecto con plantillas sin el paquete instalado: se ve igual y el panel sigue editando.

## Cuadros intermedios: mezcla y flujo óptico (D-063)

1. Pon un clip con movimiento (un paneo o alguien caminando) a **0,25×** en Inspector › Velocidad. Reproduce: va a saltos. Cambia **Cuadros intermedios** a «Mezcla de cuadros»: fundidos entre cuadros, con contornos dobles. Cambia a «Flujo óptico»: el movimiento debe verse continuo y nítido. El clip lleva **≈** delante de la velocidad (`≈ 0,25×`).
2. Con «Flujo óptico», para y avanza cuadro a cuadro (flechas): mira los bordes de lo que se mueve delante del fondo. Di si ves bordes de goma o halos y en qué tipo de plano.
3. Reproduce 10 s a 0,25× en una secuencia de 60 cuadros/s: la barra de estado no debe contar cuadros perdidos. Prueba lo mismo con un 4K.
4. Un clip de 24 cuadros/s en una secuencia de 30, a 1×: con «Cuadro más cercano» se nota el tirón de cada cuarto cuadro; con «Flujo óptico» debe desaparecer. El clip a 1× conserva el modo y muestra solo `≈`.
5. Un clip que por dentro tenga un cambio de plano, a 0,25× con flujo: en el corte no debe verse un fundido ni una deformación entre los dos planos, solo el salto.
6. Selecciona varios clips y cambia el modo: **Ctrl+Z** una vez los devuelve todos. En un congelado, una foto, un título o un compuesto el desplegable sale apagado.
7. Reversa a 0,5× con flujo y una rampa (curva «Bala»): igual de continuo que hacia adelante.
8. Exporta el tramo a 0,25× con flujo: el archivo debe verse como la vista previa en pausa.
9. Guarda, cierra y abre el proyecto: el modo sigue. Ábrelo con Rodaje 1.0.3: se ve como «Cuadro más cercano» (lo esperado).

## Rodaje en Linux (D-066, fase 8)

En el PC de siempre con Ubuntu 24.04 desde el USB (sesión X11 o Wayland con XWayland). Hace falta el driver de NVIDIA (o Mesa) con Vulkan y zenity (viene con GNOME). La app es el artefacto pp-linux de la CI (o uild/app-linux.sh en el propio Linux): carpeta pp/, ejecutable 
odaje.

1. **Arranque**: abre 
odaje sin argumentos dos veces seguidas y cronometra desde el doble clic hasta poder pulsar un menú (en caliente, la segunda). Objetivo: menos de 1 s. Dilo en segundos.
2. **Abrir y guardar**: Archivo › Importar medios (diálogo de zenity con el filtro «Videos, audios e imágenes»), luego Guardar como… y Abrir proyecto. Di si el diálogo se ve bien y si la ventana de Rodaje se queda en gris mientras está abierto (es lo esperado de momento).
3. **Visor**: reproduce un 1080p y un 4K HEVC 10 bits. El visor va por la CPU en esta fase (el nativo es la fase 9): di si va fluido, cuántos cuadros perdidos marca la barra de estado y si el tamaño del visor sigue a la ventana al redimensionar. Pausa en un título: se debe ver igual que en Windows.
4. **Editar**: corta (B o «Dividir»), recorta un borde, deshaz y rehaz, mueve un clip con el ratón. Mira si las líneas finas (curvas del inspector, mezclador, regla de la línea de tiempo) se dibujan: Uno no implementa LineGeometry y puede que falten.
5. **Arrastrar y soltar**: arrastra un video desde el gestor de archivos (Nautilus) a la biblioteca y a la línea de tiempo. Esto la CI no lo puede probar.
6. **Portapapeles**: en Ayuda, copia un informe o una línea de ffmpeg (Exportar › Avanzado) y pégala en un editor de texto.
7. **Mapeo de rutas**: copia al USB un proyecto de Windows con sus medios. En Preferencias › General › Mapeo de rutas escribe D:\Tu carpeta | /media/<tú>/<USB>/Tu carpeta y abre el proyecto: los medios deben aparecer sin «Volver a vincular».
8. **Exportar**: exporta 10 s a ProRes y a H.264 (si la lista ofrece h264_nvenc o h264_vaapi). Abre el archivo con el reproductor del sistema.
9. **Teclado**: en un teclado español, mira cómo se llaman en Atajos las teclas de signos ( '  y  ¡ ): en Linux salen con el nombre de la tecla de EE. UU. (pendiente).
10. **Audio**: reproduce con sonido (PipeWire); di si se oye sincronizado con la imagen.

## Progreso visible en toda operación larga (B9b)

Para la vuelta de repaso con ratón (B9). Criterio común: nada se queda con solo una rueda; cada operación larga muestra una **barra** (con porcentaje o en movimiento), el texto de fase o etapa, «quedan unos N s» cuando hay porcentaje y, si pasan 5 s sin avance, «Sigue trabajando; último avance hace N s». Revisa también el tema **claro**, **oscuro** y **alto contraste** (Preferencias › Apariencia) y que la barra no parpadee ni tape el recorrido del cursor en la línea de tiempo.

1. **Barra de avisos (acción larga)**: selecciona un clip y lanza «Color automático» (barra de herramientas). Debe salir al instante la barra con porcentaje, «Cancelar» y debajo «quedan unos N s» si dura más de 3 s. Pulsa Cancelar: dice «Cancelando…» y la barra se mueve sin botón hasta que termina.
2. **Abrir un proyecto**: abre uno grande (cientos de clips) con Ctrl+O. Si tarda más de medio segundo, aparece «Abriendo el proyecto» con «Paso 1 de 2: Leyendo el archivo». Uno pequeño **no** debe hacer aparecer la barra (sin parpadeo).
3. **Importar muchos medios**: arrastra 30 videos a la biblioteca: «Importando medios», «Leyendo los medios: 12 de 30», porcentaje, «Cancelar». Cancela a la mitad: «Importación cancelada», y no se añade ninguno.
4. **Exportar**: Trabajos › Exportaciones: la tarjeta muestra la barra con porcentaje, «238 fps» y «quedan unos N s»; en «Preparando», «Midiendo la sonoridad» o «Cerrando el archivo» la barra se mueve y dice cuánto lleva. Con 2 pasadas: «Paso 1 de 2: Exportando».
5. **Render en caché**: Secuencia › Calcular de entrada a salida (Ctrl+Mayús+R): en Trabajos, fila «Render en caché» con barra, porcentaje y tiempo restante.
6. **Módulos de IA**: subtítulos automáticos, reducir ruido, quitar fondo, reencuadre: en el diálogo, «Iniciando el módulo…» con la barra en movimiento y lo que lleva; luego etapa, porcentaje y tiempo restante. En **Traducir pista**, **Texto a voz** (también al cargar las voces) y **Reenlazar medios › Buscar en carpeta** ya no hay rueda: hay barra. Importar CinemaDNG: la barra de avisos tiene porcentaje (antes quedaba en movimiento).
7. **Descargar un módulo o sus modelos** (Complementos): barra con porcentaje y, debajo, «quedan unos N s». Corta la red unos segundos: debe salir «Sigue trabajando; último avance hace N s».
8. **Proxies**: Trabajos › Proxies: cada proxy activo con barra y tiempo restante; los que esperan dicen «En cola» sin «Sigue trabajando».
9. **Intercambio**: Archivo › Importar línea de tiempo… de un `.otio` o `.fcpxml` con muchos medios: porcentaje de medios sondeados y Cancelar.
10. **Botón «Trabajos»**: con algo en marcha, la barra fina bajo su nombre (no hay rueda).


## Colaboración: seguridad (D-067, B12)

Sin saber nada técnico: dos equipos, el anfitrión (A) y un invitado (B). Cada punto dice qué hacer y qué no debe pasar. Las pruebas automáticas atacan con mensajes rotos (`PatchHostileTests` aquí y la batería de `Dathinel/sala`); esto es lo que solo se ve con la pantalla.

1. **Sin aprobar no se ve nada.** B abre el enlace de A y espera. En B no debe aparecer el proyecto, ni nombres de medios, ni la línea de tiempo; en A sale «B quiere entrar». Si A pulsa **Rechazar**, B ve que no entró y no recibe nada.
2. **Enlace de un solo uso.** Tras entrar B, un tercer equipo (C) abre el mismo enlace: no entra o queda esperando la aprobación de A, nunca entra solo.
3. **Enlace caducado o anulado.** A anula el enlace (o espera a que caduque) y B, sin haber entrado, lo abre: no entra y el mensaje dice por qué.
4. **Solo mirar.** A admite a B como «Solo mirar». B no puede mover un clip, cortar, borrar ni añadir un marcador: el gesto no empieza o se deshace, y en A no cambia nada.
5. **Pista de otra persona.** A tiene reservada la pista A2 (la está editando). B intenta tocarla: se lo impide con el aviso «A2 la tiene …» y A2 no cambia.
6. **Expulsar corta.** A expulsa a B. B pierde la conexión al momento, deja de ver los cambios y no puede volver a entrar con el mismo enlace.
7. **Cerrar la sala corta.** A cierra la sala: B ve «La sala se cerró» y su copia queda como estaba, sin errores ni cierres inesperados.
8. **Versiones distintas.** Con una versión de Rodaje distinta en B, no entra y el mensaje dice cuál falta; A no se entera de nada más.
9. **El anfitrión no pierde nada.** Mientras B hace cien cambios muy rápido (arrastra clips sin parar), A sigue editando con normalidad y no se cierra ni se congela.
10. **Sin rutas ajenas.** B no debe ver en ninguna parte la ruta de las carpetas de A (nombres de usuario, «D:\…»): solo nombres de medios. Y al guardar el proyecto de B, sus medios apuntan a donde B los tiene, no a donde los tiene A.
11. **Sin medio.** B no tiene un medio de A: aparece «sin conexión» (no un error ni un cierre) y se puede seguir editando lo demás; la falta de ese archivo no bloquea ni impide la entrada de nuevos invitados a la sala.
12. **Cortafuegos.** La primera vez, Windows pregunta por el cortafuegos al abrir la sala: la pantalla de Rodaje lo explica antes de que salga.
