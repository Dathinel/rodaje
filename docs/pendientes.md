# Pendientes

Lo que queda abierto en Rodaje, por prioridad. Cada punto se quita al cerrarlo y lo cerrado va a `CHANGELOG.md`. Lo que solo Daniel puede juzgar con el ratón está en `docs/pruebas-a-mano.md`; el orden de versiones y bloques de trabajo, en `docs/hoja-de-ruta.md`.

## Por arreglar

### Prioridad alta

1. **Keyframes de texto que saturan el editor y reproducción de muchos fps.** Con muchos keyframes en títulos el editor se satura, congela la imagen o distorsiona el audio; el material de 120 y 240 fps se traba al reproducir (`docs/hoja-de-ruta.md` B2; reporte de usuario).
2. **Autoprueba del exe AOT con cierre esporádico.** La autoprueba del ejecutable publicado con Native AOT se cierra 2 de cada 5 veces con código de error `0xC000027B` (`docs/hoja-de-ruta.md` B3).
3. **Pruebas intermitentes de la CI.** Fallos aleatorios en `Modules.Protocol` y `RenderCacheJobTests` en el ejecutor de Windows (`docs/hoja-de-ruta.md` B3; `tests/Rodaje.Modules.Protocol.Tests`, `tests/Rodaje.Export.Tests/RenderCache/RenderCacheJobTests.cs`).

### Prioridad media

