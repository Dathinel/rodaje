# Rodaje: estado

Editor de video nativo para Windows con la experiencia de uso de CapCut y código propio. Repositorio público `Dathinel/rodaje`, GPLv3 con términos adicionales de atribución y permiso para complementos (D-032); marca en `TRADEMARK.md`. Las reglas generales están en `..\CLAUDE.md`.

**Proceso aparte.** Lo que no sirve a quien usa o compila la app vive en el repo privado `Dathinel/rodaje-proceso`, clonado en `.proceso\` (ignorado; si falta: `gh repo clone Dathinel/rodaje-proceso .proceso`): `PARA-DANIEL.md`, la cola y las reglas de la nube (`nube\`), los planes de fase (`fases\`), `pendientes.md`, `pruebas-a-mano.md` y lo archivado. Aquí no se escribe nada de proceso.

**Pendiente de Daniel:** `.proceso\PARA-DANIEL.md` (pruebas a mano y activar el reporte privado de vulnerabilidades). Recuérdaselo al empezar cada sesión hasta que esté hecho.

## Cómo se trabaja aquí

- **.NET:** siempre con `herramientas\dotnet-local.cmd <args>` (SDK 10.0.401 de `.entorno\dotnet`; FFmpeg LGPL 9.0.2 en `.entorno\ffmpeg`; los dos con `herramientas\preparar-entorno.ps1`, hashes fijados). `RODAJE_ENTORNO` apunta a otro `.entorno`, por ejemplo desde un worktree.
- **`Rodaje.bat`:** publica con AOT en `dist\Rodaje\` si cambió `src\` y abre la app (`herramientas\app.ps1`; `-Medir N`, `-Autoprueba`, `-SoloPublicar` tras unir a main). Detalle en `docs/arranque-y-distribucion.md`.
- **`Probar.bat`:** pruebas (`cobertura`, `aot`, `aceptacion`, `preparar-entorno`). Las largas y de hardware solo con `RODAJE_PRUEBAS_LARGAS=1`. Variables en `docs/variables-entorno.md`.
- **Agentes:** worktrees en `.claude\worktrees\` (ignorado), se unen con `git merge`, un commit por tarea.
- **Espacios de nombres en las pruebas:** `Rodaje.X.Tests.Timing` tapa a `Rodaje.Core.Timing`: nombre completo o `using`.
- **Complementos:** contenido en `Dathinel/rodaje-transiciones` y `Dathinel/rodaje-filtros` (CC0, en `..\Complementos\`); descarga firmada desde `Dathinel/rodaje-catalogo` (`docs/descarga.md`).

## Nube y coordinación

- **Rutina** `trig_015zPXdY6meRLBGvkb5XEXMF` («Rodaje: cola de tareas de la nube», cada hora a los :23, Opus, entorno `rodaje-dotnet`): toma la siguiente tarea de `rodaje-proceso/nube/cola.md` según `nube/README.md` y abre un PR en borrador en `rodaje`; la entrega va en la descripción del PR.
- **Revisión local cada 30 min** (CronCreate; se recrea en cada sesión): une los PR `claude/*` en verde, revisión por bloques con Sonnet cada ~4, repone la cola.
- **`main` sin protección de rama:** la revisión comprueba que ningún commit de `main` venga de `claude/*` sin PR.
- **Claude chat** lee el repo: push tras cada commit y `git pull` antes de unir.

## Estado

Fases 1–6 y 5c construidas y verificadas; 5b (títulos) y 7 (IA local) en curso. Detalle y cifras en `.proceso\fases\`; decisiones en `docs/decisiones.md` (D-001 a D-034).

- **Siguiente:** fase 7 restante (traducir, visión, RAW y GPL, edición por palabras) y su verificación (Sonnet).
- **Reorganización del repo (11/12-oct):** hechos los pasos 1, 2 y 7; quedan los que tocan `.github/workflows/ci.yml` o la app (`.proceso\reorganizacion.md`).
- **Antes de publicar:** casillas de `docs/pendientes-legales.md`.
- **Para la interfaz:** decidir el ripple con enlazados de distinto largo y si se limpian los enlaces huérfanos.
