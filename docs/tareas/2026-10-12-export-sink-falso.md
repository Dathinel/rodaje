# Tarea 63: las carreras del sink sin ffmpeg.exe

Rama `claude/export-sink-falso`, PR #60.

## Lo hecho

1. **ffmpeg falso** (`testing/Rodaje.FakeFfmpeg`, net10.0, añadido a `Rodaje.slnx` junto a los demás de `/testing/`, con su `packages.lock.json`).
   - Acepta la línea de comandos del sink tal cual e ignora lo que no conoce. La salida es el último argumento, como en ffmpeg.
   - Crea la salida al arrancar y copia en ella lo que llega por la entrada estándar.
   - Opciones propias, que las pruebas pasan como argumentos del codificador:
     - `-falso-salida N`: sale con el código N cuando se acaba la entrada.
     - `-falso-colgar`: no sale nunca y espera a que lo maten. Simula un ffmpeg.exe que está cerrando el contenedor.
   - `Rodaje.Media.Tests` lo referencia como hace `Rodaje.App.Tests` con el módulo de eco, así que queda junto a las pruebas: `Rodaje.FakeFfmpeg.exe` en Windows y `Rodaje.FakeFfmpeg` en Linux.
2. **Punto de inyección en `FfmpegPipeSink.cs`.**
   - **Constructor interno `FfmpegPipeSink(string unverifiedExecutable, FfmpegPipeSinkOptions?)`.** Hacía falta porque `FfmpegExecutable` exige la LGPL, las DLL extra y la serie de libavcodec, y un ejecutable falso no puede cumplirlo. `EnsureUnchanged` se sigue llamando cuando hay un `FfmpegExecutable`. El constructor público no cambia.
   - **`ChildProcessJob.TryAssign` solo en Windows** (`OperatingSystem.IsWindows()`). En Linux, la P/Invoke a kernel32 lanzaba `DllNotFoundException`. En Windows, donde corre Rodaje, el comportamiento es el mismo.
   - **`BeforeCompleteWaits` y `KillOverride` siguen siendo propiedades.** Pasarlas al constructor era opcional, y así el cambio queda más pequeño para la tarea 62.
3. **`FfmpegPipeSinkFakeRaceTests`**: cinco `[Fact]`, sin `ProxyFact`, en los archivos nuevos `FfmpegPipeSinkFakeRaceTests.cs` y `FakeFfmpeg.cs`.
   - Cancelar el token mientras `Complete` espera: sale `OperationCanceledException` con el token, el proceso está muerto, no quedan temporales y el registro queda vacío.
   - `Abort` desde otro hilo mientras `Complete` espera.
   - `Abort` después de un `Complete` correcto: no toca el archivo entregado. Además se comprueba que llegaron todos los bytes de los cuadros.
   - Un ffmpeg que sale con código 3: es `ExportException` y no una cancelación, y no deja temporales ni registro.
   - El `Abort` que no consigue matar (con `KillOverride`): vuelve en menos de 1 s y deja la limpieza pendiente, que termina al soltarla.

   En las cuatro carreras se comprueba también que el falso estaba **vivo** cuando llegó la cancelación. Así la prueba no pasa sin ejercitar nada. No se subió ningún tope.

## Decisiones

- **Las cuatro pruebas `ProxyFact` de la tarea 44 se quedan.** Prueban el ffmpeg.exe de verdad en el PC de Daniel, y la tarea limita los cambios a archivos nuevos.
- **Si falta `DOTNET_ROOT`, las pruebas lo ponen** a partir del runtime que las corre. El lanzador del falso lo necesita cuando dotnet no está en el lugar por omisión. La CI de Windows ya lo define, y `dotnet-local.cmd` también lo hace.

## Pruebas (Linux, `dotnet test tests/Rodaje.Media.Tests -p:EnableWindowsTargeting=true`)

- **Las nuevas:** 5 de 5 pasadas, en 9 corridas seguidas.
- **`Rodaje.Media.Tests` entera:**

  | | Pasadas | Omitidas | Fallidas |
  |---|---|---|---|
  | Antes | 345 | 118 | 5 |
  | Después | 350 | 118 | 5 |

  Las 5 fallidas son las mismas antes y después y son propias de Windows: `ProxyStoreTests` (×3), `ExportTemporariesTests.A_temporary_still_open…` y `VideoColorTests.Conversion_through_swscale…`. Se deben a rutas que no distinguen mayúsculas, a archivos abiertos que no se pueden borrar y a las DLL de FFmpeg. Este proyecto no corre en la CI de Linux.
- **Por qué las omitidas no bajan:** las cuatro `ProxyFact` de la tarea 44 siguen ahí, y sin ffmpeg.exe se omiten por plataforma o codificador, igual que antes. Lo que cambia es que esos mismos casos ahora también corren, en Linux y en la CI de Windows, con el falso. Para que bajaran habría que borrar o convertir esas cuatro, y eso queda fuera de los caminos de la tarea.
- **`dotnet restore Rodaje.slnx --locked-mode -p:EnableWindowsTargeting=true`** pasa.

## Posibles choques

- **Tarea 62:** toca `FfmpegPipeSink.cs`. Esta rama cambia solo los constructores, el campo `_ffmpeg` (ahora puede ser nulo, con `_executablePath` al lado) y tres líneas de `Launch`.
- **Lockfile:** cambia una línea de `tests/Rodaje.Media.Tests/packages.lock.json` (la entrada `rodaje.fakeffmpeg`).

## Qué comprobar en Windows

1. Que en `ABRIR.bat` pasen `FfmpegPipeSinkFakeRaceTests` y `Rodaje.FakeFfmpeg.exe` quede junto a `Rodaje.Media.Tests.dll`.
2. Que con el ffmpeg.exe de `.entorno` sigan pasando `FfmpegPipeSinkAbortRaceTests` y `FfmpegPipeSinkStuckKillTests`.
