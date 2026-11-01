# Rodaje

Editor de video nativo para Windows (.NET 10, Native AOT, WinUI 3), versión 1.0.2. Repositorio público `Dathinel/rodaje`, GPLv3 con términos adicionales de atribución y permiso para complementos (D-032); marca en `TRADEMARK.md`. Las notas de trabajo y los pendientes no van en este repositorio: viven en `.proceso\` (privado e ignorado), si está clonado.

## Cómo se trabaja aquí

- **.NET:** siempre con `build\dotnet-local.cmd <args>` (SDK 10.0.401 en `.entorno\dotnet`, FFmpeg LGPL 9.0.2 en `.entorno\ffmpeg`; los dos con `build\preparar-entorno.ps1`, con huellas fijadas). `RODAJE_ENTORNO` apunta a otro `.entorno`, por ejemplo desde un worktree.
- **`Rodaje.bat`:** publica con AOT en `dist\Rodaje\` si cambió `src\` y abre la app (`build\app.ps1`; `-Medir N`, `-Autoprueba`, `-SoloPublicar`). Detalle en `docs/arranque-y-distribucion.md`.
- **`Probar.bat`:** pruebas (`cobertura`, `aot`, `aceptacion`, `preparar-entorno`). Las largas y de hardware solo con `RODAJE_PRUEBAS_LARGAS=1`. Variables en `docs/variables-entorno.md`.
- **Versión y publicación:** la versión única está en `Directory.Build.props`; `build\distribuir.ps1` arma instalador, zip y notas (`build\instalador\notas-version.md`) y `build\publicar-release.ps1` los sube. Antes de publicar, `docs/pendientes-legales.md`.
- **Estructura:** `src/` (capas), `modules/` (módulos de IA), `tests/` (pruebas y bibliotecas de apoyo), `build/` (scripts), `docs/` (índice en `docs/README.md`), `schema/` (esquemas del `.rodaje`). Carpetas e identificadores en inglés (D-011); `.md`, mensajes y scripts que se ejecutan a mano, en español.
- **Atribución:** el aviso del término 1 de `LICENSE` y `src/Rodaje.App/Services/UpstreamAttribution.cs` deben coincidir literalmente (lo comprueba `UpstreamAttributionTests`).
- **Worktrees** en `.claude\worktrees\` (ignorado); se unen con `git merge`, un commit por tarea.
- **Espacios de nombres en las pruebas:** `Rodaje.X.Tests.Timing` tapa a `Rodaje.Core.Timing`: nombre completo o `using`.
- **Complementos:** contenido en `Dathinel/rodaje-transiciones`, `-filtros`, `-fuentes`, `-titulos`, `-zonas`, `-camaras`, `-sonidos`, `-preajustes-pro` y `-360` (CC0/OFL); módulos en `Dathinel/modulo-*` (GPLv3, `build\empaquetar-modulo.ps1`); x264/x265 en `Dathinel/rodaje-codificadores-gpl` (GPL, `build\armar-codificadores-gpl.ps1`, D-034); descarga firmada desde `Dathinel/rodaje-catalogo` (release `v8`; `docs/descarga.md`).
- **Decisiones:** `docs/decisiones.md` (D-001 a D-058). Un cambio de fondo que contradiga una decisión vigente se registra como decisión nueva.

## Para retomar (1-nov)

- **v1.0.2 publicada** (31-oct). Puerta: CI verde, AOT y autoprueba del exe, suite completa (las de protocolo de módulos hay que correrlas fuera del árbol de procesos de Claude Code; las de TiempoReal/Rendimiento solo las corre `nocturna.yml`).
- **1.1 en `main`, sin PR abiertos** (noche del 31-oct): #476 arrastre sin copias, #477 personalización 2 (D-057), #478 HDR fase C parte 3 (D-058), #481 calidad de vida (recientes, autoguardado configurable, ir a tiempo `Ctrl+J`, copiar/duplicar/pegar, hoja de atajos y `docs/guia/atajos.md` generada), #482 aviso de versión nueva (`Services/Updates`, Preferencias › General, Ayuda › Buscar actualizaciones), #483 arreglos de la revisión de bloque, #484 ideas de las referencias (línea de ffmpeg, sincronía por sonido, montaje al ritmo, lote de proyectos, rueda) y #485 funciones de otros editores (exportar entre I y O, solo en cabecera, asas de fundido). AOT publicado en `dist\Rodaje\` sin advertencias (37,4 MB; carpeta 243,6 MB). `CHANGELOG.md` «Sin publicar» lo recoge todo.
- **1-nov:** repaso con ratón de la 1.1 hecho por Claude (resultado al final de `docs/pruebas-a-mano.md`: pasan exportar entre I y O, línea de ffmpeg, asas, aviso de versión y ruedas; sin probar solo, sincronía, ritmo y lote). #489 unido (regla sin sombra tras Alt+X, asa de 10 px) y AOT republicado. Tres sesiones de nube lanzadas el 1-nov (HDR fase D, portapapeles, menores de la revisión 481): sus PR llegan sin unir, revisar por bloque.
- **Siguiente:** Daniel repasa lo que falta de `docs/pruebas-a-mano.md` (solo, sincronía, ritmo, lote); unir los PR de la nube cuando lleguen; después la puerta de la 1.1 y `build\distribuir.ps1` + `publicar-release.ps1`.
- **#461** es un borrador «NO UNIR» solo para `/code-review ultra 461`: ciérralo y borra `revision/base-v1.0.1` tras usarlo.
- **HDR:** fases A, B y C completas (D-052 a D-055, D-058); la fase D (RAW en GPU) va en la 1.1 (plan en `docs/hdr.md`, brief listo para la nube).
- **Decisiones:** hasta D-058. Daniel pidió tomar siempre la opción recomendada.
- **Herramientas de IA:** Subtitular, Rastrear y Lanzador con aviso de versión nueva y arreglos de revisión unidos el 31-oct, AOT publicado; entradas `app` en el catálogo v8.
- Pendientes en `.proceso/agentes/BACKLOG-1.0.2.md`. La nube de `nocturna.yml` sigue en pausa.
