# Registro de decisiones

Este registro guarda el porqué de cada elección importante de Rodaje. Una decisión no se borra cuando cambia: se marca como reemplazada y se añade otra que explique el cambio. Todas las de esta página son del 9 de octubre de 2026.

---

## D-001. Nombre: FotoToma (reemplazada por D-014)

- **Decisión.** El nombre de trabajo "FotoShot" pasa a FotoToma ("toma" es cada plano filmado). Una búsqueda web básica no encontró software con ese nombre.
- **Por qué.** "FotoShot" queda a una letra de Photoshop (marca de Adobe) y ya existen apps de fotos llamadas "PhotoShot" (Vyro AI, H-Tech Labs). Se descartó un nombre en inglés porque el proyecto se piensa en español.
- **Consecuencia.** Una búsqueda básica no es una búsqueda de marcas: antes de publicar hay que consultar USPTO, EUIPO y SIC Colombia en las clases 9 y 42 de Niza (lista en `docs/legal.md`).

## D-002. Repositorio privado con licencia MIT (licencia sustituida por D-030)

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
- Con los efectos de la fase 5 el camino rápido no cambió (8 capas 1080p: 0,54 ms) y 3 capas 4K con color, LUT, desenfoque y superponer cuestan 2,7 ms; la tabla completa está en [fase-5.md](fase-5.md#medido-en-la-integración-10-de-octubre-rtx-3050-laptop).
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

## D-020. Línea de tiempo: marcadores en el formato, gestos que se funden y dibujo con XAML reciclado (10 de octubre de 2026)

**Decisión.**
- **Formato 2.** Las secuencias tienen `markers` y `regions`; la migración 1 → 2 agrega las listas vacías y el esquema de la versión 1 se conserva. El modo de ripple «pistas, marcadores y regiones» es `RippleScope.AllTracksAndMarkers`: lo que cae dentro de un tramo que se cierra queda en su inicio y una región vacía se borra.
- **Gestos.** Dentro de un grupo del historial los comandos con la misma clave también se funden: un arrastre ejecuta un paso por movimiento para que el reproductor lo vea en vivo, pero guarda una sola copia antes y otra después, no una por movimiento.
- **Pista principal magnética.** Reordenar es `ReorderOnMainTrack` (empaqueta desde donde empezaba la pista y corre los enlazados lo mismo); entrar o salir de ella abre o cierra el hueco también en las pistas de los enlazados, así el audio no se desincroniza aunque el clip que entra no tenga sonido.
- **Dibujo.** Elementos de XAML reciclados en una ventana de tres anchos de pantalla y no Win2D ni una cadena de intercambio propia: es apto para AOT, no suma dependencias y deja el texto y la accesibilidad a WinUI. Bloques sin trazo ni esquinas y tira de película como degradado repetido, porque lo que se rasteriza vuelve a rasterizarse en cada cambio de zoom.

**Por qué.** Arrastrar debe sentirse inmediato y deshacerse de una vez; el formato necesitaba marcadores para el ripple de Vegas.

**Consecuencias.** Desplazarse va a 60 cuadros por segundo con 200 clips; el zoom continuo queda en unos 45 (estirando lo dibujado y rearmándolo al terminar), con el tiempo en el dibujo de XAML y no en el código propio. Si hiciera falta más, el siguiente paso es dibujar las pistas con `SpriteVisual` de composición como los medidores.

## D-021. Miniaturas en la CPU y formato `.paquete` v0 (10 de octubre de 2026)

**Contexto.** La biblioteca y la línea de tiempo necesitan portadas, tiras de miniaturas y formas de onda sin frenar la interfaz ni la reproducción, y la propuesta de módulos aprobada pide un formato de paquete compartible entre programas.

**Decisión.**
- **Miniaturas por software.** Un `ThumbnailDecoder` propio en la CPU, sin filtro de desbloqueo y saltándose los cuadros que no son referencia, con swscale a BGRA. D3D11VA más un escalado en la GPU daría miniaturas algo más rápidas, pero gastaría superficies del presupuesto de VRAM de la reproducción (D-019) y obligaría a leer la textura de vuelta; en la CPU una miniatura 1080p sale en unos 50 ms, que alcanza.
- **No exactas al cuadro.** Una miniatura muestra el último cuadro decodificado que no pasa del instante. Exactitud al cuadro es cosa del reproductor.
- **Rejilla de potencias de dos** para los instantes de una tira y **clases de alto** (32 a 256 px): al hacer zoom o desplazar se reutiliza casi todo.
- **Caché en memoria y en disco** con la huella del original (la misma de los proxies) en la clave; un archivo que cambia no reutiliza nada viejo.
- **`IClipVisuals` no bloquea nunca:** devuelve lo que hay, pide lo que falta y avisa con `Changed` en el hilo de la interfaz.
- **`.paquete` v0** en una biblioteca sin dependencias (`Rodaje.Paquetes`), con manifiesto validado con `JsonDocument` (apto para AOT), instalación que nunca escribe fuera de su carpeta y referencias estables `paquete@versión/elemento` ([formato](formato-paquete.md)). Los módulos de IA se declaran pero no se ejecutan hasta la fase 7.

**Resultado.** Biblioteca con 200 medios 1080p: 60 cuadros por segundo al desplazar despacio y de golpe, sin ningún intervalo de más de 33 ms; portadas de la primera pantalla en 169 ms en frío y 107 ms con la caché en disco.

## D-022. Se retira el visor Win32 (10 de octubre de 2026)

**Contexto.** El visor de las fases 2 y 3 (D-015) era una herramienta de prueba. La app WinUI 3 ya reproduce, edita y mide (`app.ps1 -Medir N`) con el mismo motor y el mismo compositor.

**Decisión.** Se borran `src/Rodaje.Visor`, sus pruebas, `herramientas/visor.ps1`, la acción `ABRIR.bat visor` y `herramientas/sondeo-winui.ps1`. El código queda en la etiqueta de git `visor-fase3`; el informe del sondeo pasa a `docs/archivo/sondeo-winui.md`.

**Consecuencias.** Un solo ejecutable que mantener. D-015 y D-016 siguen valiendo para el motor y el render aunque nombren el visor.

## D-023. Estilos de atajos con nombres propios (10 de octubre de 2026)

**Decisión.** Los estilos de D-018 se llaman «Rodaje», «Ágil» y «Clásico» (`ShortcutPreset.Rodaje`, `Agile` y `Classic`). Ni la interfaz ni el código nombran otros programas; las referencias de experiencia de uso quedan en estas decisiones y en `docs/archivo/`. `atajos.json` y `ajustes.json` guardados con los nombres anteriores se leen como Ágil y Clásico y se reescriben con los nuevos; un estilo desconocido vuelve a Rodaje sin dar por dañado el archivo.

## D-024. Restablecer un parámetro y quitar keyframes, con aviso (10 de octubre de 2026)

**Decisión.**
- **Restablecer.** Cada parámetro del inspector tiene su botón y la sección Transformación, «Restablecer todo». Sin keyframes, el parámetro vuelve a su valor de fábrica sin preguntar. Con keyframes, primero sale un aviso que dice cuántos se borrarán (en «Restablecer todo», el total), con «Quitar keyframes y restablecer» y «Cancelar»; Cancelar es el botón por defecto y Esc también cancela. El botón se apaga si el parámetro ya está en su valor y sin keyframes.
- **Quitar keyframes** es una acción aparte, en el menú contextual del parámetro, con el mismo aviso: borra la animación y deja el parámetro fijo en el valor que se ve en el cursor.
- Las dos son un solo comando del núcleo (`Edits.ResetParameters`) y un solo paso del historial: Ctrl+Z lo devuelve todo de una vez.

**Por qué.** Restablecer suele dejar el parámetro como recién creado, pero borrar una animación sin avisar es fácil de hacer sin querer y difícil de notar. Separar «quitar keyframes» de «volver al valor» evita confundir las dos cosas.

**Consecuencias.** Un clic más cuando hay animación; quien quiera volver solo un keyframe al valor de fábrica escribe el número.

## D-025. Contrato de la fase 5: registro de efectos, formato 3 y transiciones en el corte (10 de octubre de 2026)

**Contexto.** El plan de la fase 5 ([fase-5.md](fase-5.md)) reparte el trabajo entre seis agentes en paralelo; antes hace falta un contrato que no cambie mientras trabajan.

**Decisión (aprobada por Daniel).**
- **Transiciones centradas en el corte y sin solapar clips.** Viven en la pista (`Track.Transitions`: `from`, `to`, `duration`, `parameters`, `options`); cada lado usa el material sobrante y, si no hay, congela el cuadro del borde. Seis propias (fundido cruzado, a color, barrido, empujar, zoom y desenfoque) y `rodaje.preset` para las de paquete, que cae a fundido cruzado si falta el paquete. `TransitionOps.Reconcile` corre al final de cada edición, dentro del mismo paso del deshacer.
- **Texto y títulos en una fase 5b aparte.**
- **Paquetes solo con primitivas**, sin shaders propios: filtros y efectos se copian al proyecto (con `options.source`), LUT y transiciones se referencian.
- **Registro estático** (`Rodaje.Core/Effects/EffectRegistry`) con códigos enteros estables, destino, etapa, parámetros (hasta 16, con valor por defecto y límites) y opciones, en `FrozenDictionary`, sin reflexión. Un tipo desconocido se conserva intacto.
- **Formato 3:** `EffectInstance.Options`, `Track.Effects` (keyframes en tiempo de la secuencia) y `Track.Transitions`. La migración 2 → 3 convierte `rodaje.volume` lineal en `rodaje.audio` en dB; el volumen se guarda siempre en `gainDb`, con −60 dB como silencio.
- **Hacia la GPU:** `LayerFrame` suma `Effects` (`LayerEffect` con `EffectValues` en `InlineArray`, por valor), `Blend` (13 modos) y `Transition`; sin nada de eso es el camino rápido de siempre. Los recursos (LUT) se piden con `IEffectResources`, que nunca bloquea; el motor recibe el contenido de paquetes por `IEffectContent` y no conoce `Rodaje.Paquetes`.

**Consecuencias.** Un proyecto guardado con la fase 5 no abre en versiones anteriores (la `.bak` conserva el formato 2). Cambiar un código del registro rompe la tabla de la GPU: una prueba los fija.

## D-026. Contrato de la fase 5c: velocidad como propiedad del clip y formato 4 (10 de octubre de 2026)

**Contexto.** El plan de la fase 5c ([fase-5c.md](fase-5c.md)) reparte la velocidad entre siete agentes; el formato y la aritmética del tiempo no pueden cambiar mientras trabajan.

**Decisión.**
- **`Clip.Speed`** (`ClipSpeed`: `Rate` racional de 1/10 a 100, `Curve` lineal a tramos en centésimas de 0,1× a 10× con puntos en diezmilésimas de `Span`, `Offset`, `Reverse`, `Freeze`, `PreservePitch`, `FrameSampling`) y `Clip.Duration` guardada cuando hay velocidad. `rodaje.speed` queda reservado (código 8) y oculto; la migración 3 → 4 quita sus instancias.
- **La curva vive en el tiempo de la línea de tiempo**, no en el del origen: así el origen recorrido es cuadrático por tramos y su inversa sale con una raíz entera, todo exacto en `Int128` (`RetimeMath`). Con la curva en el origen la duración sería un logaritmo.
- **`ClipTimeMap` es la única puerta.** Calcula cada instante desde el ancla (`SourceIn`, o `SourceOut` en reversa) y `Offset`, con `⌊I(offset + τ)⌋ − ⌊I(offset)⌋`; el tramo del origen mide `⌈I(offset + D)⌉ − ⌊I(offset)⌋` y el validador exige que coincida. Dividir y recortar mueven `Offset`, así que ningún cuadro cambia. Fuera del clip (transiciones) sigue a la velocidad del borde. A 1× es la cuenta de siempre.
- **Cotas:** `|Offset| + Duration ≤ 2^44` ticks (unas 6,9 h); numeradores hasta 2^104 y discriminantes hasta 2^112.
- **Validador:** además de la velocidad, limita los parámetros de efectos y transiciones registrados a su rango (las ediciones los acotan) y rechaza ids con caracteres de control.
- **Motor:** `VideoClipPlan.TimeMap` y `AudioClipPlan.Retime` llevan el mapeo sin lógica nueva; `TimelinePlan` se parte en `Video` y `Audio`.

- **Magnetismo al cambiar la velocidad** (Daniel, 11 de octubre, revisado el mismo día): en cualquier pista, si el clip se acorta por velocidad (`SetSpeed`, `RateStretch`, `SetCurve`) el siguiente se une y la transición del corte se queda; si crece, respeta los huecos y solo empuja lo que pisa, lo justo (arrastrar a pasos da lo mismo que de una vez). `FreezeFrame` inserta y corre siempre. Los comandos reciben el `RippleScope` de la sesión: con el de pista, cada pista del grupo se corre con su propio Δ; con «todas las pistas», todas (y los marcadores en ese modo) con un mismo Δ. A 1× «mantener tono» no se toca.

**Consecuencias.** Un proyecto guardado con la fase 5c no abre en versiones anteriores (la `.bak` conserva el formato 3). Un clip con velocidad no puede pasar de unas 6,9 horas en la línea de tiempo. Las lecturas directas de `SourceIn`/`SourceOut` fuera del núcleo siguen bien a 1× y las cambian los agentes de la fase.

## D-027. Inspector en secciones y tema grafito con más contraste (11 de octubre de 2026)

**Contexto.** Daniel probó la app: en Propiedades los nombres se solapaban con los números («Posición X 2 keyfram»), la transformación se sentía llena y, aunque el tema se ve profesional, las capas de gris casi iguales (fondo, paneles, campos y botones a 1,03–1,1:1 entre sí y bordes a 1,2:1) se confunden para quien no está acostumbrado.

**Decisión.**
- **Propiedades en secciones plegables** con encabezado: «Posición y tamaño», «Opacidad y mezcla» (el modo de mezcla pasa de Filtros a aquí, porque es del clip y no un filtro) y «Audio». Una fila por parámetro con etiqueta de ancho fijo, puntos suspensivos y ayuda; restablecer y el rombo atenuados hasta pasar el ratón; la navegación de keyframes y la curva solo aparecen si el parámetro está animado. «Restablecer todo» y «Guardar como transición…» van al menú «⋯» (D-024 sigue igual: mismos avisos y un paso del historial).
- **Contraste medido (WCAG AA)** en `Palette.xaml`, sin tocar el acento: texto ≥ 4,5:1, texto secundario y desactivado ≥ 3:1, y bordes de campos, botones y desplegables ≥ 3:1 contra el panel (`ControlBorderBrush`). Las superficies se separan con escalones de luminosidad claros (fondo #101010, panel #1D1D1D, cabeceras #262626, controles #2C2C2C, encima #3A3A3A, pulsado #4A4A4A: los botones siguen iluminándose al pulsarlos).

- **Ajustes de Daniel tras verlo (11 de octubre):** las pestañas nunca se cortan (íconos y, si hace falta, desplazamiento horizontal); el panel derecho arranca en 380 px (ajustes versión 2: quien tenía menos pasa a 380) y los paneles laterales ceden con la ventana estrecha; los rombos de keyframes se ven siempre igual (lleno si hay animación y un recuadro para el del cursor, nunca transparencia); Filtros, Velocidad y Transición usan las mismas secciones y filas que Propiedades, y las listas marcan lo elegido en gris en vez de naranja.

**Consecuencias.** Más bordes visibles en toda la app. Con el panel en 320 px cada parámetro ocupa dos líneas; desde 340 px, una.

## D-028. Arquitectura de la exportación: tubería a `ffmpeg.exe` y LGPL en la v1 (11 de octubre de 2026)

**Contexto.** El plan de la fase 6 ([fase-6.md](fase-6.md)) reparte la exportación entre seis agentes (A–F) y la nube; antes hace falta fijar el camino del cuadro, el color y la licencia.

**Decisión (las tres preguntas del plan, respondidas por Daniel el 11 de octubre).**
- **Licencia:** LGPL en la v1, como recomendaba el plan (detalle abajo).
- **9:16 con secuencia 16:9:** barras por defecto y un botón visible «Cambiar la secuencia a 9:16» (deshacible).
- **Rendimiento, elegido en el diálogo cada vez que se exporta** (`ExportSettings.Performance`): «Alto rendimiento (menor tiempo)» con prioridad normal/alta y la vista previa en pausa o al mínimo, o «Bajo rendimiento (app más fluida)» con prioridad baja, para seguir editando.
- **Camino del cuadro:** `Project.Clone()` → `OfflineTimeline.Render(frame)` (Engine; exacto, sin reloj ni proxies; lo que la vista previa tolera aquí es `ExportException` o un «continuar sin» explícito) → `Compositor` a resolución completa → `ExportFrameConverter` (Render) → `IExportSink.WriteVideo`. La exportación usa un **`GpuDevice` propio** con su hilo y la prioridad del modo de rendimiento, y un presupuesto de VRAM aparte; con menos de 4 GB la vista previa pasa a proxies y Mitad mientras exporta.
- **Y'CbCr en la GPU:** `ExportFrameConverter` escala, ajusta la forma (barras por defecto, recorte o estirado), convierte R'G'B' → Y'CbCr y deja el cuadro en un anillo de texturas `Staging`; a la CPU baja ya en NV12/P010 o en planos (`ExportVideoFrame` con planos y pitch), nunca en RGBA. **BT.709 de rango limitado** (BT.601 en SD), croma 4:2:0 «left» y tramado al bajar a 8 bits; 10 bits SDR con P010. Sin HDR.
- **Tasa constante:** el cuadro `n` sale en `n / FrameRate` de la secuencia; nada de VFR ni de marcas heredadas de los originales.
- **Audio a un temporal:** `OfflineAudio` (el `MixRenderer` continuo, sin margen de recarga) escribe la mezcla del rango en un WAV temporal antes del video; así no hay dos tuberías que se bloqueen entre sí y el audio se prueba muestra a muestra.
- **Salida v1 por tubería a `ffmpeg.exe`** (el mismo exe LGPL verificado de los proxies, dentro del `ChildProcessJob`): `rawvideo` por la entrada estándar, el WAV como segunda entrada, `-progress` para el avance, escritura atómica (`.rodaje-tmp` y renombrar), espacio en disco comprobado y limpieza tras un apagón. `NvencDirectSink` (textura directa a NVENC) solo en una ronda 6.2 si las cifras lo piden.
- **Licencia LGPL en la v1:** H.264 y HEVC solo por GPU (NVENC, AMF, QSV) o por Media Foundation, cuyos fabricantes cubren las patentes; la CPU exporta AV1 (SVT-AV1) y VP9 (libvpx); maestros FFV1 y DNxHR; audio AAC preferido con `aac_mf`, además `aac`, Opus, MP3 (LAME), FLAC y PCM. **kvazaar y OpenH264 quedan fuera de los valores por defecto** (contradicen la mitigación de patentes de `legal.md`): solo se usan si el usuario los elige. x264 sería un complemento GPL aparte, con su propio `ffmpeg.exe` y el mismo protocolo, más adelante y tras consultar a alguien de licencias.
- **Alcance profesional** (Daniel, 11 de octubre; `fase-6.md`): el contrato cubre ya todo lo que permite el FFmpeg LGPL aunque la v1 implemente primero lo común (MP4/MOV/MKV/WebM, H.264/HEVC por GPU, AV1, ProRes, AAC/Opus/PCM). Lo que necesite otra licencia (x264, x265) va como complemento en un repo de Dathinel, con su propio `ffmpeg.exe` y el mismo protocolo de tubería.
- **Contrato:**
  - `Rodaje.Media/Export`: `IExportSink` (con `ExportSinkRequest`: pasadas, metadatos y capítulos), `ExportVideoFrame` (hasta 4 planos con pitch), `ExportVideoFormat`, `ExportAudioFormat`, `ExportPixelFormat` (NV12, P010, YUV 4:2:0/4:2:2/4:4:4 de 8 y 10 bits, YUVA, RGB/RGBA de 8 y 16), `ExportColor` (BT.709/BT.601, rango, sRGB para imágenes), `ExportContainer` (16, de MP4 a secuencias de imágenes y solo audio), `ExportException` con `ExportErrorCode` estables, `EncoderSpec` con capacidades (perfiles con sus píxeles, niveles, modos de `RateControlMode`, escala de calidad, dos pasadas, cuadros B, presets, solo intra, alfa, frecuencias y canales; `InDefaults = false` en kvazaar y OpenH264) y `ExportCompatibility`, la matriz contenedor × códec × píxel × perfil × audio con el motivo de cada rechazo.
  - `Rodaje.Engine/Offline`: `OfflineTimeline.Render(long frame, CancellationToken)` y `RenderAt(Time, …)` para la conversión de tasa; `OfflineAudio` (lectura continua, `MeasureLoudness` EBU R128 y `WriteWave` con ganancia).
  - `Rodaje.Render/Export`: `ExportFrameConverter` (`Submit`, `SubmitBlend`, `TryRead` con un anillo de `Staging`).
  - `Rodaje.Export` (proyecto nuevo, sin interfaz): `ExportSettings` (contenedor; video con códec, `RateControl` —modo, CRF/CQ, tasas, búfer, pasadas, tamaño objetivo—, perfil, nivel, preset, tune, GOP, bits, croma, alfa, color, tasa con conversión, tamaño, forma, filtro y recorte; audio con códec, tasa, perfil, frecuencia, canales, PCM y sonoridad LUFS; metadatos, capítulos desde marcadores, `faststart` y `Performance`), `ExportRequest`, `ExportPreset` (de fábrica, del usuario o de un `.paquete`), `ExportProgress`, `ExportJobState` y `ExportSettingsValidator`.

**Consecuencias.** La exportación necesita `ffmpeg.exe` junto a las DLL, igual que los proxies. La tubería cuesta una copia por cuadro a la CPU (a 4K60 unos 750 MB/s); el prototipo del paso 0 mueve 2,8–3,8 GB/s a 4K y da 250–290 fps sin codificar con 3 capas 1080p, así que la v1 no necesita `NvencDirectSink` (cifras en `fase-6.md`). Un equipo sin GPU ni Media Foundation (Windows N) no exporta H.264: se le ofrece AV1 o VP9.

## D-029: Texto y títulos (5b), decisiones de producto (11-oct)

Daniel aceptó las cuatro recomendaciones de la propuesta (PR #11):
1. **Animación del bloque entero en la v1**; por letra o palabra, más adelante.
2. **Fuentes empaquetadas desde el principio** (paquete `rodaje-fuentes`, unas 10 familias OFL/Apache sin modificar), solo de paquetes que instala la persona.
3. **Entrada y salida pegadas a los bordes del clip** (siguen al recorte), con «convertir en keyframes» opcional; al dividir, los bordes internos quedan sin animación.
4. **El proyecto guarda una copia del estilo**: actualizar un paquete no cambia títulos de proyectos existentes (igual que los filtros).

## D-030. Licencia PolyForm Noncommercial 1.0.0 en lugar de MIT (11 de octubre de 2026)

- **Decisión.** El código propio de Rodaje pasa de MIT a la PolyForm Noncommercial License 1.0.0, con `Required Notice: Copyright (c) 2026 Daniel (Dathinel)`. Sustituye a la licencia de D-002. Daniel es el único titular y el repositorio es privado y nunca se publicó bajo MIT, así que no hay copias MIT de terceros que respetar.
- **Por qué.** Daniel quiere compartir el programa para uso personal, educativo y de investigación, pero impedir que terceros lo vendan. No tiene pretensión comercial propia; el uso comercial queda para permisos escritos caso a caso.
- **Alternativas descartadas.**
  - MIT: permite que cualquiera venda el programa o una copia apenas cambiada.
  - GPL o AGPL: también permiten la venta (solo obligan a dar el código), y ninguna de las dos admite añadir una restricción no comercial.
  - Fuente cerrada: impide el estudio y la modificación que Daniel sí quiere permitir.
- **Consecuencias.** Es una licencia de código disponible, no de código abierto: no está aprobada por la OSI y en los textos públicos no se llama a Rodaje «código abierto». «No comercial» tiene zonas grises (uso dentro de una empresa, creadores que monetizan sus videos, formación de pago) que se llevan al abogado. La compatibilidad con la LGPL de FFmpeg se mantiene; el Windows App SDK necesita un EULA complementario (`legal.md`). Los repositorios de contenido siguen en CC0 hasta que Daniel decida. Pendientes en [`pendientes-legales.md`](pendientes-legales.md).


**Nota a D-030 (11-oct):** los complementos de contenido (`rodaje-transiciones`, `rodaje-filtros` y los que vengan) siguen en CC0: la licencia no comercial es solo del programa. El nombre «Rodaje» no está registrado en la SIC; la consulta legal se aplaza hasta antes de la versión pública (hoy es de uso propio y cercanos).
