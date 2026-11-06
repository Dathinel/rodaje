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
- **1.1 entera en `main`** (detalle en `CHANGELOG.md` «Sin publicar»): #476-#485 (noche del 31-oct: arrastre sin copias, personalización 2, HDR fase C parte 3, calidad de vida, aviso de versión nueva, ideas de las referencias, funciones de otros editores), #489 (regla y asa), y los tres de la nube unidos el 1-nov: #488 HDR fase D parte 1 (D-059), #486 revisión menor (un tipo por archivo en atajos, menú Reproducción) y #487 portapapeles (pegar insertando Ctrl+Alt+V, otro proyecto, transiciones). AOT en `dist\Rodaje\` sin advertencias (37,5 MB; carpeta 243,7 MB).
- **Repaso con ratón (1-nov):** resultado al final de `docs/pruebas-a-mano.md` (sin probar: solo, sincronía, ritmo, lote, «Portapapeles», «CinemaDNG revelado en la GPU»).
- **Revisión de bloque de #486-#489 (codex, 1-nov) → #490 unido el 1-nov:** pegar tras importar captura el destino antes del `await` (si cambia, avisa y no pega; pregunta: ¿pegar en la posición capturada cuando solo se movió el cursor?); el CFA se valida en sus ocho filas antes de anunciar Bayer; pegar en el mismo proyecto resuelve por identificador aunque el medio se haya reenlazado; `SetInterval` con el mismo intervalo no reinicia el autoguardado; el diálogo CinemaDNG decide GPU/CPU con `CinemaDngDevelopChoice`, el mismo criterio que la importación. AOT republicado (37,5 MB, sin advertencias). La receta RAW ya se explica una sola vez en `hdr.md` (#491, agy). Queda para después: el contrato de `mosaico` en un solo sitio (módulo, `Program.cs` y `CinemaDngImport` lo repiten).
- **Preguntas para Daniel:** ¿la 1.1 sale con `modulo-raw` 1.1.0 empaquetado y firmado en el catálogo, o al principio sigue el respaldo CPU?; ¿la segunda parte de la fase D (intermedio de 12 bits, `hdr.md`) entra en la 1.1 o después?; ¿qué material real y qué diferencia frente a AHD aceptan D-059? Faltan las cifras del RTX 3050 en `docs/hdr.md`.
- **Siguiente:** Daniel repasa lo que falta de `docs/pruebas-a-mano.md` (con `RODAJE_FORZAR_WARP=1` y el módulo 1.0.0 en CinemaDNG) y apunta los ms por fotograma; después la puerta de la 1.1: empaquetar `modulo-raw` 1.1.0, `build\distribuir.ps1` + `publicar-release.ps1`.
- **#461** es un borrador «NO UNIR» solo para `/code-review ultra 461`: ciérralo y borra `revision/base-v1.0.1` tras usarlo.
- **HDR:** fases A, B y C completas (D-052 a D-055, D-058); fase D parte 1 en `main` (D-059: el módulo lee el mosaico Bayer y el editor lo revela con Malvar-He-Cutler en un compute shader; sin GPU, con `modulo-raw` 1.0.0 o con RAW no Bayer sigue por `revelar`); parte 2 (intermedio de 12 bits) pendiente, plan en `docs/hdr.md`.
- **Decisiones:** hasta D-059. Daniel pidió tomar siempre la opción recomendada.
- **Herramientas de IA:** Subtitular, Rastrear y Lanzador con sus PR de la nube unidos el 1-nov y AOT republicado; entradas `app` en el catálogo v8.
