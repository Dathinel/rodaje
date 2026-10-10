# Registro de decisiones

Este registro guarda el porqué de cada elección importante de Rodaje. Una decisión no se borra cuando cambia: se marca como reemplazada y se añade otra que explique el cambio. Todas las de esta página son del 9 de octubre de 2026.

---

## D-001. Nombre: FotoToma (reemplazada por D-014)

- **Decisión.** El nombre de trabajo "FotoShot" pasa a FotoToma ("toma" es cada plano filmado). Una búsqueda web básica no encontró software con ese nombre.
- **Por qué.** "FotoShot" queda a una letra de Photoshop (marca de Adobe) y ya existen apps de fotos llamadas "PhotoShot" (Vyro AI, H-Tech Labs). Se descartó un nombre en inglés porque el proyecto se piensa en español.
- **Consecuencia.** Una búsqueda básica no es una búsqueda de marcas: antes de publicar hay que consultar USPTO, EUIPO y SIC Colombia en las clases 9 y 42 de Niza (lista en `docs/legal.md`).

## D-002. Repositorio privado con licencia MIT

- **Decisión.** Repositorio privado (creado como `Dathinel/fototoma`, hoy `Dathinel/rodaje` por D-014), código propio bajo MIT a nombre de Daniel (Dathinel), con el fondo legal completo (`LICENCIAS.md`, `THIRD-PARTY-NOTICES.txt`, `docs/legal.md`) desde el principio.
- **Por qué.** Es la regla por defecto de la carpeta de programas. GPL-3 permitiría reutilizar código de Kdenlive, Shotcut u Olive pero obligaría a distribuir todo como GPL; Apache-2.0 aporta poco a un proyecto personal; sin licencia nadie podría usarlo.
- **Consecuencia.** MIT es compatible con enlazar dinámicamente bibliotecas LGPL como FFmpeg. No se puede copiar código GPL de los editores estudiados, solo estudiar su diseño (ver [`archivo/investigacion.md`](archivo/investigacion.md)).

## D-003. Stack: .NET 10 LTS con WinUI 3

- **Decisión.** C# sobre .NET 10 (LTS) con interfaz WinUI 3 (Windows App SDK). Lo decisivo es `SwapChainPanel`, que muestra una cadena de intercambio de Direct3D 11 dentro de la interfaz sin copias. Solo se escribirá C++ donde una medición demuestre que C# no alcanza.
- **Por qué.** WPF integra D3D11 mediante `D3DImage` (pensado para D3D9, con copias); Avalonia presenta D3D11 de forma menos directa y su ventaja multiplataforma no aplica; C++ con Qt 6 rinde más pero rinde menos a una sola persona y trae las obligaciones de Qt; Rust tiene un ecosistema de interfaz inmaduro en Windows; Python no da el rendimiento ni el empaquetado nativo.
- **Consecuencia.** El programa queda ligado a Windows. Los binarios del Windows App SDK se redistribuyen bajo los términos de licencia de Microsoft, no MIT (anotado en `LICENCIAS.md`).

## D-004. Entrega autocontenida con Native AOT

