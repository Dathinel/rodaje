# Formato de proyecto `.rodaje`

Un proyecto de Rodaje se guarda en un archivo con extensión `.rodaje`. Es texto JSON en UTF-8, sin marca BOM, indentado con dos espacios y con saltos de línea LF. Se eligió JSON legible y no un formato binario por dos razones. La primera es que el proyecto es el trabajo de quien edita y debe poder abrirse, revisarse e incluso repararse con un editor de texto si algo sale mal. La segunda es que Daniel quiere que sus otros programas, como un futuro editor de audio o herramientas en Python, puedan leer y escribir el mismo proyecto sin depender del código de Rodaje. Por eso este documento describe el formato completo, con sus reglas, y junto a él se publica un esquema JSON en `schema/rodaje-proyecto.v4.schema.json` (los de las versiones 1 a 3 se conservan para los archivos viejos) que cualquier validador del estándar draft 2020-12 entiende.

El archivo nunca contiene video, audio ni imágenes. Solo guarda dónde están los archivos de origen y cómo se usan, de modo que editar jamás modifica el material original y un proyecto pesa unos pocos kilobytes.

## Unidades: el tiempo en ticks

Todos los tiempos del archivo, sean posiciones o duraciones, son enteros que cuentan *ticks*. Un segundo tiene exactamente 705 600 000 ticks. El número no es caprichoso: es divisible entre la duración de un cuadro a 24, 25, 30, 48, 50, 60, 90, 100 y 120 cuadros por segundo, también en las variantes NTSC que multiplican la tasa por 1000/1001 (23,976, 29,97 y 59,94 fps), y entre la duración de una muestra de audio a 8, 16, 22,05, 24, 32, 44,1, 48, 88,2, 96 y 192 kHz. Gracias a eso, el inicio de cada cuadro y de cada muestra cae siempre en un tick entero, y sumar o restar posiciones no acumula el error de redondeo que aparece cuando el tiempo se guarda como segundos en un número decimal. Por ejemplo, a 29,97 fps un cuadro dura 705 600 000 × 1001 / 30 000 = 23 543 520 ticks exactos.

Para convertir, un programa externo divide entre 705 600 000 y obtiene segundos. Al escribir, debe redondear al tick entero más cercano; un tiempo con decimales o escrito como texto hace que el archivo se rechace. Los valores caben en un entero de 64 bits con signo. En JavaScript conviene leerlos como `BigInt` si pudieran superar 2^53, aunque ese límite equivale a unos 148 días de material, así que en la práctica un `number` normal basta.

Las tasas de cuadros no son decimales sino fracciones exactas con la forma `{"num": 30000, "den": 1001}`. Los dos campos son obligatorios, enteros y mayores que cero. Al leer, la fracción se simplifica, de modo que `{"num": 60, "den": 2}` se entiende como 30/1, y Rodaje siempre la escribe ya simplificada.

## Un ejemplo completo

El siguiente archivo es un proyecto real y válido. Tiene una toma de video de 10 segundos grabada a 29,97 fps con su sonido y una foto. En la línea de tiempo, el video y su audio se colocan enlazados al principio, con un fundido de entrada en la opacidad, y la foto aparece en una pista superior a partir del segundo 2.

