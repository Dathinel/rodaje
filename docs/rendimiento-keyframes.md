# Rendimiento con muchos keyframes y con material de muchos fps

Reporte de un usuario: con un texto «tambaleando» y muchos keyframes el editor se satura («el sonido explota o la imagen se congela»), y un video de muchos fps se traba. Medido en Debug, con prioridad baja, sin abrir la app (`tests\Rodaje.Core.Tests\Performance`, `tests\Rodaje.App.Tests\Performance`).

## Dónde estaba el coste

| Pieza | Antes | Ahora |
|---|---|---|
| Gráfica del inspector (1250 keyframes en un parámetro), un dibujo | 59.953 puntos, 26,8 ms (ya con bisección) y más con la búsqueda lineal; se repite en cada movimiento del cabezal | 2.499 puntos, 1,4 ms |
| Evaluar un parámetro (`Parameter.Evaluate`, `MotionPath`) con 1250 keyframes | búsqueda lineal: 3,7 µs | bisección: 2,3 µs (el resto es la fórmula) |
| Rombos de keyframes en gráfica y línea de tiempo | un objeto de composición por keyframe (5000 en pantalla) | uno cada 5–6 px como mucho; el seleccionado siempre |
| Añadir un keyframe (comando + instantánea) a 5000 en total | 0,19 ms por keyframe | igual, 0,15-0,19 según la tanda (crece con N: copia la lista del clip; el deshacer guarda hasta 500 pasos, ~19 MB a 5000) |
| Deshacer / rehacer un paso | 0,015 ms | igual |
| Guardar el proyecto, 5000 keyframes | 12 ms (1 MB) | igual |

Lo que NO era el problema: añadir, deshacer y guardar son lineales y baratos; el motor (`Engine.Curve`) ya evaluaba por bisección sobre arreglos y el texto no se vuelve a maquetar con cada muestra (la caché del rasterizador va por contenido, no por la transformación animada). La saturación venía de la interfaz: la gráfica muestreaba 48 puntos por tramo y creaba un `Polyline` y un rombo por keyframe en cada cambio de cabezal o de selección, en el hilo de la interfaz.

## Presets

`TextAnimationPresets` trae **Tambaleo** (giro y balanceo que se apagan) y **Temblor** (sacudida que se calma): ~7 y ~14 keyframes por curva generados, sin ponerlos a mano. Están en `docs/guia/titulos-y-subtitulos.md`.

## Audio

- El audio (WASAPI, motor y reproductor) no depende del hilo de la interfaz: ni `Rodaje.Audio`, ni `Rodaje.Engine`, ni `Rodaje.Playback` referencian WinUI ni un `Dispatcher`; el hilo de salida va con prioridad alta y lee de un búfer que llena el hilo de decodificación. Una interfaz bloqueada no deja el audio sin alimentar (por lectura del código: no se midió con la interfaz colgada a propósito).
- **Defecto real corregido:** al faltar audio en un bloque (`FrameRenderer.Pull`) el hueco se rellenaba con ceros de golpe y, al volver, la señal arrancaba de golpe: un escalón que suena como chasquido. Ahora entra y sale con un fundido de 128 cuadros (2,7 ms a 48 kHz). Prueba: ningún salto entre muestras seguidas supera 0,5/100 con una señal de 0,5.
- La imagen no espera a la interfaz para decodificar; lo que se congelaba era el propio hilo de interfaz ocupado dibujando (arriba).

## Material de 120 y 240 fps

El plan (`VideoClipPlan.NextShown`) pide solo los cuadros que se ven: en 120/240 fps sobre 30/60 salta de 4 en 4 (o 2, 8) y la decodificación descarta los que no son de referencia. Prueba nueva `HighFrameRateSourceTests`: 30 o 60 cuadros por segundo pedidos, con el paso exacto. **No quedó medido** con un clip sintético de FFmpeg de 120 fps decodificando de verdad (el PC compartido estaba ocupado con otra medición): queda como paso para una máquina libre.

## Lo que se vio con el ratón (repaso del 19-nov) y lo que se midió después

- **Deslizador entre dos keyframes con 1000 puestos: el contador no sube.** No es un defecto: un deslizador de un parámetro animado edita el keyframe más cercano si cae a menos de medio cuadro del cursor (`ClipPropertyEditor.KeyframeNear`), y con keyframes cada pocos milisegundos cualquier posición del cursor cae cerca de uno. El valor cambia (el del keyframe cercano) y «Sin guardar» es correcto; solo crea uno nuevo si el cursor está a más de medio cuadro de todos.
- **Recorrido de la trayectoria sobre el visor** (la causa más probable de «se queda dibujando»): `MotionPathView.Sample` tomaba 24 muestras por tramo y el visor creaba una elipse y dos líneas por keyframe en cada redibujo; con 1000 keyframes, ~24.000 puntos más ~5000 objetos de composición. Ahora tiene tope de 1500 puntos y un punto por casilla de 8 px (prueba `KeyframeGraphScalingTests`).
- **Historial:** cada alta de keyframe es un paso y guarda una copia del clip, pero el coste medido es lineal y pequeño (arriba: 0,15 ms por alta, 19 MB con 5000 altas y el tope de 500 pasos); no hace falta fundir pasos distintos (cada alta es una acción del usuario). Lo que ya se funde es el arrastre del mismo keyframe.
- Reproducir un título con 1000 keyframes cuesta lo mismo que con 60 (1,3–1,5 s de CPU por 3 s a 720p según el repaso): la evaluación por cuadro no es el problema.

## Lo que ya existe en textos animados (auditoría, 20-nov)

- **Animaciones de entrada y salida** (`TextAnimationPresets`): Desvanecer, Subir, Bajar, Rebote, Escala, Elástico, Desenfoque, Girar, Máquina de escribir y Líneas, por bloque, línea, palabra o letra, con desfase y orden; más Tambaleo y Temblor (nuevos, de entrada y salida).
- **Resaltado tipo karaoke** (`StyleHighlight`): la palabra que suena o un barrido de color; necesita las palabras transcritas del subtítulo.
- **Estilos de fábrica:** solo «Texto» (blanco, negrita, sombra) en la biblioteca y «Subtítulo» (blanco sobre caja negra, sin animación) para pistas de subtítulos. Los demás estilos vienen de paquetes o son propios. **Ningún estilo de fábrica usa la entrada por palabra ni el karaoke**: existen las piezas, no los estilos armados.
- **Plantillas de movimiento (D-064):** rótulos y títulos de varias capas con campos para rellenar; no se auditaron una a una.
- **Animación continua (en bucle):** no existe. Un movimiento que no se acaba hoy solo se logra con keyframes repetidos, que es lo que saturaba.
