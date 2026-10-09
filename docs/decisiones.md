# Registro de decisiones

Este registro guarda el porqué de cada elección importante de Rodaje. Cada decisión sigue la misma forma: el contexto que la motivó, lo que se decidió, las alternativas que se consideraron y las consecuencias que se aceptan al elegirla. Una decisión no se borra cuando cambia; se marca como reemplazada y se añade una nueva que explique el cambio, para que el historial de razonamiento no se pierda.

---

## D-001. Nombre del programa: FotoToma (reemplazada por D-014)

**Fecha:** 2026-10-09 · **Estado:** aceptada, con verificación pendiente antes de publicar

**Contexto.** El nombre de trabajo inicial era "FotoShot". Una búsqueda básica mostró que ya existen varias aplicaciones de edición de fotos llamadas "PhotoShot" en tiendas de aplicaciones móviles (por ejemplo, las de Vyro AI y H-Tech Labs), y que "FotoShot" queda a una sola letra de distancia de Photoshop, marca registrada de Adobe. Un nombre tan cercano a una marca famosa y a productos existentes de la misma categoría es un riesgo innecesario si el proyecto se hace público algún día.

**Decisión.** El programa se llama FotoToma. Combina "foto" con "toma", la palabra que en español se usa para cada plano filmado, lo que describe bien un editor de video y suena natural en el idioma del proyecto. Una búsqueda web básica realizada el 9 de octubre de 2026 no encontró software llamado "FotoToma".

**Alternativas.** Mantener "FotoShot", descartado por la cercanía a marcas existentes; un nombre en inglés, descartado porque el proyecto se piensa y documenta en español.

**Consecuencias.** La búsqueda básica no equivale a una búsqueda de marcas. Antes de hacer público el repositorio hay que consultar las bases de la USPTO (Estados Unidos), la EUIPO (Unión Europea) y la Superintendencia de Industria y Comercio de Colombia en las clases de software (clase 9 de Niza) y servicios relacionados (clase 42). Este paso está en la lista de `docs/legal.md`.

---

## D-002. Repositorio privado con licencia MIT

**Fecha:** 2026-10-09 · **Estado:** aceptada

**Contexto.** Las reglas de la carpeta de programas indican que, sin otra indicación, cada repositorio es privado y usa MIT. Rodaje está en sus primeras fases y no tiene sentido exponerlo todavía, pero se quiere poder publicarlo más adelante sin rehacer nada.

**Decisión.** El repositorio `Dathinel/fototoma` es privado y el código propio se licencia bajo MIT, con el aviso de copyright a nombre de Daniel (Dathinel). Desde el principio se mantiene el fondo legal completo (`LICENCIAS.md`, `THIRD-PARTY-NOTICES.txt`, `docs/legal.md`) como si fuera público.

**Alternativas.** GPL-3, que habría permitido estudiar y reutilizar código de Kdenlive, Shotcut u Olive, pero obligaría a que cualquier distribución del programa fuera también GPL; Apache-2.0, que añade una licencia explícita de patentes pero es más larga y no aporta mucho a un proyecto personal; dejarlo sin licencia, que impediría cualquier uso por terceros.

**Consecuencias.** MIT es compatible con enlazar dinámicamente bibliotecas LGPL como FFmpeg, siempre que se cumplan sus condiciones. A cambio, no se puede copiar código GPL de los editores estudiados: solo se estudia su diseño (ver `docs/investigacion.md`).

---

## D-003. Stack: .NET 10 LTS con WinUI 3

**Fecha:** 2026-10-09 · **Estado:** aceptada

**Contexto.** Un editor de video necesita presentar en pantalla cuadros que ya están en la GPU sin copiarlos a la memoria del sistema y de vuelta, porque esas copias son las que destruyen la fluidez a resoluciones altas. También necesita una interfaz moderna en Windows, buena productividad para una sola persona y la posibilidad de compilar de antemano a código nativo.

**Decisión.** El programa se escribe en C# sobre .NET 10, que es versión de soporte extendido (LTS), con la interfaz en WinUI 3 a través del Windows App SDK. La razón decisiva es el control `SwapChainPanel`, que permite que una cadena de intercambio de Direct3D 11 se muestre directamente dentro de la interfaz, sin copias. Las partes críticas que las mediciones demuestren que C# no puede cubrir se escribirán en C++ y se llamarán desde .NET, pero solo cuando haya una medición que lo justifique.

**Alternativas.**

