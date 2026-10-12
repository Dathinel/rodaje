# Formato de proyecto `.rodaje`

Un proyecto de Rodaje se guarda en un archivo con extensión `.rodaje`. Es texto JSON en UTF-8, sin marca BOM, indentado con dos espacios y con saltos de línea LF. Se eligió JSON legible y no un formato binario por dos razones: el proyecto es el trabajo de quien edita y debe poder abrirse, revisarse e incluso repararse con un editor de texto, y otros programas (un editor de audio, herramientas en Python) deben poder leer y escribir el mismo proyecto sin depender del código de Rodaje. Por eso este documento describe el formato completo, con sus reglas, y junto a él se publica un esquema JSON en `schema/rodaje-proyecto.v6.schema.json` (los de las versiones 1 a 5 se conservan para los archivos viejos) que cualquier validador del estándar draft 2020-12 entiende.

El archivo nunca contiene video, audio ni imágenes. Solo guarda dónde están los archivos de origen y cómo se usan, de modo que editar jamás modifica el material original y un proyecto pesa unos pocos kilobytes.

## Unidades: el tiempo en ticks

Todos los tiempos del archivo, sean posiciones o duraciones, son enteros que cuentan *ticks*. Un segundo tiene exactamente 705 600 000 ticks. El número no es caprichoso: es divisible entre la duración de un cuadro a 24, 25, 30, 48, 50, 60, 90, 100 y 120 cuadros por segundo, también en las variantes NTSC que multiplican la tasa por 1000/1001 (23,976, 29,97 y 59,94 fps), y entre la duración de una muestra de audio a 8, 16, 22,05, 24, 32, 44,1, 48, 88,2, 96 y 192 kHz. Gracias a eso, el inicio de cada cuadro y de cada muestra cae siempre en un tick entero, y sumar o restar posiciones no acumula el error de redondeo que aparece cuando el tiempo se guarda como segundos en un número decimal. Por ejemplo, a 29,97 fps un cuadro dura 705 600 000 × 1001 / 30 000 = 23 543 520 ticks exactos.

Para convertir, un programa externo divide entre 705 600 000 y obtiene segundos. Al escribir, debe redondear al tick entero más cercano; un tiempo con decimales o escrito como texto hace que el archivo se rechace. Los valores caben en un entero de 64 bits con signo. En JavaScript conviene leerlos como `BigInt` si pudieran superar 2^53, aunque ese límite equivale a unos 148 días de material, así que en la práctica un `number` normal basta.

Las tasas de cuadros no son decimales sino fracciones exactas con la forma `{"num": 30000, "den": 1001}`. Los dos campos son obligatorios, enteros y mayores que cero. Al leer, la fracción se simplifica, de modo que `{"num": 60, "den": 2}` se entiende como 30/1, y Rodaje siempre la escribe ya simplificada.

## Un ejemplo completo

El siguiente archivo es un proyecto real y válido. Tiene una toma de video de 10 segundos grabada a 29,97 fps con su sonido y una foto. En la línea de tiempo, el video y su audio se colocan enlazados al principio, con un fundido de entrada en la opacidad, y la foto aparece en una pista superior a partir del segundo 2.