```json
{
  "schemaVersion": 4,
  "id": "project-6c1f0a2e",
  "name": "Paseo en bicicleta",
  "media": [
    {
      "id": "media-1",
      "path": "material/toma 01.mp4",
      "name": "toma 01",
      "duration": 7063056000,
      "hasVideo": true,
      "hasAudio": true,
      "frameRate": {
        "num": 30000,
        "den": 1001
      },
      "width": 1920,
      "height": 1080,
      "sampleRate": 48000,
      "audioChannels": 2
    },
    {
      "id": "media-2",
      "path": "fotos/montaña.jpg",
      "name": "montaña",
      "duration": 2540160000000,
      "hasVideo": true,
      "hasAudio": false,
      "frameRate": null,
      "width": 4000,
      "height": 3000,
      "sampleRate": 0,
      "audioChannels": 0
    }
  ],
  "sequences": [
    {
      "id": "sequence-1",
      "name": "Secuencia 1",
      "frameRate": {
        "num": 30000,
        "den": 1001
      },
      "width": 1920,
      "height": 1080,
      "sampleRate": 48000,
      "videoTracks": [
        {
          "id": "track-1",
          "kind": "video",
          "name": "Video 1",
          "muted": false,
          "locked": false,
          "hidden": false,
          "clips": [
            {
              "id": "clip-1",
              "mediaId": "media-1",
              "sourceIn": 706305600,
              "sourceOut": 4237833600,
              "start": 0,
              "linkId": "link-1",
              "enabled": true,
              "effects": [
                {
                  "id": "effect-1",
                  "type": "rodaje.transform",
                  "enabled": true,
                  "parameters": {
                    "opacity": {
                      "value": 1,
                      "keyframes": [
                        {
                          "time": 706305600,
                          "value": 0,
                          "interpolation": "easeOut"
                        },
                        {
                          "time": 1411905600,
                          "value": 1,
                          "interpolation": "linear"
                        }
                      ]
                    }
                  },
                  "options": {}
                }
              ]
            }
          ],
          "effects": [],
          "transitions": []
        },
        {
          "id": "track-2",
          "kind": "video",
          "name": "Video 2",
          "muted": false,
          "locked": false,
          "hidden": false,
          "clips": [
            {
              "id": "clip-2",
              "mediaId": "media-2",
              "sourceIn": 0,
              "sourceOut": 2116800000,
              "start": 1411200000,
              "linkId": null,
              "enabled": true,
              "effects": []
            }
          ],
          "effects": [],
          "transitions": []
        }
      ],
      "audioTracks": [
        {
          "id": "track-3",
          "kind": "audio",
          "name": "Audio 1",
          "muted": false,
          "locked": false,
          "hidden": false,
          "clips": [
            {
              "id": "clip-3",
              "mediaId": "media-1",
              "sourceIn": 706305600,
              "sourceOut": 4237833600,
              "start": 0,
              "linkId": "link-1",
              "enabled": true,
              "effects": []
            }
          ],
          "effects": [],
          "transitions": []
        },
        {
          "id": "track-4",
          "kind": "audio",
          "name": "Audio 2",
          "muted": false,
          "locked": false,
          "hidden": false,
          "clips": [],
          "effects": [],
          "transitions": []
        }
      ],
      "markers": [
        {
          "id": "marker-1",
          "time": 1411200000,
          "name": "Entra la foto"
        }
      ],
      "regions": [
        {
          "id": "region-1",
          "start": 0,
          "end": 3528000000,
          "name": "Intro"
        }
      ]
    }
  ]
}
```

Leído en voz alta, el ejemplo dice lo siguiente. La toma dura 300 cuadros a 29,97 fps, que son 300 × 23 543 520 = 7 063 056 000 ticks. El clip `clip-1` usa de esa toma desde el cuadro 30 (`sourceIn` = 706 305 600) hasta el cuadro 180 (`sourceOut` = 4 237 833 600, excluido), es decir 150 cuadros, unos cinco segundos, y los coloca en el instante cero de la pista. El clip `clip-3` hace exactamente lo mismo con el sonido de la misma toma, y como los dos comparten `linkId` el editor los mueve y recorta juntos. La foto no tiene tasa de cuadros, por eso su `frameRate` es `null`, y se le asigna una duración larga (una hora) para que se pueda estirar en la línea de tiempo; el clip `clip-2` usa tres segundos de ella y empieza en el segundo 2. Como la pista `Video 2` está por encima de `Video 1`, la foto tapa el video mientras dura. El efecto de transformación del primer clip anima la opacidad de 0 a 1 durante el primer segundo del clip, con una curva que arranca rápido y frena al final. Un marcador señala el segundo 2, donde entra la foto, y la región «Intro» cubre los primeros cinco segundos.

## Qué significa cada campo

