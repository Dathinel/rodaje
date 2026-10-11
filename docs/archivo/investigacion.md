# Investigación: cómo resuelven otros editores los problemas que Rodaje va a enfrentar

Este documento recoge lo que se aprendió al estudiar editores de video de código abierto antes de escribir una sola línea del núcleo de Rodaje. El objetivo no era encontrar código para reutilizar, sino entender qué decisiones de diseño tomaron otros proyectos, qué consecuencias tuvieron con los años y qué errores conviene no repetir. Cada sección describe la arquitectura de un proyecto a nivel conceptual: cómo modela la línea de tiempo, cómo guarda el proyecto, cómo deshace las operaciones, cómo reproduce y cómo exporta. Al final se explica qué toma Rodaje de cada uno y por qué.

## Declaración de sala limpia

Todo lo que aparece aquí se obtuvo leyendo documentación pública, formatos de archivo documentados, discusiones de los propios proyectos y la estructura general de su código, con el único fin de comprender la arquitectura y el comportamiento. No se copió, tradujo ni adaptó código de ninguno de estos proyectos, y Rodaje no comparte con ellos ni nombres internos de clases ni algoritmos transcritos. Varios de los proyectos estudiados tienen licencia GPL; si en algún momento se considerara tomar un fragmento literal de uno de ellos, el trabajo se detiene y se consulta, porque eso obligaría a cambiar la licencia de Rodaje a GPL, incompatible con su licencia no comercial (ver `docs/legal.md`).

Los programas comerciales que inspiran la experiencia de uso, en especial el estilo de edición rápida popularizado por las aplicaciones móviles de video corto, solo aportan ideas de producto. Ninguno de sus binarios se ha descompilado ni analizado, y ningún recurso suyo se ha extraído.

| Proyecto | Licencia verificada | Fuente |
|---|---|---|
| MLT Framework | LGPL-2.1 (algunos módulos GPL) | https://github.com/mltframework/mlt/blob/master/COPYING |
| Kdenlive | GPL-3.0 | https://github.com/KDE/kdenlive/blob/master/COPYING |
| Shotcut | GPL-3.0 | https://github.com/mltframework/shotcut/blob/master/COPYING |
| libopenshot | LGPL-3.0 | https://github.com/OpenShot/libopenshot/blob/develop/COPYING |
| openshot-qt | GPL-3.0 con excepción propia (sección 18) | https://github.com/OpenShot/openshot-qt/blob/develop/COPYING |
| Olive | GPL-3.0 | https://github.com/olive-editor/olive/blob/master/LICENSE |
| LosslessCut | GPL-2.0 | https://github.com/mifi/lossless-cut/blob/master/LICENSE |

## MLT, Kdenlive y Shotcut

### El modelo de servicios de MLT

MLT es un motor multimedia escrito en C que sirve de base a dos de los editores libres más usados, Kdenlive y Shotcut. Su idea central es que todo lo que produce o transforma imágenes y sonido es un servicio, y que hay cuatro tipos de servicio. Un productor genera cuadros, ya sea leyendo un archivo, generando un color sólido o un texto. Un filtro toma los cuadros de un productor y los modifica, por ejemplo corrigiendo el color o cambiando el volumen. Una transición combina dos entradas en una, como un fundido o una composición con transparencia. Un consumidor es el extremo final que pide cuadros: puede ser la ventana de vista previa, la tarjeta de sonido o un codificador que escribe un archivo.

El funcionamiento es perezoso y de arrastre: nada se calcula hasta que el consumidor pide el cuadro siguiente. Cuando lo pide, la petición recorre el grafo hacia atrás, cada servicio pide a su vez lo que necesita a los anteriores, y el cuadro final se arma de regreso. Esto tiene la gran ventaja de que la misma estructura sirve para previsualizar y para exportar, porque solo cambia el consumidor que hace las peticiones.

Para organizar el tiempo, MLT usa tres contenedores. La lista de reproducción (playlist) es una secuencia de entradas una detrás de otra, y equivale a una pista. El multipista (multitrack) agrupa varias listas en paralelo. El tractor envuelve el multipista y le añade las transiciones y filtros que mezclan las pistas entre sí.

### La posición implícita y sus consecuencias