| Opción | A favor | En contra |
|---|---|---|
| WPF | Muy madura, mucha documentación | La integración con D3D11 pasa por `D3DImage`, pensado para D3D9, con copias y sincronización delicada |
| Avalonia | Multiplataforma, MIT | Presentación de superficies D3D11 sin copia menos directa; Rodaje solo apunta a Windows |
| C++ con Qt 6 | Rendimiento máximo, lo usan Kdenlive, Shotcut y Olive | Productividad menor para una persona; Qt comercial o LGPL con sus obligaciones |
| Rust | Seguridad de memoria, rendimiento | Ecosistema de interfaz en Windows inmaduro para una aplicación de esta escala |
| Python | Rapidez de prototipado, ecosistema de IA | Rendimiento insuficiente para composición y reproducción; empaquetado nativo pobre |

**Consecuencias.** El programa queda ligado a Windows, lo que está bien porque es su objetivo. Los binarios del Windows App SDK que se distribuyen por NuGet no están bajo MIT sino bajo los términos de licencia de software de Microsoft, que permiten redistribuirlos con la aplicación con ciertas condiciones; esto queda anotado en `LICENCIAS.md`.

---

## D-004. Entrega como ejecutable autocontenido con Native AOT

**Fecha:** 2026-10-09 · **Estado:** aceptada

