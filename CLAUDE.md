# Rodaje: estado

Editor de video nativo para Windows con la experiencia de uso de CapCut y código propio. Repositorio público `Dathinel/rodaje`, licencia GNU GPLv3 con términos adicionales de atribución (Sección 7) y permiso para complementos; marca en `TRADEMARK.md`; crédito visible en la barra de título y en «Ayuda → Acerca de» (D-032, sustituye a la PolyForm de D-030). Las reglas generales están en `..\CLAUDE.md`.

**Pendiente de Daniel:** `PARA-DANIEL.md` (crear y respaldar la firma de descargas, pruebas a mano, preguntas abiertas). Recuérdaselo al empezar cada sesión hasta que esté hecho.

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

## Estado

Fases 1–5, 5c y 6 construidas; 5b (títulos) y 7 (IA local) en curso. Detalle y cifras de cada fase en `docs/fase-*.md`; decisiones en `docs/decisiones.md`.

**En curso (11-oct, tarde):**
- **En main:** fase 5c completa (5c.1, curvas, reversa con su interfaz); fase 6 construida (contrato, conversor GPU, salida FFmpeg, motor fuera de línea, diálogo y orquestación: la app exporta de verdad, 1 min 1080p en ~9 s con NVENC); paso 0 de la 5b (formato 5 con títulos, D-029); licencia GPLv3 con atribución obligatoria (D-032, sustituye a la PolyForm de D-030; complementos de contenido siguen CC0; abogado aplazado, pendientes en `docs/legal.md`).
- **Agentes locales:** verificación de la 5c (Sonnet); fase 6 F (medición y aceptación); 5b títulos en motor y render; 5b títulos en la app; fase 7 plan y paso 0 (protocolo de módulos).
- **Pendiente:** la reversa en la exportación hace un seek por cuadro (conectar `ReverseFrameReader` en `OfflineTimeline` cuando termine el agente de la 5b del motor); verificación de la fase 6 (Sonnet) tras F.
- **Nube:** Opus, hasta 5 tareas por corrida, sin revisor por tarea; la sesión local une los PR en verde y revisa por bloques cada ~4 PR (lista en `docs/nube/cola.md`).
- **Siguiente:** verificación de la 6 → cierre de la 5b → fase 7 → reorganización del repo (`docs/reorganizacion.md`).
- **Al retomar tras el corte por límite (11-oct):** recrear la revisión de 30 min (`CronCreate`, prompt en «Nube y coordinación»); `git branch --no-merged main` y por cada `worktree-agent-*` con commits: mirar su `git status` en `.claude\worktrees\<agente>` (puede haber trabajo sin commit), unir si compila y pasan sus pruebas, o relanzar el encargo desde ese punto. Ramas en curso: verificación 5c (Sonnet, solo informe), fase 6 F, 5b motor/render, 5b app. Fase 7: plan y protocolo unidos (D-031, `docs/fase-7.md`), con 8 preguntas para Daniel (se avanza con la recomendada; la 2, repos de módulos, espera respuesta). Nube: PR #17 (GOP abierto) esperaba su CI; #15 (licencias) y #14 (codificadores) en curso; «PR sin revisar»: #16.

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

- **Registro:** `docs/decisiones.md`, de D-001 a D-032.
- **Módulos:** `docs/propuesta-modulos.md`, aprobada. Núcleo en el exe, contenido en `.paquete` sin código, IA como procesos C#.
- **Pendientes para la interfaz:** decidir cómo funciona el ripple con enlazados de distinto largo y si se limpian los enlaces huérfanos.
- **Archivo:** la investigación y el sondeo están en `docs/archivo/`; el código del sondeo, en la etiqueta git `sondeo-interop-aot`.
