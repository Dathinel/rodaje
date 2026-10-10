# Rodaje: estado

Editor de video nativo para Windows con la experiencia de uso de CapCut y código propio. Repositorio privado `Dathinel/rodaje`, licencia MIT. Las reglas generales están en `..\CLAUDE.md`.

## Cómo se trabaja aquí

- **.NET:** siempre con `herramientas\dotnet-local.cmd <args>`, que usa el SDK 10.0.401 de `.entorno\dotnet`. La FFmpeg LGPL 9.0.2 está en `.entorno\ffmpeg`. Las dos se instalan con `herramientas\preparar-entorno.ps1`, con hashes fijados. `RODAJE_ENTORNO` apunta a otro `.entorno`, por ejemplo desde un worktree.
- **`Rodaje.bat`:** abre la app WinUI 3 (`herramientas\app.ps1`, publica en `dist\Rodaje\`); se le pueden arrastrar videos (se importan y van seguidos a la pista principal) o un `.rodaje`. Solo recompila si cambió `src\`. `app.ps1 -Medir N` mide arranque y reproducción.
- **`ABRIR.bat`:** sin argumentos corre las pruebas. Acepta:

  | Argumento | Qué hace |
  |---|---|
  | `cobertura` | Pruebas con cobertura |
  | `aot` | Prueba de humo con Native AOT |
  | `aceptacion` | Criterios de la fase 2 en serie, unos 9 min; `-Completa` añade 10 min reales |
  | `preparar-entorno` | Prepara `.entorno` |

- **Pruebas largas y de hardware:** solo corren con `RODAJE_PRUEBAS_LARGAS=1`, que activa la aceptación. Los medios de prueba con el número de cuadro codificado están en `testing/Rodaje.TestMedia` y se guardan en caché en `TestResults\media-cache`.
- **Agentes:** trabajan en worktrees dentro de `.claude\worktrees\` (ignorado), se unen con `git merge` y hacen un commit por tarea.
- **Espacios de nombres en las pruebas:** `Rodaje.X.Tests.Timing` tapa a `Rodaje.Core.Timing`, así que hay que usar el nombre completo o un `using`.

## Estado al 10 de octubre de 2026

**Fase 1, núcleo:**
- Tiempo exacto en ticks de 1/705 600 000 s.
- Modelo de proyecto → secuencias → pistas → clips, con efectos y keyframes.
- Ediciones con deshacer y rehacer: `Edits` y `UndoStack`.
- Formato `.rodaje` en JSON con versiones y guardado atómico.

**Fase 2, reproducción de un clip:**
- `Rodaje.Media`: FFmpeg con D3D11VA.
- `Rodaje.Audio`: WASAPI, que hace de reloj maestro.
- `Rodaje.Playback`: programador y caché de cuadros.
- `Rodaje.Engine`: `ClipPlayer`.
- El visor Win32 provisional se retiró; queda en la etiqueta git `visor-fase3`.

**Resultados de la fase 2:**
- Seek exacto en 2864 de 2864 casos.
- 0 cuadros de deriva en 10 min, con el reloj simulado y con el del dispositivo real.
- La revisión independiente quedó corregida.
- Daniel confía en las pruebas y pidió un lanzador más directo, que ya es `Rodaje.bat`.

**Fase 3: varias capas (terminada técnicamente).**
- `TimelineSession` maneja pistas con pre-roll, caché y presupuesto de memoria de video (3/8 de la VRAM), hasta 4 decodificadores por medio, mezclador de audio y proxies en la vista previa.
- `Rodaje.Render` compone las capas en la GPU.
- Proxies con NVENC (`Rodaje.Media/Proxies`).
- La revisión independiente encontró 7 defectos; todos están corregidos.
- En 4K con cortes la memoria de video pasó de 3,7 GB a 1,4 GB.
- Pruebas: 1299 pasan.

**Fase 4: interfaz WinUI 3 (construida y con la revisión corregida; falta la prueba a mano de Daniel).**
- `src/Rodaje.App` (abre con `Rodaje.bat` / `herramientas\app.ps1`):
  - **Armazón:** `EditorSession`, atajos con 3 estilos («Rodaje», «Ágil», «Clásico»), ajustes y tema grafito.
  - **Línea de tiempo:** barra de transporte, pista principal magnética, arrastres como gestos, marcadores y regiones (formato v2).
  - **Biblioteca:** miniaturas y formas de onda reales.
  - **Paquetes:** `src/Rodaje.Paquetes` (formato `.paquete` v0, `docs/formato-paquete.md`) y página de Complementos con el catálogo de Dathinel.
  - **Inspector:** keyframes en el cursor, «Guardar como transición» en `%LOCALAPPDATA%\Rodaje\complementos\propios`, filtros, historial y trabajos.
  - **Página de atajos:** teclado dibujado y hoja rápida.
  - **Medidores y reproductor:** medidores L/R sincronizados con lo que se oye, J/K/L y pantalla completa.
- **Cifras:** 1913 de 1927 pruebas pasan (14 omitidas, 0 fallos), AOT con 0 advertencias, 0 cuadros perdidos, edición en pantalla en ~10 ms.
- **Maqueta aprobada (D-018):** https://claude.ai/artifact/XXtTT675TEHWSPPCCZfXcv; fuente en `dist\maqueta` (no se versiona).
- **Revisión de la fase 4 corregida (10-oct, agentes A–D):** ripple con enlazados, Esc en el inspector, atajos tras las páginas, guardia de diálogos y red de errores (`rodaje-error.txt`), rutas inválidas, recuperación de paquetes tras apagón, límites y textos de manifiesto, hilos de miniaturas, medidores en pausa, contrato `IClipVisuals` único, `TimelineScheduler` en parciales, pruebas `TiempoReal` en pasada aparte, README y arquitectura al día (D-022, D-023).

## Siguiente paso (al retomar)

1. **Daniel prueba la app a mano** con `Rodaje.bat`: biblioteca, arrastres en la principal, inspector con keyframes (Esc a mitad del primer arrastre), atajos (con la página de Atajos abierta Espacio no hace nada y Ctrl+S sí guarda), AltGr para @ y €, flechas sobre un deslizador, medidores en pausa, cambiar un panel y cerrar de golpe (los ajustes se conservan), guardar y abrir. Medir con `app.ps1 -Medir N`.
2. **Que confirme Daniel:** las flechas sobre un deslizador enfocado (también el del zoom) lo mueven a él; Ctrl izquierdo + Alt derecho ya no lanza atajos Ctrl+Alt; los guardados van en fila en lugar de `.tmp` únicos.
3. **Pendientes conocidos:**
   - pico de ~600 ms en la primera edición tras importar: el motor no lo causa, falta medirlo en la interfaz;
   - dos instancias de Rodaje: la segunda puede borrar una instalación en curso de la primera (falta un bloqueo entre procesos);
   - una forma de onda quitada mientras se calcula vuelve a la memoria hasta que la expulsa el límite de 64.
4. **Después:** fase 5 (efectos, LUT, blend, render de filtros, audio con volumen y fundidos).

## Hacia la versión pública (consultado en Claude chat el 10-oct, sin decidir)

- **Exportar por CPU o GPU sin separar el programa.** Solo cambia la última pieza: una interfaz de salida con dos implementaciones (codificador de hardware, o bajar el cuadro con `Staging` y darlo a uno de software). Modelo, deshacer y línea de tiempo no se enteran. Es la fase 6.
- **Licencia de la exportación por CPU.** Propuesta: v1 en LGPL (H.264 solo por hardware; la CPU exporta AV1 con SVT-AV1, VP9 o HEVC con kvazaar). x264 (GPL) quedaría como complemento aparte con su `ffmpeg.exe` propio, consultando antes a alguien de licencias.
- **Codificadores de hardware:** ampliar `ProxyEncoders` a NVENC, AMF, QSV, Media Foundation y software; AMD e Intel necesitan probadores.
- **Vista previa «como CapCut»:** medir con `app.ps1 -Medir N` frente a CapCut con el mismo clip 4K de varias capas; vigilar el presupuesto de VRAM en gráficas de 2 GB o integradas.
- **Antes de publicar:** `legal.md` pide el código fuente de FFmpeg, `THIRD-PARTY-NOTICES.txt` y la revisión de patentes de H.264.

## Decisiones

- **Registro:** `docs/decisiones.md`, de D-001 a D-024.
- **Módulos:** `docs/propuesta-modulos.md`, aprobada. Núcleo en el exe, contenido en `.paquete` sin código, IA como procesos C#.
- **Pendientes para la interfaz:** decidir cómo funciona el ripple con enlazados de distinto largo y si se limpian los enlaces huérfanos.
- **Archivo:** la investigación y el sondeo están en `docs/archivo/`; el código del sondeo, en la etiqueta git `sondeo-interop-aot`.
