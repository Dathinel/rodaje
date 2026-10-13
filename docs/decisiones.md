# Registro de decisiones

Este registro guarda el porqué de cada elección importante de Rodaje. Una decisión no se borra cuando cambia: se marca como sustituida y otra explica el cambio.

**Estado.** Vigentes: D-003 a D-014, D-016 a D-029 y D-031 a D-043 (D-017 la amplía D-019; D-028 la corrige D-033 en la parte de 9:16; D-029.1 la amplía D-039). Sustituidas: D-001 (por D-014), D-002 en la licencia y D-030 (por D-032), D-015 en el visor (por D-022) y en la caché (por D-017).

---

## D-001. Nombre: FotoToma (9 de octubre de 2026). Sustituida por D-014

El nombre de trabajo «FotoShot» quedaba a una letra de Photoshop y ya existían apps llamadas «PhotoShot»; pasó a FotoToma, que después se cambió por Rodaje.

## D-002. Repositorio propio y licencia inicial MIT (9 de octubre de 2026). Licencia sustituida por D-032

- **Decisión.** Repositorio `Dathinel/rodaje` con el fondo legal completo desde el principio (`LICENCIAS.md`, `THIRD-PARTY-NOTICES.txt`, `docs/legal.md`). La licencia inicial fue MIT; nunca se publicó con ella.
- **Vigente.** El código de proyectos GPL ajenos (Kdenlive, Shotcut, Olive) solo se estudia, nunca se copia ([legal.md](legal.md), «Sala limpia»).

## D-003. Stack: .NET 10 LTS con WinUI 3 (9 de octubre de 2026)

- **Decisión.** C# sobre .NET 10 (LTS) con interfaz WinUI 3 (Windows App SDK). Lo decisivo es `SwapChainPanel`, que muestra una cadena de intercambio de Direct3D 11 dentro de la interfaz sin copias. Solo se escribe C++ donde una medición demuestre que C# no alcanza.
- **Por qué.** WPF integra D3D11 mediante `D3DImage` (pensado para D3D9, con copias); Avalonia presenta D3D11 de forma menos directa y su ventaja multiplataforma no aplica; C++ con Qt 6 rinde más, pero es más lento de escribir para una sola persona y trae las obligaciones de Qt; Rust tiene un ecosistema de interfaz inmaduro en Windows; Python no da el rendimiento ni el empaquetado nativo.
- **Consecuencia.** El programa queda ligado a Windows. Los binarios del Windows App SDK se redistribuyen bajo los términos de licencia de Microsoft, no bajo la GPLv3 ([legal.md](legal.md), punto b).

## D-004. Entrega autocontenida con Native AOT (9 de octubre de 2026)

- **Decisión.** Rodaje se publica autocontenido con Native AOT: código máquina, arranque rápido y sin runtime de .NET instalado. El núcleo es compatible con AOT (sin reflexión, JSON por generador de código). Si una biblioteca necesaria no lo es, se recurre a ReadyToRun autocontenido.
- **Por qué.** Se busca un ejecutable que funcione al copiarlo, sin instalar nada. La publicación dependiente del framework exige instalar .NET; MSIX necesita firma de confianza o modo desarrollador.
- **Consecuencia.** Algunas bibliotecas con reflexión quedan fuera o necesitan configuración. El aviso del runtime de .NET viaja con `Rodaje.exe` (`THIRD-PARTY-NOTICES.txt`). La v1 se entrega como zip portable y, además, con un instalador por usuario opcional ([arranque-y-distribucion.md](arranque-y-distribucion.md)).

## D-005. Entorno propio en `.entorno\` (9 de octubre de 2026)

