# Formato de proyecto `.rodaje`

Un proyecto de Rodaje se guarda en un archivo con extensión `.rodaje`. Es texto JSON en UTF-8, sin marca BOM, indentado con dos espacios y con saltos de línea LF. Se eligió JSON legible y no un formato binario por dos razones: el proyecto es el trabajo de quien edita y debe poder abrirse, revisarse e incluso repararse con un editor de texto, y otros programas (un editor de audio, herramientas en Python) deben poder leer y escribir el mismo proyecto sin depender del código de Rodaje. Por eso este documento describe el formato completo, con sus reglas, y junto a él se publica un esquema JSON en `schema/rodaje-proyecto.v15.schema.json` (los de las versiones 1 a 14 se conservan para los archivos viejos) que cualquier validador del estándar draft 2020-12 entiende.

El archivo nunca contiene video, audio ni imágenes. Solo guarda dónde están los archivos de origen y cómo se usan, de modo que editar jamás modifica el material original y un proyecto pesa unos pocos kilobytes.

Por compatibilidad con Rodaje 1.0.1, el guardado usa `schemaVersion: 13` cuando la biblioteca no contiene ninguna imagen fija. Solo se escribe formato 14 si hay al menos un medio con `isStillImage: true`, aunque no se use en un clip, y formato 15 si alguna secuencia es HDR (`colorSpace` distinto de `sdr-709`). Es una ampliación provisional de D-050; Daniel puede cambiarla. La lectura migra las tres versiones al modelo actual en memoria.

## Unidades: el tiempo en ticks

Todos los tiempos del archivo, sean posiciones o duraciones, son enteros que cuentan *ticks*. Un segundo tiene exactamente 705 600 000 ticks. El número no es caprichoso: es divisible entre la duración de un cuadro a 24, 25, 30, 48, 50, 60, 90, 100 y 120 cuadros por segundo, también en las variantes NTSC que multiplican la tasa por 1000/1001 (23,976, 29,97 y 59,94 fps), y entre la duración de una muestra de audio a 8, 16, 22,05, 24, 32, 44,1, 48, 88,2, 96 y 192 kHz. Gracias a eso, el inicio de cada cuadro y de cada muestra cae siempre en un tick entero, y sumar o restar posiciones no acumula el error de redondeo que aparece cuando el tiempo se guarda como segundos en un número decimal. Por ejemplo, a 29,97 fps un cuadro dura 705 600 000 × 1001 / 30 000 = 23 543 520 ticks exactos.

Para convertir, un programa externo divide entre 705 600 000 y obtiene segundos. Al escribir, debe redondear al tick entero más cercano; un tiempo con decimales o escrito como texto hace que el archivo se rechace. Los valores caben en un entero de 64 bits con signo. En JavaScript conviene leerlos como `BigInt` si pudieran superar 2^53, aunque ese límite equivale a unos 148 días de material, así que en la práctica un `number` normal basta.

Las tasas de cuadros no son decimales sino fracciones exactas con la forma `{"num": 30000, "den": 1001}`. Los dos campos son obligatorios, enteros y mayores que cero. Al leer, la fracción se simplifica, de modo que `{"num": 60, "den": 2}` se entiende como 30/1, y Rodaje siempre la escribe ya simplificada.

## Un ejemplo completo

El siguiente archivo es un proyecto real y válido. Tiene una toma de video de 10 segundos grabada a 29,97 fps con su sonido y una foto. En la línea de tiempo, el video y su audio se colocan enlazados al principio, con un fundido de entrada en la opacidad, y la foto aparece en una pista superior a partir del segundo 2.

```json
{
  "schemaVersion": 14,
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
      "duration": 0,
      "isStillImage": true,
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

La raíz del archivo es el proyecto. `schemaVersion` es la versión del formato y hoy vale 14; es el primer dato que se lee y decide cómo se interpreta el resto. `id` identifica al proyecto y `name` es el nombre que ve quien edita. `media` es la biblioteca de medios y `sequences` son las líneas de tiempo, que pueden ser varias, por ejemplo una horizontal y otra vertical del mismo material.

Cada medio describe un archivo de origen. `path` es su ruta; se prefiere relativa a la carpeta donde está el `.rodaje`, para que el proyecto se pueda mover junto con su material a otro PC, aunque se aceptan rutas absolutas. Se admiten tanto `/` como `\` como separador. `name` es el nombre visible, `duration` la duración total del archivo en ticks, `hasVideo` y `hasAudio` dicen qué contiene, `frameRate` es la tasa de su video o `null` si no tiene (una imagen fija o un archivo de solo audio), `width` y `height` son las dimensiones de la imagen en píxeles y `sampleRate` y `audioChannels` describen su sonido. Los campos que no aplican valen 0.

Cada secuencia tiene su propio `id` y `name`, su tasa de cuadros `frameRate`, su resolución `width` × `height`, la frecuencia de muestreo `sampleRate` a la que se mezcla el audio y, desde el formato 15, un `colorSpace` opcional: `sdr-709` (lo de siempre; es el valor si falta y no se escribe), `hdr-pq` (BT.2020 con PQ, HDR10) o `hdr-hlg` (BT.2020 con HLG). Con HDR la exportación en HEVC Main 10, AV1 de 10 bits y ProRes sale etiquetada BT.2020 con esa curva y el blanco SDR en 203 nits; en 8 bits o RGB sale SDR. Las pistas van en dos listas separadas, `videoTracks` y `audioTracks`, y el orden dentro de cada lista importa: en `videoTracks` el primer elemento es la capa de más abajo y cada pista siguiente se compone encima de la anterior, de abajo hacia arriba, como en cualquier editor de video. Una pista tiene `id`, `kind` (que vale `"video"` o `"audio"` y debe coincidir con la lista en la que está), `name`, y tres interruptores: `muted` la silencia, `hidden` la oculta de la imagen y `locked` impide editarla. Su lista `clips` está ordenada por posición, `effects` guarda los efectos de toda la pista y `transitions` las transiciones de sus cortes (ver más abajo). Después de las pistas, `markers` guarda los marcadores, cada uno con `id`, el instante `time` y un `name` que puede ir vacío, ordenados por tiempo; y `regions` guarda las regiones, tramos con nombre `[start, end)` que pueden solaparse, ordenadas por inicio. Los dos admiten `color` (`#RRGGBB`) y `note` (texto libre de hasta 2000 caracteres), que solo se escriben si tienen valor; un Rodaje anterior los ignora al leer, por eso no cambian el número de versión. Los dos están en tiempo de la secuencia: con el ripple en el modo «pistas, marcadores y regiones», cerrar un hueco o recortar los corre junto con los clips.

