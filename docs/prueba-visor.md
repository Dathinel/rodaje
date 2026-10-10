# Prueba del visor de la fase 3

El visor (`src/Rodaje.Visor`) reproduce una secuencia de varias capas con el motor real: `TimelineSession` decodifica cada video en la GPU y mezcla el audio, `Compositor` junta las capas a tamaño de secuencia y `Presenter` las muestra ajustadas a la ventana. Es una ventana de prueba; la interfaz de verdad llega en la fase 4 con WinUI 3. Lo que se puede medir lo mide el recorrido automático; si se ve fluido y si se oye en sincronía solo lo puede juzgar una persona, y para eso es esta lista.

## Cómo abrirlo

Arrastra dos, tres o cuatro videos tuyos a la vez sobre `Rodaje.bat`, o haz doble clic y elígelos juntos en el diálogo (Ctrl o Shift para marcar varios). El primero (el que Windows ponga primero) va de fondo a pantalla completa con su audio; cada uno de los demás va en su propia pista encima, al 35 % de tamaño, en una esquina distinta (arriba a la derecha, abajo a la derecha, abajo a la izquierda, arriba a la izquierda), con su audio en silencio. La secuencia toma el tamaño y la velocidad de cuadros del primero. También abre un proyecto `.rodaje`.

| Tecla | Qué hace |
|---|---|
| Espacio | Reproducir o pausar |
| ← / → | Pausa y retrocede o avanza un cuadro |
| Shift+← / Shift+→ | Salta 5 s atrás o adelante |
| Inicio / Fin | Primer o último cuadro |
| Clic o arrastre en la barra de abajo | Buscar |
| 1 a 9 | Ocultar o mostrar la capa de esa pista de video (1 es el fondo) |
| Ctrl+1 a Ctrl+9 | Elegir la capa que se edita (al abrir, la de más arriba) |
| Alt+flechas | Mover la capa elegida 20 píxeles |
| + / - | Agrandar o achicar la capa elegida un 10 % |
| R | Girarla 15° |
| O | Cambiar su opacidad: 100 %, 75 %, 50 %, 25 % |
| Ctrl+Z / Ctrl+Y | Deshacer / rehacer (también ocultar y mostrar) |
| Ctrl+S | Guardar el proyecto como `.rodaje` junto al primer video; el título muestra dónde |
| I | Estadísticas en el título: tiempo y cuadro, capas, cuadros tarde y perdidos por capa, milisegundos de GPU de la composición, decodificadores abiertos, caché, pre-roll y dispositivo de audio |
| F / doble clic, Esc | Pantalla completa y salir de ella |

## Qué probar y qué es "bien"

Usa de 2 a 4 videos tuyos de teléfono o cámara, mejor si alguno es de 60 fps y otro tiene a alguien hablando o aplaudiendo.

1. **Fluidez con 2, 3 y 4 capas.** Deja correr un minuto con I activado.
   - Bien: todas las capas se mueven parejas, sin tirones; "tarde" se queda en 0 o casi en cada capa. "Perdidos" sube solo en una capa de más cuadros por segundo que la secuencia (un video de 60 fps sobre uno de 30 muestra uno de cada dos: es lo esperado).
2. **Finales de clip.** Los videos más cortos terminan antes que el fondo.
   - Bien: la capa que termina desaparece limpia, sin congelar las demás ni el audio.
3. **Imagen en imagen en vivo.** Mientras reproduce, Alt+flechas, +, -, R y O sobre la capa elegida; luego Ctrl+número para elegir otra.
   - Bien: cada cambio se ve al instante (en el siguiente refresco) y la reproducción no se detiene ni tartamudea.
4. **Ocultar y mostrar.** Pulsa el número de una capa dos veces.
   - Bien: desaparece y vuelve en la posición correcta. Al volver puede tardar un instante en aparecer (ver la nota de abajo).