- **Decisión.** El SDK de .NET 10.0.401 se descarga a `.entorno\dotnet` con `build\preparar-entorno.ps1`, que verifica el SHA-512 publicado por Microsoft. La versión se fija en `global.json`; la caché de NuGet también vive en `.entorno\` y la restauración usa archivos de bloqueo. Solo `git` y `gh` son globales.
- **Por qué.** Un SDK global mezclaría versiones entre programas. Un contenedor sería excesivo para una aplicación de escritorio.
- **Consecuencia.** La primera ejecución descarga unos 280 MB. `.entorno\` no se versiona; se versiona el script que lo reconstruye.

## D-006. FFmpeg como DLL compartidas LGPL cargadas dinámicamente (9 de octubre de 2026)

- **Decisión.** Se usan las compilaciones `lgpl-shared` de BtbN (https://github.com/BtbN/FFmpeg-Builds), sin libx264 ni libx265 y con `--enable-version3`: la licencia efectiva es LGPL v3. Las DLL se cargan con FFmpeg.AutoGen (MIT). H.264 y HEVC se codifican por GPU o con Media Foundation, nunca con libx264.
- **Por qué.** FFmpeg es la única opción realista para leer casi cualquier formato. Una compilación GPL sería compatible con la GPLv3 de Rodaje, pero metería código GPL ajeno en la distribución (lo que impide conceder el permiso para complementos sobre esa parte, D-032) y los codificadores x264 y x265, con su exposición a patentes. Compilar uno propio cuesta mantenimiento; Media Foundation sola cubre menos formatos; libmpv, ver D-012.
- **Consecuencia.** Hay que cumplir la lista LGPL de [legal.md](legal.md): DLL sin renombrar, fuente exacta u oferta escrita, línea de configuración, créditos y sin prohibir la ingeniería inversa de esa parte. El FFmpeg GPL de gyan.dev es solo herramienta de desarrollo y nunca va a `dist\`.

## D-007. Unidad de tiempo: ticks de 1/705 600 000 s (9 de octubre de 2026)

- **Decisión.** Todo el tiempo del modelo es un entero de 64 bits en ticks de 1/705 600 000 s (la idea de los «flicks»), divisible por las cadencias habituales, incluidas las NTSC (1000/1001), y por las frecuencias de audio de 8 a 192 kHz. Cubre más de 400 años.
- **Por qué.** `double` acumula errores; los cuadros atan el proyecto a una cadencia y no representan bien el audio; los racionales de Olive son exactos pero cuestan más en cada operación.
- **Consecuencia.** Convertir a cuadros y muestras son divisiones enteras exactas. Una cadencia exótica se redondea al tick más cercano, documentado en el código.

## D-008. Posiciones explícitas, huecos implícitos y grupos de enlace (9 de octubre de 2026)

- **Decisión.** Cada clip guarda su inicio en la línea de tiempo y el espacio entre clips no es un objeto. Los clips que se mueven juntos (video y su audio) comparten un grupo de enlace. Los fotogramas clave van en tiempo de la fuente, así que recortar el inicio no desplaza la animación respecto al contenido.
- **Por qué.** La posición implícita de MLT dificulta la colocación libre (OpenShot usa posiciones explícitas), y los fotogramas clave relativos al clip se desplazan al recortar y sorprenden al usuario.
- **Consecuencia.** El ripple es una operación explícita que mueve los clips posteriores, y el validador comprueba que no haya solapamientos dentro de una pista.

## D-009. Deshacer con instantáneas completas de la secuencia (9 de octubre de 2026)

- **Decisión.** Cada operación es un comando tipado que guarda una copia profunda de la secuencia antes de ejecutarse; deshacer la restaura, y un fallo a mitad de camino también. Los comandos de un gesto continuo llevan una clave de fusión y se funden en un solo paso.
- **Por qué.** Las inversas escritas a mano (Shotcut), las lambdas de hacer y deshacer (Kdenlive) o las diferencias de JSON (OpenShot) fallan de forma sutil cuando una operación olvida registrar algo.
- **Consecuencia.** Cuesta memoria: kilobytes por paso con cientos de clips, con un límite configurable (500 pasos por omisión). Si pesa demasiado con proyectos grandes, el siguiente paso es copiar solo las pistas que la operación tocó.

## D-010. JSON con `schemaVersion` entero y guardado atómico (9 de octubre de 2026)

- **Decisión.** El proyecto es JSON legible con `schemaVersion` entero; al abrir uno antiguo se encadenan migraciones de versión en versión, cada una con pruebas y archivos de ejemplo. El guardado escribe un temporal, lo vacía a disco, conserva el anterior como `.bak` y reemplaza el original de forma atómica.
- **Por qué.** OpenShot sufrió por comparar versiones como cadenas y Olive acumuló un serializador por versión; un corte de luz no debe dejar un proyecto a medias. Un binario sería opaco y SQLite excesivo para un documento que se carga entero.
- **Consecuencia.** Un archivo de una versión más nueva que el programa se rechaza con un mensaje claro en vez de abrirse a medias ([formato-proyecto.md](formato-proyecto.md)).

## D-011. Identificadores en inglés, documentación y mensajes en español (9 de octubre de 2026)

- **Decisión.** Tipos, métodos, propiedades y campos del JSON en inglés; documentación, comentarios, mensajes al usuario, commits e interfaz en español.
- **Por qué.** El ecosistema de .NET está en inglés y mezclar idiomas en los nombres dificulta leer el código junto a sus API; el proyecto se piensa y se usa en español.
- **Consecuencia.** Si la interfaz se traduce algún día, los textos ya están separados del código.

## D-012. libmpv solo como visor opcional de fuentes (9 de octubre de 2026)

- **Decisión.** La vista previa de la línea de tiempo usa el compositor propio. libmpv solo podría usarse, opcionalmente, para ver un archivo de origen suelto y con una compilación LGPL verificada.
- **Por qué.** libmpv reproduce un archivo, no compone pistas con efectos y transiciones, y sus compilaciones habituales para Windows suelen incluir componentes GPL.
- **Consecuencia.** El trabajo de rendimiento de la reproducción se concentra en el motor propio.

## D-013. Interop nativo para la reproducción (9 de octubre de 2026)

- **Decisión.**
  - FFmpeg se usa con `FFmpeg.AutoGen.Bindings.DynamicallyLinked` 9.0.1.1. Las DLL se cargan por ruta absoluta con `NativeLibrary.SetDllImportResolver`, para no tomar nunca una FFmpeg GPL del PATH.
  - Direct3D 11 y DXGI se usan con `TerraFX.Interop.Windows`.
  - El audio sale por un WASAPI propio con `[GeneratedComInterface]`, y el reloj maestro es `IAudioClock::GetPosition` interpolado con QPC.
- **Alternativas.** Vortice funciona, pero SharpGen produce las advertencias IL2067 e IL2072 con AOT. La posición de XAudio2 avanza en saltos de 10 ms, insuficiente para el reloj maestro. NAudio es compatible con AOT, pero no da marca de tiempo QPC.
- **Consecuencia.** La API de TerraFX trabaja con punteros crudos, así que el motor la envuelve en clases propias.

## D-014. Nombre: Rodaje (9 de octubre de 2026; sustituye a D-001)

- **Contexto.** «FotoToma» no sonaba a editor de video.
- **Decisión.** El programa se llama **Rodaje**: repositorio `Dathinel/rodaje`, espacios de nombres `Rodaje.*`, extensión de proyecto `.rodaje` y variables de entorno `RODAJE_*`. Los paquetes de contenido usan la extensión neutral `.paquete`, porque el formato se comparte con otros programas.
- **Alternativas.** Claqueta se parece a «Claquette», un editor de video para Mac y Windows. Montaje se parece a «Montaj», un editor para Mac e iPhone. Fotograma y Encuadre no tenían conflictos, pero se prefirió Rodaje.
- **Consecuencia.** El nombre está reservado en `TRADEMARK.md`, pero no registrado; la búsqueda de marcas y el registro están en [pendientes-legales.md](pendientes-legales.md).

## D-015. Conversión de color en shader y caché de 2 × adelanto (9 de octubre de 2026). Visor sustituido por D-022; caché, por D-017

- **Decisión vigente.** La conversión NV12/P010 a RGB (BT.601, BT.709 o BT.2020, rango limitado o completo según declare el cuadro o el flujo; sin declarar, BT.601 hasta 576 líneas y BT.709 por encima, y rango completo solo en los formatos `yuvj`) se hace en un pixel shader que lee la capa del arreglo de texturas del decodificador con dos vistas (Y y UV), sin copias. El shader se compila al arrancar con `d3dcompiler_47.dll`, que viene con Windows desde 8.1: compilar Rodaje no depende del Windows SDK, cuesta unos milisegundos al abrir y es compatible con Native AOT.
- **Audio.** Se decodifica por delante en un hilo propio (`BufferedAudioSource`), para que el hilo de WASAPI solo copie memoria; si algo llega tarde se descarta en vez de sonar desplazado.
- **Histórico.** La ventana Win32 de prueba se retiró (D-022). La caché LRU necesitaba al menos `2 × adelanto + 2` cuadros para no expulsar lo adelantado antes de mostrarlo (59 seeks y 1733 cuadros decodificados en 10 s de un H.264 a 23,976 fps, frente a 4 y 280 después); la caché por urgencia de D-017 lo corrigió de fondo.
- **Consecuencia.** HDR (BT.2020, PQ, HLG) queda fuera.

## D-016. Compositor: `Rodaje.Render` con destino RGBA16F y mezcla en el espacio del video (9 de octubre de 2026)

- **Contexto.** Hace falta componer varias capas por cuadro a tamaño de secuencia, para presentarlas, leerlas como miniaturas o codificarlas.
- **Decisión.**
  - `Rodaje.Render` (sobre `Rodaje.Media` y `Rodaje.Playback`) tiene `Compositor`, `Presenter` y `GpuTimer`, además de `YuvRange`, `YuvToRgb`, `VideoLayout` y `ShaderCompiler`.
  - Cada capa es un cuadrilátero con cuatro vértices calculados en la CPU (`LayerLayout`: ajuste «contain» y después escala, rotación horaria y desplazamiento en píxeles de la secuencia). El pixel shader convierte Y'CbCr con la matriz y el rango de la capa, recorta el relleno del decodificador y mezcla con «over» premultiplicado.
  - Los bordes se suavizan con la distancia al lado en píxeles de destino, con el cuadrilátero un píxel más grande: un borde alineado queda nítido y uno girado no muestra escalones, sin MSAA.
  - El destino es `R16G16B16A16_FLOAT` por defecto: conserva los 10 bits de P010, no redondea a 8 bits en cada capa translúcida y deja lugar a la mezcla en luz lineal. `Rgba8Unorm` queda como opción.
  - La mezcla se hace sobre los valores con la curva del video, no en luz lineal: una opacidad de 0,5 promedia los códigos, como en la mayoría de editores en 8 bits.
  - El muestreo es bilineal sobre el plano del decodificador, sin mipmaps; el presentador genera los mipmaps de la composición cuando la ventana es más pequeña.
  - La relación de aspecto del píxel y la rotación de los metadatos llegan por `SourceGeometry`, porque `LayerFrame` todavía no las trae.
- **Alternativas.** RGBA8 usa la mitad de memoria y unos 0,15 ms menos con 8 capas a 1080p, pero pierde precisión con P010 y con capas translúcidas apiladas. La mezcla en luz lineal es más correcta físicamente, pero cambia el aspecto que el usuario espera; se deja para los modos de fusión. MSAA cuadruplica la memoria del destino y no resuelve el aliasing de la textura.
- **Consecuencias.**
  - Cifras en una RTX 3050 Laptop, en el peor caso de capas NV12 de 1080p a pantalla completa (mediana de 200 cuadros):

    | Capas | RGBA16F | RGBA8 |
    |---|---|---|
    | 1 | 0,10 ms | 0,05 ms |
    | 2 | 0,19 ms | 0,10 ms |
    | 4 | 0,36 ms | 0,19 ms |
    | 8 | 0,53 ms | 0,37 ms |

    Incluso con 8 capas, componer usa menos del 4 % de los 16,7 ms de un cuadro a 60 fps. Con efectos el camino rápido no cambia (8 capas 1080p: 0,54 ms), y 3 capas 4K con color, LUT, desenfoque y superponer cuestan 2,7 ms.
  - Reducir una capa a menos de la mitad produce aliasing, hasta que haya proxies o un filtro de varias muestras.
  - Cuando el motor ponga el SAR y la rotación en `LayerFrame`, el parámetro `geometry` de `Compositor.Render` se puede retirar.

## D-017. Motor de secuencia: `TimelineSession` con caché por urgencia y pre-roll por pista (9 de octubre de 2026; ampliada por D-019)

- **Decisión.**
  - `TimelineSession` (`src/Rodaje.Engine/Timeline`) reproduce una secuencia completa a partir de una instantánea inmutable (`TimelinePlan`) que se reconstruye con `Reload`/`Invalidate` sin mover ni detener el reloj. El reloj maestro es el dispositivo de audio, que suena la mezcla de todas las pistas.
  - Los decodificadores se abren en su propio hilo la primera vez que hacen falta y se cierran tras un tiempo sin uso (D-019 permite varios por medio).
  - Una sola caché de cuadros con presupuesto global y tope por origen. Expulsa primero lo que ninguna capa necesita y después lo que se mostrará más tarde; la urgencia es el tiempo que falta para mostrar cada cuadro, así que cerca de un corte el pre-roll le gana al adelanto lejano.
  - Pre-roll: 0,5 s antes de cada corte se decodifican los primeros 6 cuadros del clip siguiente de la pista.
  - El audio se mezcla por delante en un hilo (`AudioMixer` sobre `BufferedAudioSource`), exacto a la muestra en los bordes de los clips, con un limitador suave (identidad hasta 0,9 y tanh hasta 1). Tras una edición se conservan 50 ms ya mezclados y el resto se vuelve a mezclar.
- **Alternativas.** Un `PlaybackScheduler` por capa reutiliza código, pero no permite un presupuesto global ni repartir un decodificador entre capas.
- **Consecuencia.** Con un presupuesto muy justo la caché degrada a cuadros tarde, nunca a decodificar sin fin.

## D-018. Dirección de la interfaz (9 de octubre de 2026)

- **Decisión.** La interfaz sigue la maqueta aprobada:
  - **Distribución:** riel de íconos con biblioteca a la izquierda, reproductor al centro, propiedades a la derecha y línea de tiempo abajo (al estilo de CapCut).
  - **Pista principal magnética**, que se puede desactivar.
  - **Barra de la línea de tiempo** (al estilo de Vegas): herramientas normal, envolvente, selección y zoom; ajuste; ripple automático con tres modos; fundido cruzado automático; bloquear envolventes e ignorar grupos; cortar y recortar inicio o final en el cursor; marcadores y regiones.
  - **Inspector como editor de keyframes:** cada parámetro tiene un rombo y una curva, y «Guardar como transición» la deja lista para publicarla como paquete.
  - **Atajos:** una página con búsqueda, cambio de tecla con un clic, intercambio cuando hay conflicto y varios estilos (nombres en D-023).
  - **Gestor de complementos:** paquetes y módulos de IA, cada uno en su propio repositorio de `Dathinel`, con los programas en los que funciona.
  - **Estética:** paleta grafito, acento naranja, esquinas casi rectas (2 px) y transiciones de 60 ms para que se sienta rápida.
- **Por qué.** La facilidad de un editor de consumo con un toque profesional, y extras que se instalen aparte y sirvan a varios programas.

## D-019. Presupuesto de VRAM, varios decodificadores por medio y proxies (10 de octubre de 2026)

- **Contexto.** Con cuatro pistas 4K HEVC y cortes cada 3 s se midieron 3,7 GB de VRAM en una GPU de 4 GB: cada decodificador reservaba unas 52 superficies, la caché de 512 MB vivía dentro de esos pools y no acotaba nada, y los decodificadores viejos seguían abiertos 5 s. Un mismo archivo en varias pistas compartía un decodificador que iba y venía con seeks.
- **Decisión.**
  - Un presupuesto de VRAM para los pools de D3D11VA (3/8 de la VRAM dedicada: 1,5 GB en 4 GB; configurable). Cada pool cuenta sus referencias, 5 superficies retenidas fuera de la caché, 3 de búferes de NVDEC y su caché. El tamaño de la caché de cada decodificador se decide al crear su pool, repartiendo el presupuesto entre una capa visible y dos de pre-roll.
  - Referencias según el nivel del flujo (H.264 anexo A, HEVC A.4.2), no las 16 del peor caso: en 4K HEVC nivel 5.1 el pool baja de 17 a 7 superficies de referencia.
  - Para abrir un decodificador se cierran primero los que nadie usa. El pre-roll y las pistas ocultas no pasan del presupuesto; una capa visible sin decodificador sí, y se cuenta. Con muchos abiertos, el tiempo de inactividad baja a la cuarta parte.
  - Hasta 4 decodificadores por medio: las ventanas de un medio se agrupan en tramos y cada tramo va al decodificador que quedó más cerca. La caché es por medio (un cuadro sirve a cualquier capa).
  - Un cuadro adelantado que no cupo frena el adelanto de ese tramo, porque decodificar los siguientes obligaba a volver con un seek.
  - Cerrar un decodificador ocurre en un ejecutor de servicio: el hilo de la ventana nunca espera.
  - Un medio que no abre deja su capa vacía en vez de congelar el último cuadro del clip anterior, y se informa por capa.
  - La vista previa usa proxies (`UseProxies`: Auto, Always, Never) a través de `MediaSourceResolver`; cuando un proxy queda listo, la reproducción pasa a él sin cortes.
  - `PlaneViewCache` suelta las vistas de un decodificador cerrado en el cuadro siguiente y las que llevan 2 s sin usarse.
- **Resultado** (RTX 3050 Laptop, cuatro pistas a 60 Hz durante 24 s, seis archivos HEVC con cortes cada 3 s): en 4K el pico de VRAM pasó de 3708 a 1417 MB (presupuesto 1485), los refrescos tarde de 359 a 14 y la peor espera de una capa de 483 a 134 ms, con 89 % de pre-roll a tiempo; en 1080p, 100 % de pre-roll y 0 tarde con 1003 MB. Un archivo en cuatro pistas con 2 s de desfase pasó de 3285 refrescos tarde a 0.
- **Consecuencia.** En 4K el presupuesto deja cuatro capas y dos pre-roll: si en un mismo corte entran más medios nuevos, alguno llega un poco tarde. Los saltos de más de 50 ms con cuatro capas 4K vienen de la GPU repartida entre cuatro flujos de NVDEC, no del motor; para eso están los proxies.

## D-020. Línea de tiempo: marcadores en el formato, gestos que se funden y dibujo con XAML reciclado (10 de octubre de 2026)

- **Decisión.**
  - **Formato 2.** Las secuencias tienen `markers` y `regions`. El modo de ripple «pistas, marcadores y regiones» es `RippleScope.AllTracksAndMarkers`: lo que cae dentro de un tramo que se cierra queda en su inicio y una región vacía se borra.
  - **Gestos.** Dentro de un grupo del historial los comandos con la misma clave se funden: un arrastre ejecuta un paso por movimiento para que el reproductor lo vea en vivo, pero guarda una sola copia antes y otra después.
  - **Pista principal magnética.** Reordenar es `ReorderOnMainTrack`; entrar o salir de ella abre o cierra el hueco también en las pistas de los enlazados, así el audio no se desincroniza.
  - **Dibujo.** Elementos de XAML reciclados en una ventana de tres anchos de pantalla, no Win2D ni una cadena de intercambio propia: es apto para AOT, no suma dependencias y deja el texto y la accesibilidad a WinUI.
- **Por qué.** Arrastrar debe sentirse inmediato y deshacerse de una vez; el ripple con marcadores necesitaba guardarlos en el formato.
- **Consecuencia.** Desplazarse va a 60 cuadros por segundo con 200 clips; el zoom continuo queda en unos 45. Si hiciera falta más, el siguiente paso es dibujar las pistas con `SpriteVisual` de composición, como los medidores.

## D-021. Miniaturas en la CPU y formato `.paquete` (10 de octubre de 2026)

- **Decisión.**
  - **Miniaturas por software.** Un `ThumbnailDecoder` propio en la CPU, sin filtro de desbloqueo y saltándose los cuadros que no son referencia, con swscale a BGRA. En la GPU gastarían superficies del presupuesto de VRAM (D-019); en la CPU una miniatura 1080p sale en unos 50 ms, que alcanza.
  - **No exactas al cuadro.** Una miniatura muestra el último cuadro decodificado que no pasa del instante; la exactitud es cosa del reproductor.
  - **Rejilla de potencias de dos** para los instantes de una tira y **clases de alto** (32 a 256 px): al hacer zoom o desplazar se reutiliza casi todo.
  - **Caché en memoria y en disco** con la huella del original en la clave; un archivo que cambia no reutiliza nada viejo.
  - **`IClipVisuals` no bloquea nunca:** devuelve lo que hay, pide lo que falta y avisa con `Changed` en el hilo de la interfaz.
  - **`.paquete`** en una biblioteca sin dependencias (`Rodaje.Packages`), con manifiesto validado con `JsonDocument` (apto para AOT), instalación que nunca escribe fuera de su carpeta y referencias estables `paquete@versión/elemento` ([formato](formato-paquete.md)). Los módulos de IA se ejecutan con el protocolo de D-031.
- **Resultado.** Biblioteca con 200 medios 1080p: 60 cuadros por segundo al desplazar, sin ningún intervalo de más de 33 ms; portadas de la primera pantalla en 169 ms en frío y 107 ms con la caché en disco.

## D-022. Se retira el visor Win32 (10 de octubre de 2026)

- **Decisión.** Se borra la ventana Win32 de prueba (`src/Rodaje.Visor`) y sus pruebas: la app WinUI 3 ya reproduce, edita y mide (`build\app.ps1 -Medir N`) con el mismo motor y compositor. El código queda en la etiqueta de git `visor-fase3`.
- **Consecuencia.** Un solo ejecutable que mantener. D-015 y D-016 siguen valiendo para el motor y el render.

## D-023. Estilos de atajos con nombres propios (10 de octubre de 2026)

- **Decisión.** Los estilos de D-018 se llaman «Rodaje», «Ágil» y «Clásico» (`ShortcutPreset.Rodaje`, `Agile` y `Classic`). Ni la interfaz ni el código nombran otros programas; las referencias de experiencia de uso quedan en este registro. Los `atajos.json` y `ajustes.json` con los nombres anteriores se leen como Ágil y Clásico y se reescriben; un estilo desconocido vuelve a Rodaje sin dar por dañado el archivo.

## D-024. Restablecer un parámetro y quitar keyframes, con aviso (10 de octubre de 2026)

- **Decisión.**
  - **Restablecer.** Cada parámetro del inspector tiene su botón, y hay un «Restablecer todo». Sin keyframes, el parámetro vuelve a su valor de fábrica sin preguntar. Con keyframes, un aviso dice cuántos se borrarán, con «Quitar keyframes y restablecer» y «Cancelar» (el botón por defecto; Esc también cancela). El botón se apaga si el parámetro ya está en su valor y sin keyframes.
  - **Quitar keyframes** es una acción aparte, en el menú contextual del parámetro, con el mismo aviso: borra la animación y deja el parámetro fijo en el valor que se ve en el cursor.
  - Las dos son un solo comando del núcleo (`Edits.ResetParameters`) y un solo paso del historial.
- **Por qué.** Borrar una animación sin avisar es fácil de hacer sin querer y difícil de notar. Separar «quitar keyframes» de «volver al valor» evita confundir las dos cosas.
- **Consecuencia.** Un clic más cuando hay animación.

## D-025. Efectos, transiciones en el corte y formato 3 (10 de octubre de 2026)

- **Decisión.**
  - **Transiciones centradas en el corte y sin solapar clips.** Viven en la pista (`Track.Transitions`: `from`, `to`, `duration`, `parameters`, `options`); cada lado usa el material sobrante y, si no hay, congela el cuadro del borde. Seis propias (fundido cruzado, a color, barrido, empujar, zoom y desenfoque) y `rodaje.preset` para las de paquete, que cae a fundido cruzado si falta el paquete. `TransitionOps.Reconcile` corre al final de cada edición, dentro del mismo paso del deshacer.
  - **Texto y títulos** se deciden aparte (D-029).
  - **Paquetes solo con primitivas**, sin shaders propios: filtros y efectos se copian al proyecto (con `options.source`); LUT y transiciones se referencian.
  - **Registro estático** (`Rodaje.Core/Effects/EffectRegistry`) con códigos enteros estables, destino, etapa, parámetros (hasta 16, con valor por defecto y límites) y opciones, en `FrozenDictionary`, sin reflexión. Un tipo desconocido se conserva intacto.
  - **Formato 3:** `EffectInstance.Options`, `Track.Effects` (keyframes en tiempo de la secuencia) y `Track.Transitions`. La migración 2 → 3 convierte `rodaje.volume` lineal en `rodaje.audio` en dB; el volumen se guarda en `gainDb`, con −60 dB como silencio.
  - **Hacia la GPU:** `LayerFrame` suma `Effects` (`LayerEffect` con `EffectValues` en `InlineArray`, por valor), `Blend` (13 modos) y `Transition`; sin nada de eso es el camino rápido de siempre. Los recursos (LUT) se piden con `IEffectResources`, que nunca bloquea; el motor recibe el contenido de paquetes por `IEffectContent` y no conoce `Rodaje.Packages`.
- **Consecuencia.** Cambiar un código del registro rompe la tabla de la GPU: una prueba los fija.

## D-026. Velocidad como propiedad del clip y formato 4 (10 de octubre de 2026)

- **Decisión.**
  - **`Clip.Speed`** (`ClipSpeed`: `Rate` racional de 1/10 a 100, `Curve` lineal a tramos en centésimas de 0,1× a 10× con puntos en diezmilésimas de `Span`, `Offset`, `Reverse`, `Freeze`, `PreservePitch`, `FrameSampling`) y `Clip.Duration` guardada cuando hay velocidad. `rodaje.speed` queda reservado (código 8) y oculto; la migración 3 → 4 quita sus instancias.
  - **La curva vive en el tiempo de la línea de tiempo**, no en el del origen: así el origen recorrido es cuadrático por tramos y su inversa sale con una raíz entera, todo exacto en `Int128` (`RetimeMath`). Con la curva en el origen la duración sería un logaritmo.
  - **`ClipTimeMap` es la única puerta.** Calcula cada instante desde el ancla (`SourceIn`, o `SourceOut` en reversa) y `Offset`, con `⌊I(offset + τ)⌋ − ⌊I(offset)⌋`; el tramo del origen mide `⌈I(offset + D)⌉ − ⌊I(offset)⌋` y el validador exige que coincida. Dividir y recortar mueven `Offset`, así que ningún cuadro cambia. Fuera del clip (transiciones) sigue a la velocidad del borde. A 1× es la cuenta de siempre.
  - **Cotas:** `|Offset| + Duration ≤ 2^44` ticks (unas 6,9 h); numeradores hasta 2^104 y discriminantes hasta 2^112.
  - **Validador:** además de la velocidad, limita los parámetros de efectos y transiciones registrados a su rango y rechaza ids con caracteres de control.
  - **Motor:** `VideoClipPlan.TimeMap` y `AudioClipPlan.Retime` llevan el mapeo; `TimelinePlan` se parte en `Video` y `Audio`.
  - **Magnetismo al cambiar la velocidad:** en cualquier pista, si el clip se acorta (`SetSpeed`, `RateStretch`, `SetCurve`) el siguiente se une y la transición del corte se queda; si crece, respeta los huecos y solo empuja lo que pisa. `FreezeFrame` inserta y corre siempre. Los comandos reciben el `RippleScope` de la sesión. A 1× «mantener tono» no se toca.
- **Consecuencia.** Un clip con velocidad no puede pasar de unas 6,9 horas en la línea de tiempo.

## D-027. Inspector en secciones y tema grafito con más contraste (11 de octubre de 2026)

- **Contexto.** En las pruebas de uso, en Propiedades los nombres se solapaban con los números, la transformación se sentía llena y las capas de gris casi iguales (contrastes de 1,03 a 1,1:1 entre superficies y bordes a 1,2:1) se confundían.
- **Decisión.**
  - **Propiedades en secciones plegables:** «Posición y tamaño», «Opacidad y mezcla» (el modo de mezcla pasa aquí desde Filtros, porque es del clip) y «Audio». Una fila por parámetro con etiqueta de ancho fijo, puntos suspensivos y ayuda; restablecer y el rombo atenuados hasta pasar el ratón; la navegación de keyframes y la curva solo aparecen si el parámetro está animado. «Restablecer todo» y «Guardar como transición…» van al menú «⋯».
  - **Contraste medido (WCAG AA)** en `Palette.xaml`, sin tocar el acento: texto ≥ 4,5:1, texto secundario y desactivado ≥ 3:1, y bordes de controles ≥ 3:1 contra el panel. Superficies en escalones claros: fondo #101010, panel #1D1D1D, cabeceras #262626, controles #2C2C2C, encima #3A3A3A, pulsado #4A4A4A.
  - **Ajustes posteriores:** las pestañas nunca se cortan (íconos y, si hace falta, desplazamiento horizontal); el panel derecho arranca en 380 px y los laterales ceden con la ventana estrecha; los rombos de keyframes se ven siempre igual; Filtros, Velocidad y Transición usan las mismas secciones y filas que Propiedades, y las listas marcan lo elegido en gris.
- **Consecuencia.** Más bordes visibles en toda la app. Con el panel en 320 px cada parámetro ocupa dos líneas; desde 340 px, una.

## D-028. Exportación: tubería a `ffmpeg.exe` y solo codificadores LGPL en la app (11 de octubre de 2026; la parte de 9:16, corregida por D-033)

- **Decisión.**
  - **Rendimiento, elegido en el diálogo cada vez** (`ExportSettings.Performance`): «Alto rendimiento (menor tiempo)», con prioridad normal o alta y la vista previa en pausa o al mínimo, o «Bajo rendimiento (app más fluida)», con prioridad baja para seguir editando.
  - **Camino del cuadro:** `Project.Clone()` → `OfflineTimeline.Render(frame)` (exacto, sin reloj ni proxies) → `Compositor` a resolución completa → `ExportFrameConverter` → `IExportSink.WriteVideo`. La exportación usa un `GpuDevice` propio con su hilo y su presupuesto de VRAM; con menos de 4 GB la vista previa pasa a proxies y a mitad de resolución mientras exporta.
  - **Y'CbCr en la GPU:** `ExportFrameConverter` escala, ajusta la forma, convierte R'G'B' → Y'CbCr y deja el cuadro en un anillo de texturas `Staging`; a la CPU baja ya en NV12/P010 o en planos, nunca en RGBA. BT.709 de rango limitado (BT.601 en SD), croma 4:2:0 «left» y tramado al bajar a 8 bits; 10 bits SDR con P010. Sin HDR.
  - **Tasa constante:** el cuadro `n` sale en `n / FrameRate` de la secuencia; nada de VFR.
  - **Audio a un temporal:** `OfflineAudio` escribe la mezcla del rango en un WAV antes del video; así no hay dos tuberías que se bloqueen entre sí y el audio se prueba muestra a muestra.
  - **Salida por tubería a `ffmpeg.exe`** (el mismo exe LGPL verificado de los proxies, dentro de un Job Object): `rawvideo` por la entrada estándar, el WAV como segunda entrada, `-progress` para el avance, escritura atómica (`.rodaje-tmp` y renombrar), espacio en disco comprobado y limpieza tras un corte de luz.
  - **Codificadores:** H.264 y HEVC solo por GPU (NVENC, AMF, QSV) o por Media Foundation; la CPU exporta AV1 (SVT-AV1) y VP9 (libvpx); maestros ProRes, FFV1 y DNxHR; audio AAC (preferido `aac_mf`, además `aac`), Opus, MP3 (LAME), FLAC y PCM. kvazaar y OpenH264 quedan fuera de los valores por defecto (`InDefaults = false`): solo se usan si el usuario los elige. x264 y x265 van en el complemento GPL aparte (`rodaje-codificadores-gpl`, D-034), con su propio `ffmpeg.exe` y la misma tubería.
  - **Contrato.** `Rodaje.Media/Export` (`IExportSink`, `ExportVideoFrame`, formatos, color, 16 contenedores, `EncoderSpec` con capacidades y `ExportCompatibility`, la matriz contenedor × códec × píxel × perfil × audio con el motivo de cada rechazo); `Rodaje.Engine/Offline` (`OfflineTimeline`, `OfflineAudio` con sonoridad EBU R128); `Rodaje.Render/Export` (`ExportFrameConverter`); y `Rodaje.Export`, sin interfaz (`ExportSettings`, `ExportRequest`, `ExportPreset`, `ExportProgress`, `ExportJobState` y `ExportSettingsValidator`). Detalle en [arquitectura/exportacion.md](arquitectura/exportacion.md).
- **Consecuencias.** La exportación necesita `ffmpeg.exe` junto a las DLL, igual que los proxies. La tubería cuesta una copia por cuadro a la CPU (a 4K60 unos 750 MB/s); un prototipo movió 2,8–3,8 GB/s a 4K y dio 250–290 fps sin codificar con 3 capas 1080p, así que no hace falta enviar la textura directa a NVENC. Un equipo sin GPU compatible ni Media Foundation (Windows N) no exporta H.264: se le ofrece AV1 o VP9.

## D-029. Texto y títulos (11 de octubre de 2026)

- **Decisión.**
  1. **Animación del bloque entero** en la v1; por letra o palabra, más adelante.
  2. **Fuentes empaquetadas** (paquete `rodaje-fuentes`, unas 10 familias OFL o Apache sin modificar), solo de paquetes que instala la persona.
  3. **Entrada y salida pegadas a los bordes del clip** (siguen al recorte), con «convertir en keyframes» opcional; al dividir, los bordes internos quedan sin animación.
  4. **El proyecto guarda una copia del estilo:** actualizar un paquete no cambia los títulos de proyectos existentes, igual que con los filtros.

## D-030. Licencia PolyForm Noncommercial (11 de octubre de 2026). Sustituida por D-032

Se pasó de MIT a una licencia no comercial para impedir la venta por terceros. Se descartó el mismo día porque no es código abierto y deja zonas grises de «uso comercial». Nunca se publicó con ella. Los repositorios de contenido siguieron en CC0.

## D-031. Módulos de IA: protocolo v1, análisis en la app y descarga firmada (11 de octubre de 2026)

- **Decisión.**
  - **Protocolo v1** en `src/Rodaje.Modules.Protocol` (sin referencias a Rodaje, AOT, compartible entre programas): marco de 4 bytes + JSON UTF-8 de hasta 1 MiB con `type`; mensajes `hello`, `welcome`, `invoke`, `progress`, `result`, `error`, `cancel`, `ping`, `pong`, `log` y `shutdown` con `JsonSerializerContext`; negociación de versión por rango, capacidades en `welcome`, avance limitado a uno cada 50 ms, cancelación confirmada con 2 s de gracia y muerte del proceso si no llega, y códigos de error estables. Campos y mensajes desconocidos se ignoran; uno inválido cierra la conexión ([protocolo-modulos.md](protocolo-modulos.md)).
  - **Desconfianza hacia el módulo:** límites en cada campo, UTF-8 estricto y búferes solo en regiones que creó el editor, con límites comprobados; cualquier violación corta, falla las llamadas con `ModuleException` y mata el proceso. Tubería con nombre aleatorio y `CurrentUserOnly` en los dos lados.
  - **Cuadros y audio por memoria compartida** (`Local\Rodaje.Mod.<guid>`, la crea el editor). El editor decodifica: los módulos no abren archivos del usuario ni traen FFmpeg.
  - **Ciclo de vida:** un proceso por módulo, al primer uso y cerrado tras 2 min ocioso, Job Object con «matar al cerrar», `ping` de vigilancia y un modelo pesado a la vez según `vramMb`.
  - **La IA no edita:** devuelve datos que la app convierte en comandos con vista previa y un solo Ctrl+Z.
  - **Sin modelo, en la app:** silencios, ritmo, ducking, estabilizar y color automático.
  - **Runtimes:** ONNX Runtime (con DirectML donde hace falta GPU), Whisper.net con Vulkan y LLamaSharp para el traductor; CUDA no se redistribuye. Modelos y voces solo con licencias que permitan redistribuirlos; espeak-ng (GPL) se sustituye por un fonemizador propio.
  - **Descarga:** un índice firmado con ECDSA P-256 (clave pública dentro del exe), SHA-256 de cada archivo, reanudación e instalación atómica; releases de GitHub de `Dathinel` y Hugging Face fijado por commit para los modelos grandes ([descarga.md](descarga.md)). `paquete.json` suma a `module` los campos `runtime`, `vramMb` y `models` sin cambiar el `formatVersion`.
- **Consecuencias.** Mediciones: 0,1 ms por cuadro 1080p por referencia, 1,4 ms copiado (unos 700 cuadros/s), `ping` de unos 0,1 ms, arranque de 31 ms con AOT y cancelación en menos de 1 ms: el protocolo no es cuello para ningún modelo. Los módulos viven en `modules/`.

## D-032. Licencia GNU GPLv3 con atribución obligatoria, permiso para complementos y marca reservada (11 de octubre de 2026; sustituye a D-030)

- **Decisión.** El código de Rodaje se publica bajo la GNU GPLv3, solo versión 3 (`GPL-3.0-only`; texto oficial en `LICENSE`, con la cabecera `Copyright (C) 2026 Dathinel` y https://github.com/Dathinel/rodaje), más términos adicionales de la Sección 7 (b) y (c):
  1. crédito visible y con enlace en la interfaz: «Basado en el editor de video de código abierto de Dathinel / Rodaje (https://github.com/Dathinel/rodaje)»;
  2. aviso en las páginas de descarga o venta;
  3. prohibición de retirar el crédito;
  4. permiso adicional para complementos que se conectan por las interfaces públicas o por IPC/CLI, que pueden llevar cualquier licencia, también de pago.

  El nombre, el logotipo, el icono y el material gráfico quedan reservados en `TRADEMARK.md`. La app muestra «Código abierto upstream: Rodaje» en la barra de título y el aviso completo en «Ayuda → Acerca de Rodaje» (`UpstreamAttribution`, fijado por una prueba y por la autoprueba).
- **Por qué.** Se prefiere código abierto de verdad, con atribución obligatoria y marca reservada, a impedir la venta; y que los complementos de terceros (efectos, módulos, extensiones) queden libres del copyleft. El titular es único, así que el cambio no afectó a terceros.
- **Alternativas descartadas.** PolyForm Noncommercial (D-030): no es código abierto. MIT: no obliga a publicar el código de los derivados ni a conservar un crédito visible. AGPL: el copyleft por red no aporta a una app de escritorio y complica los complementos.
- **Consecuencias.** Cualquiera puede vender Rodaje o un derivado con su código fuente, la atribución y otro nombre. No se copia código GPL ajeno: el permiso para complementos y los términos de atribución solo los puede dar el titular. Los repositorios de contenido (`rodaje-transiciones`, `rodaje-filtros` y los que vengan) siguen en CC0. Lo que queda por revisar (Windows App SDK, alcance del término 2, activos reservados) está en [legal.md](legal.md), «Compatibilidad con la GPLv3», y en [pendientes-legales.md](pendientes-legales.md).

## D-033. Formato del proyecto en la edición, exportar sigue al proyecto y herramientas rápidas (12 de octubre de 2026)

- **Contexto.** En las pruebas de uso, el lienzo 9:16 (negro sobre el negro del visor) solo se distinguía por las guías de los márgenes seguros; cambiar el formato desde Exportar no se encontraba y se esperaba hacerlo desde la edición; los preajustes de Exportar no dejaban elegir otro; y las herramientas rápidas no estaban a la vista. Sustituye la parte de D-028 sobre 9:16.
- **Decisión.**
  - **Formato en el reproductor:** el botón de la cabecera («9:16 · 1080p») abre 16:9, 9:16, 1:1, 4:5, 4:3, 21:9 y Personalizado…, «Ajustar clips» (Encajar por defecto, Rellenar, Mantener tamaño; se recuerda), cuadros por segundo y «Resolución y más opciones…». Cada cambio es un paso del historial (`SequenceFormatEdit`): encajar no toca la escala, rellenar y mantener la corrigen en `rodaje.transform` (keyframes incluidos) y la posición se escala con el tamaño dibujado; títulos y pistas bloqueadas no cambian. Tras elegir un formato, el primer video importado ya no lo pisa.
  - **Visor:** el lienzo se ve con su forma real: gris #242424 alrededor, la imagen en negro encima y un contorno de 1 px. Los márgenes seguros vienen apagados.
  - **Exportar sigue al proyecto:** la forma es siempre la de la secuencia; un preajuste solo aporta el lado corto (YouTube 4K sobre 9:16 sale 2160×3840). Si el preajuste es para otra forma, se avisa con el enlace «Cambiar el formato del proyecto». Otra forma solo se elige en Avanzado (resolución), con barras, recorte o estirado.
  - **Exportar sin controles fijos:** el diálogo se redibuja en su sitio tras cada cambio del modelo.
  - **Herramientas rápidas encima de la línea de tiempo**, siempre a la vista y junto a los clips que se eligen: Quitar silencios, Detectar ritmo, Bajar música bajo la voz, Reducir ruido, Estabilizar, Color automático, Balance de blancos, Subtítulos automáticos, Texto a voz y Añadir texto. Las que no aplican se ven atenuadas pero pulsables, y la ayuda dice por qué. Las reglas están en `QuickTools` (sin WinUI, con pruebas).
- **Consecuencia.** La línea de tiempo cede 36 px a la barra. Cambiar los fps con clips no los reajusta al cuadro nuevo.

## D-034. Módulos de IA y medios generados: decisiones de producto (12 de octubre de 2026)

- **Decisión.**
  - **D-033 confirmada:** barra de herramientas rápidas encima de la línea de tiempo, gris alrededor del lienzo y «Encajar» por defecto.
  - **Repositorios de los módulos:** privados hasta publicarlos.
  - **JIT** solo en los módulos cuya biblioteca no admita Native AOT: autocontenidos y recortados. La app sigue en AOT.
  - **Traductor:** Qwen3-4B, local y descargado aparte.
  - **Voz:** solo Kokoro en la v1; en portugués, solo pt-BR.
  - **Quitar fondo:** MODNet por defecto y BiRefNet_lite como «Calidad alta» opcional (pesos MIT entrenados con DIS5K, de uso no comercial: ver [pendientes-legales.md](pendientes-legales.md)).
  - **CinemaDNG:** se importa a ProRes 422 HQ.
  - **Complemento GPL (`rodaje-codificadores-gpl`):** un `ffmpeg.exe` GPL con x264 y x265, compilado a propósito para que su fuente correspondiente sean pocos tarballs.
  - **Subtítulos sin GPU:** un modelo de Whisper menor solo para CPU.
  - **Reducción de ruido:** en CPU, sin DirectML.
  - **Medios generados** (audio limpio, voz, ProRes de CinemaDNG): por defecto en `<proyecto>.medios\` junto al proyecto; la persona puede elegir otra carpeta en Configuración y al generar.
  - **Estabilización:** el análisis va en la caché de la app, no junto al proyecto.
- **Consecuencia.** BiRefNet_lite no puede ir en una distribución comercial sin resolver la licencia de sus datos de entrenamiento.

## D-035. Color, efectos y monitores de nivel profesional (12 de octubre de 2026)

- **Contexto.** Faltaban monitores para corregir el color con datos, una corrección secundaria, curvas de tono y efectos ópticos (grano, resplandor, desenfoques de lente, direccional y radial…), y la pila de filtros no dejaba comparar ni guardar presets.
- **Decisión.**
  - **Espacio de trabajo:** el color sigue en gamma, como la mezcla de capas (D-016), para que los controles respondan como los de cualquier editor; lo óptico (resplandor y desenfoque de lente) va en luz lineal (gamma 2,2) y vuelve a gamma. Los intermedios son RGBA16F: una rampa de 10 bits pasa por el color sin perder códigos.
  - **Monitores en la GPU** (compute shaders): forma de onda de luma y RGB, desfile, vectorscopio con objetivos al 75 % y línea de piel, histograma. Solo miden con el panel abierto y como mucho 30 veces por segundo; la imagen baja por un anillo sin esperar a la GPU.
  - **Máscaras que limitan efectos:** una máscara puede recortar la capa (como siempre) o, con uso «limitar efectos», marcar dónde van los efectos del intermedio, cada uno con su zona (toda la imagen, dentro o fuera). Queda un punto de enganche para una máscara propia por efecto.
  - **A/B por efecto y ver la máscara del calificador:** solo en la copia del proyecto que recibe el reproductor; no tocan el historial ni la exportación.
  - **Reducción de ruido temporal:** usa el cuadro anterior de la misma capa solo si es el inmediatamente anterior del origen (al reproducir y al exportar); en pausa o tras un salto se ve la parte espacial. Es la única diferencia admitida entre vista previa y exportación.
  - **Presets:** las miniaturas se componen en la GPU sobre el cuadro actual sin el color del clip; «Guardar como preset» escribe un `rodaje.filter` en los complementos propios, el mismo archivo que va en un `.paquete`.
- **Consecuencia.** Un efecto nuevo del intermedio cuesta un intermedio del tamaño de la capa más sus pasadas (medido por efecto en `EffectCost`); seis efectos en una capa 4K entran en 5 ms de mediana en la RTX 3050.

## D-036. Transformación profesional y formato 7 (12 de octubre de 2026)

- **Contexto.** La transformación tenía posición, escala, giro y opacidad con cinco curvas fijas; faltaban ancla, estiramiento, inclinación, volteo, curvas Bézier, trayectorias, desenfoque de movimiento y una forma de animar de un clic.
- **Decisión.**
  - **Formato 7:** keyframes con `ease` (Bézier de tiempo como `cubic-bezier`), `spatialIn`/`spatialOut` (tangentes de trayectoria) e interpolaciones `bezier`, `bounce` y `elastic`; `rodaje.transform` gana ancla, escala por eje, inclinación, volteo (opciones) y obturador. Todo opcional: la migración solo cambia el número.
  - **Una sola matemática** en el núcleo (`KeyframeMath`, `MotionPath`) para el motor, la interfaz y quien escriba keyframes después (un seguidor de movimiento). La trayectoria se recorre a velocidad constante sobre la curva (largo medido en 48 tramos) y la interpolación del keyframe de X reparte el recorrido.
  - **El ancla no mueve la imagen:** es el pivote de escala, giro e inclinación; arrastrarla en el visor corrige la posición. El volteo es alrededor del centro de la imagen.
  - **Desenfoque de movimiento por submuestreo temporal** de la transformación (2 a 64 muestras, ángulo y fase), calculado por el motor y promediado por el compositor: igual en vista previa y exportación. No desenfoca el movimiento dentro de la imagen (eso pediría flujo óptico) ni una capa en transición.
  - **Animaciones de fábrica** como keyframes normales (relativas a la transformación de partida, en segundos de la línea de tiempo) y no como un efecto aparte: se editan en la gráfica como cualquiera.
  - **Asas en el visor solo con Propiedades a la vista**, como el recorte con Filtros: el visor queda limpio el resto del tiempo.
- **Consecuencia.** Un Rodaje con formato 6 pide actualizar para abrir un proyecto nuevo. El desenfoque cuesta una pasada por muestra (16 muestras en 4K, 2,8 ms en la RTX 3050).

## D-037. La IA se adapta al equipo (12 de octubre de 2026)

- **Contexto.** Los módulos de IA decidían solos y con reglas fijas: Whisper turbo en la GPU o small en la CPU, el traductor probaba todas las capas, la mitad y ninguna, Quitar fondo siempre a 512 px y un solo modelo pesado a la vez aunque sobrara VRAM. En una integrada o con 2 GB fallaban o iban a ciegas; con 12–24 GB y 32 hilos se desaprovechaba el equipo.
- **Decisión.**
  - **Perfil de IA en el núcleo** (`Rodaje.Media/Ai`, puro y probado con equipos simulados): de la VRAM dedicada y compartida, si la GPU es integrada, la RAM total y libre y los hilos, `AiHardwarePlan` decide por módulo la variante (Whisper turbo, small o base), el dispositivo, las capas del traductor en la GPU (descarga parcial calculada: 82 MB por capa más 450 de búferes), el lado de trabajo de MODNet (384, 512 o 640), si BiRefNet cabe en la GPU, el lote, los hilos y cuántos tramos o cuadros van a la vez.
  - **Presupuesto de VRAM:** la dedicada menos lo de la vista previa (3/8, como en D-031, pero **hasta 3 GB**: con 24 GB quedan 21 para la IA). En una integrada, la reserva propia más un cuarto de la compartida, sin pasar de un cuarto de la RAM ni de 2 GB (1,5 con «Rápida», 3 con «Máxima»). Con 9 GB o más para la IA conviven dos módulos pesados; con 18 GB, tres.
  - **CPU:** un hilo de cómputo por núcleo físico (la mitad de los lógicos desde 8); trabajos de IA por CPU a la vez según el equipo (1 con 4 hilos o 8 GB de RAM, 2 hasta 12 hilos, 3 desde ahí): el resto espera «Esperando la CPU» en Trabajos.
  - **Por el protocolo, sin versión nueva:** el plan viaja en los parámetros de cada llamada, como campos opcionales. Los módulos 1.0 ya entendían casi todos (`device`, `threads`, `model`, `cpuModel`, `gpuLayers`, `dispositivo`) e ignoran los nuevos; subtítulos y visión 1.1 suman `parallel` y `lado`, `hilos`, `paralelo`. Si la GPU falla a mitad, cada módulo sigue en la CPU con el modelo de la CPU que también se le pasa.
  - **Aviso antes de lanzar:** cada diálogo muestra el tiempo estimado en este equipo y dónde corre; si la memoria no alcanza, lo dice y ofrece la alternativa (calidad normal, CPU, Whisper base). La estimación parte de una tabla de referencia (RTX 3050 de 4 GB, 16 hilos) y, en cuanto la función corre una vez, usa lo medido en el equipo (`ia-tiempos.json`).
  - **Ajuste manual:** Ver › Calidad de la IA: automática, rápida o máxima.
  - **Pruebas:** `RODAJE_SIMULAR_VRAM_MB` y `RODAJE_SIMULAR_RAM_MB` cambian lo que ve el perfil (no los módulos); solo para pruebas.
- **Medido** (RTX 3050 de 4 GB, 16 hilos, con otros 8 procesos de compilación y pruebas en marcha; `AiProfileRealTests`, 12 s de voz, 90 cuadros con MODNet y 15 con BiRefNet): todas las funciones terminan con el perfil real (2477 MB para la IA: turbo en la GPU, 24 capas del traductor en la GPU), con 2 GB simulados (small en la GPU, 10 capas; la calidad alta pasa a la CPU, 228 s en lugar de 59) y sin GPU (todo en la CPU: subtítulos en 12,5 s, traducción en 32 s, MODNet en 19 s). En la 3050 el reparto es casi el de antes y, con el equipo cargado, los tiempos quedaron dentro del ruido (turbo 4,5 s frente a 3,6; traducción 10 s frente a 8,4): la ganancia está en los extremos, donde antes se probaba a ciegas. Lo que sí acelera en la 3050 son los tramos a la vez de Whisper: 3 min de voz en 17,7 s con dos en la GPU frente a 29,6 con uno (WER 3,3 % en los dos), y en la CPU 111 s con dos de 4 hilos frente a 167 con uno de 8 (WER 10,2 %); por eso van dos desde que caben (1850 MB con turbo) y desde 16 hilos.
- **Consecuencias.** Las decisiones dependen de cifras de memoria medidas en un solo equipo; en AMD e Intel el reparto es el mismo, pero las velocidades de la tabla no se han medido. El módulo de subtítulos 1.1 pesa 57 MB más (Whisper base). Un traductor más pequeño que Qwen3-4B queda pendiente: con poca RAM solo se avisa.
## D-038. Complementos profesionales: looks de cámara, preajustes, sonidos y video 360 (12 de octubre de 2026)

- **Contexto.** Lo profesional va como complemento aparte en el GitHub de Dathinel (pendiente del 11-oct): looks de cámara, preajustes de exportación, biblioteca de efectos de sonido y video 360. El formato ya nombraba `sound`, pero sin lector ni uso, y no tenía preajustes de exportación.
- **Decisión.**
  - **Cuatro repositorios de contenido, CC0 y sin código:** `rodaje-camaras` (LUT 33³ de log a Rec.709 calculadas con las fórmulas publicadas por cada fabricante), `rodaje-preajustes-pro`, `rodaje-sonidos` (síntesis propia por script, sin muestras ajenas) y `rodaje-360`.
  - **Tipo `export-preset`** (`rodaje.exportpreset` v1): el lector de `Rodaje.Packages` es neutro (nombres de contenedor y códec como texto) y la app lo traduce a sus ajustes; un códec que no conoce se rechaza con «actualiza el programa». Los preajustes de paquete van en el diálogo entre los de fábrica y los propios.
  - **Entrelazado al exportar** (para la entrega a televisión): se compone a la tasa de campos y FFmpeg teje los pares con filtros LGPL (`separatefields`, `select`, `weave`). Solo ProRes, que lo codifica por campos.
  - **`sound` es un WAV** (PCM de 16, 24 o 32 bits o float): al usarlo se copia a la carpeta de medios del proyecto (`<proyecto>.medios\sonidos`), así el proyecto no depende del paquete instalado.
  - **Video 360: el motor trae el código y el complemento lo activa.** `rodaje.reframe360` está en el registro con `Feature = "360"`: se aplica siempre que un proyecto lo traiga (nada se rompe al desinstalar), pero la interfaz solo lo ofrece con `rodaje-360` instalado. Los metadatos esféricos (Spherical Video V2) los escribe un editor de cajas MP4 propio después de FFmpeg, que no los escribe desde la línea de comandos.
- **Consecuencia.** Un Rodaje anterior no entiende `export-preset`: `rodaje-preajustes-pro` le resulta un paquete sin elementos y no lo instala. Los efectos de `rodaje-360` en un Rodaje sin el reencuadre se conservan en el proyecto, pero no se aplican.

## D-039. Títulos profesionales: unidades en un atlas, formato 9 y `rodaje.textstyle` v2 (12 de octubre de 2026)

- **Contexto.** D-029 dejó para después la animación por letra o palabra. Los títulos tenían un solo borde y una sombra, color liso, sin formato dentro del texto, sin tipografía fina ni arco, y el karaoke cambiaba de textura en cada palabra.
- **Decisión.**
  - **Un atlas por título, no un rasterizado por cuadro.** Un título por unidades (animación por línea, palabra o letra, arco o karaoke cuadro a cuadro) se rasteriza una vez: cada unidad en su celda (sombra, borde, relleno y relleno resaltado) de un atlas con mipmaps. En cada cuadro el compositor las coloca con su pose en un intermedio y de ahí sigue el camino de siempre (transformación, efectos, mezcla). El desenfoque por unidad sale del nivel de mip; el barrido del karaoke, de recortar la celda resaltada y la normal. Así la vista previa y la exportación son el mismo cálculo y animar no cuesta rasterizar.
  - **El motor solo manda la fase.** `TextMotion` (fase, avance y palabra dicha con su avance) va por valor en la capa; las poses salen de `TextAnimator` del núcleo, con las mismas interpolaciones de los keyframes (`KeyframeMath`, rebote y elástico incluidos). El desfase es una fracción de la duración, así el motor no necesita saber cuántas unidades hay.
  - **Las sombras se juntan antes que los bordes y los bordes antes que los rellenos**, para todas las unidades: en reposo, un título por letras se ve como el bloque.
  - **Tramos en el clip, no en el estilo:** el formato de una palabra es del texto, se mueve con él al escribir y un estilo nuevo no lo pisa.
  - **Formato 9 y `rodaje.textstyle` v2**, todo opcional; «Guardar como estilo» escribe la versión 1 si el estilo no usa nada nuevo, así lo sigue leyendo un Rodaje anterior.
- **Consecuencia.** Un emoji de color junto a una letra animada puede asomar en su celda (se dibuja con su paleta, no con el pincel que oculta al resto). Un atlas 4K de tres líneas por letra pesa unos 55 MB con mips y tarda unos 120 ms en el hilo de títulos; la composición por cuadro no reserva memoria.

## D-040. Mezclador: inserciones en orden, fader después de ellas, buses y formato 10 (12 de octubre de 2026)

- **Las inserciones de audio suenan todas, en el orden de la pila.** Antes sonaban solo el primer ecualizador y el primer compresor, siempre el ecualizador antes. La migración 9 → 10 deja igual lo que sonaba (ese ecualizador pasa delante y los que no sonaban se desactivan).
- **El volumen de la pista es un fader después de sus inserciones**, como en una mesa: bajar la voz no cambia lo que hace su compresor. Cambia el sonido de un proyecto con compresor de pista y volumen de pista a la vez; los dos llegaron en la misma versión sin publicar.
- **Buses solo en la secuencia principal y sin buses que salgan a buses:** no hay ciclos que comprobar. Las pistas se mezclan por delante lo que retrasan los buses a los que llegan (compensación del retraso), con el mismo mecanismo que las llaves de sidechain.
- **Automatización «tocar» con comandos:** cada pase se escribe en keyframes dentro de un gesto (un paso del historial) y lo que se oye es lo que queda escrito, sin un camino aparte para la vista previa.
- **A/B por inserción solo en la vista previa**, como el de Dinámica: la etapa sigue procesando y sale la entrada con su retraso.

## D-041. Línea de tiempo y biblioteca profesionales, sincronía de enlazados y formato 11 (12 de octubre de 2026)

- **Contexto.** Faltaban las herramientas de un editor de oficio: cuchilla, ripple, slip y rango; reemplazar, ajustar a rellenar y superponer desde el origen; grupos, colores y alto de pista; huecos; edición por teclado; aviso de desfase; sincronía por forma de onda y multicámara. La biblioteca no tenía valoración, etiquetas, colecciones, metadatos buscables, subclips ni avisos de medios sin conexión o repetidos.
- **Decisión.**
  - **Formato 11, solo opcional:** `color` y `groupId` en el clip y `height` en la pista. La migración 10 → 11 solo cambia el número (reemplaza la reservada `ReservedEleven`), para que un Rodaje anterior rechace el archivo en vez de perder los grupos al guardar.
  - **Un grupo no es un enlace.** El enlace (`LinkId`) recorta, corta y corre junto y no admite dos clips en una pista; el grupo solo se elige y se mueve junto (`MoveClips`, sin pisar nada) y puede tener varios clips en la misma pista. Así agrupar no cambia ninguna regla de recorte.
  - **Insertar ya no desincroniza.** `TrackOps.InsertSpaceLinked` parte lo que pasa por el punto con sus enlazados, pasa a la mitad derecha lo que estaba enlazado entero después del corte y corre los enlazados de lo que se corre (y lo que les sigue en su pista). Lo usan insertar medios, mover con inserción (un solo espacio para todo el grupo) y la edición de tres puntos. Lo encontró la propiedad nueva de sincronía, que también encontró dos desfases que ya había: el ripple (`RippleEach`, y `RippleShift` en los dos alcances) ahora corre la pista de un enlazado desde el inicio de ese enlazado, no desde el punto del corte, y el desfase de dos clips a velocidad constante se mide exacto (diferencia de entradas menos lo recorrido entre inicios) en vez de muestrear el mapeo. Un borrado ripple que aun así desincronizaría (un enlazado que empieza antes del tramo) se rechaza.
  - **El desfase se mide, no se guarda.** `LinkSync` compara el material de cada clip con el de su primer enlazado del mismo medio; solo se rompe editando con «ignorar grupos» o con una pista bloqueada que no se corre. «Volver a sincronizar» mueve o desliza. Cerrar todos los huecos deja los que desincronizarían.
  - **Sincronía por forma de onda propia** (`Rodaje.Analysis.Sync`): correlación por FFT de la subida de energía en bloques de 1 ms (toda la grabación, sin guardar el audio) y refinado muestra a muestra en los 4 s con más señal, con parábola en el pico. Error de 0 muestras en las pruebas; sin sonido común, la confianza baja y no se usa.
  - **Multicámara básica sin modelo nuevo:** los ángulos y sus desfases viven en la biblioteca; cortar a un ángulo (1 a 9, también reproduciendo) parte el clip de la principal y le cambia el medio en el mismo instante del reloj común. El sonido no cambia.
  - **La biblioteca sigue fuera del `.rodaje`** (como sus carpetas): valoración, rechazados, etiquetas, subclips, colecciones inteligentes (reglas, no listas) y multicámaras van en el archivo de organización, versión 2. Fecha de grabación y cámara se leen del archivo (`MediaTags`) y se cachean con los códecs.
- **Consecuencia.** Un proyecto copiado a otro equipo llega sin etiquetas, subclips ni multicámaras (los cortes ya hechos sí). Un Rodaje anterior aparta el archivo de organización v2 y empieza con la biblioteca en la raíz. La semilla `0pZ2Mq9N8IE2` de regresión de velocidad sale de la lista: insertar arma otro proyecto con ella.

## D-042. Transiciones profesionales: alineación, curva, mezclas nuevas en la GPU, desenfoque de movimiento y audio (12 de octubre de 2026)

- **Contexto.** Las transiciones iban siempre centradas en el corte, con avance lineal, seis mezclas y un cruce de audio único. Faltaba lo de un editor profesional: alinearlas a un lado del corte, darles curva, más mezclas de calidad, desenfoque de movimiento, bordes de color, disolución por luma con imagen, varias formas de cruce de audio y una vista previa fiel.
- **Decisión.**
  - **Alineación en el modelo** (`alignment`: centrada, antes o después del corte). La regla de que no se pisen pasa de «la mitad cabe en cada clip» a «las dos partes que caen en un clip suman a lo sumo su duración»; al acortar un clip, `TransitionOps.Reconcile` reparte el clip entre las dos (la de la izquierda le guarda a la siguiente lo que usa, hasta la mitad), así ninguna desaparece.
  - **Curva del avance con las fórmulas de los keyframes** (`KeyframeMath`), recortada a 0–1 y exacta en los extremos. La misma curva mueve la mezcla, las curvas de un paquete y el cruce de audio: lo que se ve y lo que se oye van juntos. El rebote que se pasa del final es una mezcla propia («Empujar con rebote»), no una curva.
  - **Nueve mezclas nuevas de fábrica en `PsTransition`** (deslizar, zoom con desenfoque, giro, barrido con luz, fallo digital, quemado, destello, luma y empuje con rebote): necesitan shader y los paquetes solo traen primitivas (D-025). Las nuevas son exactas en 0 y 1 por construcción; el ruido es un hash de enteros, igual en la GPU, en WARP y en la referencia de la CPU.
  - **Desenfoque de movimiento de la transición como promedio de la mezcla en el obturador** (16 avances entre la apertura y el cierre, centrados en el cuadro y cerrados en los extremos de la transición), calculado por el motor (`LayerTransition.Shutter`): igual en vista previa y exportación. Va en una variante aparte del shader (`MOTION`) para que las demás no carguen con el bucle.
  - **Disolución por luma con imagen gris** por el mismo camino que las máscaras de imagen (`IEffectResources.TryGetMatte` con `file:`), sin un tipo de recurso nuevo.
  - **Cruces de audio como tipos de transición** (potencia constante, ganancia constante y exponencial), solo en pistas de audio; una transición de imagen en una pista de audio sigue sonando con potencia constante.
  - **Miniaturas con el compositor de verdad** (`Compositor.RenderTransitionStill` y `TransitionPreview.Frames`, la misma resolución que el plan) entre dos imágenes de muestra: la galería y el inspector muestran lo que se exporta.
  - **Formato 12**: solo cambia el número; un Rodaje anterior rechaza el archivo en vez de ver centrada una transición alineada.
- **Consecuencias.** Con desenfoque de movimiento una transición 4K cuesta 7,5–10,5 ms en una RTX 3050 Laptop (sin él, 1,4–4,5 ms según la mezcla). Ctrl+T queda para la transición por defecto (ajuste de la app, no del proyecto).

## D-043. Seguimiento de puntos, anclaje de esquinas y formato 13 (12 de octubre de 2026)

- **Contexto.** Solo se podía seguir una región para mover una máscara. Faltaba un seguidor de puntos con subpíxel, pegar otra cosa a lo seguido (un título, una pantalla con perspectiva), estabilizar por un punto y tapar caras que se mueven, con datos que se revisan y corrigen.
- **Decisión.**
  - **Seguidor propio en la CPU** (`PointTracker`): por punto, búsqueda gruesa por ZNCC (reducida a ½ o ¼ si la zona es grande) a través de la deformación del cuadro anterior, y Lucas y Kanade inverso compuesto afín contra el patrón original con la luz normalizada; el patrón no se actualiza, así no hay deriva. Predicción con la velocidad o con los otros puntos; un punto perdido se busca en una zona que crece y solo se da por encontrado con confianza ≥0,85 y donde lo ponen los demás. No va en la GPU: trabaja en ventanas de unos cientos de píxeles (≈7 ms por cuadro con cuatro puntos, igual en 4K) y subir cada cuadro costaría más; manda decodificar.
  - **Seguimientos guardados en el clip como efecto `rodaje.track`** (etapa diferida: no dibuja), con los puntos de partida y los cuadros en texto compacto (`PointTrack`, tiempos del origen y confianza por punto). Así se copian, se deshacen y viajan con el proyecto sin tocar el modelo; pegar atributos no los copia.
  - **Aplicar es escribir keyframes normales** en un paso del historial (no un vínculo vivo): el cuadro del cursor es la referencia (desfase) y los cuadros perdidos no ponen keyframe. Se retocan como cualquiera.
  - **Anclaje de esquinas `rodaje.cornerpin`** como desplazamiento de cada esquina en píxeles de la secuencia (todo en 0 no cambia nada; restablecer y keyframes como cualquier parámetro), aplicado antes de la transformación como una homografía. En el render, una w por esquina en el vértice: la capa sigue siendo un cuadrilátero por todos los caminos, sin pasadas nuevas.
  - **Formato 13** (los 10 a 12 quedan para el mezclador, la línea de tiempo y las transiciones): solo cambia el número (los tipos son nuevos y opcionales), para que un Rodaje anterior rechace el archivo en vez de dibujar una pantalla sin su perspectiva.
- **Consecuencia.** Un seguimiento largo pesa en el `.rodaje` (unos 33 caracteres por punto y cuadro; tope de 4 MiB por seguimiento, unos 25 minutos de un punto a 30 cuadros por segundo). El anclaje de esquinas solo se aplica a clips de medio (no a títulos: su tamaño lo conoce el rasterizador).
