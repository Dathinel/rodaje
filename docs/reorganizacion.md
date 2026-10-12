# Reorganización del repositorio (aprobada el 11-oct, pendiente de aplicar)

Criterio de Daniel: lo que no le sirve a quien instala o construye la app no queda a la vista en este repo. Este archivo se borra al terminar.

## Decisiones

1. **Proceso en un repo privado aparte, `Dathinel/rodaje-proceso`**, clonado en `Rodaje\.proceso\` (ignorado). Allí van la cola y las reglas de la nube, el plan de la fase en curso, `pendientes.md` y `pruebas-a-mano.md`. La rutina de la nube se reconfigura con los dos repos (si no admite dos, plan B: carpeta `.proceso/` versionada). Lo ya cerrado (`docs/archivo/`, `docs/tareas/`, planes de fases terminadas) va a la etiqueta `archivo-proceso-2026-10` y se borra. Las entregas de la nube pasan a la descripción del PR.
2. **Estructura en inglés** (D-011): `build/` → `build/`, `Rodaje.Paquetes` → `Rodaje.Packages` (este, al terminar la 5c). Los `.md` y los scripts que se ejecutan a mano siguen en español.
3. **`ABRIR.bat` → `Probar.bat`**; se borran `probar.json` (huérfano) y `.env.example` (vuelve en la fase 8).
4. **`testing/Rodaje.TestMedia` → `tests/Rodaje.TestMedia`**; la medición (`-Medir`) se junta en `Rodaje.App/Diagnostics/` y sigue en el ejecutable. Además `Views/Plugins` → `Views/Packages` y `Views/Player/PlayerEngine.cs` → `Services/Playback/`.

Se quedan donde están: `schema/` (su `$id` está publicado), `docs/formato-proyecto.md` (lo lee una prueba), `docs/decisiones.md`, `docs/arquitectura/` (con `propuesta-modulos.md` fundida en su README), legal y avisos.

## Orden (un commit por paso, suite completa y CI verde tras cada uno)

Requisitos: ningún agente local editando (`git branch --no-merged main` vacío), sin PR `claude/*` abiertos y la rutina pausada.

1. Proceso fuera (solo docs): etiqueta, crear `rodaje-proceso`, mover, reconfigurar la rutina y la revisión horaria, actualizar enlaces (`git grep` de `docs/archivo`, `docs/tareas`, `docs/nube`, `fase-5` sin resultados).
2. Raíz: `Probar.bat`, borrar `probar.json` y `.env.example` (y `!.env.example` del `.gitignore`), README al día (cifras y esquema v4).
3. `testing/` → `tests/` (`Rodaje.slnx`, `ProjectReference` de las pruebas, docs).
4. `build/` → `build/` (los `.bat`, la caché y el paso de `ci.yml`, docs y mensajes).
5. Dentro de `Rodaje.App`: medición a `Diagnostics/`, `Views/Packages`, `PlayerEngine` a `Services/Playback` (con AOT sin advertencias y `app.ps1 -Medir 10`).
6. `Rodaje.Paquetes` → `Rodaje.Packages` tras la 5c (regenerar `packages.lock.json` de App y App.Tests con `restore --force-evaluate`).
7. Limpieza: ramas `worktree-agent-*` unidas y `claude/*` de PR cerrados; `CLAUDE.md` a ~40 líneas; `app.ps1 -SoloPublicar`.
