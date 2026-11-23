# Hoja de ruta de Rodaje: de la 1.0.3 a la colaboración (Daniel, 19-nov-2026)

Orden de trabajo de Rodaje, con el mismo método que Unicomp: bloques en orden, cada uno con sus pasos, su «Hecho cuando» y su estado. Lo abierto por prioridad está en `pendientes.md`; lo que solo Daniel puede juzgar con el ratón, en `pruebas-a-mano.md`; lo cerrado, en `CHANGELOG.md`.

**Daniel, 20-nov (cambio):** la **1.0.4 lleva todo lo planeado, incluida la colaboración (B10 a B16)**; solo queda para la 1.0.5 lo de «Ideas guardadas» (editor automático vertical e interfaz para nuevos). Una vez publicada, Daniel la prueba con una persona **en otro país**: tiene que funcionar por internet y salir **extremadamente pulida**. Además: ir **más rápido**, con jefes en paralelo. **Sin Linux de momento** (queda aparcado al final de este documento; Mac también: exige registro de Apple).

## Cómo se trabaja

1. **Un bloque, una rama.** Cada bloque va en su rama y su worktree (`.claude\worktrees\`), con un PR por tema, CI verde y squash. Nada de auto-merge, `--admin` ni push forzado. No se toca el PR #461 («NO UNIR»).
2. **Un jefe por bloque.** El jefe reparte lo mecánico con la **Cuadrilla** (ChatGPT o Devin para lo que bloquea; Gemini para documentos, guías y revisiones que corren al lado; nunca Gemini en lo que bloquea) y revisa cada diff. No se hace a mano lo que la Cuadrilla puede hacer.
3. **Revisión independiente por bloque, no por pieza.** Al cerrar un bloque, otro modelo lo revisa entero (lógica, estructura, ahorro de tokens, preguntas). Lo corto se arregla en el momento; lo largo entra en `pendientes.md`.
4. **Estado de cada paso:** ☐ sin empezar · ◐ en curso (con jefe, rama o PR) · ✔ hecho (con PR). Se actualiza al unir, en el mismo PR.
5. **Al cerrar un bloque:** se quitan sus puntos de `pendientes.md`, entra una línea en `CHANGELOG.md` (`[Sin publicar]`) y lo que necesita el ratón de Daniel va a `pruebas-a-mano.md`. **No se para por eso**: se sigue con el bloque siguiente; lo que Daniel encuentre entra como corrección.
6. **PC compartido con otra sesión que toma datos.** Todo lo local va en segundo plano, con prioridad baja, `DOTNET_PROCESSOR_COUNT=2`, `-m:2` y una sola compilación o tanda a la vez. Si existe `D:\Programas de uso libre\medicion-en-curso.txt`, no se lanza nada local hasta que desaparezca (si lleva más de 40 min sin cambios, se pregunta a la sesión de Unicomp). **Nunca se abre Rodaje a la vista**: solo `--autoprueba`, `--arranque` y `--medir` en segundo plano con un exe publicado desde `main` actual.
7. **Solo se para por** publicar, licencias, decisiones irreversibles o claves. La clave privada del catálogo nunca se imprime ni se copia.
8. **El orden de abajo no se salta.** Un bloque que depende de otro no empieza antes de que el otro se una.

## Versión 1.0.4 (Windows)

### B1 · Integración con Sala
El protocolo de módulos vive en `Dathinel/sala` y Rodaje lo usa por el submódulo `externo/sala` (fase 1d de `propuestas/colaboracion.md` §8).
1. ◐ `Dathinel/sala#13`: aviso de `chrono` en `cargo deny` y lectura de la identidad con reintentos en Windows.
2. ☐ `Dathinel/sala#12`: protocolo con su historial. Se une **sin squash**; el historial prueba la autoría que usa `docs/legal.md`.
3. ◐ `rodaje#618`: submódulo y referencias repuntadas. La CI del trabajo Uno en Linux falló en «Publicar y autoprueba»: se mira el registro antes de unir. No se une antes del paso 2.
**Hecho cuando:** un clon limpio con `--recurse-submodules` compila y pasa las pruebas en Windows, Linux y macOS, con el submódulo en un commit que está en `main` de Sala.

### B2 · Rendimiento: keyframes de texto y material de muchos fps
Reporte de un usuario real: con un texto «tambaleando» y muchos keyframes, el editor se satura (a veces «el sonido explota o la imagen se congela») y un video de muchos fps se traba.
1. ◐ Prueba de consola que reproduce 50, 200, 1000 y 5000 keyframes en un título y mide añadir, deshacer, evaluar por cuadro, línea de tiempo, inspector y guardado.
2. ☐ Arreglo de la causa, con cifras antes y después.
3. ☐ Textos animados de fábrica. **Primero se audita** lo que ya hay para no duplicarlo: diez entradas y salidas (Desvanecer, Subir, Bajar, Rebote, Escala, Elástico, Desenfoque, Girar, Máquina de escribir, Líneas, por bloque, línea, palabra o letra), la galería de estilos, el estilo «Subtítulo», karaoke con barrido de color y las plantillas de movimiento. Después se añade solo lo que falte: **animaciones continuas** (Tambaleo, Temblor, Pulso, Flotar), que no existen, para no poner cientos de keyframes a mano, y unos pocos **estilos de subtítulo animados** listos, palabra por palabra, hechos con las funciones existentes.
4. ☐ Audio con la interfaz bloqueada: un vaciado de búfer no puede sonar como un estallido ni congelar la imagen.
5. ☐ Reproducción de material de 120 y 240 fps en una línea de 30 o 60.
**Hecho cuando:** `docs/rendimiento-keyframes.md` trae las cifras; con 1000 keyframes añadir, reproducir y guardar no dan tirones; la reproducción de 120 fps no se atasca.

### B3 · Defectos y cabos sueltos
1. ☐ La autoprueba del exe AOT se cierra a veces (2 de 5) con `0xC000027B`: causa y arreglo.
2. ☐ Pruebas intermitentes de la CI (`Modules.Protocol`, `RenderCacheJobTests`): estabilizarlas de verdad.
3. ◐ Filtro `&` de `build\huellas-pixeles.ps1`: `rodaje#619`.
4. ☐ Tareas cortas que dejaron las revisiones de bloque (por ejemplo la duplicación entre `FrameInterpolator` y `SurfaceInterpolator`), solo si son cortas.
**Hecho cuando:** 5 autopruebas seguidas sin cierre anómalo y la CI sin intermitentes conocidas.

### B4 · SDK de .NET 10.0.403
Aceptado por Daniel por un aviso de vulnerabilidad del 10.0.12. Empieza cuando B1 esté unido, por los `packages.lock.json`.
1. ☐ `global.json`, `build\preparar-entorno.ps1` (versión y hashes SHA-512 de los tres RID, de `releases.json` de Microsoft), ILCompiler y paquetes de runtime, locks regenerados, `docs/legal.md` y `LICENCIAS.md` si citan versiones.
2. ☐ Lo mismo en `Dathinel/sala`.
**Hecho cuando:** compila sin advertencias, pasan las pruebas y la autoprueba, y la CI sale en verde en los tres sistemas.

### B5 · Verificación de la versión candidata
En segundo plano y frente a la base de la 1.0.3.
1. ☐ Compilación sin advertencias y solución de pruebas de Windows (≈11 800).
2. ☐ Autoprueba del exe AOT publicado desde `main`, 5 corridas.
3. ☐ Arranque, ms de GPU por cuadro y tamaño frente a la 1.0.3 (el envío de trabajo a la GPU con 8 capas subió un 21 % en la fase 3: aceptado).
**Hecho cuando:** las cifras están en `CHANGELOG.md` y ninguna empeora sin explicación.

### B6 · Preparar la publicación (sin publicar)
1. ◐ Versión subida donde la subió la 1.0.3 (`Directory.Build.props`).
2. ◐ `CHANGELOG.md` de la 1.0.4 en lenguaje de usuario, README y guías al día.
3. ✔ `pruebas-a-mano.md` con la lista corta y ordenada de lo nuevo que Daniel debe probar con el ratón.
4. ✔ `docs/publicar-1.0.4.md` con los pasos exactos que quedan, marcando los que usan la clave privada.
**Hecho cuando:** `build\distribuir.ps1` arma instalador, zip y notas sin errores.

### B7 · Revisión final
1. ☐ Un modelo distinto revisa el diff `v1.0.3..main` completo; lo corto se corrige.
**Hecho cuando:** sin hallazgos graves abiertos.

### B8 · Publicación (Daniel)
1. ☐ Catálogo firmado, etiqueta y release. **Solo con el OK de Daniel.**

## Colaboración dentro de la 1.0.4 (antes iba después) y repaso final

**Orden y paralelismo (20-nov).** B10 (parches en Core) y B13 (internet y diagnóstico) arrancan a la vez, porque no se tocan; B11 (sesión y pantallas) arranca también, con el esqueleto y la parte de red, y engancha los parches cuando B10 se una; B12 (pruebas hostiles) empieza con B11; B14, B15 y B16 empiezan cuando B11 tenga lo básico. B4 a B7 siguen en paralelo; **B9 (repaso con ratón, repetido) y la prueba de colaboración entre dos equipos cierran antes de B8**. Un jefe por bloque; los jefes de B10, B11 y B13 se coordinan por las interfaces de `propuestas/colaboracion.md`.

**Requisito de interfaz (consejo de Daniel, 20-nov):** en toda operación larga (cargar un proyecto, renderizar, exportar, analizar, descargar, generar proxies, enviar o recibir por la red) tiene que haber algo visual que diga que no se quedó trabado, y debe ser una **barra de progreso con porcentaje, no una rueda** (la rueda no dice si avanza). Si el porcentaje no se conoce: por fases («paso 2 de 5: …») o con el último avance («sigue trabajando, último avance hace 4 s»), con el tiempo restante estimado cuando se pueda y un botón de cancelar. Bloque **B9b** (auditoría y componente común); la red de B11 y B13 lo usa desde el principio.

**Listón de pulido para la prueba en otro país:** nada de errores sin explicar (todo fallo de red dice qué pasó y qué hacer), conexión directa o por relé visible, comprobación de la misma versión al entrar, aprobación del anfitrión siempre, progreso visible en toda operación larga, un panel de diagnóstico que Daniel pueda leer por teléfono, y `docs/prueba-colaboracion.md` con el guion paso a paso para los dos.

## Repaso y colaboración: detalle (sin Linux)

### B9 · Repaso con ratón, repetido hasta que quede bien
Lo nuevo desde la 1.0.3: render en caché, editar por texto, intercambio EDL/FCPXML/OTIO, flujo óptico, plantillas de movimiento, HDR de 12 bits y los arreglos de B2 y B3. **Daniel, 19-nov: se revisa varias veces hasta que quede bien.** Este bloque va **antes de publicar** (entre B7 y B8) y se repite en vueltas:
**Vuelta 1 (19-20 nov, exe de `main` del 19-nov, `docs/repaso-raton-19-nov.md`):** 7 ✔, 1 ✘, 4 sin probar. Lo más grave: borrar una frase en el panel Transcripción se llevó 15 palabras (F2, posible pérdida de datos), un error de E/S en el hilo de exportación cierra la app (F5) y el panel no deja seleccionar palabras con el ratón (F4). Arreglos: bloque **B9a**, ✔ **hecho el 20-nov** con 13 PR unidos (#625 a #633 y #635 a #638: F2, F1, F5, F6, F3 y F4, R1 a R8; los de F3, F4 y F2 salieron de leer el código y de la autoprueba, **sin confirmar con ratón real**). Sin tocar: R9 (miniaturas del proyecto anterior; probable artefacto de los proyectos de repro, sin verificar). Lo que queda va a `pendientes.md`.
1. Vuelta: una sesión de Claude con control de pantalla recorre `pruebas-a-mano.md` con el exe de `main` actual y escribe `repaso-raton-<fecha>.md` (✔, ✘ y no probado, gravedad y pasos).
2. Se arreglan todos los ✘ «bloquea» y «molesta» (jefe + Cuadrilla, PR con CI verde) y se publica un exe nuevo de `main`.
3. Nueva vuelta, empezando por lo que falló y siguiendo con el recorrido completo.
**Vuelta 2 (20-nov, `main` del 20-nov, `docs/repaso-raton-20-nov.md`): ✔ hecha con ratón (control de pantalla)**: de lo probado, casi todo ✔ (correcciones F1 a F6 salvo lo marcado, R1 a R7 y R9, keyframes y gráficos con 300 y 1000, textos animados y los 4 estilos de subtítulo, exportar = visor) y 1 ✘ leve (R8: el aviso «Faltan medios» sigue tras deshacer la importación). **No pudo probarse con ese control** (y queda para Daniel con ratón real): selección por arrastre en el panel Transcripción y **Supr sobre una frase entera (F2/F4)**, alta de keyframes con la reproducción en marcha, el zoom de la gráfica con la rueda, el recorrido del visor con X e Y, Pulso/Temblor/Flotar (casi no se distinguieron), cancelar una exportación, F6 desde el buscador, importar FCPXML, «Borrar el render en caché», el audio y las mediciones de fluidez sin carga ajena.
**Vuelta 3 (☐):** tras arreglar R8 y con lo que salga de la vuelta de Daniel; debe cerrar con una vuelta completa sin ✘ «bloquea» ni «molesta».
**Hecho cuando:** una vuelta completa sin ✘ «bloquea» ni «molesta» y, como mínimo, dos vueltas hechas. Los ✘ «cosmético» van a `pendientes.md`. Daniel lo repasa al final con el ratón solo para confirmar.

### B10 a B16 · Colaboración, dentro de la 1.0.4 (D-067, `propuestas/colaboracion.md`)
Rodaje usa `Dathinel/sala`, que ya trae lo central (red, roles, reservas, traspaso y entradas hostiles; pruebas de Sala en verde en los tres sistemas). Falta todo el lado de Rodaje, en este orden:

| Bloque | Fase del diseño | Qué | Hecho cuando |
|---|---|---|---|
| B10 | 1a | Parches en Core: `ContentKey`, `ContentHash` (v16 con lector y escritor), `PatchDiff`, `PatchApply`, `CollabHistory`, `WireSanitizer` | 10 000 secuencias aleatorias de parches convergen igual en dos réplicas |
| B11 | 1c | `CollabSession`, `RoomService`, `MediaMatch` y las vistas de Invitar y Unirse | Tres Rodaje en la misma red editan a la vez y ven a los demás |
| B12 | 2 | Pruebas hostiles en Rodaje | Ninguna entrada hostil tumba al anfitrión ni escribe fuera de la carpeta |
| B13 | 3 | Internet: relé, billetes con relé, «Solo por relé» y el cortafuegos de Windows explicado | Casa ↔ datos móviles conectan y dicen si es directo o por relé; latencia p95 < 250 ms por relé |
| B14 | 4 | Proxies del anfitrión: almacén por `ContentHash`, medio «solo proxy», «Pedir proxy» | Un invitado sin los originales edita y oye con proxies |
| B15 | 5 | Anfitrión que se va: traspaso, suplente, parches pendientes y «Cambios que no entraron» | Apagar el anfitrión a mitad: nadie pierde cambios y, al volver, converge |
| B16 | 6 | Render del anfitrión: «Exportar en el anfitrión» y caché compartido | Un invitado lanza la exportación y sale en el anfitrión |

**B15 ✔ en código (22-nov):** motor (#671), pantallas de traspaso, suplente y «Cambios que no entraron» (#677), borrados y reordenaciones que se recuperan y acuse de entrega de cambios separados (#690), y el registro que reintenta guardar (#703). Falta la prueba con ratón: apagar el anfitrión a mitad, tomar la sala como suplente y conservar o descartar lo que no entró (`pruebas-a-mano.md`, Colaboración 14 a 17).

**Repaso con ratón del 22-nov (arreglado):** la copia ligera no se veía (#684), cerrar la sala dejaba a los invitados reintentando (#685), EXPORTAR desaparecía a media pantalla (#682), dos diálogos de medios con ids (#689), «Pedir la pista» (#686), `dathinel-red` en `dist` (#679) y Sala al día (#683). Nuevo por petición de Daniel: «Pedir el original» y Preferencias › Colaboración (#705). Hallazgos 4, 9 y 10 de la revisión de B12 (#692); progreso común de la colaboración (#678).

**B10 ✔ hecho el 20-nov** (#648 tipos y huella de contenido, #649 implementación): `Rodaje.Core.Collab` con `SharedState`, `PatchDiff`, `PatchApply`, `PatchWire`, `WireSanitizer`, `PatchReservations`, `CollabHistory` y `ProjectWire`; `MediaRef.ContentKey/ContentHash` en el formato 16 (solo si existen). 10 000 secuencias aleatorias de ediciones convergen en dos réplicas (y con parches independientes reordenados); contrato en `propuestas/colaboracion-api-parches.md`. Queda en `pendientes.md`: coste de `Capture` con 5 000 clips y la conversión `file:` ↔ `blob:` con archivos reales (de `Rodaje.App`).

**Todas van en la 1.0.4** (Daniel, 20-nov). Cada bloque se une cuando compila sin advertencias y pasa la CI, y la prueba real entre dos equipos va antes de publicar.

## Ideas guardadas para la 1.0.5 (Daniel, 20-nov)
Detalle y respuesta sobre la IA local en `propuestas/editor-automatico-vertical.md`. Sin plan aprobado todavía: antes de escribir código va el plan por fases con el stack comparado.
- **Editor automático para video vertical** (TikTok, Instagram y similares), aparte de las opciones normales, con recetas (vlog, contar una historia, tutorial, informativo…), niveles de edición de baja a alta, reglas de retención y ganchos, y categorías y tendencias como complementos. Recomendado: nivel 1 y 2 con reglas y los módulos actuales en la 1.0.5; nivel 3 con un modelo local pequeño y los paquetes de tendencias en la 1.1.
- **Interfaz más clara para quien llega nuevo** (a Daniel le gusta como está): tutorial por tareas con un proyecto de ejemplo guiado, modo sencillo opcional, panel «qué hago ahora» y una pasada de nombres probada con usuarios nuevos.
- Bucle de texto por letra o por palabra (hoy el bucle mueve el bloque entero).

**B11 (20-nov):** ◐ PR apilados abiertos (esqueleto, invitar y unirse, proyecto y medios, presencia y edición compartida con diagnóstico y progreso). Falta el repaso con ratón y la prueba entre tres equipos (`pruebas-a-mano.md`, «Colaboración»), el progreso común de B9b y el empaquetado de `dathinel-red` (B13).

**B13 (20-nov, Sala #16 y #18; guion en `docs/prueba-colaboracion.md`):** hecho en Sala y sin salir del PC. Dos `dathinel-red` solo por relé (local y el público de n0) dicen si el camino es directo o relé, con la versión de cada par y un mensaje de versión que dice a quién actualizar; textos del relé, cortafuegos y redes sin UDP en `Sala/docs/internet.md`; ficha de diagnóstico por teléfono. Cifras por n0 desde Colombia: entrar ~1,2 s, edición ida y vuelta p50 ~185 ms con p95 de 247 a 740 ms según la carga (el relé está en EE. UU.: el p95 < 250 ms **se cumple a ratos**; con relé local a 25 ms por tramo, sí), reconexión ~1,5 s, 0 % de pérdida, captura del relé sin texto legible. Sin medir: pérdida de paquetes simulada (sin permisos para `netem`). Falta lo de Rodaje: el panel de conexión (B11) y la prueba de verdad entre dos países con el guion.

**B16, caché compartido (22-nov): pasa a la 1.0.5.** «Exportar en el anfitrión» va en la 1.0.4 (#676); compartir los fragmentos del render en caché por la red local, no. Por qué: (1) un fragmento que llega de otro equipo no se puede comprobar contra su receta sin volver a calcularlo, así que un anfitrión dañado o malicioso metería cuadros falsos en la reproducción y, peor, en una exportación del invitado; hace falta limitarlos a reproducir (`PlaybackOnly`) y marcarlos aparte en el almacén para que la limpieza y la exportación los distingan; (2) la huella de la receta usa `ContentFingerprint` (tamaño y tres trozos de 64 KB), no `ContentHash`, y entre equipos «el mismo medio» debe confirmarse con el hash entero; (3) pesan ~220 Mb/s en ProRes 1080p30: solo red local, y la prueba de verdad necesita dos equipos en la misma red. Con eso queda como B16b de la 1.0.5.

## Aparcado
- **Linux, fases 9 y 10** (GPU de punta a punta, AppImage): necesitan la prueba de Daniel con el USB y la tarjeta NVIDIA. Fallos de Uno ya conocidos: tiempo del transporte en 0:00, `LineGeometry`, tamaño de ventana.
- **Mac, fase 11:** exige registro de Apple (Daniel, 17-nov).
- `modulo-raw` en Linux y Mac: no hay binarios de LibRaw.
- Reducción sin niveles de detalle (mips) en la exportación con Vulkan: excepción abierta y medida en `mac-y-linux-fase4.md`.
