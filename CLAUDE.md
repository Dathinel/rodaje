# Rodaje: estado

Editor de video nativo para Windows con la experiencia de uso de CapCut y código propio. Repositorio público `Dathinel/rodaje`, licencia GNU GPLv3 con términos adicionales de atribución (Sección 7) y permiso para complementos; marca en `TRADEMARK.md`; crédito visible en la barra de título y en «Ayuda → Acerca de» (D-032, sustituye a la PolyForm de D-030). Las reglas generales están en `..\CLAUDE.md`.

**Pendiente de Daniel:** `PARA-DANIEL.md` (respaldar la clave de descargas, pruebas a mano, preguntas abiertas). Recuérdaselo al empezar cada sesión hasta que esté hecho.

**Al retomar (12-oct, corte por límite):** (1) la rutina de la nube está **pausada** (`enabled: false`) por la reorganización: reactivarla al unir la rama de la reorganización, con las rutas nuevas (`.proceso/` en el repo privado `Dathinel/rodaje-proceso`). (2) Ramas locales por unir (`git branch --no-merged main`): reorganización como app profesional (proceso a `rodaje-proceso`, README profesional, `testing/`→`tests/`, `herramientas/`→`build/`, `Rodaje.Paquetes`→`Rodaje.Packages`, D-034 con las respuestas de Daniel, fase 8 eliminada) y atajos con ratón y edición gráfica; si se cortaron, revisar sus worktrees y relanzar desde ahí. (3) Luego: cerrar la fase 7 (módulo de visión, edición por palabras, carpeta de medios elegible, Whisper menor para CPU, quitar DirectML del ruido) y su verificación; después las funciones de calidad de vida aprobadas para la v1 (`pendientes.md`), instalador y primera versión.

## Cómo se trabaja aquí

