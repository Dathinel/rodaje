# IA local

Toda la IA de Rodaje corre en tu equipo: nada se envía a internet. Cada función vive en un **módulo**, un programa aparte con sus modelos que se instala desde [Complementos](complementos.md) (sección «Módulos de IA») y que la distribución completa ya trae. Lo que un módulo propone se aplica como un paso normal del historial: **Ctrl+Z lo deshace**.

## Qué módulo hace qué

| Módulo | Funciones en la app | Modelos | Dónde corre |
|---|---|---|---|
| **Subtítulos automáticos** (`modulo-subtitulos`) | Subtítulos automáticos, con el tiempo de cada palabra | Whisper large-v3-turbo (550 MB) en GPU; Whisper small (180 MB) en CPU | GPU por Vulkan (unos 1,5 GB de VRAM) o CPU |
| **Traducir subtítulos** (`modulo-traducir`) | Traducir pista…, a 20 idiomas conservando los tiempos | Qwen3-4B-Instruct (unos 2,5 GB; se baja desde Complementos, no cabe en la distribución completa) | GPU por Vulkan (unos 3 GB de VRAM) o CPU; si no cabe entero en la GPU, reparte capas y si no, va a la CPU |
| **Texto a voz** (`modulo-voz`) | Texto a voz… | Kokoro-82M (170 MB) y 34 voces | CPU |
| **Audio con IA** (`rodaje-audio-ia`) | Reducir ruido; detección de voz para el ducking | DeepFilterNet3 (9 MB) y Silero VAD (2 MB) | CPU |
| **Visión** (`rodaje-vision`; en los diálogos, «Visión con IA») | Quitar fondo, Reencuadre automático | MODNet (26 MB), BiRefNet-lite (115 MB), YuNet (rostros) y RT-DETRv2 (personas, 81 MB) | GPU por DirectML (cualquier GPU con DirectX 12; unos 1,3 GB de VRAM, 2,8 GB en calidad alta) o CPU |
| **RAW de cine** (`rodaje-raw`) | Importar CinemaDNG… | Sin modelos (LibRaw) | CPU |

No usan módulos, aunque están en la misma barra: Quitar silencios, Detectar ritmo, Estabilizar, Color automático, Balance de blancos y Grabar voz. Funcionan siempre.

### GPU y VRAM

- Sin GPU, o si la GPU falla a mitad, cada módulo pasa a la CPU solo: tarda más, pero termina. Los subtítulos en CPU van unas 4 veces más rápido que el tiempo real.
- Rodaje reparte la memoria de video: los módulos pueden usar la VRAM dedicada menos tres octavos, que se reservan para la vista previa. Solo corre un módulo «pesado» (más de 1 GB) a la vez; los demás esperan con «Esperando memoria de video» en Trabajos.
- Mientras corre un módulo de GPU, la vista previa pasa a proxies.
- Un módulo que lleva 2 minutos sin trabajo se cierra solo y libera la memoria.
- Con 4 GB de VRAM todo funciona; la calidad «Alta» de Quitar fondo es lenta (unos 2 s por cuadro en una RTX 3050) y la traducción puede repartirse entre GPU y CPU.

## Dónde están

- Barra **Herramientas**, encima de la línea de tiempo: Quitar silencios, Detectar ritmo, Bajar música bajo la voz, Reducir ruido, Estabilizar, Color automático, Balance de blancos, Quitar fondo, Reencuadre, Subtítulos automáticos, Texto a voz, Añadir texto y Grabar voz. Cada botón actúa sobre lo seleccionado; si no aplica, se ve atenuado y su ayuda explica por qué.
- Menú **Editar**: Quitar silencios..., Detectar ritmo..., Ducking automático..., Reducir ruido..., Texto a voz...
- **Menú contextual del clip**: todo lo anterior más Estabilizar..., Color automático, Balance de blancos automático, Quitar fondo..., Reencuadre automático..., Subtítulos automáticos... y Traducir pista...
- Categoría **IA** de la biblioteca (riel izquierdo): los módulos y si están instalados.

Si falta el módulo, el botón lo dice: «Hace falta el módulo «Audio con IA». Instálalo desde Complementos.»

