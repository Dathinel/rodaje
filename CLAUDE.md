# Rodaje: estado

Editor de video nativo para Windows con la experiencia de uso de CapCut y código propio. Repositorio privado `Dathinel/rodaje`, licencia MIT. Las reglas generales están en `..\CLAUDE.md`.

## Cómo se trabaja aquí

- **.NET:** siempre con `herramientas\dotnet-local.cmd <args>`, que usa el SDK 10.0.401 de `.entorno\dotnet`. La FFmpeg LGPL 9.0.2 está en `.entorno\ffmpeg`. Las dos se instalan con `herramientas\preparar-entorno.ps1`, con hashes fijados. `RODAJE_ENTORNO` apunta a otro `.entorno`, por ejemplo desde un worktree.
- **`Rodaje.bat`:** abre la app WinUI 3 (`herramientas\app.ps1`, publica en `dist\Rodaje\`); se le pueden arrastrar videos (se importan y van seguidos a la pista principal) o un `.rodaje`. Solo recompila si cambió `src\`. `app.ps1 -Medir N` mide arranque y reproducción. El visor anterior sigue en `ABRIR.bat visor`.
- **`ABRIR.bat`:** sin argumentos corre las pruebas. Acepta:

  | Argumento | Qué hace |
  |---|---|
  | `cobertura` | Pruebas con cobertura |
  | `aot` | Prueba de humo con Native AOT |
  | `aceptacion` | Criterios de la fase 2 en serie, unos 9 min; `-Completa` añade 10 min reales |
  | `visor` | Abre el visor |
  | `preparar-entorno` | Prepara `.entorno` |

- **Pruebas largas y de hardware:** solo corren con `RODAJE_PRUEBAS_LARGAS=1`, que activa la aceptación. Los medios de prueba con el número de cuadro codificado están en `testing/Rodaje.TestMedia` y se guardan en caché en `TestResults\media-cache`.
- **Agentes:** trabajan en worktrees dentro de `.claude\worktrees\` (ignorado), se unen con `git merge` y hacen un commit por tarea.
- **Espacios de nombres en las pruebas:** `Rodaje.X.Tests.Timing` tapa a `Rodaje.Core.Timing`, así que hay que usar el nombre completo o un `using`.

## Estado al 9 de octubre de 2026

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
- `Rodaje.Visor`: ventana Win32 provisional, que se reemplaza en la fase 4.

**Resultados de la fase 2:**
- Seek exacto en 2864 de 2864 casos.
- 0 cuadros de deriva en 10 min, con el reloj simulado y con el del dispositivo real.
- La revisión independiente quedó corregida.
- Daniel confía en las pruebas y pidió un lanzador más directo, que ya es `Rodaje.bat`.

**Fase 3: varias capas (terminada técnicamente).**
- `TimelineSession` maneja pistas con pre-roll, caché y presupuesto de memoria de video (3/8 de la VRAM), hasta 4 decodificadores por medio, mezclador de audio y proxies en la vista previa.
- `Rodaje.Render` compone las capas en la GPU.
- Proxies con NVENC (`Rodaje.Media/Proxies`).
- El visor acepta varios videos con imagen en imagen.
- La revisión independiente encontró 7 defectos; todos están corregidos.
- En 4K con cortes la memoria de video pasó de 3,7 GB a 1,4 GB.
- Pruebas: 1299 pasan.

**Fase 4: interfaz WinUI 3 (en curso).**
- El sondeo (`docs/sondeo-winui.md`) confirma que se publica sin paquete, autocontenido y con Native AOT, con 0 advertencias y 0 refrescos perdidos con 4 capas.
- Diseño: la maqueta aprobada en D-018, más lo de Shotcut: medidores de audio, pila de filtros, historial y trabajos.
- Armazón de la app hecho (`docs/arquitectura/app.md`): `EditorSession` como fuente única de verdad, tema grafito, atajos con estilos Rodaje, CapCut y Vegas guardados en `atajos.json`, ajustes en `ajustes.json`, distribución con separadores y el reproductor en `Views/Player`. Importar, doble clic para añadir a la pista principal, cortar, recortar, borrar, deshacer, historial, abrir y guardar ya funcionan. Arranque en caliente: ventana a los 290 ms y primer cuadro a los 690 ms; `dist\Rodaje` pesa 218 MB.
- Ojo legal: los binarios de Windows App SDK tienen términos de Microsoft que no son MIT; están en `LICENCIAS.md`.

## Siguiente paso

1. **Construir la app en `src/Rodaje.App`:**
   - las vistas en paralelo: línea de tiempo con la barra estilo Vegas y la pista principal magnética, biblioteca y complementos, inspector de keyframes, página de atajos, medidores y paneles.
2. **Cuando la interfaz esté usable, Daniel la prueba.** Se para y se le dice qué probar.
3. Retirar el visor cuando la app lo reemplace.

## Decisiones

- **Registro:** `docs/decisiones.md`, de D-001 a D-015.
- **Módulos:** `docs/propuesta-modulos.md`, aprobada. Núcleo en el exe, contenido en `.paquete` sin código, IA como procesos C#.
- **Fase 4:** la app final será un proyecto WinUI 3 nuevo y el visor se borra.
- **Pendientes para la interfaz:** decidir cómo funciona el ripple con enlazados de distinto largo y si se limpian los enlaces huérfanos.
- **Archivo:** la investigación y el sondeo están en `docs/archivo/`; el código del sondeo, en la etiqueta git `sondeo-interop-aot`.
