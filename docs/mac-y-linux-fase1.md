# Rodaje en Linux y macOS: fases 1 y 2

Fases 1 (núcleo portátil) y 2 (entorno y FFmpeg) de D-066 ([propuestas/mac-y-linux.md](propuestas/mac-y-linux.md) §5). Hechas el 16 de noviembre de 2026 sobre la fase 0 ([mac-y-linux-fase0.md](mac-y-linux-fase0.md)). En el equipo de Daniel no se instaló nada: Linux y macOS corren en GitHub Actions.

## Cómo quedó

| Pieza | Qué hace | PR |
|---|---|---|
| Destinos | Media, Audio y Engine pasan a `net10.0`; lo de D3D11 y WASAPI lleva `[SupportedOSPlatform]` y se llama tras `OperatingSystem.IsWindows*` o una guarda. Render pasa a `net10.0` con atributo de ensamblado solo-Windows, sin cambios de código (IGpu es la fase 3). Se compila todo en los tres sistemas: no hay archivos excluidos por anfitrión | #572, #578 |
| `ChildProcessGuard` | Windows: el Job Object de siempre. Linux y macOS: registro por sesión con PID y hora de inicio; se matan al salir y el siguiente arranque barre los de un editor muerto | #572 |
| `FfmpegLibrary` | `avcodec-63.dll` / `libavcodec.so.63` / `libavcodec.63.dylib`; `.entorno/ffmpeg/lib` y `ffmpeg` en `../bin` fuera de Windows | #572, #578 |
| Rutas (Core) | `PortablePath`: forma de la ruta (Windows, POSIX, relativa), mapeo del usuario antes de resolver, relativas con `\` en Unix; `SamePath` distingue mayúsculas solo entre rutas POSIX en Linux. `schemaVersion` sin cambios | #577 |
| Protocolo de módulos | Versión 1 igual. Socket Unix en `$XDG_RUNTIME_DIR/rodaje/e.<pid>/m.<16 hex>` o `/tmp/rodaje-<uid>/…` (0700), PID del otro extremo por `SO_PEERCRED`/`LOCAL_PEERPID`, regiones como archivos 0600 exclusivos, barrido de sesiones muertas, el módulo muere si muere el editor | #574 |
| Packages | Índice por sistema (`indice.json` igual en Windows; `indice-linux-x64.json`, `indice-osx-arm64.json`), enlaces duros con `link(2)`, `module.entry` 0755 al instalar | #575 |
| CI | `build/pruebas-linux.sh` con todos los proyectos sin GPU; trabajo de macOS en `ci.yml`; prueba colgada cortada a los 10 min | #574 |
| Entorno | `preparar-entorno.ps1` en PowerShell 5.1 y en `pwsh` 7 (Linux x64, macOS arm64), `dotnet-local.sh`, `entorno.yml` | #573 |
| FFmpeg | LGPL v3 para `linux64` (receta de BtbN, mismas exclusiones) y `macarm64` (receta propia, ocho bibliotecas no GPL fijadas por huella), auditoría de dependencias por RID, fuente correspondiente; `ffmpeg-lgpl.yml` deja paquetes y fuente como artefactos y la CI de Unix los instala con la huella fijada | #579, #581 |

Decisiones tomadas por el camino (opción recomendada, sin parar):

- **Sin archivos por sistema anfitrión.** El diseño decía `*.Windows.cs` incluidos por condición; con un solo `net10.0` es más simple compilar todo y dejar que el analizador de plataforma (CA1416, error) obligue a guardar cada llamada. AOT quita lo que no se usa.
- **`getppid` en vez de `PR_SET_PDEATHSIG`.** El aviso de Linux sigue al hilo que creó el proceso, y en .NET puede ser uno del pool que se retira solo: mataría módulos sanos. El módulo mira a su padre cada 250 ms (igual en macOS).
- **Una ruta de Windows no distingue mayúsculas en ningún sistema** (`SamePath`).

## Hecho cuando

**Fase 1: cumplido.** Corrida de referencia en la CI del #581, ya con el FFmpeg LGPL de la fase 2: [Linux](https://github.com/Dathinel/rodaje/actions/runs/43969890246/job/133658955674) (`ubuntu-latest`) y [macOS](https://github.com/Dathinel/rodaje/actions/runs/43969890246/job/133658956201) (`macos-15`, arm64), filtro `Categoria!=SoloWindows&Categoria!=TiempoReal&Categoria!=Rendimiento`. Windows sigue verde en todas las corridas de los PR (solución completa).

| Proyecto | Linux: pasan / omitidas | macOS: pasan / omitidas |
|---|---|---|
| Core | 4 285 / 6 | 4 285 / 6 |
| Packages (con las hostiles de #496) | 1 576 / 14 | 1 576 / 14 |
| Modules.Protocol (con las hostiles de #496) | 628 / 0 | 627 / 1 |
| Media | 767 / 65 | 767 / 65 |
| Engine | 814 / 12 | 810 / 16 |
| Audio | 226 / 7 | 226 / 7 |
| Playback, Analysis, TestServer, TestMedia | 154, 92, 77, 69 / 3 | igual |
| **Total que pasan** | **8 688** | **8 683** |

- **Muerte abrupta del editor sin hijos vivos**, en los dos sistemas: `UnixTransportTests.A_module_does_not_outlive_an_editor_killed_abruptly` (un `sh` lanza el módulo de eco y se mata con SIGKILL: el módulo desaparece en menos de 5 s, antes de su espera de conexión de 10 s) y `ChildProcessGuardTests.Abrupt_death_of_the_editor_leaves_no_child_alive_after_the_sweep` (FFmpeg y demás hijos: el barrido del arranque siguiente los mata y respeta un PID reciclado).
- **Hostiles de #496:** `ProtocolHostileEdgeTests`, `PathsAndCultureProtocolTests`, el fuzz de regiones (antes solo Windows) y las de Packages pasan en Linux y macOS. El protocolo sigue en la versión 1.
- **Omitidas:** Direct3D 11 (D3D11VA, texturas, WARP), NVENC y la salida de audio, que no existen fuera de Windows; en macOS, además, congelar un módulo (`SIGSTOP` colgaba la corrida) y la instantánea de pilas de ClrMD.
- **`SoloWindows` nuevas** (Windows las sigue corriendo): borrar un archivo abierto (proxies, caché de render, temporales de exportación), rutas con semántica de Windows, la salida de WARP y una comparación exacta de bytes reservados por Tick que en Linux varía unos bytes.

**Fase 2: cumplido, sin publicar.** `entorno.yml` reproduce `.entorno` con las huellas fijadas en los tres sistemas ([Ubuntu 24.04](https://github.com/Dathinel/rodaje/actions/runs/43959170814/job/133621632101), [macOS 15](https://github.com/Dathinel/rodaje/actions/runs/43959170814/job/133621631314), [Windows](https://github.com/Dathinel/rodaje/actions/runs/43959170814/job/133621631973)): SDK 10.0.401 por SHA-512 de `releases.json` y FFmpeg por SHA-256 (Windows desde la release; Linux y macOS desde los artefactos, con la misma comprobación). La auditoría de `ffmpeg-lgpl.yml` (corridas [43943855884](https://github.com/Dathinel/rodaje/actions/runs/43943855884) (Linux) y [43968642464](https://github.com/Dathinel/rodaje/actions/runs/43968642464) (macOS, con zlib)) no encontró nada GPL ni no libre en ninguno de los dos RID; detalle por RID en [legal.md](legal.md), «Linux x64 y macOS arm64». Linux tardó 1 h 27 min en compilar en el corredor; macOS, 8 min.

## Lo que queda

- `ChildProcessGuard.SweepOrphans()` y el mapeo de rutas en Preferencias se llaman desde la app de Linux (fase 8).
- La prueba de módulo congelado se omite en macOS: con `SIGSTOP` allí colgaba la corrida (sin investigar).
- **Publicar el FFmpeg de Linux y macOS** (paquetes y fuente) en la release `ffmpeg-n9.0.2-17-g2a571b6068-rodaje1`: lo decide Daniel. Hasta entonces la CI los toma de los artefactos de `ffmpeg-lgpl.yml`, que caducan a los 90 días (19 de febrero de 2027).
- De la revisión de bloque (Gemini), sin hacer: el registro de `ChildProcessGuard` crece en sesiones largas, la emulación de Windows en `PortablePath.Resolve` solo la usan las pruebas, P/Invoke de `libc` repetidos y dos pruebas poco exigentes. Pregunta suya para Daniel: OpenH264 compilado desde el código en macOS no lo cubre la licencia de patentes de Cisco (lo mismo que en Windows).
