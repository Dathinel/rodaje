# Prueba del visor de la fase 2

El visor (`src/Rodaje.Visor`) reproduce un solo archivo con el motor real: decodificación D3D11VA en la GPU, el audio por WASAPI como reloj maestro y `PlaybackScheduler` eligiendo el cuadro de cada refresco. Lo que se puede medir lo mide el recorrido automático; si se ve y se oye fluido solo lo puede juzgar una persona, y para eso es esta lista.

## Cómo abrirlo

```
powershell -NoProfile -ExecutionPolicy Bypass -File herramientas\visor.ps1 -Archivo "C:\ruta\video.mp4"
```

La primera vez compila el exe con Native AOT en `dist\Rodaje-visor\` (un minuto); con `-SinPublicar` abre el ya compilado. También se puede abrir sin archivo y arrastrar un video a la ventana, o pulsar Ctrl+O.

| Tecla o gesto | Qué hace |
|---|---|
| Espacio | Reproducir o pausar |
| ← / → | Pausa y retrocede o avanza un cuadro |
| Shift+← / Shift+→ | Salta 5 s atrás o adelante |
| Inicio / Fin | Primer cuadro / último cuadro |
| Clic o arrastre en la barra de abajo | Buscar; mientras se arrastra, la imagen sigue al ratón |
| Doble clic en la imagen, o F | Pantalla completa sin bordes; Esc sale |
| I | Muestra en el título: tiempo, cuadro, modo de decodificación, cuadros perdidos y tarde, desfase A/V máximo y dispositivo de audio |
| `--cpu`, `--sin-audio` | Opciones de línea de órdenes para comparar con decodificación por software o sin audio |

## Qué probar y qué es "bien"

Usa al menos un video tuyo de teléfono o cámara (H.264 o HEVC, idealmente uno de 60 fps y uno grabado en vertical) y, si quieres, los de prueba que deja el recorrido automático.

1. **Reproducción continua, 1 minuto.** Pulsa I y deja correr el video.
   - Bien: el movimiento se ve parejo, sin tirones periódicos; en el título "perdidos" y "tarde" se quedan en 0 o casi (unos pocos justo después de cada salto son normales).
2. **Sincronía de labios o de golpes.** Un video donde alguien hable, aplauda o golpee algo.
   - Bien: el sonido y la imagen coinciden; no se nota que el audio vaya antes o después. "A/V máx" en el título queda por debajo de lo que dura un cuadro (33 ms a 30 fps, 17 ms a 60 fps).
3. **Pausa y cuadro a cuadro.** Pausa en un movimiento rápido y pulsa → varias veces, luego ← varias veces.
   - Bien: cada pulsación cambia exactamente un cuadro, el número de cuadro del título sube o baja de a uno, y hacia atrás también responde (puede tardar un poco más, porque tiene que volver al último keyframe).
4. **Saltos.** Shift+→ y Shift+← varias veces seguidas mientras reproduce, y luego Inicio y Fin.
   - Bien: la imagen nueva aparece en una fracción de segundo, el audio sigue desde el punto correcto y no queda desfasado después del salto.
5. **Arrastrar la barra.** Arrastra la barra de abajo de un lado a otro, despacio y rápido.
   - Bien: la imagen va siguiendo al ratón (no tiene que ser instantánea, pero no se congela todo el arrastre); al soltar continúa desde ahí, y si estaba reproduciendo, sigue reproduciendo.
6. **Ventana.** Cambia el tamaño de la ventana, maximízala, pulsa F y luego Esc.
   - Bien: la imagen conserva su proporción con barras negras arriba y abajo o a los lados, nunca se estira; un video vertical se ve vertical.
7. **Colores.** Compara con el reproductor de Windows.
   - Bien: los colores y el contraste se ven iguales (negros negros, sin tono lavado). Un video HDR (HLG o PQ) se verá lavado: es esperado, esta fase solo convierte BT.709 (ver abajo).
8. **Sin audio.** Desconecta los audífonos o desactiva la salida mientras suena.
   - Bien: el video sigue; al volver el dispositivo, vuelve el sonido sincronizado. Con I, el título dice el dispositivo o por qué no hay sonido.

Anota cualquier cosa que se sienta rara (tirones, saltos de audio, colores, demora al pausar) con el archivo y el momento; es justo lo que las pruebas automáticas no ven.

## Recorrido automático

```
powershell -NoProfile -ExecutionPolicy Bypass -File herramientas\visor.ps1 -Archivo video.mp4 -Prueba 10
```

Abre la ventana, reproduce, busca al 50 % y al 10 %, pausa, avanza cinco cuadros y retrocede dos comprobando que cada paso llega al cuadro exacto, vuelve a reproducir, salta 5 s y al final imprime las estadísticas (también quedan en `TestResults\visor-prueba-*.txt`). Sale con código 0 si todos los pasos de cuadro llegaron al cuadro pedido y no hubo errores de decodificación.

Cómo leer las cifras:

- **Desfase A/V máximo.** Distancia entre el reloj de audio y el inicio del cuadro en pantalla, sin contar la espera que sigue a cada seek. Al mostrar siempre el cuadro que contiene al reloj, el valor normal es casi un cuadro (41,7 ms a 23,976 fps): está en sincronía mientras no lo pase.
- **Perdidos y tarde.** Cuadros que el reloj dejó atrás sin mostrarse y refrescos en los que el cuadro debido no estaba listo. Tras un seek en reproducción, el audio sigue corriendo mientras el video llega al cuadro pedido; los pocos que se pierden ahí aparecen en estos contadores.
- **Refrescos perdidos.** Intervalos entre presentaciones más largos que 1,5 veces el normal. Hoy aparecen solo en los seeks durante la reproducción: la GPU está ocupada decodificando desde el keyframe y la cadena de intercambio tarda dos o tres refrescos en admitir otro cuadro.

## Fuera de esta fase

- Solo BT.709 en rango limitado. BT.2020, PQ y HLG (HDR) se ven con colores incorrectos; el croma se muestrea en el centro, sin corregir el desplazamiento de 4:2:0.
- Un solo archivo, sin línea de tiempo ni capas (fase 3).
- Mientras se arrastra o redimensiona la ventana con el ratón, Windows detiene el bucle de la ventana y la imagen se congela un momento; el audio sigue.