El clip es el corazón del formato y se describe con tres instantes. `sourceIn` es el punto de entrada dentro del archivo de origen, incluido; `sourceOut` es el punto de salida, excluido; y `start` es el lugar de la pista donde se coloca el clip. Su duración es `sourceOut − sourceIn` y su final en la pista es `start + duración`. Estos dos valores no se guardan porque se calculan, y un programa externo no debe escribirlos. Toda la edición no destructiva consiste en cambiar estos tres números: recortar mueve `sourceIn` o `sourceOut`, deslizar mueve `start`. `mediaId` dice qué medio usa, `enabled` permite apagar un clip sin borrarlo, y `linkId`, si no es `null`, agrupa clips que se editan juntos; normalmente el video y el audio de una misma toma. El valor de `linkId` es un texto cualquiera que comparten los clips del grupo y no tiene que aparecer en ningún otro lugar.

Los efectos de un clip están en `effects` y se aplican en el orden de la lista. Cada efecto tiene `id`, un `type` con espacio de nombres, como `rodaje.transform`, un interruptor `enabled` y un diccionario `parameters` cuyas claves son los nombres de los parámetros. Un programa no debe depender del orden de esas claves. Cada parámetro es numérico: `value` es su valor cuando no está animado y `keyframes` es la animación. Cada keyframe tiene `time`, `value` e `interpolation`, que dice cómo se va desde ese keyframe hasta el siguiente: `hold` mantiene el valor y salta de golpe, `linear` avanza en línea recta, `easeIn` arranca despacio, `easeOut` frena al final, `easeInOut` hace las dos cosas, `bounce` llega rebotando, `elastic` se pasa y vuelve como un resorte y `bezier` sigue la curva de tiempo de `ease`; si falta, vale `linear`. Desde el formato 7 un keyframe puede llevar además `ease`, los puntos de control `{x1, y1, x2, y2}` de esa curva como `cubic-bezier` de CSS (x de 0 a 1, y de −2 a 3; sin `ease`, `bezier` es como `easeInOut`), y `spatialIn`/`spatialOut`, las tangentes de la trayectoria en unidades del valor: en `x` e `y` de `rodaje.transform`, con keyframes de los dos en los mismos instantes, forman los manejadores de una curva de Bézier que la capa recorre a velocidad constante (la interpolación del keyframe de `x` reparte el recorrido en el tiempo); una tangente que falta deja recto ese lado. Las fórmulas exactas están en `KeyframeMath` y `MotionPath` del núcleo. Antes del primer keyframe el parámetro vale lo del primero y después del último vale lo del último. Lo más importante de los keyframes es que su `time` está en **tiempo del origen**, no de la línea de tiempo, y en la misma escala que `sourceIn`. Así, si el clip se mueve en la pista, la animación lo acompaña sin recalcular nada, y si se recorta, los keyframes siguen pegados al mismo cuadro del material. Los valores vectoriales, como una posición x/y o un color, se guardan como varios parámetros escalares. Lo que no es un número va en `options`, un diccionario de textos: el modo de mezcla, la LUT o el paquete del que salió el efecto.

### Efectos, opciones y transiciones (formato 3)

Los tipos que el motor entiende están en `EffectRegistry` (`src/Rodaje.Core/Effects`), cada uno con un código entero estable, su destino (video, audio o transición), su etapa en el motor, sus parámetros con valor por defecto y límites, y sus opciones. Un parámetro que falta vale su valor por defecto. Un tipo que no está en el registro, por ejemplo de otro programa o de un paquete que falta, se conserva tal cual al guardar, con sus parámetros y opciones, pero no se aplica.

