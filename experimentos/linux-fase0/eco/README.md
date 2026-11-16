# Eco del protocolo de módulos en Unix (fase 0e)

Prototipo de la fila «Tuberías de módulos», «Memoria compartida» y «Procesos hijos» de la tabla «Lo demás»
de `docs/propuestas/mac-y-linux.md` (D-066). El mismo ejecutable hace de editor (padre) y de módulo (hijo):
se saludan con el protocolo v1 real (`hello`/`welcome`, marco de 4 bytes + JSON de `MessageChannel.cs`,
enlazado desde `src/Rodaje.Modules.Protocol/` junto con `Messages.cs` y `ModuleExceptions.cs`), el editor
llama a una función `eco` que copia un cuadro 1920×1080 RGBA dentro de una región de memoria compartida y
verifica el resultado byte a byte. Sale con 0 solo si todas las comprobaciones pasan.

## Cómo se ejecuta

- **CI (Ubuntu 24.04 y `macos-15` arm64):** `bash experimentos/linux-fase0/eco/ci.sh` — publica con
  `PublishAot=true` para el RID del corredor y corre el binario. No instala nada.
- **A mano en Unix:** `dotnet publish eco.csproj -c Release -r linux-x64 -p:PublishAot=true` y
  `./bin/Release/net10.0/linux-x64/publish/eco`. Escribe `resultado.json` en el directorio de trabajo.
- **En Windows:** solo `eco --solo-socket` (los sockets de dominio Unix de .NET funcionan en Windows 10+):
  escucha en `%TEMP%`, hace el saludo y un `ping` dentro del mismo proceso. El resto (credenciales,
  `mmap`, `prctl`/`kqueue`) es solo Unix.
- Papeles internos (los lanza la batería, no se usan a mano): `--rodaje-pipe <ruta>` es el módulo;
  `--intruso <ruta>` conecta sin ser el hijo; `--editor-intermedio <ruta>` hace de editor que muere.

## Qué comprueba cada paso

| Comprobación | Qué demuestra |
|---|---|
| `carpeta-base-0700`, `carpeta-ejecucion-0700` | La base (`$XDG_RUNTIME_DIR/rodaje` en Linux con respaldo `/tmp/rodaje-<uid>`; `/tmp/rodaje-<uid>` en macOS) y la carpeta de la ejecución `e.<pid>` quedan con 0700. |
| `ruta-socket-corta` | El socket `e.<pid>/m.<8 hex>` cabe en `sun_path` (108 en Linux, 104 en macOS). Se imprime el largo real. |
| `riesgo-ruta-tmpdir` | Un socket bajo `$TMPDIR` con el nombre de tubería actual `CoreFxPipe_Rodaje.Mod.pipe.<32 hex>`: largo real frente al límite y qué hizo el bind (en macOS se pasa de 104 y tiene que fallar). |
| `region-crear`, `region-borrada-al-cerrar` | La región es un archivo de la carpeta de la ejecución, creado en exclusiva con 0600 (`FileStreamOptions.UnixCreateMode`) y mapeado con `MemoryMappedFile`; se borra al cerrar. |
| `region-nombre-duplicado` | Crear dos veces el mismo nombre falla (`FileMode.CreateNew`, o sea `O_EXCL`). |
| `region-enlace-rechazado` | Abrir un nombre que es un enlace simbólico se rechaza (`O_NOFOLLOW`). |
| `region-tamano-distinto`, `region-permisos-distintos` | Al abrir, `fstat` exige archivo regular del mismo usuario, modo 600 y el tamaño esperado. |
| `huerfanos-barridos` | Al arrancar se borran las `e.<pid>` de procesos que ya no existen. |
| `credenciales-extremo` | Tras el accept, `SO_PEERCRED` (Linux) o `LOCAL_PEERPID`/`LOCAL_PEERCRED` (macOS) dan el pid y el uid del otro lado: el pid es el del hijo lanzado y el uid el propio. |
| `intruso-rechazado` | Un segundo proceso del mismo usuario conecta; su pid no es el del hijo y la conexión se cierra. |
| `saludo-v1` | `hello`/`welcome` reales por el `MessageChannel`: protocolo 1 y la función «eco» anunciada. |
| `eco-cuadro-1080p`, `eco-200-cuadros` | `invoke eco` con dos búferes (entrada y salida) de la misma región; el módulo copia y el editor verifica byte a byte. La segunda da los cuadros/s en `medidas.eco_cuadros_s`. |
| `ping-1000` | Ida y vuelta de `ping`/`pong`: p50 y p95 de 1000 en `medidas.ping_p50_us`/`ping_p95_us`. |
| `cierre-limpio` | `shutdown` → el módulo sale con código 0. |
| `muerte-padre-2s` | Un editor intermedio lanza el módulo y se mata con SIGKILL; el módulo desaparece en < 2 s (vigilancia del padre: `prctl(PR_SET_PDEATHSIG)` en Linux, `kqueue NOTE_EXIT` en macOS con sondeo de `getppid` de respaldo; más el fin de flujo del socket). |

## Qué cambia frente a Windows (para la fase 1)

- **Transporte:** el `NamedPipeServerStream` `Rodaje.Mod.pipe.<guid>` pasa a un socket de dominio Unix en
  `…/e.<pid>/m.<8 hex>`; el hijo recibe `--rodaje-pipe <ruta absoluta>` igual que hoy recibe el nombre.
- **Autenticación:** `GetNamedPipeClientProcessId` pasa a las credenciales del socket (`SO_PEERCRED` /
  `LOCAL_PEERPID`+`LOCAL_PEERCRED`); se comprueban pid y uid, no solo el pid.
- **Región:** el nombre `Rodaje.Mod.<hex>` ya no es un objeto del kernel: se resuelve a
  `<carpeta 0700>/<nombre>` (la carpeta del socket, que el módulo deduce del propio `--rodaje-pipe`).
  `MemoryMappedFile` sigue sirviendo (`mmap` por dentro); la creación exclusiva con 0600 sale de
  `FileStreamOptions.UnixCreateMode` (el `open` con modo es variádico y en arm64 de Apple no se puede
  llamar por P/Invoke) y al abrir se exige `O_NOFOLLOW` + `fstat` (regular, dueño, 600, tamaño).
- **Muerte del editor:** en Windows el Job Object mata al módulo; en Unix se protege el propio módulo
  (`PR_SET_PDEATHSIG` / `kqueue NOTE_EXIT`, más el EOF del socket) y el editor barre las `e.<pid>` de
  procesos muertos al arrancar.
- **Limpieza:** el archivo del socket hay que borrarlo a mano (el kernel no lo quita); toda la carpeta
  `e.<pid>` se borra al terminar.

## Cifras

Las rellena la CI; aquí solo van los huecos.

| Medida | Ubuntu 24.04 | macos-15 arm64 |
|---|---|---|
| ping p50 / p95 (1000, µs) | — | — |
| eco 1080p RGBA (cuadros/s, 200) | — | — |
| muerte del padre (ms) | — | — |
