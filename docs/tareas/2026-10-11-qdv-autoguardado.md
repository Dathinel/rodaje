# Tarea 22: autoguardado y recuperación

Rama `claude/qdv-autoguardado`, PR #20. Función 30 de `docs/pendientes.md`.

## Hecho

**Núcleo (`src/Rodaje.Core/Serialization`, archivos nuevos), probado en Linux:**

- `AutosaveSession`: una carpeta por ejecución en `Autoguardado\<fecha>-<pid>-<aleatorio>\`.
  - Copias `copia-000001.rodaje`, `copia-000002.rodaje`… escritas con `ProjectSerializer.Save`, que ya es atómico (temporal llevado al disco y renombrado). En la carpeta solo hay copias completas.
  - Se conservan las 10 más recientes (`MaxVersions`); al escribir una más se borra la más antigua.
  - `sesion.json` (escrito a un temporal y renombrado) guarda el nombre del proyecto, su `.rodaje` y si hay cambios sin guardar.
  - `sesion.lock` queda abierto, sin compartir para escritura, mientras la ejecución vive. No lleva `DeleteOnClose`, porque Windows también borraría el archivo al caerse el proceso.
  - `Complete()` (cierre normal) borra la carpeta. `MarkSaved` anota que no hay nada que ofrecer y deja las copias como historial. `Clear` borra las copias (se abrió otro proyecto).
- `AutosaveRecovery`:
  - `FindRecoverable` devuelve las sesiones cuyo `sesion.lock` existe y se puede abrir para escribir: su proceso murió. Además tienen que tener cambios sin guardar y al menos una copia. Las devuelve de la más reciente a la más antigua.
  - Cada `RecoverableSession` se queda el bloqueo mientras vive, así que dos ventanas que arrancan a la vez no ofrecen la misma sesión.
  - `LoadNewest` abre la copia más nueva que se pueda leer; si está dañada, prueba con las anteriores.
  - `Discard` quita el bloqueo para que no se vuelva a ofrecer.
  - `Prune` borra las sesiones sin proceso vivo con más de 7 días, o sin ninguna copia. No toca nunca una sesión viva, ni una carpeta de menos de 1 min.
- `AutosaveSchedule`: decide cuándo toca copiar. Se copia tras 5 s sin cambios y, si se edita sin parar, como mucho cada 2 min, con al menos 20 s entre intentos (también tras un fallo). Recibe la hora en cada llamada, así que se prueba sin esperas.

**Pruebas nuevas** (`tests/Rodaje.Core.Tests/Serialization/AutosaveScheduleTests.cs`, `AutosaveSessionTests.cs`; 27 en total):

- Escritura atómica: copia completa sin temporales; un fallo a mitad conserva las anteriores y no deja copia a medias; se ignora el `.tmp` de una escritura cortada.
- Rotación del historial.
- Detección de cierre inesperado: cierre normal, sesión viva en otra ventana, caída con y sin cambios guardados, `Clear`, oferta única mientras está tomada, orden, copia dañada.
- Limpieza y plan de copias.

**App (`src/Rodaje.App/Services/Autosave/AutosaveService.cs`, sin WinUI; compila en `Rodaje.App.Tests`):**

- Sigue `ProjectEdited`, `HistoryChanged` (vuelta al estado guardado) y `ProjectOpened` de `EditorSession`.
- `Tick()` lo llama el temporizador de 2 s de la ventana. Si toca, y no hay un gesto, una apertura o un guardado en curso, hace `Project.Clone()` en el hilo de la interfaz y escribe en el pool. Las escrituras van en fila y la edición no espera al disco.
- Si una escritura falla, avisa una vez («No se pudo autoguardar») y lo reintenta más tarde.
- `OfferRecoveryAsync()` limpia y busca en un hilo del pool. Si encuentra algo, avisa «Rodaje se cerró de forma inesperada» con el botón «Recuperar».
- Recuperar nunca pisa el original: escribe `<nombre> (recuperado).rodaje` (o `(recuperado 2)`…) junto al `.rodaje` original y lo abre con `OpenProjectAsync`. Si el proyecto nunca se guardó o su carpeta ya no existe, pregunta dónde guardarlo.

**Arranque (`Shell/MainWindow.xaml.cs`, lo mínimo, unas 20 líneas):**

- Crea el servicio, salvo en `-Medir` y en la autoprueba.
- Lo engancha al temporizador de ajustes.
- Llama a `OfferRecoveryAsync` tras los avisos de ajustes.
- `Complete()` en `OnClosed`.
- `ShellDialogs` pasa a una variable local para dársela también al servicio.

## Decisiones

- **Una carpeta por ejecución, no por proyecto.** Así varias ventanas no se pisan, y el bloqueo dice qué ejecución está viva. Al abrir otro proyecto se borran las copias del anterior: ya se guardó o se descartó a propósito.
- **Cierre normal.** Con «No guardar» también se borran las copias, porque es una decisión explícita.
- **Recuperar abre una copia aparte.** No se reemplaza el proyecto original ni queda abierto un archivo de la carpeta de autoguardado; así Ctrl+S guarda junto al proyecto de la persona.
- **El aviso usa la barra de avisos (`Session.Notify` con botón), no un `ContentDialog`.** No necesita WinUI en `Services/` y no tapa la ventana al arrancar. Un aviso posterior (por ejemplo «Faltan paquetes» al abrir un proyecto desde la línea de comandos) puede reemplazarlo. En ese caso la sesión se vuelve a ofrecer en el próximo arranque, porque solo `Discard` (tras recuperar) la retira.
- **Copias con rutas absolutas.** Las rutas de los medios quedan como en memoria. Al recuperar, `ProjectPaths.ForSaving` las vuelve relativas a la carpeta del archivo recuperado.
- **Tiempos fijos por ahora** (5 s, 2 min, 20 s, 10 versiones, 7 días), sin ajuste en la interfaz.
- **`AutosaveService` es `partial`.** Lo pide CsWinRT (CsWinRT1028) a toda clase con `IDisposable` en la app, como `InstanceRegistry`.

## Pendiente

- No hay ajuste para cambiar el intervalo ni para desactivar el autoguardado.
- No hay forma, desde la interfaz, de abrir una copia antigua del historial. Están en `%LOCALAPPDATA%\Rodaje\Autoguardado\<sesión>\copia-*.rodaje` y se pueden abrir con Abrir proyecto.
- No hay pruebas de `AutosaveService` en `tests/Rodaje.App.Tests`: ese camino no estaba permitido en esta tarea. La lógica comprobable está en el núcleo.

## Posibles choques

- `src/Rodaje.App/Shell/MainWindow.xaml.cs`: cambios pequeños en el constructor y en `OnClosed`; puede chocar con otras tareas que toquen esas zonas.

## Pruebas

- Linux: `Rodaje.Core.Tests` pasan 1241 de 1242 y `Rodaje.Playback.Tests` 66 de 66. La que falla, `ProjectFileTests.Save_TempLockedByAnotherProgram_ThrowsAndKeepsExistingFile`, ya fallaba igual en `main` en este Linux (bloqueos de archivo de Linux) y no la toca esta tarea. El trabajo «Linux» de la CI sale en verde.
- `Rodaje.Paquetes.Tests`: `CubeReaderTests.Lee_65_al_cubo_en_150_ms_o_menos` falló una vez por tiempo con la máquina cargada; sola pasa.
- `tests/Rodaje.App.Tests` compila en Linux con el servicio nuevo (no se puede ejecutar aquí).
- CI Windows: la solución completa (con `Rodaje.App` y `MainWindow`) compila sin advertencias. `Rodaje.Core.Tests` pasa 1253 de 1253. La primera corrida falló solo en `Rodaje.Render.Tests.Text.TitleRasterizerTests.Composing_a_ready_title_costs_little_gpu_time` (4,686 ms de GPU con el «Microsoft Basic Render Driver» del runner): es una prueba de rendimiento de títulos que esta tarea no toca. Al relanzar, GitHub ya no arrancó los trabajos: «recent account payments have failed or your spending limit needs to be increased» (límite de gasto de Actions). Hay que volver a correr la CI de Windows cuando se resuelva la facturación.
- La primera corrida de Windows encontró CsWinRT1028 (`AutosaveService` debía ser `partial`); está corregido.
- No se omitió ninguna prueba (omitidas antes y después: las mismas).

## Qué comprobar en Windows

1. Abrir Rodaje, importar un clip y editar algo. A los ~5 s debe aparecer `%LOCALAPPDATA%\Rodaje\Autoguardado\<sesión>\copia-000001.rodaje`.
2. Matar `Rodaje.exe` desde el Administrador de tareas y volver a abrir. Debe aparecer el aviso «Rodaje se cerró de forma inesperada» con «Recuperar».
3. Con el proyecto guardado antes: «Recuperar» crea `<nombre> (recuperado).rodaje` junto al original, lo abre y no toca el original. Sin guardar antes: pregunta dónde guardarlo.
4. Cerrar normalmente (con y sin «No guardar»): la carpeta de la sesión desaparece y al volver a abrir no se ofrece nada.
5. Guardar con Ctrl+S, matar el proceso y volver a abrir: no se ofrece nada.
6. Dos ventanas a la vez: ninguna ofrece recuperar la otra.
7. Editar sin parar (arrastrar clips) no debe notar tirones al escribir las copias.