El formato MLT XML describe una lista de reproducción como una sucesión de elementos `<entry producer="..." in="..." out="..."/>` intercalados con elementos `<blank length="..."/>`. Ninguna entrada dice en qué momento de la línea de tiempo empieza: su posición es la suma de las duraciones de todo lo que tiene delante, sean clips o espacios en blanco. Este diseño hace trivial el comportamiento de "rizado" (ripple), en el que borrar un clip desplaza automáticamente todo lo que viene detrás, porque basta con quitar la entrada.

El precio aparece en la colocación libre. Si el usuario quiere mover un clip a un punto arbitrario sin afectar a los demás, el editor tiene que recalcular los espacios en blanco antes y después, partirlos o fusionarlos, y cuidar que la suma siga cuadrando. Lo mismo ocurre con operaciones que el usuario piensa como "poner este clip en el segundo 12": en un modelo implícito no existe un campo que diga 12, solo una cadena de duraciones. Gran parte de la complejidad de la capa de modelo de Kdenlive existe precisamente para traducir entre la visión del usuario, que piensa en posiciones absolutas, y la representación de MLT, que piensa en secuencias.

### El deshacer de Kdenlive

Kdenlive reescribió su línea de tiempo alrededor de 2017 con un diseño de deshacer funcional. Cada operación elemental se expresa como un par de funciones, una que hace el cambio y otra que lo revierte, guardadas como lambdas de C++ de un tipo genérico que el proyecto llama `Fun`. Las operaciones compuestas, como mover un grupo de clips, se construyen encadenando esas lambdas con una macro (`PUSH_LAMBDA`) que va acumulando tanto el "rehacer" como el "deshacer". Si a mitad de una operación compuesta una sub-operación falla, por ejemplo porque el destino está ocupado, el sistema ejecuta de inmediato el deshacer acumulado hasta ese punto, de modo que el modelo nunca queda a medio modificar. Al terminar con éxito, el par completo se envuelve en un comando genérico que se empuja a la pila de deshacer de Qt (`QUndoStack`).

Los modelos de Kdenlive identifican cada objeto con un entero único en lugar de con punteros: hay un modelo de línea de tiempo, un modelo por clip y un modelo de grupos que sabe qué clips se mueven juntos. Los identificadores enteros permiten que una lambda de deshacer se refiera a un clip aunque ese clip haya sido destruido y recreado entre medio, algo que con punteros sería frágil.

### El deshacer de Shotcut

Shotcut eligió el camino clásico del patrón Comando. Cada operación de la línea de tiempo es una subclase de `QUndoCommand` (reunidas en el archivo de comandos de la línea de tiempo del proyecto) con su propio `redo` y `undo`. Para los arrastres, que generan decenas de pequeños movimientos por segundo, los comandos implementan `mergeWith`, de modo que todo un arrastre se fusiona en un único paso de deshacer. Para las operaciones difíciles de invertir a mano, Shotcut usa un ayudante que toma una instantánea del estado de las listas de reproducción afectadas antes y después del cambio, y deshace restaurando la instantánea anterior. Esta mezcla de comandos finos con instantáneas es un punto intermedio pragmático: las instantáneas son más caras en memoria, pero eliminan clases enteras de errores de inversión incompleta.

### Reproducción en tiempo real

En MLT, el consumidor de vista previa tiene un hilo de lectura anticipada que pide cuadros por delante de la posición de reproducción y los guarda en una cola. El reloj maestro es el audio: la tarjeta de sonido consume muestras a un ritmo que no se puede negociar, y el video se sincroniza con ese ritmo. Cuando el sistema no alcanza a producir los cuadros a tiempo, se descartan cuadros de video, nunca muestras de audio, porque un salto en la imagen se tolera mucho mejor que un chasquido o un corte de sonido.

Para que la reproducción sea fluida con material pesado, ambos editores ofrecen proxies: versiones de baja resolución y códec ligero de cada archivo que se usan durante la edición y se sustituyen por los originales al exportar. También reducen la escala de la vista previa, componiendo a la mitad o a un cuarto de la resolución del proyecto. Kdenlive añade la pre-renderización de zonas de la línea de tiempo: el usuario marca un tramo con muchos efectos, el programa lo renderiza en segundo plano a un archivo temporal y lo reproduce desde ahí.