**Contexto.** Las reglas de la carpeta piden que cada programa se entregue lo más parecido posible a un ejecutable nativo, sin instalador, en una carpeta `dist\` que se pueda copiar a otro PC.

**Decisión.** Rodaje se publica como aplicación autocontenida compilada con Native AOT, lo que produce código máquina sin JIT, arranque rápido y ninguna dependencia de un runtime de .NET instalado. El núcleo se marca como compatible con AOT desde el primer día (sin reflexión, con serialización JSON por generador de código). Si alguna biblioteca necesaria resulta incompatible con AOT, se recurre a ReadyToRun autocontenido, que precompila casi todo pero conserva el JIT como respaldo.

**Alternativas.** Publicación dependiente del framework, que exigiría instalar .NET en cada equipo; un instalador MSIX, que añade un paso y depende de componentes que AtlasOS puede no traer.

**Consecuencias.** Algunas bibliotecas basadas en reflexión no podrán usarse o necesitarán configuración extra. El runtime de .NET viaja dentro del ejecutable, así que su aviso de licencia MIT debe acompañar a `Rodaje.exe` (ver `THIRD-PARTY-NOTICES.txt`).

---

## D-005. Entorno propio en `.entorno\`

**Fecha:** 2026-10-09 · **Estado:** aceptada

**Contexto.** El equipo de desarrollo solo tiene los runtimes 6 y 8 de .NET, sin SDK. Instalar un SDK global mezclaría versiones entre programas y haría que la compilación dependiera del estado de la máquina.

**Decisión.** El SDK de .NET 10.0.401 se descarga dentro de `.entorno\dotnet` mediante `herramientas\preparar-entorno.ps1`, que comprueba la huella SHA-512 publicada por Microsoft antes de descomprimirlo. La versión queda fijada en `global.json`. La caché de paquetes NuGet también vive en `.entorno\`, y la restauración usa archivos de bloqueo. Solo `git` y `gh` son herramientas globales.

**Alternativas.** Instalar el SDK con winget de forma global, más sencillo pero contrario a la regla de entorno propio; usar un contenedor, excesivo para una aplicación de escritorio con interfaz.

**Consecuencias.** La primera ejecución descarga unos 280 MB. `.entorno\` nunca se versiona; lo que se versiona es el script que lo reconstruye.

---

## D-006. FFmpeg como DLL compartidas LGPL cargadas dinámicamente

**Fecha:** 2026-10-09 · **Estado:** aceptada (implementación prevista en fases posteriores)

**Contexto.** FFmpeg es la única opción realista para leer casi cualquier formato de video y audio. Su licencia depende de cómo se compile: con `--enable-gpl` o `--enable-nonfree` el resultado es GPL o no redistribuible, y eso es incompatible con un programa MIT. El FFmpeg 9.0 de gyan.dev instalado en el equipo de desarrollo es una compilación GPL.

**Decisión.** Rodaje usará las compilaciones `lgpl-shared` de BtbN (https://github.com/BtbN/FFmpeg-Builds), que excluyen bibliotecas GPL como libx264 y libx265 y entregan las bibliotecas `avcodec`, `avformat` y demás como DLL separadas. Esas compilaciones se configuran con `--enable-version3`, así que la licencia efectiva es LGPL versión 3 (el archivo de licencia que incluyen es `COPYING.LGPLv3`). Las DLL se cargan dinámicamente mediante FFmpeg.AutoGen, cuyas vinculaciones son MIT. La codificación H.264 y HEVC se hará con codificadores por hardware (NVENC) o con los de Media Foundation del sistema, nunca enlazando libx264.

**Alternativas.** Usar el FFmpeg GPL del sistema, imposible de redistribuir con MIT; compilar FFmpeg propio, más control pero mucho trabajo de mantenimiento; Media Foundation sola, que cubre menos formatos; libmpv, ver D-012.

**Consecuencias.** Hay que cumplir la lista de LGPL detallada en `docs/legal.md`: DLL sin renombrar, código fuente exacto o una oferta escrita, línea de configuración, créditos y ninguna prohibición de ingeniería inversa de esa parte. El FFmpeg de gyan.dev sigue siendo útil como herramienta de desarrollo, pero nunca se copia a `dist\`.

---

## D-007. Unidad de tiempo: ticks de 1/705 600 000 de segundo

**Fecha:** 2026-10-09 · **Estado:** aceptada

**Contexto.** Representar el tiempo en segundos con coma flotante acumula errores de redondeo, y representarlo en cuadros ata el proyecto a una sola cadencia. Mezclar material de 24, 25, 30 y 29,97 cuadros por segundo con audio de 44,1 y 48 kHz exige una unidad común exacta.

**Decisión.** Todo el tiempo del modelo es un entero de 64 bits que cuenta ticks de 1/705 600 000 de segundo, la misma idea de los "flicks". Ese número es divisible exactamente por las cadencias habituales de video, incluidas las de NTSC con su factor 1000/1001, y por las frecuencias de muestreo de audio comunes desde 8 kHz hasta 192 kHz. Un entero de 64 bits con esta unidad cubre más de 400 años.

**Alternativas.** Números racionales como Olive, exactos pero con más costo en cada operación y normalización de fracciones; segundos en `double`, sencillos pero inexactos; cuadros del proyecto, que no representan bien el audio.

**Consecuencias.** Las conversiones a cuadros y a muestras son divisiones enteras exactas. Una cadencia exótica que no divida al número exige redondear al tick más cercano, lo cual se documenta en el código.

---

## D-008. Modelo con posiciones explícitas, huecos implícitos y grupos de enlace

**Fecha:** 2026-10-09 · **Estado:** aceptada

**Contexto.** El formato de MLT da a cada clip una posición implícita, que hace difícil la colocación libre; el de OpenShot usa posiciones explícitas. Además, los clips de video y su audio deben poder moverse juntos, y los fotogramas clave deben comportarse bien al recortar.

**Decisión.** Cada clip guarda su posición de inicio en la línea de tiempo; el espacio entre clips no es un objeto. Los clips que deben moverse juntos, como un video y su audio, comparten un grupo de enlace. Los fotogramas clave se expresan en tiempo de la fuente, no en tiempo del clip ni de la línea de tiempo, de modo que al recortar el inicio de un clip la animación sigue pegada al mismo contenido en vez de desplazarse respecto a él.

**Alternativas.** Posición implícita al estilo MLT; fotogramas clave relativos al inicio del clip, que se desplazan al recortar y sorprenden al usuario.

**Consecuencias.** El rizado es una operación explícita que mueve los clips posteriores. El validador del modelo debe comprobar que no haya solapamientos dentro de una pista.

---

## D-009. Deshacer con instantáneas completas de la secuencia

**Fecha:** 2026-10-09 · **Estado:** aceptada

**Contexto.** Los deshaceres basados en operaciones inversas escritas a mano, o en diferencias que guardan el valor anterior, fallan de forma sutil cuando alguna operación olvida registrar algo. Kdenlive y Shotcut dedican mucho código a este problema.

**Decisión.** Cada operación es un comando tipado que, antes de ejecutarse, guarda una instantánea completa (una copia profunda de las pistas y sus clips) de la secuencia afectada. Deshacer es restaurar esa instantánea. Si el comando falla a mitad de camino, la instantánea se restaura automáticamente. Los comandos que forman parte de un gesto continuo, como un arrastre, llevan una clave de fusión, y los comandos consecutivos con la misma clave se fusionan en un solo paso del historial.

**Alternativas.** Lambdas de hacer y deshacer como Kdenlive; comandos con inversa manual como Shotcut; diferencias de JSON como OpenShot.

**Consecuencias.** El costo es memoria: una secuencia de miles de clips ocupa algo en cada paso del historial. El modelo es mutable, así que cada instantánea copia todos los clips de la secuencia; con cientos de clips eso son kilobytes por paso y el historial tiene un límite configurable (500 pasos por defecto). Si una medición con proyectos grandes muestra que pesa demasiado, el siguiente paso es guardar solo las pistas que la operación tocó.

---

## D-010. Formato JSON con `schemaVersion` entero y guardado atómico

**Fecha:** 2026-10-09 · **Estado:** aceptada

**Contexto.** El formato del proyecto va a cambiar muchas veces. OpenShot sufrió un error al comparar versiones como cadenas, y Olive acumuló un serializador completo por versión. El portátil de desarrollo ha tenido apagados inesperados, y un apagado durante el guardado no puede dejar un proyecto corrupto.

**Decisión.** El proyecto se guarda en JSON legible con un campo `schemaVersion` entero. Al abrir un archivo antiguo se aplican en cadena las migraciones de cada versión a la siguiente, cada una con sus pruebas y archivos de ejemplo. El guardado escribe primero un archivo temporal, lo vacía a disco, conserva la versión anterior como `.bak` y luego reemplaza el original en una operación atómica del sistema de archivos.

**Alternativas.** Un formato binario, más compacto pero opaco y difícil de depurar; SQLite, robusto pero excesivo para un documento que se carga entero en memoria.

**Consecuencias.** Un archivo de una versión más nueva que la del programa se rechaza con un mensaje claro en lugar de abrirse a medias.

---

## D-011. Identificadores en inglés, documentación y mensajes en español

**Fecha:** 2026-10-09 · **Estado:** aceptada

**Contexto.** El proyecto se piensa en español, pero el ecosistema de .NET, las bibliotecas y los ejemplos están en inglés, y mezclar idiomas en los nombres de tipos y métodos hace el código más difícil de leer junto a las API del sistema.

**Decisión.** Los identificadores del código (tipos, métodos, propiedades, campos del JSON) se escriben en inglés. La documentación, los comentarios explicativos, los mensajes de error para el usuario, los mensajes de commit y la interfaz se escriben en español.

**Alternativas.** Todo en español, que choca con las API de .NET; todo en inglés, que va contra la forma de trabajo del proyecto.

**Consecuencias.** Si la interfaz se traduce algún día, los textos ya estarán separados del código.

---

## D-012. libmpv solo como visor opcional de fuentes

**Fecha:** 2026-10-09 · **Estado:** aceptada

**Contexto.** libmpv es un reproductor excelente, con decodificación por hardware muy probada, y sería tentador usarlo para la vista previa. Sin embargo, reproduce un archivo, no compone varias pistas con efectos y transiciones. Además, sus compilaciones habituales para Windows suelen incluir componentes GPL.

**Decisión.** La vista previa de la línea de tiempo usa el compositor propio de Rodaje. libmpv solo podría usarse, de forma opcional, como visor de un archivo de origen individual, y únicamente con una compilación LGPL verificada.

**Alternativas.** Usar libmpv como motor de vista previa, imposible para la composición multipista.

**Consecuencias.** La reproducción fluida depende del motor propio, lo que concentra el trabajo de rendimiento en un solo lugar.

## D-013. Interop nativo para la reproducción (9 de octubre de 2026)

**Decisión.**
- FFmpeg se usa con `FFmpeg.AutoGen.Bindings.DynamicallyLinked` 9.0.1.1. Las DLL se cargan por ruta absoluta con `NativeLibrary.SetDllImportResolver`, para no tomar nunca una FFmpeg GPL del PATH.
- Direct3D 11 y DXGI se usan con `TerraFX.Interop.Windows`.
- El audio sale por un WASAPI propio con `[GeneratedComInterface]`, y el reloj maestro es `IAudioClock::GetPosition` interpolado con QPC.

**Alternativas.**
- Vortice: funciona, pero SharpGen produce las advertencias IL2067 e IL2072 al compilar con AOT.
- XAudio2: su posición avanza en saltos de 10 ms, insuficiente para el reloj maestro.
- NAudio: es compatible con AOT, pero no da marca de tiempo QPC.

**Consecuencias.** La API de TerraFX trabaja con punteros crudos, así que el motor la envuelve en clases propias. Las evidencias están en `docs/sondeo-interop-aot.md`.

## D-014. Nuevo nombre: Rodaje (9 de octubre de 2026, reemplaza a D-001)

**Contexto.** «FotoToma» no sonaba a editor de video.

**Decisión.** El programa pasa a llamarse **Rodaje**. Eso cambia:
- el repositorio, que pasa a `Dathinel/rodaje`;
- los espacios de nombres, que pasan a `Rodaje.*`;
- la extensión de proyecto, que pasa a `.rodaje`;
- las variables de entorno, que pasan a `RODAJE_*`.

Los paquetes de contenido usan la extensión neutral `.paquete`, porque el formato se comparte con otros programas.

**Alternativas.**
- Claqueta: descartada por parecerse a «Claquette», un editor de video para Mac y Windows que ya existe.
- Montaje: descartada por parecerse a «Montaj», un editor para Mac e iPhone que ya existe.
- Fotograma y Encuadre: no tenían conflictos, pero Daniel eligió Rodaje.

**Consecuencias.** Antes de hacer público el repositorio hay que repetir la búsqueda de marcas en USPTO, EUIPO y SIC Colombia.