![Color automático en marcha, con el avance y Cancelar en la barra de avisos](img/accion-en-marcha.png)

Mientras una acción corre, su botón gira con el porcentaje y la barra de avisos muestra el avance y «Cancelar». Al terminar queda un aviso con **Deshacer**.

## Cada función

**Subtítulos automáticos.** Selecciona un clip con voz. Diálogo: «Idioma de la voz» (Detectar el idioma, Español, Inglés, Portugués), la lista de subtítulos con sus tiempos (un clic lleva el cursor), «Transcribir de nuevo» y «Aplicar». Deja clips de texto con el estilo «Subtítulo» en una pista de subtítulos (si no hay, crea «Subtítulos»), partidos a 42 caracteres por línea, dos líneas, de 1 a 7 s, cortando en pausas. Cada subtítulo guarda sus palabras con su hora: ver [Títulos y subtítulos](titulos-y-subtitulos.md). Límite: tres horas por clip.

**Traducir pista.** Clic derecho en un subtítulo › Traducir pista…: «Pista», «Idioma del original» (Detectar o uno de la lista) y «Traducir a». Crea una pista nueva encima, «<pista> (<idioma>)», con los mismos tiempos; la original no cambia. Idiomas: español, inglés, portugués (Brasil y Portugal), francés, italiano, alemán, neerlandés, catalán, gallego, euskera, polaco, ruso, ucraniano, turco, árabe, hindi, japonés, coreano y chino simplificado.

**Texto a voz.** Escribe o pega el texto («[pausa 1]» mete un silencio de 1 s) o usa «Usar el texto del clip seleccionado». Idioma (español de España o de Latinoamérica, portugués de Brasil, inglés de EE. UU. o del Reino Unido), voz (34, marcadas «(mujer)» u «(hombre)»), velocidad (0,5× a 2×), tono (±6 semitonos), pausa entre frases y en comas; «Escuchar» antes de «Insertar en el cursor». Deja un WAV en la carpeta de medios generados y un clip «Voz: …» en la primera pista de audio libre o en una nueva «Voz». Con el clip de voz seleccionado, el mismo diálogo ofrece «Regenerar».

**Reducir ruido.** Selecciona el clip; escucha «A: original» / «B: limpio», elige «Cuánto ruido se quita» (hasta 48 dB) y dónde se guarda el archivo limpio. Crea «<nombre> (sin ruido)» en la biblioteca y el clip pasa a usarlo; el original queda intacto. Límite: una hora.

**Bajar música bajo la voz** (ducking). Ver [Audio](audio.md#ducking-bajar-la-música-bajo-la-voz). Con «Audio con IA» instalado detecta la voz con Silero; sin él, por la energía del audio.

**Quitar fondo.** Calidad «Normal · personas, rápido (MODNet)» o «Alta · bordes finos y objetos, lento (BiRefNet)». «Empezar» analiza y «Aplicar» deja una máscara de imagen sobre el clip (un video en escala de grises en medios generados). Se ajusta en la pestaña Máscaras (invertir, intensidad).

**Reencuadre automático.** Formato «9:16 · vertical», «1:1 · cuadrado» o «4:5 · retrato»; «Seguir» rostros (YuNet) o personas de cuerpo entero (RT-DETRv2); «Suavidad». «Analizar» deja keyframes de posición y escala que siguen al sujeto y, si hace falta, cambia el formato de la secuencia.

**Importar CinemaDNG.** Archivo › Importar CinemaDNG...: elige un `.dng` de la secuencia, «Balance de blancos» (el de la toma o luz de día), «Exposición» (±3 EV) y «Calidad del revelado» (alta o rápida). Se guarda una copia ProRes 422 HQ en medios generados y entra en la biblioteca; los DNG no cambian.

## Si un módulo falla

Los avisos terminan siempre con «El proyecto no cambió.»:

- ««X» dejó de responder y se cerró.» Vuelve a intentarlo; si se repite, reinstala el módulo desde Complementos.
- ««X» se quedó sin memoria. Cierra otros programas o prueba con un tramo más corto.»
- ««X» necesita otra versión de Rodaje.» Actualiza Rodaje o el módulo.