### Composición y la lección de los efectos en GPU de Shotcut

La composición se hace de abajo hacia arriba: la pista inferior es el fondo y cada pista superior se superpone con su transición de mezcla. Shotcut integró en su momento Movit, una biblioteca de efectos en GPU basada en sombreadores GLSL, para acelerar la composición y la corrección de color. Hacia la versión 18.11 el modo GPU quedó oculto y desaconsejado por inestabilidad: diferencias entre controladores, fallos difíciles de reproducir y una interacción compleja entre el hilo de OpenGL y el resto del motor, que había sido diseñado pensando en la CPU. La lección no es que la GPU sea mala idea, sino que añadirla después a un motor pensado para CPU, con dos caminos de render que deben dar el mismo resultado, es una fuente constante de errores.

### Fotogramas clave en MLT

MLT representa la animación de un parámetro como una cadena de texto de la forma `cuadro[tipo]=valor;cuadro[tipo]=valor`, donde el tipo indica la interpolación hacia el siguiente punto. Con los años la lista de interpolaciones creció para incluir muchas curvas de suavizado (lineal, discreta, suave y numerosas variantes de aceleración y rebote). El formato es compacto y legible, pero al estar en texto y referido a números de cuadro depende de la cadencia del proyecto y requiere análisis sintáctico cada vez que se lee.

### Exportación

La exportación reutiliza exactamente el mismo grafo de servicios que la vista previa, cambiando el consumidor por el de avformat, que es la interfaz de MLT con las bibliotecas de FFmpeg. Al exportar, el consumidor se configura en modo no real (un valor negativo de `real_time` que indica cuántos hilos usar sin descartar cuadros), de modo que el render tarda lo que tenga que tardar pero no pierde ningún cuadro. Los codificadores por hardware se eligen simplemente cambiando el códec, por ejemplo `vcodec=h264_nvenc` para las GPU de NVIDIA. Que vista previa y exportación compartan el pipeline es la mejor garantía de que lo que el usuario ve es lo que obtiene.

## OpenShot

### Formato de proyecto y modelo de capas

OpenShot se divide en una biblioteca de C++ (libopenshot, LGPL-3) que hace el trabajo multimedia, y una interfaz en Python con Qt (openshot-qt, GPL-3). Su archivo de proyecto `.osp` es JSON. Una peculiaridad es que no hay objetos de pista que contengan clips: cada clip tiene un número de capa (layer), y las capas son números grandes y espaciados, de modo que insertar una capa intermedia no obliga a renumerar todas. La posición de cada clip es explícita, un campo `position` en segundos, lo que hace sencilla la colocación libre.

### Deshacer mediante diferencias de JSON

Todo cambio al proyecto pasa por un gestor de actualizaciones que registra acciones de tres tipos: insertar, actualizar y borrar, cada una con la ruta dentro del JSON, el valor nuevo y, para poder deshacer, los valores antiguos. Deshacer consiste en aplicar la acción inversa. Para operaciones compuestas existe una noción de transacción que agrupa varias acciones en un solo paso. El diseño es elegante porque cualquier cambio, sea cual sea, se expresa con el mismo vocabulario, y porque las mismas acciones sirven para sincronizar la interfaz de Python con la biblioteca de C++. La contrapartida es que la corrección depende de que cada acción registre fielmente su valor anterior, y los errores de ese tipo se manifiestan como deshaceres que dejan el proyecto en un estado ligeramente distinto al original.

### Migraciones y un error instructivo

Al abrir un proyecto antiguo, OpenShot compara la versión con la que se guardó para decidir qué conversiones aplicar. Durante un tiempo esa comparación se hizo entre cadenas de texto, y como las cadenas se comparan carácter por carácter, "10.0.0" resulta menor que "3.1.1" porque "1" va antes que "3". Es un recordatorio de que la versión del formato debe ser un entero que se compara como entero, y de que las migraciones deben probarse con archivos reales de cada versión anterior.

### Lectura de video, cachés y composición en CPU