La raíz del archivo es el proyecto. `schemaVersion` es la versión del formato y hoy vale 4; es el primer dato que se lee y decide cómo se interpreta el resto. `id` identifica al proyecto y `name` es el nombre que ve quien edita. `media` es la biblioteca de medios y `sequences` son las líneas de tiempo, que pueden ser varias, por ejemplo una horizontal y otra vertical del mismo material.

Cada medio describe un archivo de origen. `path` es su ruta; se prefiere relativa a la carpeta donde está el `.rodaje`, para que el proyecto se pueda mover junto con su material a otro PC, aunque se aceptan rutas absolutas. Se admiten tanto `/` como `\` como separador. `name` es el nombre visible, `duration` la duración total del archivo en ticks, `hasVideo` y `hasAudio` dicen qué contiene, `frameRate` es la tasa de su video o `null` si no tiene (una imagen fija o un archivo de solo audio), `width` y `height` son las dimensiones de la imagen en píxeles y `sampleRate` y `audioChannels` describen su sonido. Los campos que no aplican valen 0.

Cada secuencia tiene su propio `id` y `name`, su tasa de cuadros `frameRate`, su resolución `width` × `height` y la frecuencia de muestreo `sampleRate` a la que se mezcla el audio. Las pistas van en dos listas separadas, `videoTracks` y `audioTracks`, y el orden dentro de cada lista importa: en `videoTracks` el primer elemento es la capa de más abajo y cada pista siguiente se compone encima de la anterior, de abajo hacia arriba, como en cualquier editor de video. Una pista tiene `id`, `kind` (que vale `"video"` o `"audio"` y debe coincidir con la lista en la que está), `name`, y tres interruptores: `muted` la silencia, `hidden` la oculta de la imagen y `locked` impide editarla. Su lista `clips` está ordenada por posición, `effects` guarda los efectos de toda la pista y `transitions` las transiciones de sus cortes (ver más abajo). Después de las pistas, `markers` guarda los marcadores, cada uno con `id`, el instante `time` y un `name` que puede ir vacío, ordenados por tiempo; y `regions` guarda las regiones, tramos con nombre `[start, end)` que pueden solaparse, ordenadas por inicio. Los dos están en tiempo de la secuencia: con el ripple en el modo «pistas, marcadores y regiones», cerrar un hueco o recortar los corre junto con los clips.

El clip es el corazón del formato y se describe con tres instantes. `sourceIn` es el punto de entrada dentro del archivo de origen, incluido; `sourceOut` es el punto de salida, excluido; y `start` es el lugar de la pista donde se coloca el clip. Su duración es `sourceOut − sourceIn` y su final en la pista es `start + duración`. Estos dos valores no se guardan porque se calculan, y un programa externo no debe escribirlos. Toda la edición no destructiva consiste en cambiar estos tres números: recortar mueve `sourceIn` o `sourceOut`, deslizar mueve `start`. `mediaId` dice qué medio usa, `enabled` permite apagar un clip sin borrarlo, y `linkId`, si no es `null`, agrupa clips que se editan juntos; normalmente el video y el audio de una misma toma. El valor de `linkId` es un texto cualquiera que comparten los clips del grupo y no tiene que aparecer en ningún otro lugar.

Los efectos de un clip están en `effects` y se aplican en el orden de la lista. Cada efecto tiene `id`, un `type` con espacio de nombres, como `rodaje.transform`, un interruptor `enabled` y un diccionario `parameters` cuyas claves son los nombres de los parámetros. Un programa no debe depender del orden de esas claves. Cada parámetro es numérico: `value` es su valor cuando no está animado y `keyframes` es la animación. Cada keyframe tiene `time`, `value` e `interpolation`, que dice cómo se va desde ese keyframe hasta el siguiente: `hold` mantiene el valor y salta de golpe, `linear` avanza en línea recta, `easeIn` arranca despacio, `easeOut` frena al final y `easeInOut` hace las dos cosas; si falta, vale `linear`. Antes del primer keyframe el parámetro vale lo del primero y después del último vale lo del último. Lo más importante de los keyframes es que su `time` está en **tiempo del origen**, no de la línea de tiempo, y en la misma escala que `sourceIn`. Así, si el clip se mueve en la pista, la animación lo acompaña sin recalcular nada, y si se recorta, los keyframes siguen pegados al mismo cuadro del material. Los valores vectoriales, como una posición x/y o un color, se guardan como varios parámetros escalares. Lo que no es un número va en `options`, un diccionario de textos: el modo de mezcla, la LUT o el paquete del que salió el efecto.

### Efectos, opciones y transiciones (formato 3)

Los tipos que el motor entiende están en `EffectRegistry` (`src/Rodaje.Core/Effects`), cada uno con un código entero estable, su destino (video, audio o transición), su etapa en el motor, sus parámetros con valor por defecto y límites, y sus opciones. Un parámetro que falta vale su valor por defecto. Un tipo que no está en el registro, por ejemplo de otro programa o de un paquete que falta, se conserva tal cual al guardar, con sus parámetros y opciones, pero no se aplica.

| Tipo | Parámetros (por defecto) | Opciones |
|---|---|---|
| `rodaje.transform` | `x` (0), `y` (0) en píxeles de la secuencia, `scale` (1), `rotation` (0) en grados, `opacity` (1) | |
| `rodaje.color` | `exposure` en pasos, `contrast`, `saturation`, `temperature`, `tint`, `highlights`, `shadows` (todos 0, de −1 a 1 salvo la exposición, de −4 a 4), `lutIntensity` (1) | `lut`: referencia `paquete@versión/elemento` de un `.cube`, o `file:` más la ruta de un `.cube` suelto (con `/`, relativa a la carpeta del `.rodaje` o absoluta); vacía o ausente sin LUT |
| `rodaje.vignette` | `amount`, `size`, `softness` (0,5) | |
| `rodaje.blur` | `radius` (10) en píxeles de la secuencia | |
| `rodaje.sharpen` | `amount` (0,5), `radius` (1) | |
| `rodaje.blend` | | `blendMode`: `normal`, `add`, `multiply`, `screen`, `overlay`, `softLight`, `hardLight`, `darken`, `lighten`, `difference`, `exclusion`, `colorDodge` o `colorBurn` |
| `rodaje.audio` | `gainDb` (0; −60 o menos es silencio), `fadeIn` y `fadeOut` en segundos (0, sin keyframes) | |
| `rodaje.stabilize` | Reservado: se reconoce, pero todavía no se aplica | |
| `rodaje.speed` | Reservado y oculto (código 8): la velocidad es `speed` del clip (formato 4) y un efecto de este tipo se rechaza | |

Cualquier efecto puede llevar además la opción `source` con la referencia del filtro o efecto de paquete del que es copia. Los efectos de una pista (`effects` de la pista, por ejemplo su volumen) tienen la misma forma, pero sus keyframes están en tiempo de la secuencia, porque una pista no tiene origen, y solo admiten tipos de su clase: de audio en una pista de audio y de video en una de video. Aun así siguen al contenido: cuando un ripple (borrar, recortar, cerrar un hueco o insertar con ripple) corre la pista, sus keyframes desde ese punto se corren lo mismo y los de un tramo borrado se quitan; al reordenar la pista principal, los del tramo de cada clip viajan con él. En cada borde se añade un keyframe con el valor que había, para que la curva no cambie fuera del tramo, salvo que valga lo mismo que sus vecinos; un tramo con suavizado que queda cortado por un borde vuelve a empezar ahí y se aproxima.

En los tipos registrados, una opción definida debe tener un valor aceptado: uno de la lista en las de elección (`blendMode`, `direction`) y, en las referencias (`lut`, `preset`, `source`), la forma `paquete@versión/elemento`, con paquete y elemento en minúsculas ASCII, dígitos, guiones y puntos, y una versión que empieza con un dígito. `lut` y `preset` admiten además el texto vacío, y `lut` también `file:` seguido de una ruta sin caracteres de control ni `" < > |`, como la de un medio (`OptionDefinition.AllowsFile`). Si el paquete existe se comprueba al resolver la referencia, no al abrir. Las opciones que el tipo no define se conservan sin mirarlas.