4. **Investigar el arranque tras B2 a B13 (1.0.5).** La ventana lista pasa de 709 a 788 ms en caliente (+11 %) y de 1092 a 1161 ms en frío frente a la 1.0.3; con 3 capas el proceso gasta un 22 % más de CPU (144 % frente a 118 %). Aceptado para la 1.0.4 (sigue por debajo de 1 s). Perfilar con `build\medir-arranque.ps1` y las marcas de `--arranque` qué suman colaboración, SDK 10.0.403, plantillas y módulos (`docs/publicar-1.0.4.md`, «Cifras de la verificación»).
5. **Envío de trabajo a la GPU subió un 21 % con 8 capas.** La fase 3 de `IGpu` incrementó los milisegundos de despacho en GPU frente a la 1.0.3; aceptado para la 1.0.4 pero sin afinar (`docs/hoja-de-ruta.md` B5, `docs/mac-y-linux-fase3.md`).
6. **Duplicación en interpoladores y rasterizadores.** Duplicación de lógica entre `FrameInterpolator` y `SurfaceInterpolator`, y duplicación de trazado de segmentos y arcos entre los rasterizadores de `IGpu` (`docs/hoja-de-ruta.md` B3, `docs/mac-y-linux-fase5-7.md`, `src/Rodaje.Render/Interpolation/`).
7. **Preguntas de Daniel sobre HDR y cifras del RTX 3050.** Falta confirmar qué material real y qué diferencia frente a AHD se aceptan (D-059), y faltan las cifras de milisegundos por fotograma del RTX 3050 (`docs/hdr.md`, `docs/decisiones.md` D-059, `CLAUDE.md`).
8. **Fase F7 opcional de xmeml en intercambio.** Soporte para importar y exportar líneas de tiempo en formato FCP 7 XML (xmeml), pendiente como ampliación opcional (`docs/intercambio.md`, `docs/decisiones.md` D-062, `CLAUDE.md`).
9. **Preguntas de seguridad en verificación de paquetes.** Casos límite del PR #496: módulos con tamaño correcto y otro contenido, `integridad.json` sin firmar, índice con fecha futura y `FfmpegExecutable.VerifyPackageIntegrity` (`CLAUDE.md`).
10. **Compilación de shaders de flujo en el hilo de render.** Las siete pasadas con FXC se compilan en caliente en el primer cuadro y pueden pausar la vista previa; deben compilarse en segundo plano (`docs/interpolacion.md`).
11. **Falta de caída a mezcla si el flujo pierde cuadros.** Si la GPU pierde cuadros reproduciendo a cámara lenta con flujo óptico, hoy el flujo corre siempre sin caer a mezcla como preveía el diseño (`docs/interpolacion.md`).
12. **Retención de texturas del interpolador en el presupuesto.** Las texturas libres retienen espacio en `RenderTargetPool` hasta 240 cuadros y `Rent` no puede desalojarlas (`docs/interpolacion.md`).
13. **Rendimiento de cálculo de render en caché.** Calcular tramos pesados va a 19,3 cuadros/s en 1080p; alcanzar 60 cuadros/s (2× tiempo real) requiere reducir la sobrecarga de entrega y cierre de `ffmpeg.exe` (`docs/render-en-cache.md`, `docs/decisiones.md` D-060).
14. **Cuadros tarde en caché indexados por número y no por huella.** `LateFrames` usa números de cuadro planos; tras una edición en rizado sigue calculando en modo inteligente zonas cuyo contenido ya cambió (`docs/render-en-cache.md`, `docs/decisiones.md` D-060).
15. **«Calcular de entrada a salida» se cancela si se edita.** Al editar mientras calcula, el modo inteligente o manual descarta los trozos pedidos en lugar de reencolarlos con las nuevas recetas (`docs/render-en-cache.md`, `docs/decisiones.md` D-060).
16. **Parámetros de transiciones y efectos de plantilla sin inspector.** Las plantillas de transición y efecto se aplican solo con valores de fábrica; no tienen miniatura animada ni edición en inspector (`docs/decisiones.md` D-064, `docs/propuestas/plantillas-movimiento.md`).
17. **Control de fuente en plantillas pisa propiedades.** Escribe el objeto `font` completo en todos sus destinos y puede pisar el peso tipográfico de otras capas (`docs/decisiones.md` D-064).
18. **Alcance de rizado fijo en el panel de transcripción.** El panel fuerza siempre `RippleScope.Track` y no el alcance seleccionado en la línea de tiempo, para evitar desincronizaciones en pistas no copiadas (`docs/decisiones.md` D-061).
19. **Exportación de intercambio calcula pérdidas en el hilo de interfaz.** Al exportar EDL, FCPXML u OTIO, la clonación del proyecto y el cálculo del informe se ejecutan en la interfaz, ralentizando proyectos grandes (`docs/intercambio.md`).
20. **Solape con transición en FCPXML pierde la transición.** Al importar un FCPXML con clips solapados y transición, la importación los separa a otra pista y descarta la transición (`docs/intercambio.md`).
21. **Medición de peso y fluidez de CinemaDNG HDR con metraje real.** Cifras de intermedio FFV1 de 16 bits medidas solo con cartas sintéticas planas; falta medir megabytes por cuadro y tasa con material real (`docs/hdr.md`, `docs/pruebas-a-mano.md`).
22. **Pruebas de `Rodaje.Render.Tests` en Vulkan.** Quedan 114 pruebas omitidas atadas a D3D11 y dos que comprueban la caché perezosa de sombreadores que en Vulkan no existe (`docs/mac-y-linux-fase4.md`).
23. **Arranque en frío de render en caché sin shaders cacheados.** La primera composición sin caché previa tarda hasta 20 s en compilar; optimizar calentamiento inicial (`docs/render-en-cache.md`).

### Prioridad baja