| Tipo | Parámetros (por defecto) | Opciones |
|---|---|---|
| `rodaje.transform` | `x` (0), `y` (0) en píxeles de la secuencia, `scale` (1), `rotation` (0) en grados, `opacity` (1); desde el formato 7, `anchorX`, `anchorY` (0: punto de anclaje desde el centro de la imagen, en píxeles a escala 1; escala, giro e inclinación pivotan en él y moverlo no mueve la imagen), `scaleX`, `scaleY` (1: escala por eje que multiplica a `scale`), `skewX`, `skewY` (0, de −85 a 85 grados), `shutterAngle` (0: desenfoque de movimiento apagado; 180 es el de cine, hasta 720), `shutterPhase` (0, de −360 a 360: corrimiento del centro del obturador) y `motionSamples` (16, de 2 a 64); los tres del obturador, sin keyframes; `hdrReferenceNits` (0: blanco PQ de 100 nits o pico HLG de 1000; en un clip SDR con `hdrExpand`, el pico de 203 a 1000, 0 usa 1000) | `flipX`, `flipY`: `off` u `on` (voltear alrededor del centro de la imagen); `hdrExpand`: `off` u `on` (expandir un clip SDR a HDR en una secuencia HDR, D-058) |
| `rodaje.color` | `exposure` en pasos, `contrast`, `saturation`, `temperature`, `tint`, `highlights`, `shadows` (todos 0, de −1 a 1 salvo la exposición, de −4 a 4), `lutIntensity` (1) | `lut`: referencia `paquete@versión/elemento` de un `.cube`, o `file:` más la ruta de un `.cube` suelto (con `/`, relativa a la carpeta del `.rodaje` o absoluta); vacía o ausente sin LUT |
| `rodaje.vignette` | `amount`, `size`, `softness` (0,5), `roundness` (0: elipse del cuadro; 1: círculo), `centerX`, `centerY` (0,5) | |
| `rodaje.blur` | `radius` (10) en píxeles de la secuencia, `angle` (0, grados, direccional), `centerX`, `centerY` (0,5, radial) | `kind`: `gaussian` (por defecto), `lens`, `directional`, `radial`; `region` |
| `rodaje.sharpen` | `amount` (0,5), `radius` (1), `threshold` (0, hasta 0,25) | `region` |
| `rodaje.grain` | `amount` (0,25), `size` (1,5 px), `color` (0,15), `response` (0,6: más en los medios) | `region` |
| `rodaje.glow` | `threshold` (0,65), `radius` (40 px), `intensity` (0,8, hasta 4) | `region` |
| `rodaje.aberration` | `amount` (4 px), `angle` (0, lateral) | `mode`: `radial` o `lateral`; `region` |
| `rodaje.denoise` | `strength` (0,5), `temporal` (0,5), `detail` (0,5) | `region` |
| `rodaje.mirror` | `axis` (0,5), `tiles` (2, de 1 a 6) | `mode`: `left`, `right`, `top`, `bottom`, `quad`, `tiles` |
| `rodaje.distortion` | `amount` (0,2, de −1 cojín a 1 barril), `zoom` (1) | |
| `rodaje.reframe360` | `yaw` (0, de −180 a 180: positivo mira a la derecha), `pitch` (0, de −90 a 90: positivo mira arriba), `roll` (0, de −180 a 180: positivo gira la imagen en sentido horario), `fov` (90, de 20 a 330: campo de visión horizontal en grados; la rectilínea llega a 170) | `projection`: `rectilinear` (por defecto) o `stereographic` (pequeño planeta). Lee el clip como equirectangular 360 y lo convierte en una vista plana que llena la secuencia; va antes que los demás efectos de imagen. Lo trae el motor y se aplica siempre, pero la interfaz solo lo ofrece con el complemento `rodaje-360` (D-038) |
| `rodaje.qualifier` | Selección: `hueCenter` (120°), `hueWidth` (30°), `hueSoftness` (15°), `satLow` (0,1), `satHigh` (1), `lumaLow` (0), `lumaHigh` (1), `softness` (0,1). Corrección: `hueShift` (0°), `saturation` (0), `exposure` (0 EV) | `invert`: `off` u `on` |
| `rodaje.blend` | | `blendMode`: `normal`, `add`, `multiply`, `screen`, `overlay`, `softLight`, `hardLight`, `darken`, `lighten`, `difference`, `exclusion`, `colorDodge` o `colorBurn` |
| `rodaje.audio` | `gainDb` (0; −60 o menos es silencio), `fadeIn` y `fadeOut` en segundos (0, sin keyframes), `pan` y `balance` (0, de −100 a 100; formato 10) | |
| `rodaje.stabilize` | Reservado: se reconoce, pero todavía no se aplica | |
| `rodaje.mask` | `x`, `y` (0,5: centro en fracción de la imagen de la capa), `width`, `height` (0,5: fracción del ancho y del alto), `rotation` (0, grados), `feather` (0: borde suave en fracción del lado corto, centrado en el contorno), `opacity` (1), `expansion` (0, de −0,5 a 0,5: corre el contorno hacia afuera o hacia adentro, en fracción del lado corto), `roundness` (0 a 1: radio de las esquinas del rectángulo en fracción de la mitad de su lado corto), `thickness` (0,02: grosor de la pluma de trazo en fracción del lado corto) | `shape`: `rectangle`, `ellipse`, `path`, `image`, `bezier` o `stroke`; `invert`: `off` u `on`; `combine`: `add`, `subtract`, `intersect` o `difference` (sin la opción, `intersect`); `path` (trazo libre de antes: `x,y;x,y;…`, de 3 a 256 puntos relativos a la caja, de −0,5 a 0,5); `points` (vértices de `bezier` y `stroke`, ver abajo); `shapeKeys` (forma animada, ver abajo); `matte` (`file:` y la ruta del video gris de la máscara de imagen, relativa al proyecto) y `matteStart` (segundos del origen de su primer cuadro); `target` (id de un efecto de imagen del mismo clip: la máscara limita ese efecto en vez de la capa); `name` y `color` (`#RRGGBB`), solo para la interfaz |
| `rodaje.crop` | `left`, `top`, `right`, `bottom` (0: fracción de la imagen que se quita de cada borde, hasta 0,98; si dos opuestos suman más, se reducen a la par) | `aspect`: `free`, `16:9`, `9:16`, `1:1`, `4:5` o `4:3` (la impone la interfaz al ajustar; el motor solo lee los bordes). Lo que queda se ajusta al cuadro como la imagen entera |
| `rodaje.pixelate` | `size` (24, de 2 a 256: lado del bloque en píxeles de la secuencia) | |
| `rodaje.cornerpin` | `topLeftX`, `topLeftY`, `topRightX`, `topRightY`, `bottomRightX`, `bottomRightY`, `bottomLeftX`, `bottomLeftY` (0: cuánto se corre cada esquina de la imagen ajustada al cuadro, en píxeles de la secuencia, antes de la transformación; formato 13) | La imagen va a ese cuadrilátero con perspectiva (una homografía). Si queda cruzado o con una esquina detrás del horizonte, la capa sale sin anclaje |
| `rodaje.track` | `threshold` (0,7, de 0,05 a 0,99: confianza bajo la que un cuadro es dudoso; no se anima) | `kind` (`point`, `two`, `corners` o `region`), `name`, `color`, `points` (`x,y,anchoPatrón,altoPatrón,anchoBúsqueda,altoBúsqueda` por punto, separados por `;`, en fracciones de la imagen) y `samples`: `ticks@x,y,c;x,y,c|…` con los tiempos del origen en orden estricto, la misma cantidad de puntos (1 a 4) en cada cuadro y la confianza `c` de 0 a 1 (0: perdido y estimado; 1: puesto a mano), hasta 4 194 304 caracteres. No dibuja nada: guarda un seguimiento para aplicarlo (formato 13). Un texto que no se puede leer se ignora |
| `rodaje.chromakey` | `keyRed`, `keyGreen`, `keyBlue` (0; 0,7; 0,2: color clave en R'G'B', sin keyframes), `tolerance` (0,2), `softness` (0,1), `spill` (0,5), `cleanup` (0) | |
| `rodaje.wheels` | `liftRed`, `liftGreen`, `liftBlue`, `lift` (0, de −0,5 a 0,5), `gammaRed`, `gammaGreen`, `gammaBlue`, `gamma`, `gainRed`, `gainGreen`, `gainBlue`, `gain` (0, de −1 a 1): tinte por canal y valor general de sombras, medios y luces | |
| `rodaje.curves` | | `master`, `red`, `green`, `blue` (texto `x,y;x,y;…` de 0 a 1, hasta 16 puntos; vacía es la identidad). Cada canal pasa por su curva y luego por la general. `hueHue`, `hueSat` (periódicas en el tono) y `lumaSat`: curvas de ajuste con 0,5 sin cambio (giro de tono de ±media vuelta; ganancia de saturación 2·y) |
| `rodaje.speed` | Reservado y oculto (código 8): la velocidad es `speed` del clip (formato 4) y un efecto de este tipo se rechaza | |

**Máscaras (formato 8).** Las máscaras de un mismo destino (la capa, o un efecto con `target`) se combinan (`combine`) en el orden de la pila, como operadores de Porter y Duff sobre la cobertura `m` (de 0 a 1, ya invertida y por su `opacity`): `add` es la unión `a + m − a·m`, `subtract` la resta `a·(1 − m)`, `intersect` la intersección `a·m` y `difference` la o exclusiva `a + m − 2·a·m`. Se arranca en 0 si la primera es `add` o `difference`, y en 1 si es `subtract` o `intersect`; por eso las de un archivo del formato 6, sin `mode`, se siguen multiplicando. Los vértices de `points` son `x,y[,inX,inY,outX,outY[,peso]]` separados por `;`, relativos a la caja (−0,5 a 0,5), con hasta 256 vértices y 32 768 caracteres: los manejadores van relativos a su vértice (sin ellos es una esquina) y el peso (1 si falta, de 0 a 16) multiplica el borde suave de ese vértice en una `bezier` y el grosor en un `stroke`; entre dos vértices el peso se interpola. Una `bezier` es cerrada y necesita 3 vértices; un `stroke` es abierto y necesita 2. `shapeKeys` anima los vértices: `ticks@vértices|ticks@vértices…` con los tiempos del origen en orden estricto, hasta 262 144 caracteres; entre dos keyframes con la misma cantidad de vértices se interpola en línea recta y, si cambia, se mantiene el anterior. Si tiene keyframes, manda sobre `points`. Una máscara cuyo `target` no existe, está apagado o no es de imagen no hace nada. Al cortar un clip, congelar un cuadro o pegar atributos, las copias llevan sus máscaras a la copia del efecto, y los keyframes de forma se corren como los demás.

`region` (en los efectos que van en el intermedio): `all` (por defecto), `inside` u `outside` de las máscaras con `use` = `effects`. `rodaje.mask` lleva además `use`: `cut` (recorta la capa, por defecto) o `effects` (no recorta: marca dónde van los efectos con `region`). `rodaje.color` lleva además `skinProtection` (0).

Cualquier efecto puede llevar además la opción `source` con la referencia del filtro o efecto de paquete del que es copia. Los efectos de una pista (`effects` de la pista, por ejemplo su volumen) tienen la misma forma, pero sus keyframes están en tiempo de la secuencia, porque una pista no tiene origen, y solo admiten tipos de su clase: de audio en una pista de audio y de video en una de video. Aun así siguen al contenido: cuando un ripple (borrar, recortar, cerrar un hueco o insertar con ripple) corre la pista, sus keyframes desde ese punto se corren lo mismo y los de un tramo borrado se quitan; al reordenar la pista principal, los del tramo de cada clip viajan con él. En cada borde se añade un keyframe con el valor que había, para que la curva no cambie fuera del tramo, salvo que valga lo mismo que sus vecinos; un tramo con suavizado que queda cortado por un borde vuelve a empezar ahí y se aproxima.

En los tipos registrados, una opción definida debe tener un valor aceptado: uno de la lista en las de elección (`blendMode`, `direction`) y, en las referencias (`lut`, `preset`, `source`), la forma `paquete@versión/elemento`, con paquete y elemento en minúsculas ASCII, dígitos, guiones y puntos, y una versión que empieza con un dígito. `lut` y `preset` admiten además el texto vacío, y `lut` también `file:` seguido de una ruta sin caracteres de control ni `" < > |`, como la de un medio (`OptionDefinition.AllowsFile`). Si el paquete existe se comprueba al resolver la referencia, no al abrir. Las de texto (`path`, `matte`, `matteStart`, `target`, `name`, `color`, las curvas) admiten hasta 16 384 caracteres sin caracteres de control; `points`, 32 768, `shapeKeys`, 262 144, y `samples`, 4 194 304. Las opciones que el tipo no define se conservan sin mirarlas.

Una transición vive en la pista y une dos clips seguidos, sin hueco: `from` es el clip que termina en el corte y `to` el que empieza ahí. Dura `duration` ticks repartidos según `alignment` (formato 12): `center` (por defecto, se omite) pone la mitad antes del corte y la mitad después (un tick impar va después), `before` la pone toda antes (termina donde termina el clip que sale) y `after` toda después (empieza donde empieza el que entra). La parte de antes cabe en el clip que sale y la de después en el que entra, y las dos transiciones de un mismo clip no se pisan: la parte de después de la que entra en él más la de antes de la que sale de él no pasan de su duración; centrada, además, no pasa del más corto de sus dos clips. Los clips nunca se solapan: cada lado usa el material que sobra más allá de su borde y, si no hay, se congela el cuadro del borde. Sus `parameters` son números sin keyframes (`{"angle": 90}`) y sus `options`, textos.

`curve` (formato 12, opcional) es la curva del avance con los nombres de las interpolaciones de los keyframes (`easeIn`, `easeOut`, `easeInOut`, `bezier`, `bounce`, `elastic`, `hold`); sin ella o con `linear` el avance es lineal. Con `bezier`, `ease` lleva los puntos `x1`, `y1`, `x2`, `y2` como en un keyframe. El avance vale exactamente 0 y 1 en los extremos y se recorta a 0–1 en medio. Mueve la mezcla, las curvas de una transición de paquete y, en audio, el cruce de volumen.

| Tipo | Parámetros (por defecto) | Opciones |
|---|---|---|
| `rodaje.crossfade` | | |
| `rodaje.dip` | `red`, `green`, `blue` (0: a negro) | |
| `rodaje.wipe` | `angle` (0, en grados), `softness` (0,05) | |
| `rodaje.push` | | `direction`: `left`, `right`, `up` o `down` |
| `rodaje.zoom-dissolve` | `zoom` (0,25) | |
| `rodaje.blur-dissolve` | `radius` (40) | |
| `rodaje.preset` | | `preset`: referencia de una transición de paquete; si falta el paquete, se ve como `rodaje.crossfade` |
| `rodaje.slide` | `angle` (180: hacia donde avanza el que entra; 0 a la derecha, 90 hacia abajo), `parallax` (0,3: cuánto acompaña el que sale), `motionBlur` (0,5) | |
| `rodaje.zoom-blur` | `strength` (0,6), `centerX`, `centerY` (0,5) | |
| `rodaje.spin` | `angle` (360: grados de giro en toda la transición), `zoom` (0,3), `motionBlur` (0,6) | |
| `rodaje.light-wipe` | `angle` (0), `softness` (0,08), `width` (0,12: ancho de la luz), `intensity` (1,5), `red`, `green`, `blue` (1; 0,82; 0,55) | |
| `rodaje.glitch` | `amount` (0,7), `bands` (18), `split` (0,02 del ancho), `seed` (0) | |
| `rodaje.film-burn` | `scale` (0,35 del alto), `red`, `green`, `blue` (1; 0,45; 0,12), `seed` (0) | |
| `rodaje.flash` | `intensity` (1), `width` (0,4 de la transición), `red`, `green`, `blue` (1) | |
| `rodaje.luma` | `angle` (0), `softness` (0,1), y el borde de color | `gradient`: `linear`, `radial`, `angular`, `diamond`, `noise` o `bars`; `invert`: `off` u `on`; `image`: `file:` y la ruta de una imagen gris (manda sobre el degradado) |
| `rodaje.bounce` | `bounce` (0,5), `motionBlur` (0,4) | `direction`, como el empuje |
| `rodaje.audio-power`, `rodaje.audio-gain`, `rodaje.audio-exponential` | | Solo en pistas de audio: cruce de potencia constante (seno y coseno), de ganancia constante (lineal) y exponencial (lineal en dB a lo largo de 60 dB) |

Desde el formato 12, `rodaje.wipe` y `rodaje.luma` llevan además el borde de color `borderWidth` (0, hasta 0,2 del cuadro; 0 sin borde) y `borderRed`, `borderGreen`, `borderBlue` (1), y `rodaje.push` lleva `motionBlur` (0). `motionBlur` es la fracción del cuadro con el obturador abierto (0,5 son 180°). Una transición de imagen en una pista de audio (de antes del formato 12) suena como la de potencia constante.

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
| `style` | **Copia completa** del estilo `rodaje.textstyle` del que salió (D-029), obligatoria en `text`. `source` (`paquete@versión/estilo`) solo dice de dónde salió: el proyecto se ve igual sin el paquete y no cambia si el paquete se actualiza. Las medidas van en píxeles de `reference` y se escalan a la secuencia con `min(ancho/refAncho, alto/refAlto)`; los colores son `#RRGGBB` o `#RRGGBBAA`. `stroke`, `shadow`, `background`, `highlight`, `in` y `out` son opcionales. `highlight` (`{ "color" }`, solo del proyecto, no del formato de paquete) es la «palabra resaltada»: en un subtítulo con `words`, la palabra que suena se pinta de ese color (karaoke) desde que empieza hasta que empieza la siguiente. |
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

### Títulos profesionales (formato 9)

Todo es opcional; un título sin nada de esto se escribe igual que en el formato 8.

| Campo | Qué es |
|---|---|
| `generator.spans` | Tramos con formato propio, en orden y sin solaparse: `{ "start", "length" }` en caracteres UTF-16 del texto y lo que cambian: `weight` (100–900), `italic`, `underline`, `color` y `scale` (multiplica el tamaño, de 0,1 a 10). Lo que no dicen lo pone el estilo; un tramo sin nada no se guarda. Hasta 512. Al escribir, los tramos se mueven con el texto (lo que se escribe al final de un tramo lo continúa); unir subtítulos o partirlos por palabra los quita. |
| `generator.box.heightFraction` | Alto de la caja (0–1] del alto de la secuencia, para el ajuste del tamaño. |
| `style.typography` | `kerning` (verdadero por omisión), `case` (`none`, `upper`, `lower`, `smallCaps`: versalitas sintéticas al 75 %), `features` (hasta 16 etiquetas OpenType de 4 caracteres, como `liga`, `tnum` o `ss01`; con `-` delante se apagan) y `fit` (`none`, `shrink`: achica si no cabe en la caja, `fill`: la llena). Las mayúsculas se aplican al dibujar, letra por letra: el texto guardado no cambia. |
| `style.fill.gradient` | Degradado sobre la caja del bloque: `kind` (`linear` con `angle` en grados, 0 = de izquierda a derecha, 90 = de arriba abajo, o `radial`) y de 2 a 8 `stops` `{ "position", "color" }` en orden. Manda sobre `fill.color`; el color de un tramo, sobre el degradado. |
| `style.extraStrokes`, `style.extraShadows` | Hasta 4 bordes y 4 sombras más, con los mismos campos que `stroke` y `shadow`. Los bordes se dibujan del más ancho (abajo) al más angosto; las sombras, en orden, encima de `shadow`. |
| `style.path` | Texto en arco: `{ "kind": "arc", "bend" }`, los grados (−360 a 360) que abarca la línea más larga; positivo, el centro del círculo queda abajo (el texto sube por el medio). Cada letra va girada con el arco y las líneas de abajo, más adentro. |
| `style.highlight.mode` | `word` (por omisión: la palabra que suena) o `sweep`: un barrido que avanza con la voz dentro de la palabra y deja pintado lo dicho. |
| `style.in.unit`, `.stagger`, `.order` | Animación por unidades: `unit` es `block` (por omisión), `line`, `word` o `letter`; `stagger` (0–1) la parte de la duración en que las unidades van arrancando una tras otra (1 = máquina de escribir); `order` es `forward`, `backward`, `center`, `edges` o `random` (un orden fijo, igual en cada cuadro). Por unidades, las curvas se recorren en la ventana de cada unidad, alrededor de su centro, y no son un delta de `rodaje.transform`. La curva `blur` (radio de 0 a 64 en píxeles de la referencia) solo vale por unidades; con ella un bloque también se anima así. «Convertir en keyframes» deja estas animaciones en el estilo. |

### Mezclador (formato 10)

Los efectos de audio de una pila (de un clip, una pista, un bus o `masterEffects`) se aplican **en el orden de la pila**, todos los activos; el volumen (`rodaje.audio`) no es una inserción. El sidechain (`sidechain` del compresor) lo escucha el primer compresor. En una pista o un bus, `rodaje.audio` es su fader: volumen, `pan` (ley de −3 dB: una fuente mono sale con √2·cos θ y √2·sin θ, θ = (pan/100 + 1)·π/4, 0 dB en el centro) y `balance` (baja solo el lado contrario), con keyframes en tiempo de la secuencia, después de las inserciones. En un clip, el paneo y el balance van con su volumen.

Una secuencia puede llevar `audioBuses`, las submezclas, en el orden del mezclador: cada bus tiene `id`, `name`, `muted`, `solo` y `effects` (inserciones y su fader). Una pista de audio puede llevar `output`, el `id` del bus al que sale (sin él, a la mezcla), `sends`, los envíos a buses (`busId`, `gainDb` de −60 a 12, `preFader`: antes del fader y del paneo de la pista, y `muted`), `solo` y `armed` (la pista que propone «Grabar voz»). Con algún solo puesto suenan solo las pistas con solo, las que salen a un bus con solo y los buses con solo o que reciben de una pista con solo; un bus suena después de sus pistas y antes de `masterEffects`. Los buses de un compuesto no se usan: lo anidado suena con su fader y sus inserciones.

```
"audioBuses": [ { "id": "bus-1", "name": "Voces", "effects": [] } ],
"audioTracks": [ { "id": "track-3", "kind": "audio", "name": "Locución", "output": "bus-1",
  "sends": [ { "busId": "bus-2", "gainDb": -12 } ], "clips": [], "effects": [], "transitions": [] } ]
```

| Tipo | Parámetros (por defecto) |
|---|---|
| `rodaje.equalizer` | `lowCut` (0 Hz: apagado), `lowGain`, `lowFrequency` (120), `midGain`, `midFrequency` (500), `midQ` (1), `presenceGain`, `presenceFrequency` (3000), `presenceQ` (1), `highGain`, `highFrequency` (8000); ganancias de −18 a 18 dB |
| `rodaje.graphicEq` | `band31`, `band63`, `band125`, `band250`, `band500`, `band1k`, `band2k`, `band4k`, `band8k`, `band16k` (0, de −12 a 12 dB), `output` (0) |
| `rodaje.compressor` | Ver [Audio](arquitectura/audio.md#ecualizador-y-compresor-processing): umbral, relación, tiempos, multibanda, limitador; opción `sidechain` |
| `rodaje.gate` | `threshold` (−45 dBFS), `range` (40 dB), `ratio` (20: puerta; menos, expansor 1:N), `attack` (1 ms), `hold` (40 ms), `release` (120 ms), `hysteresis` (4 dB), `sidechainHighPass` (0 Hz) |
| `rodaje.deesser` | `frequency` (6000 Hz), `threshold` (−30 dB), `range` (8 dB), `splitBand` (1: solo la banda alta) |
| `rodaje.saturation` | `drive` (6 dB), `character` (0 %: cinta; 100: válvula), `tone` (20 000 Hz: apagado), `output` (0 dB), `mix` (100 %) |
| `rodaje.delay` | `time` (350 ms), `sync` (0), `tempo` (120), `division` (7: corchea con puntillo; 0 redonda, 1 blanca, 2 negra, 3 corchea, 4 semicorchea, 5 blanca con puntillo, 6 negra con puntillo, 8 negra de tresillo, 9 corchea de tresillo), `feedback` (35 %), `pingPong` (0), `lowCut` (120 Hz), `highCut` (7000 Hz), `mix` (25 %: 50 deja seco y eco a tope) |
| `rodaje.reverb` | `preDelay` (20 ms), `size` (50 %), `decay` (1,6 s de RT60), `damping` (7000 Hz), `width` (100 %), `lowCut` (120 Hz), `mix` (25 %) |
| `rodaje.pitch` | `semitones` (0, de −12 a 12), `cents` (0), `mix` (100 %) |
| `rodaje.limiter` | `ceiling` (−1 dBTP), `inputGain` (0 dB), `release` (60 ms) |

Los parámetros de estas inserciones no llevan keyframes (sí los del fader, y umbral, relación y mezcla del compresor).

### Organización de la línea de tiempo (formato 11)

Todo es opcional; un proyecto sin nada de esto se escribe igual que en el formato 10.

| Campo | Qué es |
|---|---|
| `clip.color` | Color de la etiqueta del clip, `#RRGGBB` (se guarda en mayúsculas). Sin él, el color de su tipo. |
| `clip.groupId` | Grupo del clip: los del mismo grupo se eligen y se mueven juntos, también en la misma pista. No se recortan ni se cortan juntos: eso es el enlace (`linkId`). Texto sin caracteres de control. |
| `track.height` | Alto de la pista en la línea de tiempo, en píxeles, de 24 a 320. Sin él, el de siempre. |

## Reglas que todo programa debe respetar

El esquema JSON describe la forma del archivo, pero hay reglas que relacionan unos datos con otros y que el esquema no puede expresar. Rodaje las comprueba todas al abrir un proyecto y lo rechaza con un mensaje que las enumera si alguna falla, así que un programa que escriba un `.rodaje` debe cumplirlas.

Todos los identificadores (`id`) del proyecto, de los medios, de las secuencias, de las pistas, de los clips, de los efectos, de los marcadores y de las regiones deben ser textos no vacíos, sin caracteres de control y únicos en todo el archivo, compartiendo un mismo espacio: un clip no puede llamarse igual que una pista. Rodaje genera identificadores del tipo `clip-3f2a…` y otros programas pueden usar cualquier texto, por ejemplo un UUID.

Cada medio debe tener una duración positiva. Cada secuencia debe tener ancho, alto y frecuencia de muestreo positivos, y una tasa de cuadros de 1/5 a 1000 por segundo: con un cuadro de siglos o de menos de un tick no se podría empujar ni recortar un cuadro (el selector de formato y la exportación piden de 1 a 1000, los límites que acepta Rodaje al leer un video). Ningún clip termina después de 2^62 ticks (4 611 686 018 427 387 904, unos 207 años): la otra mitad del rango de los enteros de 64 bits queda de margen para que mover, recortar o llevar el cursor al cuadro siguiente no desborde. Un archivo de la v1.0.0 fuera de estos límites se abre igual, con aviso: la tasa se acota al rango y el clip que pasa del final se recorta hasta él, salvo que empiece después del final o tenga velocidad: entonces se quita. El aviso dice cuántas tasas, clips recortados, clips quitados (con su nombre si es uno solo) y valores de efecto se acotaron, y el proyecto queda sin guardar. Una ruta de medio más larga de lo que admite Windows se conserva y el medio queda sin archivo. Los marcadores y las regiones no alargan la secuencia y pueden llegar al final del rango. Una pista de `videoTracks` debe ser de tipo `video` y una de `audioTracks` de tipo `audio`.

Cada clip debe apuntar con `mediaId` a un medio que exista en `media`, llevar `generator` o apuntar con `sequenceId` a otra secuencia del proyecto: exactamente una de las tres. El `sourceIn` de un clip de medio no puede ser negativo (el de un generador sí), su `sourceOut` debe ser mayor que su `sourceIn` y no puede pasar de la duración del medio, y su `start` no puede ser negativo. Un clip en una pista de video necesita un medio con imagen (`hasVideo`) y uno en una pista de audio, un medio con sonido (`hasAudio`). Dentro de una pista, los clips van ordenados por `start` y no se solapan: cada uno empieza en el final del anterior o después. Los huecos entre clips no se guardan; son simplemente el espacio que queda entre uno y el siguiente.

Cada bus tiene nombre y solo efectos de audio. El `output` y los `sends` de una pista de audio apuntan a buses de su secuencia, con como mucho un envío por bus; una pista de video no lleva `output`, `sends`, `solo` ni `armed`. Un marcador no puede estar antes del instante cero, y una región tampoco puede empezar antes y debe terminar después de empezar. Los keyframes de cada parámetro van en orden estrictamente creciente de `time`, sin dos en el mismo instante. Un mismo `linkId` puede repetirse en varias pistas, pero nunca en dos clips de la misma pista. Cada transición une dos clips contiguos de su propia pista (`to` es el siguiente de `from` y empieza justo donde este termina), cada corte tiene como mucho una y su duración es positiva y no pasa de la del más corto de sus clips. En los tipos registrados, cada parámetro (y cada keyframe) es un número finito dentro de los límites del registro: una opacidad de 2 o un ángulo de barrido de 720 se rechazan; los parámetros de tipos desconocidos no se miran. Un tipo registrado como transición no puede ir en una lista de efectos ni uno de efecto en `transitions`, y las opciones y los efectos de pista siguen las reglas de la sección anterior.

Además, el archivo debe ser JSON estricto: sin comentarios, sin comas sobrantes y sin el mismo campo repetido dentro de un objeto, porque no habría forma de saber cuál vale. Las listas y las `options` no pueden contener `null`, y los campos de texto, listas y objetos tampoco; los únicos campos que admiten `null` son `linkId`, el `frameRate` de un medio, `speed`, `duration`, `curve`, `mediaId`, `generator` y `sequenceId` en un clip, `role` y `output` en una pista y los campos opcionales del generador y su estilo (`style`, `box`, `words`, `width`, `widthFraction`, `source`, `name`, `stroke`, `shadow`, `background`, `highlight`, `in`, `out`); `null` vale lo mismo que no escribirlo.

Un campo que falta toma su valor por defecto: listas vacías, números en 0, `enabled` en verdadero, la secuencia en 1920 × 1080 a 30 fps y 48 kHz, la interpolación en `linear`. Los campos desconocidos se ignoran al leer, lo que permite que un archivo escrito por una versión futura con campos opcionales nuevos se abra en una anterior de la misma versión de formato, pero esos campos no se conservan cuando Rodaje vuelve a guardar. Por eso otro programa no debe guardar datos propios dentro del `.rodaje` esperando que sobrevivan a una edición en Rodaje; si necesita datos adicionales, debe guardarlos en un archivo aparte o proponer que se agreguen al formato en una nueva versión.

## Versiones y migraciones

`schemaVersion` es un entero que solo crece. Cuando el formato cambia de una manera que una versión anterior de Rodaje no podría leer correctamente, por ejemplo al agregar la velocidad de los clips, que cambia la relación entre la duración en la pista y el tramo usado del origen, se sube la versión. Al abrir un archivo, Rodaje lee primero ese número. Si falta, no es un entero o es menor que 1, el archivo no es un proyecto de Rodaje. Si es mayor que la versión que el programa conoce, el archivo fue creado con una versión más nueva y se pide actualizar el programa en lugar de abrirlo a medias. Si es menor, se aplican en cadena las migraciones de la versión 1 a la 2, de la 2 a la 3 y así hasta la actual, trabajando sobre el árbol JSON antes de convertirlo en objetos, y después se valida el resultado como cualquier otro proyecto. Cada versión tiene su propio esquema con el número en el nombre del archivo, de modo que `rodaje-proyecto.v1.schema.json` no cambia nunca una vez publicado. La migración de la 1 a la 2 agrega a cada secuencia las listas `markers` y `regions` vacías. La de la 2 a la 3 agrega a cada pista `effects` y `transitions` vacías y a cada efecto `options` vacío, y convierte la envolvente vieja `rodaje.volume` (ganancia lineal en `gain`) en `rodaje.audio` con `gainDb` = 20·log10(gain), keyframes incluidos; una ganancia de 0 o menos queda en −60 dB. La de la 3 a la 4 quita los efectos `rodaje.speed`, que estaban reservados y nunca se aplicaron; los clips quedan sin `speed`, a 1×. La de la 4 a la 5 solo cambia el número: un archivo del formato 4 no tiene generadores ni roles. La de la 5 a la 6 también solo cambia el número: un archivo del formato 5 no tiene clips compuestos, y un Rodaje del formato 5 rechaza el 6 con «actualiza el programa». La de la 6 a la 7 también: los campos nuevos de los keyframes (`ease`, `spatialIn`, `spatialOut`, las interpolaciones `bezier`, `bounce` y `elastic`) y los parámetros y opciones nuevos de `rodaje.transform` son opcionales, pero un Rodaje del formato 6 no los entendería. Un Rodaje del formato 4 rechaza el 5 con «actualiza el programa» en lugar de un error confuso sobre un clip sin medio. Una migración publicada tampoco se modifica: si tuviera un error se corrige con otra migración. La de la 7 a la 8 solo cambia el número: una máscara sin `combine` vale `intersect`, que es como se combinaban, y un Rodaje del formato 7 rechaza el 8 en lugar de dibujar mal las Bézier, los modos o las máscaras de un efecto. La de la 8 a la 9 también: los campos de los títulos profesionales son opcionales, pero un Rodaje del formato 8 dibujaría mal un título por letra o en arco. La de la 9 a la 10 deja sonando lo mismo con los efectos de audio en el orden de la pila: antes sonaban solo el primer ecualizador y el primer compresor activos, siempre el ecualizador antes, así que ese ecualizador pasa delante de ese compresor y los demás ecualizadores y compresores activos (que no sonaban) quedan desactivados. La de la 10 a la 11 solo cambia el número: el color, el grupo y el alto de pista son opcionales, pero un Rodaje del formato 10 perdería los grupos al guardar. La de la 11 a la 12 también solo cambia el número: la alineación, la curva, los tipos nuevos (también los de audio) y los parámetros nuevos del barrido y el empuje son opcionales, pero un Rodaje del formato 11 dibujaría como fundido cruzado una transición nueva y como centrada una alineada. La de la 12 a la 13 solo cambia el número: un archivo anterior no tiene seguimientos (`rodaje.track`) ni anclajes de esquinas (`rodaje.cornerpin`), y un Rodaje anterior rechaza el 13 en lugar de dibujar una capa sin su perspectiva.

Al volver a guardar, Rodaje siempre escribe la versión actual, así que un proyecto abierto y guardado con una versión nueva ya no se puede abrir con una anterior. La copia `.bak` descrita abajo conserva la versión previa del archivo.

## Guardado seguro y copia `.bak`

Un corte de luz a mitad de una escritura puede dejar un archivo cortado. Por eso Rodaje nunca escribe directamente sobre el proyecto. Primero escribe el contenido completo en un archivo temporal con el mismo nombre y la terminación `.tmp` (`Mi proyecto.rodaje.tmp`), en la misma carpeta, y le pide al sistema que lo lleve físicamente al disco antes de seguir. Solo entonces lo cambia por el proyecto. Si el proyecto ya existía, el cambio se hace con una operación de reemplazo de Windows que deja la versión anterior como `Mi proyecto.rodaje.bak`; si no existía, el temporal simplemente se renombra. Se corte cuando se corte, en disco queda la versión anterior completa o la nueva completa, nunca una mezcla.

Si alguna vez un proyecto no abre porque está dañado, la copia `.bak` contiene el guardado anterior: basta con hacer una copia de ella, quitarle la terminación `.bak` y abrirla. Un `.tmp` que quede en la carpeta es el resto de un guardado interrumpido; no hace falta borrarlo, porque el siguiente guardado lo reemplaza. Otros programas que escriban archivos `.rodaje` deberían seguir el mismo procedimiento.

En el formato 14, `MediaRef.isStillImage` distingue PNG, JPEG, WebP, BMP y TIFF de los videos. Su `duration` vale 0, `frameRate` es nulo y no tienen audio. Sus clips empiezan con 5 s por defecto; `sourceIn` y `sourceOut` describen un origen virtual sin límite de material (también permite entrada negativa al alargar por la izquierda). Los keyframes siguen ese tiempo virtual, pero la imagen siempre es el cuadro 0. La migración 13 → 14 solo cambia la versión: los medios anteriores conservan sus metadatos y el indicador nuevo vale falso.

En el formato 15, `Sequence.colorSpace` dice en qué espacio se trabaja y se exporta la secuencia. La migración 14 → 15 solo cambia la versión: una secuencia sin el campo es `sdr-709`, como eran todas; un Rodaje anterior rechaza el 15 en lugar de exportar una secuencia HDR como SDR sin avisar.