Una transición vive en la pista y une dos clips seguidos, sin hueco: `from` es el clip que termina en el corte y `to` el que empieza ahí. Dura `duration` ticks, centrada en el corte (la mitad antes y la mitad después; un tick impar va después), y nunca más que el más corto de sus dos clips: así su mitad cabe en cada uno y las dos transiciones de un mismo clip no se pisan. Los clips nunca se solapan: cada lado usa el material que sobra más allá de su borde y, si no hay, se congela el cuadro del borde. Sus `parameters` son números sin keyframes (`{"angle": 90}`) y sus `options`, textos.

| Tipo | Parámetros (por defecto) | Opciones |
|---|---|---|
| `rodaje.crossfade` | | |
| `rodaje.dip` | `red`, `green`, `blue` (0: a negro) | |
| `rodaje.wipe` | `angle` (0, en grados), `softness` (0,05) | |
| `rodaje.push` | | `direction`: `left`, `right`, `up` o `down` |
| `rodaje.zoom-dissolve` | `zoom` (0,25) | |
| `rodaje.blur-dissolve` | `radius` (40) | |
| `rodaje.preset` | | `preset`: referencia de una transición de paquete; si falta el paquete, se ve como `rodaje.crossfade` |

```
"transitions": [
  { "id": "transition-1", "type": "rodaje.wipe", "from": "clip-1", "to": "clip-2",
    "duration": 352800000, "parameters": { "angle": 90 }, "options": {} }
]
```

