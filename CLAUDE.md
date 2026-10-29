# Rodaje

Editor de video nativo para Windows (.NET 10, Native AOT, WinUI 3), versión 1.0.1. Repositorio público `Dathinel/rodaje`, GPLv3 con términos adicionales de atribución y permiso para complementos (D-032); marca en `TRADEMARK.md`. Las notas de trabajo y los pendientes no van en este repositorio: viven en `.proceso\` (privado e ignorado), si está clonado.

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
- **Decisiones:** `docs/decisiones.md` (D-001 a D-056). Un cambio de fondo que contradiga una decisión vigente se registra como decisión nueva.

## Para retomar (25-oct, madrugada)

- **v1.0.1 publicada** (16-oct). La **1.0.2** está casi lista en `main`: 30 PR unidos el 24 y 25-oct (#441–#472), todo en CHANGELOG «Sin publicar». Falta la puerta: suites completas en local, autoprueba, repaso a mano de `.proceso/PARA-DANIEL.md` y `.proceso/diseno/pruebas-raton-24oct.md`, subir la versión en `Directory.Build.props`, `build\distribuir.ps1` y `build\publicar-release.ps1`.
- **Revisión:** pasada completa con el ratón y auditoría por partes (96 hallazgos, `.proceso/diseno/auditoria-por-partes-24oct.*`) arreglados salvo lo aplazado en los informes `informe-auditoria-*.md`. #461 es un PR en borrador «NO UNIR» solo para `/code-review ultra 461`: ciérralo y borra `revision/base-v1.0.1` tras usarlo.
- **HDR:** fases A, B y C (partes 1 y 2) unidas (D-052 a D-055); la parte 3 (MaxCLL/MaxFALL medidos, SDR → HDR) en `.proceso/diseno/encargos/hdr-fase-c-3.md`; la fase D (RAW en GPU) va en la 1.1. Plan en `docs/hdr.md`.
- **Decisiones:** hasta D-056 (personalización y temas). Daniel pidió tomar siempre la opción recomendada.
- **Herramientas de IA:** Subtitular y Rastrear v1.0.0 publicadas, Lanzador v1.0.0 prerelease; sus entradas `app` están en el catálogo v8.
- Intermitentes abiertos y pendientes en `.proceso/agentes/BACKLOG-1.0.2.md`. La nube y `nocturna.yml` siguen en pausa.
