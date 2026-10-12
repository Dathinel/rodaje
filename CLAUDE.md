# Rodaje

Editor de video nativo para Windows (.NET 10, Native AOT, WinUI 3), versión 1.0.0. Repositorio público `Dathinel/rodaje`, GPLv3 con términos adicionales de atribución y permiso para complementos (D-032); marca en `TRADEMARK.md`. Las notas de trabajo y los pendientes no van en este repositorio: viven en `.proceso\` (privado e ignorado), si está clonado.

## Cómo se trabaja aquí

- **.NET:** siempre con `build\dotnet-local.cmd <args>` (SDK 10.0.401 en `.entorno\dotnet`, FFmpeg LGPL 9.0.2 en `.entorno\ffmpeg`; los dos con `build\preparar-entorno.ps1`, con huellas fijadas). `RODAJE_ENTORNO` apunta a otro `.entorno`, por ejemplo desde un worktree.
- **`Rodaje.bat`:** publica con AOT en `dist\Rodaje\` si cambió `src\` y abre la app (`build\app.ps1`; `-Medir N`, `-Autoprueba`, `-SoloPublicar`). Detalle en `docs/arranque-y-distribucion.md`.
- **`Probar.bat`:** pruebas (`cobertura`, `aot`, `aceptacion`, `preparar-entorno`). Las largas y de hardware solo con `RODAJE_PRUEBAS_LARGAS=1`. Variables en `docs/variables-entorno.md`.
- **Versión y publicación:** la versión única está en `Directory.Build.props`; `build\distribuir.ps1` arma instalador, zip y notas (`build\instalador\notas-version.md`) y `build\publicar-release.ps1` los sube. Antes de publicar, `docs/pendientes-legales.md`.
- **Estructura:** `src/` (capas), `modules/` (módulos de IA), `tests/` (pruebas y bibliotecas de apoyo), `build/` (scripts), `docs/` (índice en `docs/README.md`), `schema/` (esquemas del `.rodaje`). Carpetas e identificadores en inglés (D-011); `.md`, mensajes y scripts que se ejecutan a mano, en español.
- **Atribución:** el aviso del término 1 de `LICENSE` y `src/Rodaje.App/Services/UpstreamAttribution.cs` deben coincidir literalmente (lo comprueba `UpstreamAttributionTests`).
- **Worktrees** en `.claude\worktrees\` (ignorado); se unen con `git merge`, un commit por tarea.
- **Espacios de nombres en las pruebas:** `Rodaje.X.Tests.Timing` tapa a `Rodaje.Core.Timing`: nombre completo o `using`.
- **Complementos:** contenido en `Dathinel/rodaje-transiciones`, `-filtros`, `-fuentes`, `-titulos` y `-zonas` (CC0/OFL); módulos en `Dathinel/modulo-*` (GPLv3, `build\empaquetar-modulo.ps1`); descarga firmada desde `Dathinel/rodaje-catalogo` (release `v5`; `docs/descarga.md`).
- **Decisiones:** `docs/decisiones.md` (D-001 a D-037). Un cambio de fondo que contradiga una decisión vigente se registra como decisión nueva.

## Para retomar (12-oct, créditos semanales agotados)

- v1.0.0 lista y verificada (Sonnet: lista para publicar). Falta: recompilar con `build\distribuir.ps1` sobre el último commit y publicar con `build\publicar-release.ps1` **solo con el sí del autor**.
- La nube y la revisión cada 30 min están apagadas.