Al editar, Rodaje mantiene cada transición pegada a su corte, en el mismo paso del deshacer: si se divide uno de los clips sigue en el mismo corte; si un recorte, un roll o un corte dejan un clip más corto que la transición, esta se acorta y no recupera su duración si el clip vuelve a alargarse, como en CapCut; y si el corte desaparece (se abre un hueco, se borra o se mueve uno de sus clips) la transición se quita. Un ejemplo con todo lo del formato 3 está en `tests/Rodaje.Core.Tests/Serialization/ejemplos/proyecto-v3.rodaje`.

### Velocidad de los clips (formato 4)

Un clip puede llevar `speed`; si no lo lleva (o es `null`) se reproduce a 1× y todo es como en el formato 3. Con `speed`, el clip guarda además `duration`, su largo en la línea de tiempo, porque ya no es `sourceOut − sourceIn`; sin `speed`, un `duration` escrito se ignora.

```
"sourceIn": 1411200000, "sourceOut": 2822400000, "start": 2116800000, "duration": 705600000,
"speed": { "rate": { "num": 2, "den": 1 }, "offset": 0, "reverse": true, "freeze": false,
           "preservePitch": true, "frameSampling": "nearest" }
```

| Campo | Qué es |
|---|---|
| `rate` | Velocidad constante como fracción, de 1/10 a 100, con numerador y denominador hasta 2^20. Vale 1 con `curve` o `freeze`. |
| `curve` | Opcional. Velocidad lineal a tramos durante `span` ticks de la línea de tiempo. `points` lleva de 2 a 64 puntos `{at, hundredths}`: `at` en diezmilésimas de `span` (el primero en 0, el último en 10000, en orden estricto) y la velocidad en centésimas, de 10 (0,1×) a 1000 (10×). Antes del primero y después del último sigue constante. |
| `offset` | Ticks del mapeo donde empieza el clip. Recortar el inicio o dividir lo mueven para que el resto del clip siga mostrando los mismos cuadros. |
| `reverse` | El origen corre hacia atrás: el clip empieza en `sourceOut`. |
| `freeze` | Cuadro congelado: muestra `sourceIn` durante `duration`, y `sourceOut` es `sourceIn + 1`. Solo en pistas de video, sin `reverse`, curva ni otra velocidad. |
| `preservePitch` | El audio mantiene el tono (por omisión, sí). |
| `frameSampling` | `nearest`; `blend` y `opticalFlow` están reservados y hoy se ven como `nearest`. |

