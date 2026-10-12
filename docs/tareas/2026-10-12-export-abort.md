# Tarea 33: cancelar la exportación en ≤1 s también con VP9

Rama `claude/export-abort`, PR #33.

## Causa

`CpuExportCancellationTests.Canceling_mid_export_leaves_no_process_and_no_file` tardaba 3–7 s en la CI de Windows. El tope era de 3 s. Según la entrega de la tarea 40, `Abort` volvía siempre a los 917–945 ms, cuando se agotaba `KillWait` (900 ms) sin que ffmpeg.exe hubiera salido.

- `Kill` llamaba a `Process.Kill(entireProcessTree: true)`, que pide la terminación y después recorre todos los procesos del sistema buscando hijos. Luego `Abort` esperaba 900 ms bajo el cerrojo.
- `TerminateProcess` no es instantáneo: cada hilo del proceso tiene que llegar a ejecutar su salida. ffmpeg.exe corre en `BelowNormal` (modo bajo, el valor por defecto de `FfmpegPipeSinkOptions.Priority`). Con la CPU saturada por hilos `Normal` de las demás pruebas, esos hilos no tienen turno. Windows solo da prioridad a los hilos hambrientos tras unos 4 s, y eso cuadra con los 3–7 s medidos.
- Mientras ffmpeg.exe no termina, la lectura de la tubería sigue abierta y el escritor de la prueba queda bloqueado en `WriteFile`. Esa es la latencia que mide la prueba.
- El «NaN» de `ExitTime` en el diagnóstico venía de la propia prueba. Cuando leía `ExitTime`, el sink ya había soltado su `Process` y el proceso había desaparecido, así que no quedaba handle abierto.

## Arreglo

- **`FfmpegPipeSink.Kill`:**
  - Primero sube ffmpeg.exe a `ProcessPriorityClass.High`. Un proceso que ya está muriendo no compite con nadie, y sus hilos ejecutan la salida enseguida.
  - Después hace `Process.Kill()`, un `TerminateProcess` directo, y espera `KillWait`.
  - Solo si no salió a tiempo, mata el árbol (`Kill(entireProcessTree: true)`) y espera otra vez. ffmpeg.exe no lanza hijos con estos argumentos, y el `ChildProcessJob` sigue matando todo si Rodaje se cierra.
  - `Abort` conserva el cerrojo. Ahora la espera termina en cuanto ffmpeg sale, y sin ffmpeg muerto no se pueden borrar los temporales (Windows no borra un archivo abierto). No hizo falta esperar fuera del cerrojo.
- **No se usa `TerminateJobObject`:** el trabajo es uno solo para todos los hijos (proxies, sondas, exportación). Terminarlo mataría también a los demás, y además usa el mismo mecanismo que `TerminateProcess`.
- **`CpuExportCancellationTests`:**
  - El tope baja de 3 s a 1 s, el criterio de la fase 6.
  - El handle del proceso vigilado se abre antes de cancelar, para que `ExitTime` se pueda leer y la línea de diagnóstico diga cuándo murió ffmpeg.exe.

## Pruebas omitidas

No se omitió ni se desactivó ninguna prueba.

## Estado de la CI

- **Linux:** en verde. `Rodaje.Media.Tests` compila en Linux con `-p:EnableWindowsTargeting=true`, sin advertencias.
- **Windows:** sin resultado de pruebas. En las dos corridas (run 38206623708, intentos 1 y 2, sobre la unión con `ecd16a9` de main) `dotnet restore --locked-mode` falla antes de compilar, con NU1004 en `src/Rodaje.Modules.Protocol` (tarea 43, fuera de los caminos). Por eso `CpuExportCancellationTests` todavía no se ha visto pasar con el tope de 1 s. Al unirse la tarea 43 hay que relanzar la CI de este PR y mirar las líneas «cancelada a los … ms».

## Posibles choques

- `FfmpegPipeSink.cs` no lo toca ninguna otra rama `claude/*`.
- **PR #22 (`claude/export-ci`, tarea 20, abierto) también cambia `CpuExportCancellationTests.cs`.** Agrega el código y el momento de salida de ffmpeg.exe al diagnóstico. Si se unen los dos, hay que conservar el tope de 1 s de este PR. No se resolvió aquí.
- El fallo de restauración NU1004 (tarea 43) y otras fallas de main ajenas a esta tarea pueden tapar el resultado de Media en la CI hasta que se unan la tarea 40 (PR #31) y la 43.

## Qué comprobar en Windows

1. `ABRIR.bat`: que pasen `CpuExportCancellationTests` y `FfmpegPipeSinkFailureTests`, y que las líneas de diagnóstico muestren ffmpeg.exe muerto a los pocos cientos de ms.
2. En la app, cancelar una exportación VP9 o AV1 en «Bajo rendimiento» con el equipo ocupado: debe parar en menos de 1 s sin dejar archivo.