```json
{
  "schemaVersion": 6,
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

La raíz del archivo es el proyecto. `schemaVersion` es la versión del formato y hoy vale 6; es el primer dato que se lee y decide cómo se interpreta el resto. `id` identifica al proyecto y `name` es el nombre que ve quien edita. `media` es la biblioteca de medios y `sequences` son las líneas de tiempo, que pueden ser varias, por ejemplo una horizontal y otra vertical del mismo material.

Cada medio describe un archivo de origen. `path` es su ruta; se prefiere relativa a la carpeta donde está el `.rodaje`, para que el proyecto se pueda mover junto con su material a otro PC, aunque se aceptan rutas absolutas. Se admiten tanto `/` como `\` como separador. `name` es el nombre visible, `duration` la duración total del archivo en ticks, `hasVideo` y `hasAudio` dicen qué contiene, `frameRate` es la tasa de su video o `null` si no tiene (una imagen fija o un archivo de solo audio), `width` y `height` son las dimensiones de la imagen en píxeles y `sampleRate` y `audioChannels` describen su sonido. Los campos que no aplican valen 0.

Cada secuencia tiene su propio `id` y `name`, su tasa de cuadros `frameRate`, su resolución `width` × `height` y la frecuencia de muestreo `sampleRate` a la que se mezcla el audio. Las pistas van en dos listas separadas, `videoTracks` y `audioTracks`, y el orden dentro de cada lista importa: en `videoTracks` el primer elemento es la capa de más abajo y cada pista siguiente se compone encima de la anterior, de abajo hacia arriba, como en cualquier editor de video. Una pista tiene `id`, `kind` (que vale `"video"` o `"audio"` y debe coincidir con la lista en la que está), `name`, y tres interruptores: `muted` la silencia, `hidden` la oculta de la imagen y `locked` impide editarla. Su lista `clips` está ordenada por posición, `effects` guarda los efectos de toda la pista y `transitions` las transiciones de sus cortes (ver más abajo). Después de las pistas, `markers` guarda los marcadores, cada uno con `id`, el instante `time` y un `name` que puede ir vacío, ordenados por tiempo; y `regions` guarda las regiones, tramos con nombre `[start, end)` que pueden solaparse, ordenadas por inicio. Los dos admiten `color` (`#RRGGBB`) y `note` (texto libre de hasta 2000 caracteres), que solo se escriben si tienen valor; un Rodaje anterior los ignora al leer, por eso no cambian el número de versión. Los dos están en tiempo de la secuencia: con el ripple en el modo «pistas, marcadores y regiones», cerrar un hueco o recortar los corre junto con los clips.

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
| `rodaje.mask` | `x`, `y` (0,5: centro en fracción de la imagen de la capa), `width`, `height` (0,5: fracción del ancho y del alto), `rotation` (0, grados), `feather` (0: borde suave en fracción del lado corto, centrado en el contorno), `opacity` (1) | `shape`: `rectangle`, `ellipse`, `path` o `image`; `invert`: `off` u `on`; `path` (texto: `x,y;x,y;…`, de 3 a 256 puntos relativos a la caja, de −0,5 a 0,5); `matte` (`file:` y la ruta del video gris de la máscara de imagen, relativa al proyecto) y `matteStart` (segundos del origen de su primer cuadro). Varias máscaras se multiplican |
| `rodaje.crop` | `left`, `top`, `right`, `bottom` (0: fracción de la imagen que se quita de cada borde, hasta 0,98; si dos opuestos suman más, se reducen a la par) | `aspect`: `free`, `16:9`, `9:16`, `1:1`, `4:5` o `4:3` (la impone la interfaz al ajustar; el motor solo lee los bordes). Lo que queda se ajusta al cuadro como la imagen entera |
| `rodaje.chromakey` | `keyRed`, `keyGreen`, `keyBlue` (0; 0,7; 0,2: color clave en R'G'B', sin keyframes), `tolerance` (0,2), `softness` (0,1), `spill` (0,5), `cleanup` (0) | |
| `rodaje.wheels` | `liftRed`, `liftGreen`, `liftBlue`, `lift` (0, de −0,5 a 0,5), `gammaRed`, `gammaGreen`, `gammaBlue`, `gamma`, `gainRed`, `gainGreen`, `gainBlue`, `gain` (0, de −1 a 1): tinte por canal y valor general de sombras, medios y luces | |
| `rodaje.curves` | | `master`, `red`, `green`, `blue` (texto `x,y;x,y;…` de 0 a 1, hasta 16 puntos; vacía es la identidad). Cada canal pasa por su curva y luego por la general |
| `rodaje.speed` | Reservado y oculto (código 8): la velocidad es `speed` del clip (formato 4) y un efecto de este tipo se rechaza | |

Cualquier efecto puede llevar además la opción `source` con la referencia del filtro o efecto de paquete del que es copia. Los efectos de una pista (`effects` de la pista, por ejemplo su volumen) tienen la misma forma, pero sus keyframes están en tiempo de la secuencia, porque una pista no tiene origen, y solo admiten tipos de su clase: de audio en una pista de audio y de video en una de video. Aun así siguen al contenido: cuando un ripple (borrar, recortar, cerrar un hueco o insertar con ripple) corre la pista, sus keyframes desde ese punto se corren lo mismo y los de un tramo borrado se quitan; al reordenar la pista principal, los del tramo de cada clip viajan con él. En cada borde se añade un keyframe con el valor que había, para que la curva no cambie fuera del tramo, salvo que valga lo mismo que sus vecinos; un tramo con suavizado que queda cortado por un borde vuelve a empezar ahí y se aproxima.

En los tipos registrados, una opción definida debe tener un valor aceptado: uno de la lista en las de elección (`blendMode`, `direction`) y, en las referencias (`lut`, `preset`, `source`), la forma `paquete@versión/elemento`, con paquete y elemento en minúsculas ASCII, dígitos, guiones y puntos, y una versión que empieza con un dígito. `lut` y `preset` admiten además el texto vacío, y `lut` también `file:` seguido de una ruta sin caracteres de control ni `" < > |`, como la de un medio (`OptionDefinition.AllowsFile`). Si el paquete existe se comprueba al resolver la referencia, no al abrir. Las de texto (`path`, `matte`, `matteStart`, las curvas) admiten hasta 16 384 caracteres sin caracteres de control. Las opciones que el tipo no define se conservan sin mirarlas.

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

### Títulos y clips generados (formato 5)

Un clip puede llevar `generator` en lugar de `mediaId`: su imagen no sale de un archivo sino que la genera Rodaje. Hoy el único tipo es `text`, un título; `kind` es abierto (color sólido y formas llegarán sin cambiar el formato) y un tipo desconocido se conserva al guardar y no se dibuja. Un generador no tiene medio, así que su origen es virtual y no tiene límites: `sourceOut − sourceIn` sigue siendo la duración en la pista, los keyframes de sus efectos siguen en tiempo del origen y `sourceIn` puede ser negativo (al alargar el inicio de un título). Va solo en pistas de video, sin `speed`, y `mediaId` no se escribe.

```
{ "id": "clip-9", "sourceIn": 0, "sourceOut": 2116800000, "start": 705600000, "linkId": null, "enabled": true,
  "generator": {
    "kind": "text", "text": "Hola\nmundo",
    "style": {
      "source": "rodaje-titulos@1.0.0/clasico", "name": "Clásico",
      "font": { "family": "Inter", "weight": 700, "italic": false, "source": "rodaje-fuentes@1.0.0/inter", "fallback": ["Segoe UI"] },
      "size": 96, "reference": { "width": 1920, "height": 1080 }, "lineHeight": 1.1, "letterSpacing": 0, "align": "center",
      "fill": { "color": "#FFFFFF" },
      "stroke": { "color": "#000000", "width": 6, "join": "round" },
      "shadow": { "color": "#00000099", "dx": 4, "dy": 6, "blur": 8 },
      "background": { "color": "#00000080", "padding": [24, 12], "radius": 12 },
      "in": { "durationSeconds": 0.4, "curves": { "opacity": [ { "t": 0, "value": 0, "interp": "easeOut" }, { "t": 0.4, "value": 1, "interp": "linear" } ] } },
      "out": { "durationSeconds": 0.3, "curves": { "opacity": [ { "t": 0, "value": 1, "interp": "linear" }, { "t": 0.3, "value": 0, "interp": "linear" } ] } } },
    "box": { "widthFraction": 0.8 },
    "edges": { "in": true, "out": true } },
  "effects": [ { "id": "effect-9", "type": "rodaje.transform", "enabled": true, "parameters": { "y": { "value": 380, "keyframes": [] } }, "options": {} } ] }
```

| Campo | Qué es |
|---|---|
| `text` | El texto. Admite saltos de línea `\n` y tabulaciones, y las marcas de dirección (bidi); ningún otro carácter de control. Hasta 10 000 caracteres y 200 líneas; los separadores de línea y de párrafo de Unicode (U+2028, U+2029) también cuentan como salto. |
| `style` | **Copia completa** del estilo `rodaje.textstyle` v1 del que salió (D-029), obligatoria en `text`. `source` (`paquete@versión/estilo`) solo dice de dónde salió: el proyecto se ve igual sin el paquete y no cambia si el paquete se actualiza. Las medidas van en píxeles de `reference` y se escalan a la secuencia con `min(ancho/refAncho, alto/refAlto)`; los colores son `#RRGGBB` o `#RRGGBBAA`. `stroke`, `shadow`, `background`, `highlight`, `in` y `out` son opcionales. `highlight` (`{ "color" }`, solo del proyecto, no del formato de paquete) es la «palabra resaltada»: en un subtítulo con `words`, la palabra que suena se pinta de ese color (karaoke) desde que empieza hasta que empieza la siguiente. |
| `style.font` | `family` y `weight` (100–900), `italic`, `source` si la fuente es de un paquete (`font`) y hasta 8 familias de respaldo en `fallback`. Si la fuente falta, se usa el respaldo con aviso. |
| `style.in`, `style.out` | Entrada y salida pegadas a los bordes del clip: `durationSeconds` (0–60) y curvas por parámetro (`x`, `y`, `scale`, `rotation`, `opacity`), con `t` en segundos desde su borde (0 a `durationSeconds`, en orden estricto), `value` dentro del rango de `rodaje.transform` e `interp` como en los keyframes: es la del tramo hasta el punto siguiente, así que en el último no tiene efecto. Son un delta sobre `rodaje.transform` del clip: suman en `x`, `y` y `rotation` y multiplican en `scale` y `opacity`. Si el clip es más corto que las dos juntas, ambas se escalan por igual al evaluar. |
| `box` | Opcional. Ancho para el ajuste de línea: `width` en píxeles de `reference` o `widthFraction` (0–1] del ancho de la secuencia, que manda si están los dos. |
| `edges` | Qué bordes animan: `in` la entrada y `out` la salida (las dos verdaderas por omisión). `before` y `after` (ticks, opcionales, 0 por omisión) dicen cuánto del título original queda antes del borde izquierdo y después del derecho: dividir un título los guarda para que cada trozo se vea igual que antes, también con el corte en plena entrada o salida, y apaga el borde interno cuya animación quedó entera al otro lado (un corte lejos de la entrada y la salida deja los bordes internos apagados y sin desfase). Recortar no los toca, así que la animación sigue al nuevo borde. |
| `words` | Opcional. Las palabras transcritas de un subtítulo automático, para editar por palabra: lista de `{ "start", "end", "text" }` con los tiempos en ticks del origen del clip (como los keyframes; un subtítulo nuevo tiene `sourceIn` 0, así que van desde su borde izquierdo y pueden empezar un poco antes de él). Cada `text` tiene de 1 a 100 caracteres sin espacios ni caracteres de control; `start` ≤ `end`, en orden de `start`, dentro de ±360 000 s; hasta 2000 palabras. El texto que se ve es siempre `text` del generador: si se edita a mano, las palabras no cambian. Dividir el clip las copia enteras a los dos trozos (las que quedan fuera de `sourceIn`–`sourceOut` no se ven). |

Topes del estilo: `size` mayor que 0 y hasta 1000; `stroke.width` hasta la mitad de `size`; `shadow.blur` hasta 64; `lineHeight` mayor que 0 y hasta 10; desplazamientos, márgenes y radios hasta 1000 en valor absoluto; `reference` de 1 a 16 384; nombres de familia hasta 128 caracteres; hasta 64 puntos por curva. En todo el proyecto, los textos de los generadores y sus palabras transcritas suman como mucho 2 000 000 de caracteres.

`words` y `style.highlight` se agregaron dentro del formato 5 sin subir la versión ni migrar: son opcionales, un archivo sin él se lee como antes y el esquema `rodaje-proyecto.v5.schema.json` lo describe. Un Rodaje anterior abre un archivo con `words` pero, como con cualquier campo desconocido, lo pierde al guardar; el texto de los subtítulos no cambia.

Una pista puede llevar `role`, su función. Hoy se usa `subtitles` (solo en pistas de video): sus clips de texto son subtítulos. El nombre de la pista sigue siendo solo para mostrar. Un rol es un texto de 1 a 32 caracteres con minúsculas ASCII, dígitos y guiones; uno desconocido se conserva. Un ejemplo con títulos, rol y un `sourceIn` negativo está en `tests/Rodaje.Core.Tests/Serialization/ejemplos/proyecto-v5.rodaje`.

### Clips compuestos (formato 6)

Un clip puede llevar `sequenceId` en lugar de `mediaId`: es un **clip compuesto** y muestra un tramo de otra secuencia del mismo proyecto, que se compone como si sus clips estuvieran sueltos en ese lugar. Su origen es el tiempo de esa secuencia: `sourceIn` y `sourceOut` dicen qué tramo se ve (desde 0 y, al recortar, hasta su final; si la secuencia se acorta después, lo que falta se ve vacío) y `start` dónde empieza. No lleva `speed` ni `generator` y puede ir en una pista de video (muestra las pistas de video de la secuencia) o de audio (suena con sus pistas de audio); Rodaje crea los dos enlazados.

```
{ "id": "clip-12", "sequenceId": "sequence-7", "sourceIn": 0, "sourceOut": 7056000000, "start": 2116800000, "linkId": "link-4", "enabled": true, "effects": [] }
```

La secuencia anidada es una más de `sequences` (la primera sigue siendo la principal) y tiene la misma forma, tasa y frecuencia que la que la contiene. Se pueden anidar compuestos dentro de compuestos hasta 16 niveles, pero una secuencia nunca puede llegar a contenerse a sí misma, ni directa ni indirectamente: el archivo se rechaza. Al mostrarlo, la pista `k` de lo anidado va sobre la pista del compuesto en el lugar `k` (sus pistas ocultas o mudas no se ven ni suenan). Los efectos del propio clip compuesto (transformación, opacidad, color, máscaras, mezcla) y sus transiciones se aplican a todo lo que muestra, como a un clip más; en audio, su volumen, fundidos, ecualizador y compresor, después de los efectos de pista de la secuencia anidada. En una transición, lo de dentro que toca el borde del compuesto se alarga con su propio material, como un clip suelto.

## Reglas que todo programa debe respetar

El esquema JSON describe la forma del archivo, pero hay reglas que relacionan unos datos con otros y que el esquema no puede expresar. Rodaje las comprueba todas al abrir un proyecto y lo rechaza con un mensaje que las enumera si alguna falla, así que un programa que escriba un `.rodaje` debe cumplirlas.

Todos los identificadores (`id`) del proyecto, de los medios, de las secuencias, de las pistas, de los clips, de los efectos, de los marcadores y de las regiones deben ser textos no vacíos, sin caracteres de control y únicos en todo el archivo, compartiendo un mismo espacio: un clip no puede llamarse igual que una pista. Rodaje genera identificadores del tipo `clip-3f2a…` y otros programas pueden usar cualquier texto, por ejemplo un UUID.

Cada medio debe tener una duración positiva. Cada secuencia debe tener ancho, alto y frecuencia de muestreo positivos. Una pista de `videoTracks` debe ser de tipo `video` y una de `audioTracks` de tipo `audio`.

Cada clip debe apuntar con `mediaId` a un medio que exista en `media`, llevar `generator` o apuntar con `sequenceId` a otra secuencia del proyecto: exactamente una de las tres. El `sourceIn` de un clip de medio no puede ser negativo (el de un generador sí), su `sourceOut` debe ser mayor que su `sourceIn` y no puede pasar de la duración del medio, y su `start` no puede ser negativo. Un clip en una pista de video necesita un medio con imagen (`hasVideo`) y uno en una pista de audio, un medio con sonido (`hasAudio`). Dentro de una pista, los clips van ordenados por `start` y no se solapan: cada uno empieza en el final del anterior o después. Los huecos entre clips no se guardan; son simplemente el espacio que queda entre uno y el siguiente.

Un marcador no puede estar antes del instante cero, y una región tampoco puede empezar antes y debe terminar después de empezar. Los keyframes de cada parámetro van en orden estrictamente creciente de `time`, sin dos en el mismo instante. Un mismo `linkId` puede repetirse en varias pistas, pero nunca en dos clips de la misma pista. Cada transición une dos clips contiguos de su propia pista (`to` es el siguiente de `from` y empieza justo donde este termina), cada corte tiene como mucho una y su duración es positiva y no pasa de la del más corto de sus clips. En los tipos registrados, cada parámetro (y cada keyframe) es un número finito dentro de los límites del registro: una opacidad de 2 o un ángulo de barrido de 720 se rechazan; los parámetros de tipos desconocidos no se miran. Un tipo registrado como transición no puede ir en una lista de efectos ni uno de efecto en `transitions`, y las opciones y los efectos de pista siguen las reglas de la sección anterior.

Además, el archivo debe ser JSON estricto: sin comentarios, sin comas sobrantes y sin el mismo campo repetido dentro de un objeto, porque no habría forma de saber cuál vale. Las listas y las `options` no pueden contener `null`, y los campos de texto, listas y objetos tampoco; los únicos campos que admiten `null` son `linkId`, el `frameRate` de un medio, `speed`, `duration`, `curve`, `mediaId`, `generator` y `sequenceId` en un clip, `role` en una pista y los campos opcionales del generador y su estilo (`style`, `box`, `words`, `width`, `widthFraction`, `source`, `name`, `stroke`, `shadow`, `background`, `highlight`, `in`, `out`); `null` vale lo mismo que no escribirlo.

Un campo que falta toma su valor por defecto: listas vacías, números en 0, `enabled` en verdadero, la secuencia en 1920 × 1080 a 30 fps y 48 kHz, la interpolación en `linear`. Los campos desconocidos se ignoran al leer, lo que permite que un archivo escrito por una versión futura con campos opcionales nuevos se abra en una anterior de la misma versión de formato, pero esos campos no se conservan cuando Rodaje vuelve a guardar. Por eso otro programa no debe guardar datos propios dentro del `.rodaje` esperando que sobrevivan a una edición en Rodaje; si necesita datos adicionales, debe guardarlos en un archivo aparte o proponer que se agreguen al formato en una nueva versión.

## Versiones y migraciones

`schemaVersion` es un entero que solo crece. Cuando el formato cambia de una manera que una versión anterior de Rodaje no podría leer correctamente, por ejemplo al agregar la velocidad de los clips, que cambia la relación entre la duración en la pista y el tramo usado del origen, se sube la versión. Al abrir un archivo, Rodaje lee primero ese número. Si falta, no es un entero o es menor que 1, el archivo no es un proyecto de Rodaje. Si es mayor que la versión que el programa conoce, el archivo fue creado con una versión más nueva y se pide actualizar el programa en lugar de abrirlo a medias. Si es menor, se aplican en cadena las migraciones de la versión 1 a la 2, de la 2 a la 3 y así hasta la actual, trabajando sobre el árbol JSON antes de convertirlo en objetos, y después se valida el resultado como cualquier otro proyecto. Cada versión tiene su propio esquema con el número en el nombre del archivo, de modo que `rodaje-proyecto.v1.schema.json` no cambia nunca una vez publicado. La migración de la 1 a la 2 agrega a cada secuencia las listas `markers` y `regions` vacías. La de la 2 a la 3 agrega a cada pista `effects` y `transitions` vacías y a cada efecto `options` vacío, y convierte la envolvente vieja `rodaje.volume` (ganancia lineal en `gain`) en `rodaje.audio` con `gainDb` = 20·log10(gain), keyframes incluidos; una ganancia de 0 o menos queda en −60 dB. La de la 3 a la 4 quita los efectos `rodaje.speed`, que estaban reservados y nunca se aplicaron; los clips quedan sin `speed`, a 1×. La de la 4 a la 5 solo cambia el número: un archivo del formato 4 no tiene generadores ni roles. La de la 5 a la 6 también solo cambia el número: un archivo del formato 5 no tiene clips compuestos, y un Rodaje del formato 5 rechaza el 6 con «actualiza el programa». Un Rodaje del formato 4 rechaza el 5 con «actualiza el programa» en lugar de un error confuso sobre un clip sin medio. Una migración publicada tampoco se modifica: si tuviera un error se corrige con otra migración.

Al volver a guardar, Rodaje siempre escribe la versión actual, así que un proyecto abierto y guardado con una versión nueva ya no se puede abrir con una anterior. La copia `.bak` descrita abajo conserva la versión previa del archivo.

## Guardado seguro y copia `.bak`

Un corte de luz a mitad de una escritura puede dejar un archivo cortado. Por eso Rodaje nunca escribe directamente sobre el proyecto. Primero escribe el contenido completo en un archivo temporal con el mismo nombre y la terminación `.tmp` (`Mi proyecto.rodaje.tmp`), en la misma carpeta, y le pide al sistema que lo lleve físicamente al disco antes de seguir. Solo entonces lo cambia por el proyecto. Si el proyecto ya existía, el cambio se hace con una operación de reemplazo de Windows que deja la versión anterior como `Mi proyecto.rodaje.bak`; si no existía, el temporal simplemente se renombra. Se corte cuando se corte, en disco queda la versión anterior completa o la nueva completa, nunca una mezcla.

Si alguna vez un proyecto no abre porque está dañado, la copia `.bak` contiene el guardado anterior: basta con hacer una copia de ella, quitarle la terminación `.bak` y abrirla. Un `.tmp` que quede en la carpeta es el resto de un guardado interrumpido; no hace falta borrarlo, porque el siguiente guardado lo reemplaza. Otros programas que escriban archivos `.rodaje` deberían seguir el mismo procedimiento.