- **Decisión.** Rodaje se publica autocontenido con Native AOT: código máquina, arranque rápido y sin runtime de .NET instalado. El núcleo es compatible con AOT desde el primer día (sin reflexión, JSON por generador de código). Si una biblioteca necesaria no lo es, se recurre a ReadyToRun autocontenido.
- **Por qué.** La regla de la carpeta pide un ejecutable copiable en `dist\` sin instalador. La publicación dependiente del framework exige instalar .NET; MSIX añade un paso y depende de componentes que AtlasOS puede no traer.
- **Consecuencia.** Algunas bibliotecas con reflexión quedan fuera o necesitan configuración. El aviso MIT del runtime de .NET viaja con `Rodaje.exe` (`THIRD-PARTY-NOTICES.txt`).

## D-005. Entorno propio en `.entorno\`

- **Decisión.** El SDK de .NET 10.0.401 se descarga a `.entorno\dotnet` con `herramientas\preparar-entorno.ps1`, que verifica el SHA-512 publicado por Microsoft. La versión se fija en `global.json`; la caché de NuGet también vive en `.entorno\` y la restauración usa archivos de bloqueo. Solo `git` y `gh` son globales.
- **Por qué.** El equipo solo tiene los runtimes 6 y 8, y un SDK global mezclaría versiones entre programas. Un contenedor sería excesivo para una aplicación de escritorio.
- **Consecuencia.** La primera ejecución descarga unos 280 MB. `.entorno\` no se versiona; se versiona el script que lo reconstruye.

## D-006. FFmpeg como DLL compartidas LGPL cargadas dinámicamente

- **Decisión.** Se usan las compilaciones `lgpl-shared` de BtbN (https://github.com/BtbN/FFmpeg-Builds), sin libx264 ni libx265 y con `--enable-version3`, así que la licencia efectiva es LGPL v3 (`COPYING.LGPLv3`). Las DLL se cargan con FFmpeg.AutoGen (MIT). H.264 y HEVC se codifican con NVENC o Media Foundation, nunca con libx264.
- **Por qué.** FFmpeg es la única opción realista para leer casi cualquier formato, pero compilado con `--enable-gpl` o `--enable-nonfree` es incompatible con MIT, como el FFmpeg 9.0 de gyan.dev del equipo. Compilar uno propio cuesta mantenimiento; Media Foundation sola cubre menos formatos; libmpv, ver D-012.
- **Consecuencia.** Hay que cumplir la lista LGPL de `docs/legal.md`: DLL sin renombrar, fuente exacta u oferta escrita, línea de configuración, créditos y sin prohibir la ingeniería inversa de esa parte. El FFmpeg de gyan.dev es solo herramienta de desarrollo y nunca va a `dist\`.

## D-007. Unidad de tiempo: ticks de 1/705 600 000 s

- **Decisión.** Todo el tiempo del modelo es un entero de 64 bits en ticks de 1/705 600 000 s (la idea de los "flicks"), divisible por las cadencias habituales, incluidas las NTSC (1000/1001), y por las frecuencias de audio de 8 a 192 kHz. Cubre más de 400 años.
- **Por qué.** `double` acumula errores; los cuadros atan el proyecto a una cadencia y no representan bien el audio; los racionales de Olive son exactos pero cuestan más en cada operación.
- **Consecuencia.** Convertir a cuadros y muestras son divisiones enteras exactas. Una cadencia exótica se redondea al tick más cercano, documentado en el código.

## D-008. Posiciones explícitas, huecos implícitos y grupos de enlace

- **Decisión.** Cada clip guarda su inicio en la línea de tiempo y el espacio entre clips no es un objeto. Los clips que se mueven juntos (video y su audio) comparten un grupo de enlace. Los fotogramas clave van en tiempo de la fuente, así que recortar el inicio no desplaza la animación respecto al contenido.
- **Por qué.** La posición implícita de MLT dificulta la colocación libre (OpenShot usa posiciones explícitas), y los fotogramas clave relativos al clip se desplazan al recortar y sorprenden al usuario.
- **Consecuencia.** El ripple es una operación explícita que mueve los clips posteriores, y el validador comprueba que no haya solapamientos dentro de una pista.

## D-009. Deshacer con instantáneas completas de la secuencia

- **Decisión.** Cada operación es un comando tipado que guarda una copia profunda de la secuencia antes de ejecutarse; deshacer la restaura, y un fallo a mitad de camino también. Los comandos de un gesto continuo llevan una clave de fusión y se funden en un solo paso.
- **Por qué.** Las inversas escritas a mano (Shotcut), las lambdas de hacer y deshacer (Kdenlive) o las diferencias de JSON (OpenShot) fallan de forma sutil cuando una operación olvida registrar algo.
- **Consecuencia.** Cuesta memoria: kilobytes por paso con cientos de clips, con un límite configurable (500 pasos por omisión). Si pesa demasiado con proyectos grandes, el siguiente paso es copiar solo las pistas que la operación tocó.

## D-010. JSON con `schemaVersion` entero y guardado atómico

- **Decisión.** El proyecto es JSON legible con `schemaVersion` entero; al abrir uno antiguo se encadenan migraciones de versión en versión, cada una con pruebas y archivos de ejemplo. El guardado escribe un temporal, lo vacía a disco, conserva el anterior como `.bak` y reemplaza el original de forma atómica.
- **Por qué.** OpenShot sufrió por comparar versiones como cadenas y Olive acumuló un serializador por versión; el portátil ha tenido apagados inesperados. Un binario sería opaco y SQLite excesivo para un documento que se carga entero.
- **Consecuencia.** Un archivo de una versión más nueva que el programa se rechaza con un mensaje claro en vez de abrirse a medias.

## D-011. Identificadores en inglés, documentación y mensajes en español

- **Decisión.** Tipos, métodos, propiedades y campos del JSON en inglés; documentación, comentarios, mensajes al usuario, commits e interfaz en español.
- **Por qué.** El ecosistema de .NET está en inglés y mezclar idiomas en los nombres dificulta leer el código junto a sus API; todo en inglés iría contra la forma de trabajo del proyecto.
- **Consecuencia.** Si la interfaz se traduce algún día, los textos ya están separados del código.

## D-012. libmpv solo como visor opcional de fuentes

- **Decisión.** La vista previa de la línea de tiempo usa el compositor propio. libmpv solo podría usarse, opcionalmente, para ver un archivo de origen suelto y con una compilación LGPL verificada.
- **Por qué.** libmpv reproduce un archivo, no compone pistas con efectos y transiciones, y sus compilaciones habituales para Windows suelen incluir componentes GPL.
- **Consecuencia.** El trabajo de rendimiento de la reproducción se concentra en el motor propio.

## D-013. Interop nativo para la reproducción (9 de octubre de 2026)

**Decisión.**
- FFmpeg se usa con `FFmpeg.AutoGen.Bindings.DynamicallyLinked` 9.0.1.1. Las DLL se cargan por ruta absoluta con `NativeLibrary.SetDllImportResolver`, para no tomar nunca una FFmpeg GPL del PATH.
- Direct3D 11 y DXGI se usan con `TerraFX.Interop.Windows`.
- El audio sale por un WASAPI propio con `[GeneratedComInterface]`, y el reloj maestro es `IAudioClock::GetPosition` interpolado con QPC.

**Alternativas.**
- Vortice: funciona, pero SharpGen produce las advertencias IL2067 e IL2072 al compilar con AOT.
- XAudio2: su posición avanza en saltos de 10 ms, insuficiente para el reloj maestro.
- NAudio: es compatible con AOT, pero no da marca de tiempo QPC.

**Consecuencias.** La API de TerraFX trabaja con punteros crudos, así que el motor la envuelve en clases propias. Las evidencias están en [`archivo/sondeo-interop-aot.md`](archivo/sondeo-interop-aot.md), y el código del sondeo en la etiqueta de git `sondeo-interop-aot`.

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

## D-015. Visor de la fase 2: Win32 propio, shaders compilados al arrancar y caché de 2 × adelanto (9 de octubre de 2026)

**Contexto.** La fase 2 se cierra con una prueba humana de fluidez y sincronía, y para eso hace falta una ventana que use el motor real antes de que exista la interfaz WinUI 3 de la fase 4.

**Decisión.**
- El visor (`src/Rodaje.Visor`) es una ventana Win32 con TerraFX, una cadena de intercambio flip-model (`FLIP_DISCARD`, objeto de espera y latencia máxima de 1 cuadro) y `Present` con vsync. Pantalla completa sin bordes; no se usa el modo exclusivo.
- La conversión NV12/P010 a RGB (BT.601, BT.709 o BT.2020, en rango limitado o completo según declare el cuadro o el flujo; sin declarar, BT.601 hasta 576 líneas y BT.709 por encima, y rango completo solo en los formatos `yuvj`) se hace en un pixel shader que lee la capa del arreglo de texturas del decodificador con dos vistas (Y y UV), sin copias. El shader se compila al arrancar con `d3dcompiler_47.dll`, que viene con Windows desde 8.1, en vez de con `fxc.exe` en la compilación: así construir no depende del Windows SDK. Cuesta unos milisegundos al abrir la ventana y es compatible con Native AOT (P/Invoke de TerraFX).
- `ClipPlayer` vive en `src/Rodaje.Engine` para que la fase 3 lo convierta en una sesión de varias pistas. El audio se decodifica por delante en un hilo propio (`BufferedAudioSource`), para que el hilo de WASAPI solo copie memoria; si algo llega tarde se descarta en vez de sonar desplazado.
- La caché de cuadros guarda al menos `2 × adelanto + 2` cuadros (por defecto 16 y 6). `FrameCache` es LRU y mostrar un cuadro lo renueva, así que un cuadro adelantado envejece mientras se muestran los anteriores; con la proporción que sugería `PlaybackOptions` (adelanto + 2), el visor expulsaba lo adelantado antes de verlo y cada cuadro terminaba en un seek al keyframe (59 seeks y 1733 cuadros decodificados en 10 s de un H.264 de 23,976 fps, frente a 4 y 280 después del cambio).

**Alternativas.**
- fxc.exe en la compilación con el bytecode como recurso: arranque algo más rápido, pero obliga a tener el Windows SDK para compilar.
- Cambiar la política de `FrameCache` (expulsar primero lo ya mostrado). Es la corrección de fondo y conviene hacerla en la fase 3 junto con las capas; mientras tanto el tamaño mínimo lo garantiza `ClipPlayerOptions`.
- Esperar a WinUI 3 para la prueba humana: retrasaría la verificación de la sincronía hasta la fase 4.

**Consecuencias.** El visor es una herramienta de prueba, no la interfaz del editor. HDR (BT.2020, PQ, HLG) queda fuera. Durante un seek en reproducción la GPU decodifica desde el keyframe y la presentación se retrasa dos o tres refrescos; el audio sigue corriendo y el video lo alcanza.

## D-016. Compositor de la fase 3: `Rodaje.Render` con destino RGBA16F y mezcla en el espacio del video (9 de octubre de 2026)

**Contexto.** La fase 3 dibuja varias capas por cuadro. El visor convertía un solo cuadro NV12/P010 directo al búfer de la ventana; hace falta un paso intermedio que componga a tamaño de secuencia y que después se pueda presentar, leer para miniaturas o codificar.

**Decisión.**
- `Rodaje.Render` (sobre `Rodaje.Media` y `Rodaje.Playback`) tiene `Compositor`, `Presenter` y `GpuTimer`. `YuvRange`, `YuvToRgb` y `VideoLayout` pasan de `Rodaje.Engine.Presentation` a `Rodaje.Render`, y `ShaderCompiler` reúne la compilación con `d3dcompiler_47.dll` que antes vivía en el visor.
- Cada capa es un cuadrilátero con cuatro vértices calculados en la CPU (`LayerLayout`: ajuste "contain" y después escala, rotación horaria y desplazamiento en píxeles de la secuencia). El pixel shader convierte Y'CbCr con la matriz y el rango de la capa, recorta el relleno del decodificador y mezcla con "over" premultiplicado.
- Los bordes se suavizan con la distancia al lado en píxeles de destino, con el cuadrilátero un píxel más grande. Un borde alineado con la rejilla queda nítido y uno girado no muestra escalones, sin MSAA.
- El destino es `R16G16B16A16_FLOAT` por defecto. Conserva los 10 bits de P010, no redondea a 8 bits en cada capa translúcida y deja lugar a la mezcla en luz lineal y a los efectos. `Rgba8Unorm` queda como opción.
- La mezcla se hace sobre los valores con la curva del video, no en luz lineal; una opacidad de 0,5 promedia los códigos, como hacen la mayoría de editores en 8 bits.
- El muestreo de las capas es bilineal sobre el plano del decodificador, que no tiene mipmaps; el presentador genera los mipmaps de la composición cuando la ventana es más pequeña.
- La relación de aspecto del píxel y la rotación de los metadatos llegan por `SourceGeometry`, que entrega quien llama, porque `LayerFrame` todavía no las trae.

**Alternativas.**
- Destino RGBA8: la mitad de memoria y unos 0,15 ms menos con 8 capas a 1080p, pero pierde precisión con P010 y con capas translúcidas apiladas.
- Mezcla en luz lineal: más correcta físicamente, pero cambia el aspecto respecto a lo que el usuario espera de CapCut y otros editores. Se deja para cuando haya modos de fusión.
- MSAA para los bordes: costo de memoria ×4 en el destino y no resuelve el aliasing de la textura.

**Consecuencias.**
- Cifras en la RTX 3050 Laptop, en el peor caso de capas NV12 de 1080p a pantalla completa (mediana de 200 cuadros):

  | Capas | RGBA16F | RGBA8 |
  |---|---|---|
  | 1 | 0,10 ms | 0,05 ms |
  | 2 | 0,19 ms | 0,10 ms |
  | 4 | 0,36 ms | 0,19 ms |
  | 8 | 0,53 ms | 0,37 ms |

  Incluso con 8 capas, componer usa menos del 4 % de los 16,7 ms que dura un cuadro a 60 fps.
- El visor sigue dibujando con su `VideoRenderer`; cuando muestre varias capas usará `Compositor` y `Presenter`.
- Reducir una capa a menos de la mitad produce aliasing, hasta que existan proxies o un filtro de varias muestras.
- Cuando el motor ponga el SAR y la rotación en `LayerFrame`, el parámetro `geometry` de `Compositor.Render` se puede retirar.

## D-017. Motor de secuencia de la fase 3: `TimelineSession` con caché global por urgencia y pre-roll por pista (9 de octubre de 2026)

**Decisión.**
- `TimelineSession` (`src/Rodaje.Engine/Timeline`) reproduce una secuencia completa a partir de una instantánea inmutable (`TimelinePlan`) que se reconstruye con `Reload`/`Invalidate` sin mover ni detener el reloj. El reloj maestro sigue siendo el dispositivo de audio, que suena la mezcla de todas las pistas.
- Un decodificador por medio, abierto en su propio hilo la primera vez que hace falta y cerrado tras 5 s sin uso. Las capas que comparten medio comparten decodificador.
- Una sola caché de cuadros para todos los orígenes, con presupuesto global (512 MB) y tope por origen (lo retenido ocupa texturas del pool de D3D11VA). Expulsa primero lo que ninguna capa necesita y después lo que se mostrará más tarde; la urgencia es el tiempo que falta para mostrar cada cuadro, así que cerca de un corte el pre-roll le gana al adelanto lejano. Es la corrección de fondo que D-015 dejó pendiente.
- Pre-roll: 0,5 s antes de cada corte se decodifican los primeros 6 cuadros del clip siguiente de la pista.
- El audio se mezcla por delante en un hilo (`AudioMixer` sobre `BufferedAudioSource`), exacto a la muestra en los bordes de los clips, con un limitador suave (identidad hasta 0,9 y tanh hasta 1). Tras una edición se conservan 50 ms ya mezclados y el resto se vuelve a mezclar.

**Alternativas.**
- Un `PlaybackScheduler` por capa con su `FrameCache`: reutiliza código, pero no permite un presupuesto global ni repartir un decodificador entre capas.
- Un decodificador por clip: evita los seeks cuando un mismo medio está en dos pistas a la vez, a costa de más VRAM; se puede añadir si la prueba humana lo pide.

**Consecuencias.** Un mismo medio visible en dos pistas en posiciones lejanas obliga a su decodificador a buscar keyframes alternando entre ellas. Con un presupuesto muy justo la caché no reduce las ventanas: degrada a cuadros tarde, nunca a decodificar sin fin.

## D-018. Dirección de la interfaz (9 de octubre de 2026)

**Decisión.** La interfaz de la fase 4 sigue la maqueta aprobada en la conversación (artefacto «Rodaje — maqueta de interfaz»):
- **Distribución estilo CapCut:** riel de íconos con biblioteca a la izquierda, reproductor al centro, propiedades a la derecha y línea de tiempo abajo.
- **Pista principal magnética:** se puede desactivar.
- **Barra de la línea de tiempo estilo Vegas:**
  - herramientas normal, envolvente, selección y zoom;
  - ajuste;
  - ripple automático con tres modos;
  - fundido cruzado automático;
  - bloquear envolventes e ignorar grupos;
  - cortar y recortar inicio o final en el cursor;
  - marcadores y regiones.
- **Inspector como editor de keyframes:** cada parámetro tiene un rombo y una curva, y el botón «Guardar como transición» la deja lista para publicarla como paquete.
- **Atajos:** una página con búsqueda, cambio de tecla con un clic, intercambio cuando hay conflicto y estilos Rodaje, CapCut y Vegas.
- **Gestor de complementos:** paquetes y módulos de IA, cada uno en su propio repositorio de GitHub (Dathinel), con los programas en los que funciona.
- **Estética:** paleta grafito tipo DaVinci Resolve, acento naranja, esquinas casi rectas (2 px) y transiciones de 60 ms para que se sienta rápida.

**Por qué.** Daniel quiere la facilidad de CapCut con un toque profesional y rápido, y extras que se instalen aparte y sirvan a varios programas.

## D-019. Presupuesto de VRAM, varios decodificadores por medio y proxies en la vista previa (10 de octubre de 2026)

**Contexto.** La revisión independiente de la fase 3 midió, con cuatro pistas 4K HEVC y cortes cada 3 s, 3,7 GB de VRAM en una GPU de 4 GB: cada decodificador reservaba unas 52 superficies (las 16 referencias del peor caso de FFmpeg más 32 de sobra), la caché de 512 MB vivía dentro de esos pools y no acotaba nada, y los decodificadores viejos seguían abiertos 5 s. Un mismo archivo en varias pistas compartía un decodificador que iba y venía con seeks.

**Decisión.**
- Un presupuesto de VRAM para los pools de D3D11VA (3/8 de la VRAM dedicada: 1,5 GB en 4 GB; configurable). Cada pool cuenta sus referencias, 5 superficies retenidas fuera de la caché, 3 de búferes de NVDEC y su caché. El tamaño de la caché de cada decodificador se decide al crear su pool, con el tamaño real de las superficies, repartiendo el presupuesto entre una capa visible y dos de pre-roll; nunca más que lo adelantado, el pre-roll y dos de margen.
- Referencias según el nivel del flujo (H.264 anexo A, HEVC A.4.2) y no las 16 del peor caso: en 4K HEVC nivel 5.1 el pool baja de 17 a 7 superficies de referencia.
- Para abrir un decodificador se cierran primero los que nadie usa. El pre-roll y las pistas ocultas no pasan del presupuesto; una capa visible sin decodificador sí, y se cuenta. Con muchos abiertos, el tiempo de inactividad baja a la cuarta parte.
- Varios decodificadores por medio (hasta 4): las ventanas de un medio se agrupan en tramos y cada tramo va al decodificador que quedó más cerca. La caché pasa a ser por medio (un cuadro sirve a cualquier capa) y recuerda de qué decodificador salió cada cuadro.
- Un cuadro adelantado que no cupo frena el adelanto de ese tramo: decodificar los siguientes obligaba a volver con un seek (en 4K eran cientos).
- Cerrar un decodificador y soltar los cuadros que arrastran su pool ocurre en un ejecutor de servicio: el hilo de la ventana nunca espera. `Dispose` sí espera, porque después se libera el dispositivo.
- Un medio que no abre deja su capa vacía en vez de congelar el último cuadro del clip anterior, y se informa por capa.
- La vista previa usa proxies (`UseProxies`: Auto, Always, Never) a través de `MediaSourceResolver`; el proxy se coloca con el tamaño del original y, cuando uno queda listo, la reproducción pasa a él sin cortes: el decodificador viejo sigue con los cuadros debidos hasta que el nuevo tiene el siguiente.
- `PlaneViewCache` suelta las vistas de un decodificador cerrado en el cuadro siguiente (o con `TrimCachedViews` en pausa) y las que llevan 2 s sin usarse.

**Resultado** (RTX 3050 Laptop, cuatro pistas a 60 Hz durante 24 s, seis archivos HEVC con cortes cada 3 s): en 4K el pico de VRAM del proceso pasó de 3708 a 1417 MB (presupuesto 1485), los refrescos tarde de 359 a 14 y la peor espera de una capa de 483 a 134 ms, con 89 % de pre-roll a tiempo; en 1080p, 100 % de pre-roll y 0 tarde con 1003 MB. Un archivo en cuatro pistas con 2 s de desfase pasó de 3285 refrescos tarde (una capa esperó 6 s) a 0.

**Consecuencias.** En 4K el presupuesto deja cuatro capas y dos pre-roll: si en un mismo corte entran más medios nuevos que eso, alguno llega un poco tarde. Los saltos de más de 50 ms que se ven al presentar con cuatro capas 4K ya estaban antes y no vienen del motor (el `Tick` tarda 0,1 ms de mediana): son la GPU repartida entre cuatro flujos de NVDEC; para eso están los proxies.