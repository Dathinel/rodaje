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
- **Complementos:** contenido en `Dathinel/rodaje-transiciones`, `-filtros`, `-fuentes`, `-titulos`, `-zonas`, `-camaras`, `-sonidos`, `-preajustes-pro` y `-360` (CC0/OFL); módulos en `Dathinel/modulo-*` (GPLv3, `build\empaquetar-modulo.ps1`); x264/x265 en `Dathinel/rodaje-codificadores-gpl` (GPL, `build\armar-codificadores-gpl.ps1`, D-034); descarga firmada desde `Dathinel/rodaje-catalogo` (release `v7`; `docs/descarga.md`).
- **Decisiones:** `docs/decisiones.md` (D-001 a D-043). Un cambio de fondo que contradiga una decisión vigente se registra como decisión nueva.

## Para retomar (13-oct, 19:40)

- **v1.0.0 publicada** (release `v1.0.0`, commit 8b1e4c1; la completa trae los 16 complementos). No se publica nada nuevo hasta que el autor lo pida.
- **Ronda de depuración de la nube** hasta el 14-oct 05:00 UTC (rutina `trig_015zPXdY6meRLBGvkb5XEXMF`, cada hora). Al corte de las 19:30: 131 PR unidos y unos 85 defectos arreglados, entre ellos el cierre al elegir «escala» (#99; falta confirmarlo en Windows). La auditoría de los PR (cinco revisores con otro modelo) no encontró trampas, solo plazos subidos y pruebas `TiempoReal` que no corre ninguna CI: reglas nuevas en `.proceso/nube/README.md` y tareas 237 a 246.
- **Al terminar la ronda:** añadir la sección «Cierre» a `.proceso/nube/informe-depuracion.md` (la rutina no lo hace porque el archivo ya existe), revisar los PR unidos después de #227 y desactivar la rutina o darle las tareas pendientes.
- **Limpiar los worktrees** de `.claude\worktrees\` (unos 110; aprobado por el autor): antes de borrar cada uno, comprobar que no quede trabajo sin unir.
- **1.0.1 de estabilidad y uso** (alcance provisional en `.proceso/propuestas/v1.0.1-personalizacion.md`): la cola de depuración, las correcciones visuales, el tutorial, el aviso de versión y «Copiar informe». La personalización pasa a la 1.1.
- La nube une sus PR al pasar la CI (sin `--auto`: no hay protección de rama).
