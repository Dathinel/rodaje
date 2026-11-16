# Plantillas de movimiento

Un rótulo inferior con su barra, su nombre y su cargo, o un título de varias líneas que entran una tras otra, es siempre lo mismo con otro texto. Una **plantilla de movimiento** lo guarda entero —capas, estilos, animación y posición— y solo deja a la vista los **campos** que cambian: el nombre, el cargo, el color de acento. Se pone en la línea de tiempo como un solo clip y se rellena desde el inspector.

Hay plantillas de **títulos y rótulos** (un clip con varias capas de texto), de **transición** y de **efecto**. Llegan en paquetes de [Complementos](complementos.md) o las haces tú con tus propios títulos.

## Usar una plantilla

1. Abre **Plantillas** en el carril de la biblioteca. Cada plantilla de títulos enseña su miniatura; al pasar el ratón, se anima. La primera vez la miniatura tarda un momento: Rodaje la compone de fondo con la plantilla misma y la guarda.
2. **Doble clic** (o clic derecho › **Usar en la línea de tiempo**) pone la plantilla en el cursor, en la pista de video libre más alta, con su duración. También puedes **arrastrarla** a una pista.
3. Con el clip seleccionado, la pestaña **Texto** del inspector muestra sus campos: textos, colores, fuente, números, casillas u opciones, según lo que la plantilla exponga. Cada cambio es un paso del historial (**Ctrl+Z** lo deshace); teclear o arrastrar un deslizador cuenta como un solo paso.

El clip se mueve, se corta, se copia y se pega como cualquier título. Al **recortarlo** no baja de la duración mínima de la plantilla (lo que necesitan su entrada y su salida). El proyecto guarda una copia completa: se ve igual aunque desinstales el paquete.

Una plantilla de **transición** va al corte del clip seleccionado y una de **efecto**, al clip seleccionado, las dos con sus valores por defecto.

## Convertir en títulos

Para cambiar algo que la plantilla no expone, **Convertir en títulos** (en el inspector o en el menú del clip) la reemplaza por un título por capa, en pistas libres, con lo mismo que se veía. Desde ahí son títulos corrientes ([Títulos y subtítulos](titulos-y-subtitulos.md)) y ya no una plantilla.

## Actualizar a una versión nueva

Si instalas una versión más nueva del paquete, el inspector ofrece **Actualizar a 1.1.0** en los clips que salieron de la anterior. Al pulsarlo, Rodaje reaplica la plantilla nueva y **conserva tus valores** en los campos que sigan existiendo; los que ya no existan o ya no valgan vuelven a su valor por defecto, y un aviso dice cuáles. Nunca se actualiza sola, y **Ctrl+Z** vuelve a la versión anterior.

## Guardar tus títulos como plantilla

1. Arma el rótulo con títulos corrientes: uno por capa, cada uno en su pista, con su estilo, su posición y su animación.
2. Selecciónalos todos, clic derecho › **Guardar como plantilla…**.
3. El diálogo lista los campos de cada capa: **texto**, **color de relleno**, **color de fondo**, **color de borde**, **fuente** y **tamaño**. Marca **Expuesto** en los que se podrán cambiar y escribe el nombre que verá quien la use («Nombre», «Color de acento»).

La plantilla queda en **Mis plantillas**, dentro de Plantillas, y en disco en tus complementos propios (`propios\<nombre>.json`). Cada título es una capa con su posición y su desfase respecto del primero. No viajan los efectos del clip (salvo la posición), los keyframes ni el formato por palabra.

## Compartir: exportar e importar

- **Exportar:** clic derecho sobre una plantilla tuya › **Exportar…** escribe un `.paquete` con esa plantilla.
- **Importar:** el `.paquete` se instala como cualquier otro: desde **Complementos** o soltándolo en la biblioteca.

## Limitaciones

- En esta versión los campos de una plantilla de transición o de efecto no se editan desde el inspector: se aplican con sus valores por defecto.
- La miniatura animada es solo para las plantillas de títulos.
