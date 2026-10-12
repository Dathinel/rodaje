# Títulos y subtítulos

Los títulos son clips de texto que se dibujan sobre el visor y se editan en la pestaña **Texto** del panel derecho. Los subtítulos son títulos con el estilo «Subtítulo» en una pista de subtítulos, escritos a mano o generados por la IA con el tiempo de cada palabra.

## Añadir un título

- **T** (o **Añadir texto** en Herramientas, o Editar › Añadir texto): pone un título de 5 s en el cursor, en la pista de video libre más alta (sin contar la principal, las bloqueadas ni las de subtítulos) o en una nueva «Texto», lo selecciona y abre la pestaña Texto.
- Desde la biblioteca, categoría **Texto**: doble clic en un estilo cambia el estilo del título seleccionado o, si no hay, crea uno en el cursor; arrastrarlo a la línea de tiempo lo pone donde caiga.
- El clip del título se mueve, corta y recorta como cualquier otro; no tiene tope de duración.

## La pestaña Texto

![Un título con dos líneas, seleccionado en el visor](img/texto.png)

- **Texto**: el contenido, con varias líneas. Cada tanda de tecleo es un paso del historial.
- **Estilo**: el estilo elegido (de la biblioteca o de un paquete), **Guardar como estilo** (queda en tus complementos propios y en la biblioteca) y **Reaplicar estilo**. Fuente (lista o escrita), grosor, cursiva, tamaño, color con transparencia, alineación, interlineado y espaciado.
- **Borde**, **Sombra** y **Fondo**, plegados.
- **Entrada y salida**: los bordes por los que entra y sale, y **Convertir en keyframes** para seguir animando a mano.
- Si la fuente viene de un paquete que no está instalado o Windows no la encuentra, el panel lo avisa; la exportación usa una fuente de respaldo o pide instalar el paquete.

**En el visor:** arrastra el cuerpo del título para moverlo, una esquina para escalarlo desde el centro y el asa de arriba para girarlo. Esc revierte el gesto. Un clic sobre otro título lo selecciona. Con keyframes, el gesto ajusta el del cursor.

**Animación:** la transformación del título (posición, escala, rotación, opacidad) se anima en Propiedades con keyframes, igual que un clip de video. Los filtros de color, LUT y viñeta no se aplican a los títulos; desenfoque y nitidez sí.

## Subtítulos automáticos

Necesita el módulo «Subtítulos automáticos» ([IA local](ia-local.md)). Selecciona el clip con voz y pulsa **Subtítulos automáticos** (Herramientas o menú del clip):

1. «Idioma de la voz»: Detectar el idioma, Español, Inglés o Portugués.
2. Transcribe (en la GPU o en la CPU) y lista los subtítulos con sus tiempos; un clic lleva el cursor a cada uno. «Transcribir de nuevo» repite.
3. **Aplicar** crea los clips en la pista de subtítulos más alta que esté libre o en una nueva «Subtítulos», con el estilo «Subtítulo» (blanco sobre fondo semitransparente), partidos a 42 caracteres por línea, dos líneas, de 1 a 7 s y cortando en pausas de 0,7 s. Aviso: «Se pusieron N subtítulos.» Ctrl+Z lo deshace todo.

Para crear la pista antes: Editar › **Añadir pista de subtítulos**, o clic derecho en la cabecera de una pista de video › «Usar para subtítulos».

## Por palabra

Cada subtítulo automático guarda sus palabras con su hora. En la pestaña Texto, sección **Palabras**:

- Cada palabra es editable: corrígela y pulsa Intro.
- La tijera **Dividir aquí** parte el subtítulo en esa palabra; **Unir con el siguiente** los junta.
- La hora de cada palabra lleva el cursor a ese instante.

## Karaoke

En la misma sección, **Resaltar la palabra que suena** pinta cada palabra en el color de «Resaltado» mientras suena. El motor dibuja una variante del subtítulo por palabra, así que funciona también al exportar.

## Traducir

Clic derecho en un subtítulo o Editar › **Traducir pista…** (módulo «Traducir subtítulos»): elige la pista, el idioma del original (o Detectar) y el destino entre 20 idiomas. Crea una pista nueva encima, «<pista> (<idioma>)», con los mismos tiempos; la original no cambia. Lo que no se pudo traducir conserva el original y se avisa.

## Texto a voz

**Texto a voz** (Herramientas, Editar o menú del clip) lee un texto con una de 34 voces en español, portugués o inglés y lo pone en una pista de audio; con un subtítulo seleccionado, «Usar el texto del clip seleccionado» lo toma. Detalle en [IA local](ia-local.md).

## Estilos y fuentes de paquetes

Los paquetes pueden traer estilos de texto (categoría Texto de la biblioteca) y fuentes. Un estilo de paquete se copia al título con su origen, así que el proyecto sigue abriendo aunque desinstales el paquete. Ver [Complementos](complementos.md).

## Importar o exportar SRT y VTT

- **Importar subtítulos…** (Archivo, o clic derecho en la cabecera de una pista de subtítulos) lee un `.srt` o un `.vtt` (UTF-8, UTF-16 o Latin 1) y crea una pista de subtítulos nueva, con el nombre del archivo y el estilo de subtítulos por defecto. Conserva los saltos de línea y quita las etiquetas de estilo (`<i>`, `{\an8}`…). Los tiempos van al cuadro; si dos se pisan, el primero termina donde empieza el segundo. Es un solo paso del historial: Ctrl+Z la quita entera.
- **Exportar subtítulos…** (Archivo o el menú de la cabecera) guarda la pista como `.srt` o `.vtt` en UTF-8, al milisegundo. Desde Archivo exporta la pista del subtítulo seleccionado o, si no hay, la de subtítulos más alta.
- **Al exportar el video**, Avanzado › Contenedor › «Subtítulos» elige entre quemarlos en la imagen (lo de siempre), quemarlos y además llevarlos como pista aparte, o solo como pista aparte, que el reproductor activa o desactiva: MP4 y MOV con `mov_text`, MKV con SRT y WebM con WebVTT. Ver [Exportar](exportar.md#avanzado).