5. **Deshacer.** Mueve y gira una capa, luego Ctrl+Z varias veces y Ctrl+Y.
   - Bien: vuelve paso a paso a como estaba y rehacer repite lo deshecho. Los cambios seguidos del mismo tipo (mover, escalar, girar, opacidad) sobre la misma capa se deshacen juntos.
6. **Sincronía.** Con el video donde alguien habla de fondo.
   - Bien: el sonido coincide con los labios o los golpes, también después de saltar con Shift+→ o de pausar y seguir.
7. **Guardar.** Ctrl+S, cierra y arrastra el `.rodaje` sobre `Rodaje.bat`.
   - Bien: se abre igual, con las capas donde las dejaste.

Anota cualquier tirón, salto de audio o demora con el archivo y el momento; es justo lo que las pruebas automáticas no ven.

## Recorrido automático

```
powershell -NoProfile -ExecutionPolicy Bypass -File herramientas\visor.ps1 fondo.mp4 pip1.mp4 pip2.mp4 -Prueba 20
```

Reproduce la secuencia; oculta y vuelve a mostrar la capa de arriba; la elige y la mueve, la escala y la gira con las mismas teclas de la tabla; deshace tres veces y rehace una, comprobando cada vez que la pantalla y el modelo coinciden; busca al 50 % y al 10 %, avanza cinco cuadros y retrocede dos comprobando el cuadro exacto, vuelve a reproducir y salta 5 s. Al final guarda una copia del proyecto junto al registro, la vuelve a abrir y comprueba que es idéntica. Imprime las cifras por capa, los milisegundos de GPU, la caché y la CPU (también en `TestResults\visor-prueba-*.txt`). Sale con 0 si todas las comprobaciones pasaron y no hubo errores de decodificación.

Cifras de referencia en el portátil de Daniel (RTX 3050 Laptop), 20 s con capas de H.264 1080p a 23,976 fps. Cada acción del registro dice cuántos cuadros perdidos, tarde y refrescos perdidos llevaba hasta ese momento, así se ve qué los causó.

| Capas | Composición en GPU (mediana / máx.) | Reproducir y editar la imagen en imagen | Cambio de capa visible en | CPU del proceso |
|---|---|---|---|---|
| 1 | 0,6-0,9 / 4,8-6,4 ms | 0 perdidos, 0 tarde, 0 refrescos perdidos | 16-18 ms | 3-4 % de un núcleo |
| 2 | 0,8-0,9 / 3,1-4,4 ms | 0, 0, 0 | 16-17 ms | 4 % |
| 4 | 0,2-1,0 / 4,0-4,1 ms | 0, 0, 0 (3 refrescos perdidos al abrir) | 15-17 ms | 9,5-11 % |

La mediana de GPU varía entre corridas porque la GPU sube o baja su reloj según la carga.

Cómo leer las cifras:

- **Perdidos y tarde.** Cuadros que el reloj dejó atrás sin mostrarse y refrescos en que el cuadro debido no estaba listo. Suben en los seeks durante la reproducción y en las capas de más cuadros por segundo que la secuencia.
- **Pre-roll.** Cortes que llegaron con su primer cuadro ya decodificado. La demostración no tiene cortes: el único que aparece es el de volver a mostrar una capa oculta.
- **Refrescos perdidos.** Intervalos entre presentaciones mayores que 1,5 veces el normal; hoy aparecen en los seeks y al volver a mostrar una capa.

## Limitaciones conocidas

- Al volver a mostrar una capa oculta, esa capa llega dos o tres refrescos tarde y la presentación se detiene una vez hasta unos 130 ms; los contadores cuentan como perdidos los cuadros del tiempo que estuvo oculta.
- HDR (PQ, HLG) se ve con colores incorrectos; solo se convierten las matrices BT.601, BT.709 y BT.2020.
- Mientras se arrastra o redimensiona la ventana con el ratón, Windows detiene el bucle y la imagen se congela un momento; el audio sigue.
