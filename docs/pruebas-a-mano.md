# Pruebas a mano

Lo que conviene probar con la app abierta antes de dar por buena una función, además de la suite automática (`Probar.bat`) y de la autoprueba del ejecutable (`Rodaje.bat -Autoprueba`). Cada bloque dice qué preparar, qué hacer y qué tiene que pasar. Las pruebas que requieren medios reales usan cualquier video con sonido de la carpeta de muestras.

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