El origen de cada instante se calcula exacto con enteros, sin redondeos acumulados. Sea `I(u)` lo que avanza el origen tras `u` ticks de la línea de tiempo (`u·num/den`, o la integral de la curva). En el instante `t` del clip, con `τ = t − start` y `p(τ) = ⌊I(offset + τ)⌋ − ⌊I(offset)⌋`, se ve `sourceIn + p(τ)` (o `sourceOut − 1 − p(τ)` en reversa). Por eso `sourceOut − sourceIn` debe valer exactamente `⌈I(offset + duration)⌉ − ⌊I(offset)⌋`, y `|offset| + duration` no puede pasar de 2^44 ticks (unas 6,9 horas). Los keyframes de los efectos siguen en tiempo del origen; en un congelado avanzan con un tiempo virtual, `sourceIn + offset + τ`. Los clips de un mismo enlace llevan la misma velocidad (`rate`, `curve`, `reverse` y `freeze`). Un ejemplo con velocidad constante, reversa, curva y congelado está en `tests/Rodaje.Core.Tests/Serialization/ejemplos/proyecto-v4.rodaje`.

## Reglas que todo programa debe respetar

El esquema JSON describe la forma del archivo, pero hay reglas que relacionan unos datos con otros y que el esquema no puede expresar. Rodaje las comprueba todas al abrir un proyecto y lo rechaza con un mensaje que las enumera si alguna falla, así que un programa que escriba un `.rodaje` debe cumplirlas.

Todos los identificadores (`id`) del proyecto, de los medios, de las secuencias, de las pistas, de los clips, de los efectos, de los marcadores y de las regiones deben ser textos no vacíos, sin caracteres de control y únicos en todo el archivo, compartiendo un mismo espacio: un clip no puede llamarse igual que una pista. Rodaje genera identificadores del tipo `clip-3f2a…` y otros programas pueden usar cualquier texto, por ejemplo un UUID.

Cada medio debe tener una duración positiva. Cada secuencia debe tener ancho, alto y frecuencia de muestreo positivos. Una pista de `videoTracks` debe ser de tipo `video` y una de `audioTracks` de tipo `audio`.

Cada clip debe apuntar con `mediaId` a un medio que exista en `media`. Su `sourceIn` no puede ser negativo, su `sourceOut` debe ser mayor que su `sourceIn` y no puede pasar de la duración del medio, y su `start` no puede ser negativo. Un clip en una pista de video necesita un medio con imagen (`hasVideo`) y uno en una pista de audio, un medio con sonido (`hasAudio`). Dentro de una pista, los clips van ordenados por `start` y no se solapan: cada uno empieza en el final del anterior o después. Los huecos entre clips no se guardan; son simplemente el espacio que queda entre uno y el siguiente.

Un marcador no puede estar antes del instante cero, y una región tampoco puede empezar antes y debe terminar después de empezar. Los keyframes de cada parámetro van en orden estrictamente creciente de `time`, sin dos en el mismo instante. Un mismo `linkId` puede repetirse en varias pistas, pero nunca en dos clips de la misma pista. Cada transición une dos clips contiguos de su propia pista (`to` es el siguiente de `from` y empieza justo donde este termina), cada corte tiene como mucho una y su duración es positiva y no pasa de la del más corto de sus clips. En los tipos registrados, cada parámetro (y cada keyframe) es un número finito dentro de los límites del registro: una opacidad de 2 o un ángulo de barrido de 720 se rechazan; los parámetros de tipos desconocidos no se miran. Un tipo registrado como transición no puede ir en una lista de efectos ni uno de efecto en `transitions`, y las opciones y los efectos de pista siguen las reglas de la sección anterior.

Además, el archivo debe ser JSON estricto: sin comentarios, sin comas sobrantes y sin el mismo campo repetido dentro de un objeto, porque no habría forma de saber cuál vale. Las listas y las `options` no pueden contener `null`, y los campos de texto, listas y objetos tampoco; los únicos campos que admiten `null` son `linkId`, el `frameRate` de un medio y `speed`, `duration` y `curve` en un clip.

