# Trabajo de Claude Code en la nube

La sesión local de Rodaje (en el PC de Daniel) mantiene una cola de tareas en [`cola.md`](cola.md). Una sesión en la nube la lee desde `origin/main`, hace la siguiente tarea libre, abre un pull request en borrador y vuelve a leer la cola. La sesión local revisa y une los PR, marca las tareas como hechas y agrega nuevas. Daniel no hace de intermediario.

## Cómo elegir la siguiente tarea

1. `git fetch --prune origin` y lee `docs/nube/cola.md` **desde `origin/main`** (`git show origin/main:docs/nube/cola.md`), no desde tu copia: la cola cambia mientras trabajas.
2. Recorre las tareas en orden. Una tarea en estado `cambios` va primero: su rama y su PR ya existen; haz `git checkout` de esa rama, atiende los comentarios de revisión del PR (`gh pr view <n> --comments`) con commits nuevos en la misma rama, actualiza la entrega y la descripción del PR, y deja un comentario «Listo para revisar». Si el último comentario del PR ya es «Listo para revisar», esa tarea espera revisión: sáltala. Después, sáltate las marcadas `hecha` o `en pausa`, y las que ya tengan su rama en el remoto (`git ls-remote --heads origin <rama>`) o un PR abierto (`gh pr list --head <rama>`): las tiene otra sesión o espera revisión.
3. Toma la primera que quede. Si no queda ninguna, espera unos 30 minutos y vuelve al paso 1. No inventes tareas.

## Reglas para toda tarea

- **Contexto:** lee `CLAUDE.md`, `docs/decisiones.md` y lo que cite la tarea antes de tocar nada.
- **Entorno:** Linux. `dotnet --version` debe dar 10.0.401 (si falta, `dotnet-install.sh` en tu carpeta personal; si la red lo bloquea, dilo en el PR). Compilan aquí `src/Rodaje.Core`, `src/Rodaje.Paquetes`, `src/Rodaje.Playback` y sus pruebas. Lo de Windows se verifica con el trabajo «Windows» de la integración continua, que corre en cada push de una rama con PR.
- **Ramas:** nunca commit ni push a `main`. Crea la rama que indica la tarea desde la punta de `origin/main`. No hagas merge ni rebase de `main` sobre tu rama y no unas tu PR.
- **Cambios mínimos:** solo los caminos permitidos de la tarea; sin renombrar, mover ni reformatear; pruebas nuevas en archivos nuevos cuando se pueda; un commit por punto; push frecuente (la máquina puede perderse).
- **Idioma:** identificadores en inglés; comentarios, documentación, mensajes y commits en español (D-011). Código propio, sin dependencias nuevas.
- **No toques:** `CLAUDE.md`, `README.md`, `docs/nube/` ni nada fuera de los caminos de la tarea.
- **Entrega:** abre el PR en borrador contra `main` al primer push, con el título de la tarea. Al terminar deja `docs/tareas/<fecha>-<rama sin claude/>.md` con lo hecho, lo pendiente, las decisiones, los posibles choques y qué debe comprobar Daniel en Windows. No entregues con pruebas en rojo en Linux.
- **Pruebas omitidas:** si omites o excluyes pruebas, la entrega y el PR indican cuántas se omitían antes y cuántas después, con la causa de cada grupo. Una omisión sin causa concreta (hardware, codificador o plataforma que falta) se rechaza.
- **Choques:** si algo reciente en `origin/main` toca lo mismo, no lo resuelvas: anótalo en la entrega y en el PR.

## Trabajar con varios agentes (por defecto)

Siempre se trabaja con varios agentes en paralelo, como hace la sesión local. No se hace una tarea entera en un solo hilo.
- **Varias tareas a la vez:** elige hasta **3 tareas** que te toquen (primero las `cambios`, luego las `pendiente` en orden) cuyos caminos no se crucen, y lanza **un agente por tarea**, cada uno en su propio `git worktree` sobre su rama. Tú coordinas: no escribas tú el código de las tareas.
- **Dentro de cada tarea:** el agente de la tarea puede lanzar los suyos (localizar código y leer la CI/entregas en paralelo; una parte independiente por agente en archivos distintos).
- **Revisor por tarea, siempre:** antes de marcar «Listo para revisar», un agente nuevo, sin el contexto del autor, revisa el diff de forma adversarial con las reglas de esta página (caminos, pruebas debilitadas u omitidas, conteo de omitidas, choques con `origin/main`), reproduce los fallos que se declaran imposibles y corre las pruebas afectadas. Se corrige lo que encuentre antes de entregar.
- **Cierre:** cada tarea con su PR y su entrega; tu resumen final lista todas.

Luego vuelve a «Cómo elegir la siguiente tarea».