24. **Copia de trabajo de la Cuadrilla.** Pasar a usar el clon de `Dathinel/cuadrilla` (`cuadrilla-publicable`) como copia de trabajo de la Cuadrilla en lugar de `mesa-agentes`: mover `datos\`, poner `rutas_equivalentes` y `pago.devin` en su `mesa.json` y reinstalar la skill.
25. **Cierre de PR #461 y borrado de rama auxiliar.** Cerrar el PR borrador «NO UNIR» y borrar la rama `revision/base-v1.0.1` (`CLAUDE.md`); decide Daniel (por confirmar).
26. **Contrato de mosaico RAW en un solo sitio.** Unificar el contrato entre `modulo-raw` y el editor en una sola definición (`CLAUDE.md`).
27. **Arrastrar plantilla de transición a un corte no responde.** Soltar una plantilla de transición sobre un corte arrastrándola no hace nada; solo se aplica desde el menú contextual (`docs/decisiones.md` D-064).
28. **Mediciones pendientes en plantillas de movimiento.** Sin medir con el equipo libre el cambio de valor hasta cuadro nuevo (≤ 33 ms), el rótulo de 4 capas y la biblioteca con 50 plantillas (`docs/propuestas/plantillas-movimiento.md` tabla 2.4).
29. **Histograma de corte de escena desborda en 6K y 8K.** `Histogram` en `Rodaje.Render` se despacha en una dimensión; por encima de 16,7 MP supera 65.535 grupos y no detecta cortes (`docs/interpolacion.md`).
30. **Reproducción de 1080p a 0,25× a 60 fps y exportación WARP sin medir.** Falta verificar 0 cuadros tarde en 10 s y ≥ 3 cuadros/s en WARP (`docs/interpolacion.md`).
31. **Previsión de render en caché no prioriza ventana visible ni transiciones.** Escanea la secuencia completa en vez de la ventana visible más 30 s alrededor del cursor; las transiciones no suman coste (`docs/render-en-cache.md`, `src/Rodaje.App/Services/RenderCache/RenderCacheScan.cs:38`).
32. **Borrado de caché de render en el hilo de interfaz.** «Borrar el render en caché» y la purga periódica eliminan archivos dentro del bloqueo del host, pudiendo trabar la ventana si hay decenas de gigabytes (`docs/render-en-cache.md`).
33. **Recuperación de texto borrado con audio externo enlazado.** «Recuperar» copia la velocidad del audio al video y solo halla video del mismo archivo original (`docs/decisiones.md` D-061).
34. **«Mostrar lo quitado» interrumpido por clips ajenos.** No muestra el texto cortado de un medio si en medio de dos clips hay un plano de otro medio (`docs/decisiones.md` D-061).
35. **Aviso tras reenlazar transcripción con distinta duración.** Falta advertir en el panel que el texto transcrito puede no coincidir si el archivo nuevo tiene otra duración (`docs/decisiones.md` D-061).
36. **Presupuestos y límites de OTIO incompletos.** El lector de OTIO no contabiliza marcadores ni metadatos de `metadata.rodaje`, y omite verificar `MaxTextLength` (`docs/intercambio.md`).
37. **Rebase de medio provisional en intercambio.** Al volver a vincular un medio provisional importado se desplazan todos sus clips, incluidos los agregados a mano posteriormente (`docs/intercambio.md`).
38. **Algoritmo cuadrático en sustracción de intervalos de compuestos.** `TimelineWalk.Subtract` es cuadrático en los clips de un compuesto; pasar a intervalos ordenados si hay miles de clips (`src/Rodaje.Core/Interchange/TimelineWalk.cs:192`).
39. **Consulta de HDR del adaptador en vez del monitor de la ventana.** `CompositionSwapChain.HasHdrOutput` y `GpuDevice.MaxOutputBitsPerColor` consultan todos los monitores del adaptador y no el que aloja la ventana (`src/Rodaje.App/Views/Player/CompositionSwapChain.cs:146`, `src/Rodaje.Media/Gpu/GpuDevice.cs:414`).
40. **Repetir autoprueba completa con el equipo libre.** Tras el PR #563 solo se corrió el paso «plantilla» de forma aislada; falta ejecutar `build/app.ps1 -Autoprueba` completa (`docs/propuestas/plantillas-movimiento.md`).
41. **Medición de transcripción de medio completo en RTX 3050.** Falta medir la transcripción del medio entero y la reproducción con el panel Transcripción abierto en RTX 3050 y WARP (`CLAUDE.md`, `docs/decisiones.md` D-061).
42. **Prueba de oclusión en flujo óptico pasa con mezcla.** `FlowReferenceTests` no distingue flujo de mezcla en oclusiones sintéticas; falta una prueba discriminante (`docs/interpolacion.md`).
43. **Costo de flujo óptico no computado en la previsión de caché.** `RenderCacheScan.Classify` no suma el coste de interpolar al clasificar los tramos pesados (`docs/interpolacion.md`).
44. **Tiempo de ida y vuelta en SmpteTimecode.** La ida y vuelta de las 24 h tardó 1,8 s en equipo cargado frente al objetivo < 1 s (`docs/intercambio.md`).
45. **Cadencia de proxy de MKV corto en intermedio HDR.** Matroska almacena tiempos en ms y la tasa media puede variar; comprobar que ningún cuadro se desplace medio cuadro (`docs/hdr.md`).
46. **R9 sin verificar: miniaturas del proyecto anterior al abrir otro.** Al abrir otro proyecto, la pista Video 2 se pintó un rato con una tira del anterior. Probable artefacto de los proyectos de repro, que repiten ids y la caché de miniaturas se indexa por ellos; confirmar con proyectos reales (`docs/repaso-raton-19-nov.md`).
47. **F2, F3 y F4 del repaso sin confirmar con ratón real.** Los arreglos (#625, #626, #635) salen del código, de pruebas y de la autoprueba; falta ver con el ratón que Supr en el panel Transcripción solo borra lo elegido y que clic y arrastre sobre las palabras seleccionan (vuelta 2 del repaso).
48. **`ThrottledProgress.Deliver` ejecuta el callback del llamador en el pool.** Una excepción ahí tumbaría el proceso; protegerlo como el hilo de exportación (`src/Rodaje.Packages`).
49. **Mover una frase por texto no mueve sus subtítulos.** `MoveWords` no arrastra la pista de subtítulos (D-061).
50. **Aviso de «Detectar ritmo» con muy pocos pulsos.** Con 4 pulsos (la voz) ajusta igual; decidir si avisa (pregunta de Daniel).
51. **F6 y R7 sin prueba automática.** Espacio sobre un botón con foco de teclado, F2 desde el buscador, y el cursor y la zona de agarre de 12 px del asa de fundido.
52. **Coste de comparar el proyecto en cada paso de la colaboración.** `SharedState.Capture` recorre y serializa todo el proyecto: 124 ms con 5 000 clips en una pista (Debug, `PatchCostTests`), muy por encima del objetivo de 2 ms del diseño (`propuestas/colaboracion.md` §9). Con 500 clips son 14 ms. Si `CollabSession` captura solo al cerrar cada gesto es aceptable; si no, hace falta guardar el contenido por pista y volver a serializar solo lo tocado (`docs/propuestas/colaboracion-api-parches.md`).
53. **Conversión `file:` ↔ `blob:` con archivos reales.** `Rodaje.Core.Collab.IBlobMap` la define y Core la usa, pero la implementación (leer la LUT o la máscara de archivo, guardarla como blob y resolverla en el otro equipo) es de `Rodaje.App` (B11); sin ella, una referencia `file:` viaja vacía. El análisis de estabilizar, los medios generados y la organización de la biblioteca siguen fuera del `.rodaje` (§9 del diseño).

## Solo Daniel

- **Repaso con ratón de lo nuevo desde la 1.0.3.** Probar en la interfaz: render en caché, editar por texto, intercambio EDL/FCPXML/OTIO, flujo óptico, plantillas de movimiento, CinemaDNG en HDR de 12 bits y los arreglos de B2 y B3 (`docs/pruebas-a-mano.md`, `docs/hoja-de-ruta.md` B9).
- **Publicación de `Dathinel/rodaje-plantillas`.** Decidir si el repositorio privado con las 22 plantillas iniciales se publica en GitHub bajo CC0 (`docs/decisiones.md` D-064, `CLAUDE.md`).
- **Publicación de la versión 1.0.4.** Firmar el catálogo, verificar paquetes, crear la etiqueta Git y publicar la release de Windows (`docs/hoja-de-ruta.md` B8, `build/distribuir.ps1`).
- **Calidad IA con RIFE en flujo óptico (F7).** Decidir si se aborda la interpolación por red neuronal en `modulo-vision` (`docs/interpolacion.md`, `docs/decisiones.md` D-063).
- **Convivencia de licencias antes de distribuir.** Consultar la convivencia de la GPLv3 con los términos de redistribución del Windows App SDK (`docs/legal.md`, `docs/hoja-de-ruta.md` B8).

## Aparcado

- **Linux, fases 9 y 10 (GPU completa y AppImage).** Requieren validación de Daniel con Ubuntu 24.04 en USB y tarjeta NVIDIA (`docs/hoja-de-ruta.md`, `docs/mac-y-linux-fase8.md`, `CLAUDE.md`).
- **Defectos conocidos de Uno Platform en Linux.** Tiempo de transporte fijo en 0:00.000 en `PlayerView` por `x:Bind`, 48 instancias de `LineGeometry` no implementadas (curvas de inspector, regla, mezclador) y tamaño/posición de ventana fijos en X11 (`docs/mac-y-linux-fase8.md`, `src/Rodaje.App/Shell/MainWindow.Desktop.cs:21`).
- **Mac, fase 11.** Adaptación para macOS arm64 aparcada por requerir cuenta de desarrollador de Apple para firma y notarización (`docs/hoja-de-ruta.md`, `CLAUDE.md`).
- **`modulo-raw` en Linux y macOS.** LibRaw no publica binarios y los tamaños de `long` en 64 bits rompen los desplazamientos de memoria medidos en Windows; requiere compilar LibRaw 0.22.2 en CI (`docs/mac-y-linux-fase5-7.md`, `CLAUDE.md`).
- **Reducción sin mips en exportación con Vulkan.** Fuera de D3D11 el contrato de `IGpu` carece de vistas de tramos de mips, provocando leve aliasing al reducir a menos de 0,5× (`docs/mac-y-linux-fase4.md`, `src/Rodaje.Render/Export/ExportFrameConverter.cs:88`).
- **Servicio mDNS de Sala sin probar en Mac real.** Descubrimiento por red local no validado en hardware físico Apple (`docs/mac-y-linux-fase5-7.md`, `CLAUDE.md`).
- **Revelado RAW en tiempo real sobre la línea de tiempo (fase E).** Sin plan; hoy la importación genera archivos intermedios ProRes o FFV1 (`docs/hdr.md`).
- **Atajos de texto y teclado en Linux y Mac.** Búsqueda de fuentes del sistema sin fontconfig ni CoreText, métricas `hhea` sin USE_TYPO_METRICS, texto solo de izquierda a derecha y nombres de teclas en inglés sin XKB (`src/Rodaje.Text/TitleFontSet.cs:103`, `src/Rodaje.Text/TitleLayout.cs:24`, `src/Rodaje.App/Views/Shortcuts/SystemKeyboardLayout.Desktop.cs:6`).
- **Diálogos de archivo en Linux modales y sin repintado.** `zenity`/`kdialog` congelan el repintado de la ventana al esperar en el hilo de interfaz; pasar a portal de escritorio si hace falta (`src/Rodaje.App/Shell/FileDialogs.Desktop.cs:9`).
- **Visor de Linux por lectura a CPU y copia.** La reproducción pasa por `ReadbackRgba` a la CPU en lugar de presentación directa por swapchain Vulkan (`src/Rodaje.App/Views/Player/PlayerEngine.Desktop.cs:73`).
- **Protocolo PulseAudio sin confirmación de servidor.** `Pulse.Close` no espera la confirmación asíncrona de aceptación del servidor (`src/Rodaje.Audio/Interop/Pulse.cs:256`).