Un campo que falta toma su valor por defecto: listas vacías, números en 0, `enabled` en verdadero, la secuencia en 1920 × 1080 a 30 fps y 48 kHz, la interpolación en `linear`. Los campos desconocidos se ignoran al leer, lo que permite que un archivo escrito por una versión futura con campos opcionales nuevos se abra en una anterior de la misma versión de formato, pero esos campos no se conservan cuando Rodaje vuelve a guardar. Por eso otro programa no debe guardar datos propios dentro del `.rodaje` esperando que sobrevivan a una edición en Rodaje; si necesita datos adicionales, debe guardarlos en un archivo aparte o proponer que se agreguen al formato en una nueva versión.

## Versiones y migraciones

`schemaVersion` es un entero que solo crece. Cuando el formato cambia de una manera que una versión anterior de Rodaje no podría leer correctamente, por ejemplo al agregar la velocidad de los clips, que cambia la relación entre la duración en la pista y el tramo usado del origen, se sube la versión. Al abrir un archivo, Rodaje lee primero ese número. Si falta, no es un entero o es menor que 1, el archivo no es un proyecto de Rodaje. Si es mayor que la versión que el programa conoce, el archivo fue creado con una versión más nueva y se pide actualizar el programa en lugar de abrirlo a medias. Si es menor, se aplican en cadena las migraciones de la versión 1 a la 2, de la 2 a la 3 y así hasta la actual, trabajando sobre el árbol JSON antes de convertirlo en objetos, y después se valida el resultado como cualquier otro proyecto. Cada versión tiene su propio esquema con el número en el nombre del archivo, de modo que `rodaje-proyecto.v1.schema.json` no cambia nunca una vez publicado. La migración de la 1 a la 2 agrega a cada secuencia las listas `markers` y `regions` vacías. La de la 2 a la 3 agrega a cada pista `effects` y `transitions` vacías y a cada efecto `options` vacío, y convierte la envolvente vieja `rodaje.volume` (ganancia lineal en `gain`) en `rodaje.audio` con `gainDb` = 20·log10(gain), keyframes incluidos; una ganancia de 0 o menos queda en −60 dB. La de la 3 a la 4 quita los efectos `rodaje.speed`, que estaban reservados y nunca se aplicaron; los clips quedan sin `speed`, a 1×. Una migración publicada tampoco se modifica: si tuviera un error se corrige con otra migración.

Al volver a guardar, Rodaje siempre escribe la versión actual, así que un proyecto abierto y guardado con una versión nueva ya no se puede abrir con una anterior. La copia `.bak` descrita abajo conserva la versión previa del archivo.

## Guardado seguro y copia `.bak`

El portátil donde se desarrolla Rodaje ha tenido apagados inesperados, y un apagado a mitad de una escritura puede dejar un archivo cortado. Por eso Rodaje nunca escribe directamente sobre el proyecto. Primero escribe el contenido completo en un archivo temporal con el mismo nombre y la terminación `.tmp` (`Mi proyecto.rodaje.tmp`), en la misma carpeta, y le pide al sistema que lo lleve físicamente al disco antes de seguir. Solo entonces lo cambia por el proyecto. Si el proyecto ya existía, el cambio se hace con una operación de reemplazo de Windows que deja la versión anterior como `Mi proyecto.rodaje.bak`; si no existía, el temporal simplemente se renombra. En cualquier momento en que se corte la luz, en disco queda la versión anterior completa o la nueva completa, nunca una mezcla.

Si alguna vez un proyecto no abre porque está dañado, la copia `.bak` contiene el guardado anterior: basta con hacer una copia de ella, quitarle la terminación `.bak` y abrirla. Un `.tmp` que quede en la carpeta es el resto de un guardado interrumpido; no hace falta borrarlo, porque el siguiente guardado lo reemplaza. Otros programas que escriban archivos `.rodaje` deberían seguir el mismo procedimiento.
