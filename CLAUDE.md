# Rodaje: estado

Editor de video nativo para Windows con la experiencia de uso de CapCut y código propio. Repositorio privado `Dathinel/rodaje`, licencia PolyForm Noncommercial 1.0.0 (código disponible, uso libre no comercial; D-030). Las reglas generales están en `..\CLAUDE.md`.

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

- **Pruebas largas y de hardware:** solo corren con `RODAJE_PRUEBAS_LARGAS=1`, que activa la aceptación. Con ella `probar.ps1` suma la fase 5: tiempos en Release y `app.ps1 -Autoprueba`, que publica con AOT y arranca la app de verdad (todas las pestañas del inspector). Los medios de prueba con el número de cuadro codificado están en `testing/Rodaje.TestMedia` y se guardan en caché en `TestResults\media-cache`.
- **Nube:** la rutina «Rodaje: cola de tareas de la nube» (claude.ai/code/routines, cada hora, Opus desde el 11-oct) toma la siguiente tarea libre de `docs/nube/cola.md` y abre un PR en borrador; la sesión local revisa, une, marca `hecha` y repone la cola. La CI (`.github/workflows/ci.yml`) corre en cada PR.
- **Descarga directa de complementos:** sigue en la fase 7 (`IPackageDownloader`), decidido por Daniel el 11-oct; hasta entonces «Instalar desde archivo».
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

