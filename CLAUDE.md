# Rodaje: estado

Editor de video nativo para Windows con la experiencia de uso de CapCut y código propio. Repositorio privado `Dathinel/rodaje`, licencia MIT. Las reglas generales están en `..\CLAUDE.md`.

## Cómo se trabaja aquí

- **.NET:** siempre con `herramientas\dotnet-local.cmd <args>`, que usa el SDK 10.0.401 de `.entorno\dotnet`. La FFmpeg LGPL 9.0.2 está en `.entorno\ffmpeg`. Las dos se instalan con `herramientas\preparar-entorno.ps1`, con hashes fijados. `RODAJE_ENTORNO` apunta a otro `.entorno`, por ejemplo desde un worktree.
- **`Rodaje.bat`:** abre el visor; también se le pueden arrastrar varios videos (el primero de fondo, los demás como imagen en imagen) o un `.rodaje`. Solo recompila si cambió `src\`.
- **`ABRIR.bat`:** sin argumentos corre las pruebas. Acepta:

  | Argumento | Qué hace |
  |---|---|
  | `cobertura` | Pruebas con cobertura |
  | `aot` | Prueba de humo con Native AOT |
  | `aceptacion` | Criterios de la fase 2 en serie, unos 9 min; `-Completa` añade 10 min reales |
  | `visor` | Abre el visor |
  | `preparar-entorno` | Prepara `.entorno` |

- **Pruebas largas y de hardware:** solo corren con `RODAJE_PRUEBAS_LARGAS=1`, que activa la aceptación. Los medios de prueba con el número de cuadro codificado están en `tests/Rodaje.TestMedia` y se guardan en caché en `TestResults\media-cache`.
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

**Fase 3, varias capas (en curso).** Tres agentes trabajan en paralelo:
- `TimelineSession` en Engine: pistas, pre-roll, mezclador de audio y caché por fuente.
- `Rodaje.Render`: compositor D3D11 con `LayerTransform`.
- Proxies NVENC en `Rodaje.Media/Proxies`.

El contrato entre ellos está en `src/Rodaje.Playback/Composition` y `Core/Model/BuiltInEffects.cs`.

**Pruebas:** 1016 pasan; las 9 largas quedan para la aceptación.

## Siguiente paso

1. Unir la fase 3. Después, el visor acepta varios videos: el primero de fondo y los demás como imagen en imagen. Se para y se le dice a Daniel qué probar respecto a la fluidez con varias capas.
2. Pendiente de la mini revisión, para después de la fase 3:
   - que `TimelineSession` sea el único dueño de `GpuDevice` y de la caché global;
   - sacar `VideoRenderer` del visor y llevarlo a Render;
   - centralizar las `RODAJE_*`;
   - unificar los atributos de salto de las pruebas;
   - un módulo común para los scripts;
   - mover `TestMedia` a `testing/`;
   - dividir `docs/guia-tecnica.md` por capas;
   - actualizar `probar.json`.

## Decisiones

- **Registro:** `docs/decisiones.md`, de D-001 a D-015.
- **Módulos:** `docs/propuesta-modulos.md`, aprobada. Núcleo en el exe, contenido en `.paquete` sin código, IA como procesos C#.
- **Fase 4:** la app final será un proyecto WinUI 3 nuevo y el visor se borra.
- **Pendientes para la interfaz:** decidir cómo funciona el ripple con enlazados de distinto largo y si se limpian los enlaces huérfanos.
- **Archivo:** la investigación y el sondeo están en `docs/archivo/`; el código del sondeo, en la etiqueta git `sondeo-interop-aot`.
