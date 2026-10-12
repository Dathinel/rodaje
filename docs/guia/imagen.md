# Imagen

Efectos, color, máscaras, croma y recorte. Todo se calcula en la GPU, se ve al instante en el visor y se anima con keyframes. La exportación compone siempre a resolución completa.

## Dónde está cada cosa

- **Propiedades** (panel derecho): la transformación del clip en secciones plegables: «Posición y tamaño» (X, Y, escala, rotación), «Opacidad y mezcla» (opacidad y modo de mezcla) y «Audio».
- **Filtros**: la pila de filtros del clip («Añadir filtro», el ojo para activar o desactivar, subir, bajar, quitar) y, debajo, los parámetros del filtro elegido.
- **Máscaras**: su pestaña propia.
- Biblioteca › **Efectos** (transformar, viñeta, desenfoque, nitidez, recortar, croma, estabilizar…) y **Filtros** (Color y LUT, Ruedas de color, Curvas y los looks de los paquetes): doble clic o arrastrar al clip los aplica.

Cada parámetro va en una fila: etiqueta, deslizador, valor con unidad, rombo de keyframe y restablecer. Por debajo de 340 px de ancho el deslizador baja a una segunda línea.

## Efectos de fábrica

| Efecto | Qué hace | Parámetros |
|---|---|---|
| Transformar | Posición, escala, rotación y opacidad | X e Y en píxeles, escala 1–400 %, rotación ±360°, opacidad 0–100 % |
| Color y LUT | Corrección básica y tabla de color | Exposición (±4 EV), contraste, saturación, temperatura, matiz, luces, sombras (±100 %), LUT e intensidad de la LUT |
| Ruedas de color | Un tinte y un nivel para sombras, medios y luces | Tres ruedas y tres niveles |
| Curvas | Curva general y por canal | Pestañas RGB, R, G, B |
| Viñeta | Oscurece los bordes | Cantidad, tamaño, suavidad |
| Desenfoque | Suave, de toda la imagen | Radio 0–100 px |
| Nitidez | Realza los bordes | Cantidad 0–200 %, radio 0,5–10 px |
| Recortar | Quita bordes, libre o con proporción | Izquierda, arriba, derecha, abajo; proporción Libre, 16:9, 9:16, 1:1, 4:5, 4:3 |
| Croma (fondo verde) | Vuelve transparente un color | Color clave con cuentagotas, tolerancia, suavidad, quitar derrame, limpiar borde |
| Estabilizar | Quita el temblor | Intensidad; recorte automático o sin recorte. Se analiza desde Estabilizar… |
| Máscara | Deja ver solo una parte | Ver [Máscaras](#máscaras) |

En los títulos solo se aplican desenfoque y nitidez.

## Keyframes

- El **rombo** de cada fila añade o quita un keyframe en el cursor (hueco sin animación, lleno con keyframes, con recuadro si hay uno en el cursor). Con keyframes, cambiar el valor crea o ajusta el del cuadro del cursor; sin ellos, cambia el valor fijo.
- Debajo aparecen «Keyframe anterior» y «siguiente», cuántos hay y la **curva hasta el siguiente**: Lineal, Suave, Acelerar, Frenar o Mantener.
- Menú de la fila: Restablecer y Quitar keyframes (pregunta antes). El menú ⋯ de la cabecera: Restablecer todo y Guardar como transición… (con dos keyframes o más).
- Los rombos se ven también en el clip de la línea de tiempo. A velocidad distinta de 1× los keyframes siguen al material.

## Color

![Ruedas de color y curvas en la pestaña Filtros](img/ruedas-y-curvas.png)

1. **Color y LUT** para lo básico: exposición, contraste, saturación, temperatura, matiz, luces y sombras.
2. **Ruedas de color**: arrastra el punto de cada rueda para teñir sombras, medios o luces (rojo a 0°, verde a 120°, azul a 240°); el deslizador de cada zona sube o baja su nivel. Doble clic vuelve al centro.
3. **Curvas**: un clic añade un punto, arrastrar lo mueve entre sus vecinos, doble clic o clic derecho lo quita. General (RGB) o por canal.
4. **Color automático** y **Balance de blancos** (Herramientas o menú del clip) fijan temperatura, matiz y exposición (y contraste, en Color automático) y avisan con lo que cambiaron y **Deshacer**.
5. **LUT**: en la fila «LUT» de Color y LUT elige una de los paquetes instalados o «Cargar .cube…» (tablas 3D de 2 a 65 puntos). «Intensidad de la LUT» la mezcla con la imagen original.

**Antes y después:** el botón del visor divide la imagen con una cortina; a la izquierda se ve sin color, LUT, viñeta, ruedas ni curvas; a la derecha, con todo. Arrastra el asa. Solo es vista previa.

![Cortina de antes y después en el visor](img/antes-despues-visor.png)

## Máscaras

Pestaña **Máscaras** (o menú del clip › Máscaras):

- **Rectángulo** y **Elipse** ponen una forma centrada. **Trazo libre** dibuja el contorno con el ratón sobre el visor; al soltar se cierra; Esc cancela. **Sujeto (Quitar fondo)** la crea la IA (ver [IA local](ia-local.md)).
- Parámetros, todos con keyframes: centro X e Y, ancho y alto, giro, borde suave e intensidad. Casilla **Invertir** para dejar ver lo de fuera.
- En el visor, arrastrar dentro mueve la máscara y una esquina cambia el tamaño alrededor de su centro.

## Croma

Añade **Croma (fondo verde)** al clip. Pulsa **Cuentagotas** y haz clic en el fondo del visor (Esc cancela): se toma el promedio de 5×5 píxeles de ese cuadro, sin color ni croma aplicados. Ajusta tolerancia y suavidad; «Quitar derrame» limpia el reflejo verde en los bordes y «Limpiar borde» cierra el contorno.

## Recortar

Añade **Recortar**. Mientras el filtro está elegido con Filtros a la vista, el visor muestra la imagen entera con lo quitado en sombra y ocho asas; con una proporción fija, la esquina crece desde la opuesta y el borde mueve también el otro lado. Al elegir una proporción pasa al recorte más grande centrado. Cada arrastre es un paso del historial.

## Modos de mezcla

En Propiedades › Opacidad y mezcla: Normal, Sumar, Multiplicar, Trama, Superponer, Luz suave, Luz fuerte, Oscurecer, Aclarar, Diferencia, Exclusión, Sobreexponer color y Subexponer color.

## Estabilizar

Menú del clip › **Estabilizar...** (o Herramientas). Analiza el movimiento y muestra el resultado («Temblor: A → B px por cuadro² (N % menos). Acercamiento: N %»). Ajusta la intensidad y decide si recorta el borde automáticamente. Pone el filtro Estabilizar al principio de la pila.

## Zonas seguras

Botón del visor: **Ninguno**, **Clásicos (acción y títulos)** (acción 93 %, títulos 90 % y la cruz del centro), los juegos de fábrica para apps de video («Horizontal con controles (16:9)», «Vertical corto con barra lateral (9:16)», «Vertical corto con botones abajo (9:16)», «Vertical corto con pie amplio (9:16)», «Vertical corto con pie y lateral (9:16)», «Historia vertical (9:16)»), los de los paquetes y los tuyos. Las zonas que tapa la interfaz se sombrean con su etiqueta; los márgenes van en línea discontinua. Si el juego es para otra forma, lo avisa.

**Nuevas zonas propias…** abre el editor sobre el visor: Añadir zona, nombre y etiqueta, arrastrar para mover y estirar por las esquinas, Guardar (queda en `complementos\propios`) o Cancelar. «Editar «…»» reescribe el mismo juego.

## Calidad de la vista previa

Ver › **Calidad de la vista previa**: Automática, Completa, Mitad o Cuarto. En Automática, si la GPU no llega al reproducir, baja un escalón (la cabecera dice «Auto · ½») y vuelve a completa en pausa. **Proxies en la vista previa**: Automático, Siempre que haya o Nunca (originales), y «Generar al importar los videos pesados». Nada de esto afecta a la exportación.
