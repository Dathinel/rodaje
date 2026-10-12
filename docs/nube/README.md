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
- **Varias tareas a la vez:** elige hasta **5 tareas** que te toquen (primero las `cambios`, luego las `pendiente` en orden) cuyos caminos no se crucen, y lanza **un agente por tarea**, cada uno en su propio `git worktree` sobre su rama. Tú coordinas: no escribas tú el código de las tareas.
- **Dentro de cada tarea:** el agente de la tarea puede lanzar los suyos (localizar código y leer la CI/entregas en paralelo; una parte independiente por agente en archivos distintos).
- **Sin revisor por tarea:** basta con que compile, pasen las pruebas afectadas y la CI, y que los caminos sean los permitidos; comprueba en `origin/main` cada afirmación sobre el código antes de escribirla. La sesión local une los PR en verde sin vueltas de comentarios y hace una revisión independiente por bloques (cada ~4 PR o al cerrar una fase); lo que encuentre llega como tarea nueva.
- **Cierre:** cada tarea con su PR y su entrega; tu resumen final lista todas.

Luego vuelve a «Cómo elegir la siguiente tarea».

## Modo autónomo (Daniel, 11-oct: estará lejos del PC bastante tiempo)

Mientras la sesión local no responda, la nube **une sus propios PR** cuando se cumple todo esto: los dos trabajos de la CI (Linux y Windows) en verde, solo caminos permitidos, entrega en `docs/tareas/` y ninguna prueba omitida nueva sin causa concreta. Une con `gh pr merge <n> --merge` (nunca `--admin`, nunca push directo a main), marca la tarea `hecha` en `cola.md` en el mismo PR antes de unir, y anota el número en «PR sin revisar». Cada 4 PR unidos así, un agente revisor nuevo **con otro modelo** (`model: sonnet`) revisa el bloque y sus hallazgos se vuelven tareas nuevas en la cola (en un PR propio). Si la cola se queda sin tareas `pendiente`, agrega 1–3 de `docs/fase-*.md` o `docs/pendientes.md` (en un PR propio). Si una tarea toca `src/Rodaje.App/Shell/MainWindow.xaml.cs`, `SelfTestRun.cs` o `Rodaje.slnx`, cambia solo lo mínimo para evitar choques.
