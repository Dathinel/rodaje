# Tarea 34: autoguardado, pruebas del servicio y orden de escritura

Rama `claude/autoguardado-pruebas`, PR #37. Hallazgos M8 y M9 de la revisión en bloque.

## Hecho

**Orden de escritura (`src/Rodaje.Core/Serialization/AutosaveSession.cs`):**

- `Write` escribe ahora `sesion.json` con `unsavedChanges: true` antes de la copia, no después.
  - Antes, si el editor se caía entre la copia y el `sesion.json` justo después de un `MarkSaved`, la copia nueva quedaba con `unsavedChanges: false` y no se ofrecía nunca.
  - Ahora un corte entre los dos ofrece la copia anterior. Si no hay ninguna, no se ofrece nada, como antes.
- Pruebas nuevas en `tests/Rodaje.Core.Tests/Serialization/AutosaveWriteOrderTests.cs` (3). Dos fallan con el orden antiguo.

**Recuperar (`src/Rodaje.App/Services/Autosave/AutosaveService.cs` y `AutosaveSession`):**

- **Doble clic en «Recuperar»:**
  - Antes, dos clics seguidos escribían y abrían la copia dos veces y daban dos avisos de «Proyecto recuperado».
  - Ahora `RecoverAsync` ignora el segundo clic (devuelve `false`) mientras va el primero y también después de recuperar.
  - Si la recuperación se cancela o falla, se puede volver a intentar.
- **`Discard` que no logra borrar el bloqueo** (`AutosaveRecovery.cs:69`; se traga el `IOException` o el `UnauthorizedAccessException`):
  - Antes, la sesión ya recuperada se volvía a ofrecer en el siguiente arranque.
  - Ahora, tras recuperar, el servicio llama a `AutosaveSession.MarkRecovered`, que es nuevo. Este método escribe en `sesion.json` de esa sesión `unsavedChanges: false` y luego llama a `Discard`.
  - La sesión solo se vuelve a ofrecer si fallan las dos cosas.
- `WhenWritesDone()` (interno) permite que las pruebas esperen a la fila de escrituras.

**Pruebas del servicio (`tests/Rodaje.App.Tests/Autosave/`, 20 nuevas):**

- `AutosaveHarness.cs` contiene:
  - un reloj falso (`ManualClock : TimeProvider`);
  - `EditorSession` con los `FakeDialogs` que ya existían;
  - un ayudante que deja en la carpeta una sesión «caída».
- `AutosaveServiceTests` (12):
  - `Start`;
  - `Tick`: espera la calma, no copia durante un gesto, respeta el tiempo mínimo entre copias y no hace nada sin `Start`;
  - la fila de escrituras: si se guarda con una copia aún en la fila, `MarkSaved` queda detrás de la copia;
  - `MarkSaved` al guardar y al deshacer hasta lo guardado;
  - `Clear` al abrir otro proyecto;
  - `Complete` y cierre inesperado.
- `AutosaveRecoveryServiceTests` (11):
  - oferta con el botón «Recuperar»;
  - sin sesiones caídas;
  - última copia dañada (recupera la anterior);
  - todas las copias dañadas (avisa y se vuelve a ofrecer);
  - `sesion.json` corrupto antes de la oferta (no se ofrece y no falla) y después de ella (recupera igual);
  - bloqueo que no se deja borrar;
  - doble clic (directo y con el botón del aviso);
  - proyecto que nunca se guardó (pregunta dónde guardarlo y se puede reintentar);
  - `RecoveredPathNextTo`.
- Sin la corrección fallan 3: el doble clic, los dos casos, y el bloqueo que no se borra.

## Decisiones

- **El bloqueo que no se borra se simula sin abrirlo**, porque la sesión recuperable lo tiene abierto sin compartir:
  - en Windows se marca el archivo de solo lectura;
  - en Linux la prueba corre como administrador y los permisos no frenan, así que se reemplaza por una carpeta con algo dentro.
  - Al terminar, el bloqueo vuelve a ser un archivo normal.
- **`sesion.json` corrupto antes de la oferta no se ofrece.** Es el comportamiento que ya había y la prueba lo deja fijado. Ese archivo se escribe de forma atómica, así que este caso no debería darse.
- **Un fallo de `Write` deja `unsavedChanges: true`.** Con el proyecto sucio es lo correcto. En el peor caso se ofrece una copia anterior a lo último que se guardó, y nunca se pierde una copia.

## Pendiente

- No hay prueba del aviso «No se pudo autoguardar» ante un fallo de disco en el servicio. Para provocarlo de forma portable haría falta inyectar el sistema de archivos en `AutosaveService`, y la tarea no lo pedía.

## Pruebas

- **Linux:**
  - `Rodaje.Core.Tests`: pasan 1327 de 1327.
  - `Rodaje.Paquetes.Tests`: pasan 383 y se omiten 2, igual que en `main`.
  - `Rodaje.Playback.Tests`: pasan 65 de 65.
  - `Rodaje.App.Tests` compila en Linux con `-p:EnableWindowsTargeting=true` y sus pruebas de autoguardado corren ahí: pasan 23 de 23, repetidas 3 veces.
- **CI** del commit de pruebas (`0d2d5cf7`): Linux y Windows en verde. En Windows, `Rodaje.App.Tests` pasa 841 y omite 2, las mismas que en `main`.
- **CI del commit de la entrega (`c25cb44c`):** en la primera corrida de Windows falló una prueba ajena, `ModuleManagerTests.Una_llamada_larga_que_contesta_el_ping_no_se_toma_por_colgada`. Depende del tiempo (5 s) y el commit solo cambiaba documentación. Al relanzar, los dos trabajos salieron en verde.
- **Pruebas omitidas:** las mismas antes y después. No se omite ninguna nueva.

## Posibles choques

- La tarea 35 (`claude/limpiezas-bloque-1`) toca `EditorSession*.cs`, pero no estos archivos.
- `AutosaveSession.WriteInfo` pasa a ser estático y recibe la carpeta. Una rama que toque esas líneas chocaría de forma trivial.

## Qué comprobar en Windows

1. Editar, esperar la copia (unos 5 s), matar `Rodaje.exe` y volver a abrir. Debe ofrecerse «Recuperar».
2. Hacer doble clic rápido en «Recuperar». Debe crearse un solo `(recuperado).rodaje` y salir un solo aviso de «Proyecto recuperado».
3. Tras recuperar, cerrar y volver a abrir: no debe ofrecerse nada.
4. Guardar con Ctrl+S, matar el proceso y volver a abrir: no debe ofrecerse nada.