El lector basado en FFmpeg mantiene una caché de trabajo con los cuadros que está decodificando y una caché final con los cuadros ya listos. La propia documentación del proyecto advierte que la búsqueda de un cuadro concreto "no siempre es exacta al cuadro", porque saltar a un punto del archivo lleva al fotograma clave anterior y luego hay que decodificar hacia adelante con cuidado de no perder la cuenta. Las cachés existen en memoria y en disco.

La composición se hace en la CPU con `QImage` y `QPainter` de Qt, lo que es simple y portátil pero se vuelve el cuello de botella en resoluciones altas: superponer varias capas 4K con transformaciones píxel a píxel en la CPU no llega a tiempo real en equipos normales. Esta es una de las limitaciones más citadas por los usuarios del programa.

### Fotogramas clave y exportación

Los fotogramas clave se modelan con una clase de curva que contiene puntos, y cada punto tiene un modo de interpolación (Bézier, lineal o constante) y, en el caso de Bézier, manejadores que definen la forma de la curva. La exportación usa un escritor basado en FFmpeg. La experiencia acumulada del proyecto con aceleración por hardware es reveladora: la decodificación por hardware fue históricamente poco fiable, con fallos que dependían del controlador y del archivo, mientras que la codificación con NVENC resultó la más estable. La práctica recomendada es intentar el camino por hardware y caer automáticamente al software si falla.

## Olive

### La reescritura hacia un grafo de nodos

Olive empezó como un editor tradicional y en su versión 0.2 se reescribió por completo alrededor de un grafo de nodos: los clips, los efectos, las pistas y hasta la salida final son nodos conectados entre sí, al estilo de los compositores profesionales. El proyecto se guarda en XML, con un serializador distinto para cada versión del formato, de modo que leer un archivo antiguo consiste en elegir el serializador que corresponde a su versión.

### Composición en GPU, color y caché por hash

Olive compone en la GPU con sombreadores GLSL y gestiona el color con OpenColorIO, lo que le permite trabajar en espacios de color amplios con un flujo de nivel profesional. Su mecanismo de caché es especialmente interesante: cada cuadro renderizado se guarda en disco con una clave que es el hash del subgrafo que lo produjo. Si el usuario cambia un efecto, cambia el hash de los cuadros afectados y solo esos se invalidan; si deshace el cambio, el hash vuelve a coincidir con el de antes y los cuadros viejos se reutilizan sin recalcular. Además, en segundo plano va llenando la caché de las zonas que el usuario probablemente reproducirá.

El tiempo se representa con números racionales (numerador y denominador enteros), lo que evita los errores de redondeo de los segundos en coma flotante cuando se mezclan cadencias distintas como 24, 25 y 29,97 cuadros por segundo. El deshacer usa el patrón Comando con comandos compuestos.

### La lección de Olive

La arquitectura de Olive es probablemente la más ambiciosa y limpia de las estudiadas, y precisamente por eso su historia es una advertencia. El proyecto pasó más de seis años sin una versión estable mientras perseguía la arquitectura ideal, y durante ese tiempo los usuarios se quedaron sin un editor utilizable. Para un proyecto de una sola persona, como Rodaje, la conclusión es clara: un grafo de nodos general es más de lo que se necesita para el tipo de edición que se busca, y el riesgo de no terminar nunca es mayor que el beneficio de la flexibilidad.

## LosslessCut

LosslessCut no es un editor de línea de tiempo multipista sino una herramienta para cortar y unir video sin recodificar, y su interés está en cómo saca partido de FFmpeg. Es una aplicación Electron que no enlaza las bibliotecas de FFmpeg, sino que ejecuta `ffmpeg.exe` como un proceso aparte y lee su salida de error para extraer el progreso de cada operación.

Su función principal son los cortes sin pérdida con `-c copy`, que copian los paquetes comprimidos sin decodificarlos. El problema es que un corte solo puede empezar limpiamente en un fotograma clave, así que el programa ofrece dos modos según dónde se coloque la opción `-ss` en la línea de comandos: antes de la entrada, la búsqueda salta al fotograma clave más cercano y es rápida; después de la entrada, es precisa pero más lenta. Para evitar marcas de tiempo negativas al cortar usa `-avoid_negative_ts make_zero`, y para unir segmentos usa el demultiplexor concat de FFmpeg. Como función experimental ofrece un "corte inteligente" que recodifica únicamente el tramo entre el punto de corte y el siguiente fotograma clave, y copia el resto sin pérdida. También aprovecha los filtros de detección de FFmpeg (silencio, negro y cambio de escena) para proponer puntos de corte automáticamente.