- **.NET:** siempre con `herramientas\dotnet-local.cmd <args>`, que usa el SDK 10.0.401 de `.entorno\dotnet`. La FFmpeg LGPL 9.0.2 está en `.entorno\ffmpeg`. Las dos se instalan con `herramientas\preparar-entorno.ps1`, con hashes fijados. `RODAJE_ENTORNO` apunta a otro `.entorno`, por ejemplo desde un worktree.
- **`Rodaje.bat`:** abre la app WinUI 3 (`herramientas\app.ps1`, publica en `dist\Rodaje\`: `Rodaje.exe` es un lanzador y el programa va en `app\`, que se oculta; arranque y distribución en `docs/arranque-y-distribucion.md`, medida con `herramientas\medir-arranque.ps1`); se le pueden arrastrar videos (se importan y van seguidos a la pista principal) o un `.rodaje`. Solo recompila si cambió `src\`. `app.ps1 -Medir N` mide arranque y reproducción. Tras unir cambios a main se corre `app.ps1 -SoloPublicar` para que el doble clic de Daniel abra al instante.
- **`ABRIR.bat`:** sin argumentos corre las pruebas. Acepta:

  | Argumento | Qué hace |
  |---|---|
  | `cobertura` | Pruebas con cobertura |
  | `aot` | Prueba de humo con Native AOT |
  | `aceptacion` | Criterios de la fase 2 en serie, unos 9 min; `-Completa` añade 10 min reales |
  | `preparar-entorno` | Prepara `.entorno` |

- **Pruebas largas y de hardware:** solo corren con `RODAJE_PRUEBAS_LARGAS=1`, que activa la aceptación. Con ella `probar.ps1` suma la fase 5: tiempos en Release y `app.ps1 -Autoprueba`, que publica con AOT y arranca la app de verdad (todas las pestañas del inspector). Los medios de prueba con el número de cuadro codificado están en `testing/Rodaje.TestMedia` y se guardan en caché en `TestResults\media-cache`.
- **Nube:** la rutina «Rodaje: cola de tareas de la nube» (claude.ai/code/routines, cada hora, Opus desde el 11-oct) toma la siguiente tarea libre de `docs/nube/cola.md` y abre un PR en borrador; la sesión local revisa, une, marca `hecha` y repone la cola. La CI (`.github/workflows/ci.yml`) corre en cada PR.
- **Descarga directa de complementos:** activa desde el 12-oct (índice firmado en `Dathinel/rodaje-catalogo`); cómo publicar una versión nueva en `docs/descarga.md`.
- **Contenido:** fuera de este repo, en `Dathinel/rodaje-transiciones` y `Dathinel/rodaje-filtros` (públicos, CC0), clonados en `..\Complementos\`.
- **Agentes:** trabajan en worktrees dentro de `.claude\worktrees\` (ignorado), se unen con `git merge` y hacen un commit por tarea.
- **Espacios de nombres en las pruebas:** `Rodaje.X.Tests.Timing` tapa a `Rodaje.Core.Timing`, así que hay que usar el nombre completo o un `using`.

## Estado

Fases 1–5, 5c y 6 construidas; 5b (títulos) y 7 (IA local) en curso. Detalle y cifras de cada fase en `docs/fase-*.md`; decisiones en `docs/decisiones.md`.

**En curso (12-oct, madrugada; Daniel lejos del PC un buen tiempo):**
- **En main:** fases 5c, 5b y 6 cerradas y verificadas (6: todos los criterios salvo pausas de la vista previa en cortes con velocidad → tarea 36 de la nube); fase 7: protocolo, ModuleManager, descargas firmadas (activas; falta el respaldo de la clave, `PARA-DANIEL.md`), `Rodaje.Analysis` (silencios, ritmo, ducking, estabilizar, color automático), módulos de subtítulos (Whisper) y de audio IA (ruido, VAD); licencia GPLv3 con términos adicionales (D-032); repo público.
- **Nube en modo autónomo** (`docs/nube/README.md`): une sus propios PR con la CI en verde y revisa por bloques con Sonnet; cola con tareas 28–39.
- **Agentes locales que quedaron trabajando** (si se cortaron, `git branch --no-merged main` y revisar sus worktrees): correcciones de la verificación de la 5b (asa de rotar, escribir en el visor, estilos, fuentes por elemento `font`) y módulo de texto a voz (`modulo-voz`).
- **Siguiente al volver:** unir esas dos ramas; revisar lo que unió la nube (bloques de Sonnet); fase 7 restante (traducir, visión, RAW y GPL, edición por palabras); verificación de la fase 7 (Sonnet); reorganización del repo (`docs/reorganizacion.md`).

**Reorganización del repo aprobada (11-oct):** plan y orden en `docs/reorganizacion.md`; se aplica cuando no haya agentes editando ni PR de la nube abiertos.

## Nube y coordinación

- **Rutina en la nube** `trig_015zPXdY6meRLBGvkb5XEXMF` («Rodaje: cola de tareas de la nube», cada hora a los :23, **Opus** desde el 11-oct a pedido de Daniel, entorno `rodaje-dotnet`; sus PR los revisa Sonnet): toma la siguiente tarea libre de `docs/nube/cola.md` según `docs/nube/README.md` y abre un PR en borrador. Se ve en https://claude.ai/code/routines/trig_015zPXdY6meRLBGvkb5XEXMF.
- **Revisión local cada 30 min** (vive solo mientras la sesión está abierta; **al abrir una sesión nueva hay que volver a crearla** con CronCreate): une directo cada PR `claude/*` con la CI en verde, compilación y pruebas; cada ~4 PR unidos, un agente Sonnet revisa el bloque; repone la cola si quedan menos de 3 tareas libres.
- **CI y costo:** el repo es público desde el 11-oct, así que GitHub Actions no tiene límite de minutos (siendo privado se agotaron y la CI se paró).
- **`main` sin protección:** GitHub no permite reglas de rama en repos privados sin Pro. La regla «nunca push a main» de la nube vive en `docs/nube/README.md` y en el prompt de la rutina; la revisión horaria comprueba que ningún commit de `main` venga de una rama `claude/*` sin PR.
- **Claude chat** lee el repo para responder a Daniel: todo se sube a GitHub tras cada commit, y se hace `git pull` antes de unir.

## Hacia la versión pública (consultado en Claude chat el 10-oct, sin decidir)

- **Vista previa «como CapCut»:** medir con `app.ps1 -Medir N` frente a CapCut con el mismo clip 4K de varias capas; vigilar el presupuesto de VRAM en gráficas de 2 GB o integradas.
- **Antes de publicar:** casillas en `docs/pendientes-legales.md` (SIC, abogado, EULA por el Windows App SDK, código fuente de FFmpeg, avisos, patentes, licencia de los repos de contenido, acuerdo de contribuidor).

## Decisiones

- **Registro:** `docs/decisiones.md`, de D-001 a D-033 (D-033, provisional: formato en el reproductor, exportar sigue al proyecto y herramientas rápidas).
- **Módulos:** `docs/propuesta-modulos.md`, aprobada. Núcleo en el exe, contenido en `.paquete` sin código, IA como procesos C#.
- **Pendientes para la interfaz:** decidir cómo funciona el ripple con enlazados de distinto largo y si se limpian los enlaces huérfanos.
- **Archivo:** la investigación y el sondeo están en `docs/archivo/`; el código del sondeo, en la etiqueta git `sondeo-interop-aot`.
