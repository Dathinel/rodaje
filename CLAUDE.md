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
- **Complementos:** contenido en `Dathinel/rodaje-transiciones`, `-filtros`, `-fuentes`, `-titulos`, `-zonas`, `-camaras`, `-sonidos`, `-preajustes-pro` y `-360` (CC0/OFL); módulos en `Dathinel/modulo-*` (GPLv3, `build\empaquetar-modulo.ps1`); x264/x265 en `Dathinel/rodaje-codificadores-gpl` (GPL, `build\armar-codificadores-gpl.ps1`, D-034); descarga firmada desde `Dathinel/rodaje-catalogo` (release `v7`; `docs/descarga.md`).
- **Decisiones:** `docs/decisiones.md` (D-001 a D-055). Un cambio de fondo que contradiga una decisión vigente se registra como decisión nueva.

## Para retomar (24-oct, noche)

- **v1.0.1 publicada** (release `v1.0.1`, 16-oct-2026). No se publica nada nuevo hasta que el autor lo pida.
- **1.0.2 en marcha:** 11 PR unidos hoy (#442 a #452); todo en CHANGELOG «Sin publicar». Backlog en `.proceso/agentes/BACKLOG-1.0.2.md`; pruebas a mano en `.proceso/para-daniel.md` (sección 24-oct).
- **HDR:** fases A (HDR → SDR, #444) y B (10 bits de punta a punta, #451) unidas; la C (secuencias HDR en lineal, visor HDR, exportación HDR10/HLG) partida en dos (D-054): parte 1 (formato 15, exportación HDR10/HLG con metadatos, miniaturas convertidas) unida en #454; parte 2 (compositor lineal, visor scRGB, interfaz; D-055) en la rama `claude/hdr-c2`; parte 3 (MaxCLL/MaxFALL medidos, SDR → HDR) en `.proceso/diseno/encargos/hdr-fase-c-3.md`. Plan en `docs/hdr.md`.
- **Decisiones:** hasta D-055 (D-055, compositor HDR lineal y visor scRGB, propuesta). D-053 es propuesta pendiente de Daniel: orden de las fases C y D, si «trabajar en HDR» entra en la 1.1 o después, y el tamaño aceptable del intermedio RAW (ProRes 4444 XQ, ~2,5× el HQ).
- **Herramientas de IA** (`Dathinel/subtitular`, `rastrear`, `lanzador`): fases 1 y 2 hechas en sus repos. Falta que Daniel firme las entradas `app` en `rodaje-catalogo` (#445) y el pulido visual de la fase 3.
- La nube y `nocturna.yml` siguen en pausa.
