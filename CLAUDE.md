# Rodaje: estado

Editor de video nativo para Windows con la experiencia de uso de CapCut y código propio. Repositorio privado `Dathinel/rodaje`, licencia MIT. Las reglas generales están en `..\CLAUDE.md`.

## Cómo se trabaja aquí

- **.NET:** siempre con `herramientas\dotnet-local.cmd <args>`, que usa el SDK 10.0.401 de `.entorno\dotnet`. La FFmpeg LGPL 9.0.2 está en `.entorno\ffmpeg`. Las dos se instalan con `herramientas\preparar-entorno.ps1`, con hashes fijados. `RODAJE_ENTORNO` apunta a otro `.entorno`, por ejemplo desde un worktree.
- **`Rodaje.bat`:** abre la app WinUI 3 (`herramientas\app.ps1`, publica en `dist\Rodaje\`); se le pueden arrastrar videos (se importan y van seguidos a la pista principal) o un `.rodaje`. Solo recompila si cambió `src\`. `app.ps1 -Medir N` mide arranque y reproducción. Tras unir cambios a main se corre `app.ps1 -SoloPublicar` para que el doble clic de Daniel abra al instante.
- **`ABRIR.bat`:** sin argumentos corre las pruebas. Acepta:

  | Argumento | Qué hace |
  |---|---|
  | `cobertura` | Pruebas con cobertura |
  | `aot` | Prueba de humo con Native AOT |
  | `aceptacion` | Criterios de la fase 2 en serie, unos 9 min; `-Completa` añade 10 min reales |
  | `preparar-entorno` | Prepara `.entorno` |

- **Pruebas largas y de hardware:** solo corren con `RODAJE_PRUEBAS_LARGAS=1`, que activa la aceptación. Los medios de prueba con el número de cuadro codificado están en `testing/Rodaje.TestMedia` y se guardan en caché en `TestResults\media-cache`.
- **Nube:** la rutina «Rodaje: cola de tareas de la nube» (claude.ai/code/routines, cada 2 h, Sonnet) toma la siguiente tarea libre de `docs/nube/cola.md` y abre un PR en borrador; la sesión local revisa, une, marca `hecha` y repone la cola. La CI (`.github/workflows/ci.yml`) corre en cada PR.
- **Contenido:** fuera de este repo, en `Dathinel/rodaje-transiciones` y `Dathinel/rodaje-filtros` (públicos, CC0), clonados en `..\Complementos\`.
- **Agentes:** trabajan en worktrees dentro de `.claude\worktrees\` (ignorado), se unen con `git merge` y hacen un commit por tarea.
- **Espacios de nombres en las pruebas:** `Rodaje.X.Tests.Timing` tapa a `Rodaje.Core.Timing`, así que hay que usar el nombre completo o un `using`.

## Estado al 10 de octubre de 2026

**Fase 1, núcleo:**
- Tiempo exacto en ticks de 1/705 600 000 s.
- Modelo de proyecto → secuencias → pistas → clips, con efectos y keyframes.
- Ediciones con deshacer y rehacer: `Edits` y `UndoStack`.
- Formato `.rodaje` en JSON con versiones y guardado atómico.

**Fase 2, reproducción de un clip:**
- `Rodaje.Media`: FFmpeg con D3D11VA.
- `Rodaje.Audio`: WASAPI, que hace de reloj maestro.
- `Rodaje.Playback`: programador y caché de cuadros.
- `Rodaje.Engine`: `ClipPlayer`.
- El visor Win32 provisional se retiró; queda en la etiqueta git `visor-fase3`.

**Resultados de la fase 2:**
- Seek exacto en 2864 de 2864 casos.
- 0 cuadros de deriva en 10 min, con el reloj simulado y con el del dispositivo real.
- La revisión independiente quedó corregida.
- Daniel confía en las pruebas y pidió un lanzador más directo, que ya es `Rodaje.bat`.

**Fase 3: varias capas (terminada técnicamente).**
- `TimelineSession` maneja pistas con pre-roll, caché y presupuesto de memoria de video (3/8 de la VRAM), hasta 4 decodificadores por medio, mezclador de audio y proxies en la vista previa.
- `Rodaje.Render` compone las capas en la GPU.
- Proxies con NVENC (`Rodaje.Media/Proxies`).
- La revisión independiente encontró 7 defectos; todos están corregidos.
- En 4K con cortes la memoria de video pasó de 3,7 GB a 1,4 GB.
- Pruebas: 1299 pasan.

**Fase 4: interfaz WinUI 3 (construida y con la revisión corregida; falta la prueba a mano de Daniel).**
- `src/Rodaje.App` (abre con `Rodaje.bat` / `herramientas\app.ps1`):
  - **Armazón:** `EditorSession`, atajos con 3 estilos («Rodaje», «Ágil», «Clásico»), ajustes y tema grafito.
  - **Línea de tiempo:** barra de transporte, pista principal magnética, arrastres como gestos, marcadores y regiones (formato v2).
  - **Biblioteca:** miniaturas y formas de onda reales.
  - **Paquetes:** `src/Rodaje.Paquetes` (formato `.paquete` v0, `docs/formato-paquete.md`) y página de Complementos con el catálogo de Dathinel.
  - **Inspector:** keyframes en el cursor, «Guardar como transición» en `%LOCALAPPDATA%\Rodaje\complementos\propios`, filtros, historial y trabajos.
  - **Página de atajos:** teclado dibujado y hoja rápida.
  - **Medidores y reproductor:** medidores L/R sincronizados con lo que se oye, J/K/L y pantalla completa.
- **Cifras:** 1939 de 1953 pruebas pasan (14 omitidas, 0 fallos), AOT con 0 advertencias, 0 cuadros perdidos, edición en pantalla en ~10 ms.
- **Maqueta aprobada (D-018):** https://claude.ai/artifact/XXtTT675TEHWSPPCCZfXcv; fuente en `dist\maqueta` (no se versiona).
- **Revisión de la fase 4 corregida (10-oct, agentes A–D):** ripple con enlazados, Esc en el inspector, atajos tras las páginas, guardia de diálogos y red de errores (`rodaje-error.txt`), rutas inválidas, recuperación de paquetes tras apagón, límites y textos de manifiesto, hilos de miniaturas, medidores en pausa, contrato `IClipVisuals` único, `TimelineScheduler` en parciales, pruebas `TiempoReal` en pasada aparte, README y arquitectura al día (D-022, D-023).

**Fase 5: efectos, LUT, mezcla, transiciones y audio (construida; en integración).** Plan y criterios en `docs/fase-5.md` (D-025). Unidos a main los 6 agentes: núcleo (formato 3, transiciones en el corte, volumen de pista que sigue a los clips, 99,9 % de cobertura), render (efectos fusionados, 13 mezclas, transiciones; 3 capas 4K con efectos en 1,8–3,4 ms), motor (efectos sin reservas, carriles de transición, ganancia y fundidos, composición atómica tras seek y pistas de arriba abajo), paquetes (lectores `.cube`/filtro/efecto/preset, `PackageContentStore`), inspector y biblioteca, línea de tiempo (soltar transiciones, envolvente, volumen de pista, calidad Auto/Completa/Mitad/Cuarto). Texto y títulos van en la fase 5b.

## Siguiente paso (al retomar)

1. **Integración final** (agente en `.claude\worktrees\`, rama `worktree-agent-*` más reciente; si la sesión se cortó, revisar sus commits con `git log main..<rama>` y unir si la suite pasa): conectar `Compositor.Resources` en `PlayerEngine` y recomponer en pausa con `Changed`; pasar `MixParameters`/`MixOptions` en `EffectContent`; ocultar LUT duplicadas en Filtros; prueba de punta a punta con los paquetes reales; medidas con `app.ps1 -Medir 10 -Escenario efectos`; pruebas inestables a `TiempoReal`.
2. **Verificación de la fase 5** por un agente nuevo sin contexto y de **otro modelo (Sonnet; Daniel no quiere Fable)** contra los criterios de `docs/fase-5.md`. Luego mini revisión.
3. `herramientas\app.ps1 -SoloPublicar` y pasarle a Daniel **`docs/pruebas-a-mano.md`** (todo lo que tiene que probar y las preguntas abiertas).
4. **Pendientes conocidos:** pico de ~600 ms en la primera edición tras importar (no es el motor; medir en la interfaz); una forma de onda quitada mientras se calcula vuelve a la memoria hasta el límite de 64; `Tick` reserva ~2,5 KB por refresco por las clases `LayerFrame`/`CompositionFrame` del contrato.
5. **Velocidad de clips (pedida por Daniel el 10-oct, importante):** velocidad constante, reversa, rampas tipo CapCut y congelar cuadro. Un agente Plan prepara el diseño (formato 4, tiempo exacto, tono de audio, decodificación rápida y en reversa); se presenta a Daniel antes de construir.
6. **Después:** fase 5b (texto y títulos, repo `rodaje-titulos`), luego fase 6 (exportación).

## Nube y coordinación

- **Rutina en la nube** `trig_015zPXdY6meRLBGvkb5XEXMF` («Rodaje: cola de tareas de la nube», cada 2 h, Sonnet, entorno `rodaje-dotnet`): toma la siguiente tarea libre de `docs/nube/cola.md` según `docs/nube/README.md` y abre un PR en borrador. Se ve en https://claude.ai/code/routines/trig_015zPXdY6meRLBGvkb5XEXMF.
- **Revisión horaria local** (vive solo mientras la sesión está abierta; **al abrir una sesión nueva hay que volver a crearla** con CronCreate): cada PR `claude/*` lo revisa un agente Opus nuevo sin contexto con encargo adversarial; si dice UNIR se une, se marca `hecha` en la cola y se repone la cola con 1–2 tareas que no pisen a los agentes locales.
- **CI y costo:** el repo es privado; el trabajo de Windows de `ci.yml` gasta minutos de GitHub al doble que Linux. Se vigila en GitHub → Settings → Billing; si se acerca al límite, el trabajo de Windows pasa a dispararse solo en PR (o a mano) en lugar de en cada push a main.
- **`main` sin protección:** GitHub no permite reglas de rama en repos privados sin Pro. La regla «nunca push a main» de la nube vive en `docs/nube/README.md` y en el prompt de la rutina; la revisión horaria comprueba que ningún commit de `main` venga de una rama `claude/*` sin PR.
- **Claude chat** lee el repo para responder a Daniel: todo se sube a GitHub tras cada commit, y se hace `git pull` antes de unir.

## Hacia la versión pública (consultado en Claude chat el 10-oct, sin decidir)

- **Exportar por CPU o GPU sin separar el programa.** Solo cambia la última pieza: una interfaz de salida con dos implementaciones (codificador de hardware, o bajar el cuadro con `Staging` y darlo a uno de software). Modelo, deshacer y línea de tiempo no se enteran. Es la fase 6.
- **Licencia de la exportación por CPU.** Propuesta: v1 en LGPL (H.264 solo por hardware; la CPU exporta AV1 con SVT-AV1, VP9 o HEVC con kvazaar). x264 (GPL) quedaría como complemento aparte con su `ffmpeg.exe` propio, consultando antes a alguien de licencias.
- **Codificadores de hardware:** ampliar `ProxyEncoders` a NVENC, AMF, QSV, Media Foundation y software; AMD e Intel necesitan probadores.
- **Vista previa «como CapCut»:** medir con `app.ps1 -Medir N` frente a CapCut con el mismo clip 4K de varias capas; vigilar el presupuesto de VRAM en gráficas de 2 GB o integradas.
- **Antes de publicar:** `legal.md` pide el código fuente de FFmpeg, `THIRD-PARTY-NOTICES.txt` y la revisión de patentes de H.264.

## Decisiones

- **Registro:** `docs/decisiones.md`, de D-001 a D-025.
- **Módulos:** `docs/propuesta-modulos.md`, aprobada. Núcleo en el exe, contenido en `.paquete` sin código, IA como procesos C#.
- **Pendientes para la interfaz:** decidir cómo funciona el ripple con enlazados de distinto largo y si se limpian los enlaces huérfanos.
- **Archivo:** la investigación y el sondeo están en `docs/archivo/`; el código del sondeo, en la etiqueta git `sondeo-interop-aot`.