Para Rodaje estas técnicas son valiosas como modo de exportación rápida: cuando un proyecto consiste en recortes de un único archivo sin efectos, se puede exportar sin recodificar, y las detecciones automáticas son la base de funciones como "quitar silencios".

## Qué adopta Rodaje y por qué

Ninguno de los proyectos estudiados es un modelo a copiar entero; cada uno resolvió bien algunos problemas y pagó caro otros. Rodaje toma las siguientes posiciones.

**Posiciones explícitas y huecos implícitos.** Cada clip guarda su instante de inicio en la línea de tiempo, como en OpenShot, y los huecos no son objetos sino simplemente el espacio donde no hay clips. Así la colocación libre es directa, y el rizado se implementa como una operación explícita que desplaza los clips posteriores, en vez de ser un efecto secundario del formato como en MLT.

**Tiempo entero y exacto.** El tiempo se mide en ticks enteros de 1/705 600 000 de segundo, la idea de los "flicks". Ese número es divisible exactamente por las cadencias de video habituales (24, 25, 30, 48, 50, 60, 90, 100, 120 y las fraccionarias de NTSC al multiplicarlas por 1000/1001) y por las frecuencias de audio comunes (8, 16, 22,05, 24, 32, 44,1, 48, 88,2, 96 y 192 kHz), así que cualquier cuadro o muestra cae en un número entero de ticks. Cumple el mismo objetivo que los racionales de Olive, evitar la deriva por redondeo, con la sencillez de un único entero de 64 bits.

**Comandos tipados con instantáneas, reversión y fusión.** Como Shotcut, cada operación es un comando con nombre; como el ayudante de Shotcut y las transacciones de OpenShot, el deshacer se basa en instantáneas, en este caso de la secuencia completa, lo que hace imposible un deshacer incompleto. Como en Kdenlive, si la operación falla a medias el estado se restaura automáticamente. Y como `mergeWith`, los arrastres se fusionan en un solo paso mediante claves de fusión.

**Versión de esquema entera y migraciones encadenadas y probadas.** El archivo de proyecto lleva un `schemaVersion` entero; al abrir uno antiguo se aplican en orden las migraciones de N a N+1, cada una con sus propias pruebas. Así se evita el error de comparación de cadenas de OpenShot y la proliferación de serializadores completos de Olive.

**Pistas, clips y pila de efectos con un compositor fijo.** El modelo es el que entiende cualquier usuario de un editor rápido: pistas que contienen clips, y cada clip con una pila ordenada de efectos. El compositor que los combina es fijo y conocido, no un grafo de nodos general. Se renuncia a la flexibilidad de Olive a cambio de poder terminar.

**El mismo pipeline para vista previa y exportación.** Como en MLT, la exportación usa el mismo motor de render que la vista previa, cambiando solo el destino, para que lo que se ve sea lo que se obtiene.

**El audio como reloj maestro.** Durante la reproducción manda la tarjeta de sonido, y si hace falta se descartan cuadros de video, nunca audio.

**Proxies desde el principio.** El modelo de medios contempla desde el inicio que cada archivo pueda tener una versión ligera para edición, en lugar de añadirlo cuando el rendimiento ya sea un problema.

**Hardware opcional con respaldo automático.** La decodificación y codificación por hardware se intentan primero y, ante cualquier fallo, se recurre al software sin intervención del usuario, siguiendo la experiencia de OpenShot.

**Composición en GPU diseñada desde el primer día.** La composición se hará con Direct3D 11 desde el principio, para no quedar atrapados en el cuello de botella de la CPU que sufre OpenShot. A la vez, se aprende del caso de Movit en Shotcut: habrá un único camino de render en GPU, pensado para ella desde el inicio, en lugar de un motor de CPU con una capa de GPU añadida después, y la presentación de imagen se hará sin copias intermedias a través del `SwapChainPanel` de WinUI 3.
