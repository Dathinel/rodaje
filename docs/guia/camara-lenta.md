# Cámara lenta y cuadros intermedios

Al ralentizar un video (por ejemplo a 0,5× o 0,25×), aplicar una curva de velocidad o colocar un clip grabado a 24 cuadros/s en una secuencia de 30, el editor debe mostrar cuadros en instantes temporales que no existen en el archivo original. En la pestaña **Velocidad** del inspector (**Ctrl+R**), el desplegable **Cuadros intermedios** permite elegir cómo se calculan esos cuadros.

## Modos de cuadros intermedios

| Modo | Cómo funciona | Cuándo usarlo |
|---|---|---|
| **Cuadro más cercano** | Muestra el cuadro original más próximo al instante temporal. No altera los píxeles. | Es el ajuste de fábrica. Adecuado para edición rápida, animación paso a paso (*stop-motion*) o cuando se prefiere conservar la imagen original sin tocar. |
| **Mezcla de cuadros** | Combina los dos cuadros vecinos ponderando su transparencia según la fracción de tiempo. | Tomas con movimientos rápidos, caóticos o con mucho desenfoque; suaviza la reproducción con un fundido continuo sin crear deformaciones. |
| **Flujo óptico** | Calcula vectores de movimiento píxel a píxel entre cuadros vecinos en la GPU y deforma la imagen al instante exacto. | Tomas con movimiento continuo y fluido (paneos de cámara, caminatas, deportes). Genera una cámara lenta limpia, nítida y sin contornos dobles. |

## Cuándo usar cada uno

- **Acción constante y paneos:** Elige **Flujo óptico**. Es la opción que produce mayor sensación de fluidez real al reconstruir la trayectoria de cada detalle.
- **Mucho desenfoque o partículas:** Elige **Mezcla de cuadros**. Cuando hay humo, agua o movimientos muy rápidos con obturación lenta, el flujo óptico puede confundir los vectores; la mezcla simula el desenfoque de movimiento natural de una cámara lenta analógica.
- **Borrador o ritmo marcado:** Mantén **Cuadro más cercano** para maximizar el rendimiento y el tiempo de respuesta mientras armas el montaje inicial.

## Conformar cadencias a 1× (24 en 30)

Los modos de cuadros intermedios no están limitados a clips ralentizados: también funcionan a **1×**.

Si insertas un plano de cine (23,976 o 24 fps) en una secuencia de video para web o televisión (29,97 o 30 fps), la reproducción normal repite un cuadro de cada cuatro para rellenar la cadencia, lo que produce un pequeño tirón rítmico (*judder*). Al activar **Flujo óptico** o **Mezcla de cuadros** en el clip a 1×, el motor calcula los cuadros en los instantes precisos de la secuencia, logrando una conversión de cadencia suave y sin saltos.

## Cortes de escena y transiciones automáticas

- **Cortes de escena dentro del archivo:** Si un clip ralentizado contiene un cambio de plano, el flujo óptico analiza la diferencia de histogramas y el residuo de movimiento. Al detectar el corte de escena, conmuta automáticamente al **cuadro más cercano** en ese par de cuadros, evitando fundir o deformar dos imágenes no relacionadas.
- **Caída a mezcla por encima de 4×:** Cuando la cámara lenta es muy pronunciada (velocidades menores a 0,25×, donde el desplazamiento entre cuadros supera el rango de búsqueda en la GPU), el flujo óptico cae de manera transparente a **Mezcla de cuadros** para impedir aberraciones visuales.

## Marca en el clip («≈»)

En la línea de tiempo, cualquier clip que utilice un modo de interpolación (**Mezcla de cuadros** o **Flujo óptico**) muestra el símbolo **«≈»** junto a su etiqueta de velocidad (por ejemplo `25 % ≈`, o `50 % ◀ ≈` si además está invertido). Los clips que conservan el modo de fábrica (**Cuadro más cercano**) no muestran este distintivo.

## Límites conocidos

El flujo óptico en GPU funciona en tiempo real directamente sobre los planos de video nativos (sin conversiones intermedias de color), pero tiene limitaciones propias del análisis de movimiento clásico:
- **Bordes elásticos (*bordes de goma*) y halos:** En movimientos muy amplios (más de unos 40 píxeles por cuadro) o cuando un objeto en primer plano cruza delante de un fondo con patrones repetitivos, pueden apreciarse ligeras deformaciones elásticas en los contornos de la silueta.
- **Oclusiones complejas y transparencias:** Objetos translúcidos, reflejos en cristales o elementos que aparecen súbitamente desde fuera del encuadre no tienen correspondencia directa en el cuadro anterior; el sistema combina la verificación de ida y vuelta para aislar el defecto, pero en tomas extremas la **Mezcla de cuadros** suele ser preferible.

## Enlaces relacionados

- [Edición en la línea de tiempo](edicion.md): herramientas de corte, velocidad, reversa y curvas.
- [Exportar](exportar.md): preajustes de salida y rendimiento del motor de render.
- [Rendimiento y aceleración gráfica](rendimiento.md): optimización de la GPU y gestión de recursos.
- [Interpolación de fotogramas (arquitectura)](../interpolacion.md): especificación técnica de la fase y diseño del pipeline de render.