1. **Fase 5 integrada y en main** (11-oct): 2368 pruebas pasan, 0 fallos; AOT sin advertencias; `dist\Rodaje` publicado; paquetes generados en `..\Complementos\*\dist\`. Cifras en `docs/fase-5.md`. Los ~2,8 KB por `Tick` de las clases del contrato quedan aceptados (criterio redefinido el 11-oct).
2. **En curso al cerrar la sesión del 10-oct** (si se cortó, retomar así):
   - **Verificación de la fase 5 (Sonnet, 11-oct): FALTA**, pero revisó un commit anterior a la integración. Un agente de cierre (rama `worktree-agent-*` más reciente) comprueba sobre lo actual la carga 4K y el aviso de paquetes que faltan, arregla el informe de cobertura de `probar.ps1` (daba 0,0 %), endurece pruebas de rendimiento y transiciones, y corrige docs obsoletas. Daniel aceptó redefinir el criterio de 0 B por `Tick`: los efectos no añaden reservas; los ~2,8 KB del contrato se aceptan.
   - **Paso 0 de la fase 5c: UNIDO** (11-oct; formato 4, `ClipSpeed`, `RetimeMath`, `ClipTimeMap`, validador con rangos e ids, D-026). **Ronda 5c.1 unida a main (11-oct):** A, B, C, D, E, F, G y la conexión de la interfaz; falta que C entregue las pruebas de aceptación sin `Skip` (`SpeedSeekTests`, `SpeedDriftTests`), luego suite completa, `app.ps1 -SoloPublicar` y verificación por un agente Sonnet sin contexto. Daniel decidió (11-oct): el cambio de velocidad mantiene siempre el magnetismo (al acortarse el siguiente se une y la transición se queda; respeta el modo «todas las pistas»), en curso por el agente A en la rama `velocidad-magnetismo`; «Mantener tono» desactivado a 1×. Antes: 7 agentes A–G lanzados (A ediciones + corrección del desbordamiento del validador, B medios, C motor de video, D audio WSOLA, E inspector, F línea de tiempo, G medición), cada uno en su rama `worktree-agent-*`. Si la sesión se corta: `git branch --no-merged main`, revisar cada rama, unir en el orden A → B → C → D → E → F → G con la suite, y luego verificación por un agente Sonnet sin contexto. Antes se decía: lanzar los 7 agentes A–G de `docs/fase-5c.md` (el informe del paso 0 dejó qué toca a cada uno en el commit f87eda8 y D-026). La prueba `Evaluating_effects_reserves…` (0,3 ms) falla con carga: pasarla a `TiempoReal`.
   - **Cierre de la fase 5: UNIDO** (aviso al desinstalar en vivo, cobertura en probar.ps1, pruebas más exigentes, docs). Falta correr la suite completa sobre main y `app.ps1 -SoloPublicar`.
3. **Daniel prueba a mano** con `docs/pruebas-a-mano.md` cuando el verificador dé su veredicto.
4. **Pendientes conocidos:** **defecto real del lector** (hallado por el fuzz de la nube, PR #3): un `.rodaje` malicioso provoca `OverflowException` en `Time.op_Subtraction` vía `Clip.Duration` → `TransitionOps.MaxDuration` → `ModelValidator.ValidateTransitions:293`; corregirlo (aritmética acotada o validar rangos antes) y quitar el `Skip` de la prueba del PR #3. el validador no limita el rango de los parámetros de efectos (una opacidad de 1e308 abre) ni rechaza ids con caracteres de control (hallado por la revisión del PR #3; encaja en el paso 0 de la fase 5c); pico de ~600 ms en la primera edición tras importar (no es el motor; medir en la interfaz); una forma de onda quitada mientras se calcula vuelve a la memoria hasta el límite de 64; `Tick` reserva ~2,5 KB por refresco por las clases `LayerFrame`/`CompositionFrame` del contrato.
5. **Velocidad de clips (fase 5c, aprobada):** plan en `docs/fase-5c.md`. Arranca tras unir la integración de la fase 5 (en paralelo con su verificación): paso 0 contrato y formato 4, luego 7 agentes. Va antes que la 5b.
6. **Después:** fase 5b (texto y títulos, repo `rodaje-titulos`), luego fase 6 (exportación).

**En curso (12-oct, madrugada):**
- **En main:** 5c.1 cerrada; 5c.2 completa (núcleo, motor e interfaz de curvas); rediseño del inspector y contraste (D-027); de la nube unidos los PR #1 a #10 (estado de cada tarea en `docs/nube/cola.md`).
- **Agentes locales:** 5c.3 reversa (medios y motor, sin tocar la App; luego el interruptor «Invertir» y el indicador «◀»); paso 0 de la fase 6 (D-028, proyecto `Rodaje.Export`, contrato con el alcance profesional de `docs/fase-6.md`, prototipo con cifras).
- **Decisiones de Daniel (11-oct):** exportación LGPL v1; 9:16 con barras y botón de cambiar la secuencia; dos modos de rendimiento elegidos al exportar; alcance profesional (contenedores, códecs, control de tasa, avanzados, audio con LUFS, preajustes propios); complementos profesionales en `docs/pendientes.md` (10 bits en la app; cámaras, preajustes pro, RAW, GPL y 360 como complementos; 360 lo trae el motor y lo activa el complemento).
- **Nube:** rutina horaria con hasta 3 tareas en paralelo y revisor propio; revisión local cada 30 min con un revisor Opus por PR en paralelo. Pendientes: tareas 8 y 10 con cambios, 11, 13–16.
- **Siguiente al cerrar lo de arriba:** UI de la reversa → verificación de 5c (Sonnet) → agentes A–F de la fase 6 → 5b (texto, con la propuesta corregida) → reorganización del repo (`docs/reorganizacion.md`).

**Reorganización del repo aprobada (11-oct):** plan y orden en `docs/reorganizacion.md`; se aplica cuando no haya agentes editando ni PR de la nube abiertos.

## Nube y coordinación

- **Rutina en la nube** `trig_015zPXdY6meRLBGvkb5XEXMF` («Rodaje: cola de tareas de la nube», cada hora a los :23, **Opus** desde el 11-oct a pedido de Daniel, entorno `rodaje-dotnet`; sus PR los revisa Sonnet): toma la siguiente tarea libre de `docs/nube/cola.md` según `docs/nube/README.md` y abre un PR en borrador. Se ve en https://claude.ai/code/routines/trig_015zPXdY6meRLBGvkb5XEXMF.
- **Revisión local cada 30 min** (vive solo mientras la sesión está abierta; un revisor Opus por PR en paralelo y los detalles menores se arreglan al unir; **al abrir una sesión nueva hay que volver a crearla** con CronCreate): cada PR `claude/*` lo revisa un agente Opus nuevo sin contexto con encargo adversarial; si dice UNIR se une, se marca `hecha` en la cola y se repone la cola con 1–2 tareas que no pisen a los agentes locales.
- **CI y costo:** el repo es privado; el trabajo de Windows de `ci.yml` gasta minutos de GitHub al doble que Linux. Se vigila en GitHub → Settings → Billing; si se acerca al límite, el trabajo de Windows pasa a dispararse solo en PR (o a mano) en lugar de en cada push a main.
- **`main` sin protección:** GitHub no permite reglas de rama en repos privados sin Pro. La regla «nunca push a main» de la nube vive en `docs/nube/README.md` y en el prompt de la rutina; la revisión horaria comprueba que ningún commit de `main` venga de una rama `claude/*` sin PR.
- **Claude chat** lee el repo para responder a Daniel: todo se sube a GitHub tras cada commit, y se hace `git pull` antes de unir.

## Hacia la versión pública (consultado en Claude chat el 10-oct, sin decidir)

- **Exportar por CPU o GPU sin separar el programa.** Solo cambia la última pieza: una interfaz de salida con dos implementaciones (codificador de hardware, o bajar el cuadro con `Staging` y darlo a uno de software). Modelo, deshacer y línea de tiempo no se enteran. Es la fase 6.
- **Licencia de la exportación por CPU.** Propuesta: v1 en LGPL (H.264 solo por hardware; la CPU exporta AV1 con SVT-AV1, VP9 o HEVC con kvazaar). x264 (GPL) quedaría como complemento aparte con su `ffmpeg.exe` propio, consultando antes a alguien de licencias.
- **Codificadores de hardware:** ampliar `ProxyEncoders` a NVENC, AMF, QSV, Media Foundation y software; AMD e Intel necesitan probadores.
- **Vista previa «como CapCut»:** medir con `app.ps1 -Medir N` frente a CapCut con el mismo clip 4K de varias capas; vigilar el presupuesto de VRAM en gráficas de 2 GB o integradas.
- **Antes de publicar:** casillas en `docs/pendientes-legales.md` (SIC, abogado, EULA por el Windows App SDK, código fuente de FFmpeg, avisos, patentes, licencia de los repos de contenido, acuerdo de contribuidor).

## Decisiones

- **Registro:** `docs/decisiones.md`, de D-001 a D-026.
- **Módulos:** `docs/propuesta-modulos.md`, aprobada. Núcleo en el exe, contenido en `.paquete` sin código, IA como procesos C#.
- **Pendientes para la interfaz:** decidir cómo funciona el ripple con enlazados de distinto largo y si se limpian los enlaces huérfanos.
- **Archivo:** la investigación y el sondeo están en `docs/archivo/`; el código del sondeo, en la etiqueta git `sondeo-interop-aot`.
