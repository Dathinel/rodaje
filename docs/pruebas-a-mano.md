# Pruebas a mano

Lo que conviene probar con la app abierta antes de dar por buena una función, además de la suite automática (`Probar.bat`) y de la autoprueba del ejecutable (`Rodaje.bat -Autoprueba`). Cada bloque dice qué preparar, qué hacer y qué tiene que pasar. Las pruebas que requieren medios reales usan cualquier video con sonido de la carpeta de muestras.

## Exportar: muestreo de color y línea de ffmpeg

1. Abre un proyecto con un clip, pulsa **Ctrl+E** y despliega **Avanzado**.
2. Con el preajuste **Maestro ProRes 422 HQ**, «Muestreo de color» dice 4:2:2 y la línea «Formato» lleva «4:2:2». Con **Web 1080p** (H.264 por GPU) el combo queda en 4:2:0 y apagado si el codificador no da otra cosa.
3. Cambia a HEVC por CPU (o a un codificador que admita 4:4:4) y elige 4:4:4: la tarjeta de preajuste pasa a «Personalizado» y la estimación no cambia de orden de magnitud.
4. Pulsa **Copiar la línea de ffmpeg** y pégala en el Bloc de notas: empieza por `ffmpeg -hide_banner`, lleva `-f rawvideo`, `-i pipe:0`, el codificador elegido (`-c:v:0 …`), `-pix_fmt:v:0` con el muestreo elegido y termina con la ruta de salida entre comillas si tiene espacios. Con «Solo audio» no hay `pipe:0`.
5. Con un problema en rojo (carpeta inexistente), el enlace avisa que hay que arreglarlo primero y no copia nada.
