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

**Fase 4: interfaz WinUI 3 (construida; en pausa por corrección de la revisión).**
- `src/Rodaje.App` (abre con `Rodaje.bat` / `herramientaspp.ps1`):
  - **Armazón:** `EditorSession`, atajos con 3 estilos, ajustes y tema grafito.
  - **Línea de tiempo:** barra estilo Vegas, pista principal magnética, arrastres como gestos, marcadores y regiones (formato v2).
  - **Biblioteca:** miniaturas y formas de onda reales.
  - **Paquetes:** `src/Rodaje.Paquetes` (formato `.paquete` v0, `docs/formato-paquete.md`) y página de Complementos con el catálogo de Dathinel.
  - **Inspector:** keyframes en el cursor, «Guardar como transición» en `%LOCALAPPDATA%\Rodaje\complementos\propios`, filtros, historial y trabajos.
  - **Página de atajos:** teclado dibujado y hoja rápida.
  - **Medidores y reproductor:** medidores L/R estilo Shotcut sincronizados con lo que se oye, J/K/L y pantalla completa.
- **Cifras:** 1846 pruebas pasan, AOT con 0 advertencias, 0 cuadros perdidos, edición en pantalla en ~10 ms.
- **Maqueta aprobada (D-018):** https://claude.ai/artifact/XXtTT675TEHWSPPCCZfXcv; fuente en `dist\maqueta` (no se versiona).
- **Daniel todavía no probó la app a mano.** La lista de qué probar está en la conversación del 10-oct; resumen: biblioteca, arrastres en la principal, inspector con keyframes, atajos, medidores, guardar y abrir.

## Siguiente paso (al retomar)

1. **Decisiones ya tomadas por Daniel (10-oct):**
   - **Retirar el visor Win32.** Antes de borrar se crea la etiqueta git `visor-fase3`. Se borran `src/Rodaje.Visor`, `tests/Rodaje.Visor.Tests`, `herramientas/visor.ps1`, la entrada `visor` de `ABRIR.bat` y `probar.json`, y `docs/prueba-visor.md`. Además: `docs/sondeo-winui.md` va a `docs/archivo/` y se borra `herramientas/sondeo-winui.ps1`.
   - **Estilos de atajos con nombres neutros: «Rodaje», «Ágil» y «Clásico».** Se cambian en `ShortcutPreset`, `TitleBar.xaml`, `ShortcutsView.xaml` y `ShortcutCustomizer`, migrando los valores ya guardados en `atajos.json`. También se quitan las marcas ajenas de la UI y de los comentarios del código.
2. **Defectos de la revisión independiente de la fase 4, por corregir** (repartir en 4 agentes por archivos):
   - **A. Núcleo, sesión y shell:**
     1. `RippleDelete` y `RippleTrim` con alcance de pista no corren las pistas de los grupos enlazados de los clips desplazados. El audio se desincroniza en la principal magnética (`Core/Editing/TimelineEdits.Placement.cs:~185`, `Trimming.cs:~94`); revisar también `CloseGap` y `ReorderOnMainTrack`.
     2. Esc en el primer arrastre de un deslizador del inspector cierra la app (`ClipPropertyEditor.EndChange`). Hay que escuchar `GestureCancelled` y que `EndGesture` tolere no tener grupo abierto.
     3. Los atajos siguen activos detrás de las páginas de Atajos y Complementos: suspender el router salvo Esc y las acciones de Archivo.
     4. Varios `async void` sin try/catch y `ContentDialog` simultáneos tumban la app. Faltan un guardia de diálogos único y una red de seguridad en `App.UnhandledException`.
     5. Un `.rodaje` con caracteres inválidos en una ruta tumba la apertura: validar las rutas.
     6. Menores:
        - no se usa `IsBusy` al abrir;
        - dos Ctrl+S seguidos comparten el mismo `.tmp`;
        - el doble clic añade en `max(V1, A1)` y deja un hueco en la principal;
        - AltGr se interpreta como Ctrl+Alt;
        - `ajustes.json` solo se guarda al cerrar;
        - las flechas se pierden sobre los deslizadores;
        - pico de ~600 ms en la primera edición tras importar;
        - doble clonado por cada paso de arrastre.
   - **B. Paquetes, Complementos y Biblioteca:**
     1. Un apagón a mitad de una actualización borra la versión anterior: restaurar `.desinstalando-*`.
     2. «Actualizar» durante una instalación la borra: `SemaphoreSlim` y limpieza de restos solo al arrancar.
     3. Faltan try/catch en el instalador y en los `async void`.
     4. Bomba de tamaño agregada: límites totales y espacio libre en disco.
     5. Texto del manifiesto: limitar longitudes, quitar caracteres bidi y de control, nombres reservados (con, nul…) e `IdnHost`.
   - **C. Visuales y medidores:**
     1. Los hilos de miniaturas y formas de onda tumban el proceso con excepciones inesperadas; `WaveformPeaks` reserva ~4 GB si la duración es absurda; un video sin cuadros se redecodifica en cada redibujo; `Forget` no se llama nunca; `Dispose` espera 3 s por hilo.
     2. Los medidores en pausa: deben quedar en silencio.
     3. Unificar los contratos duplicados `IClipVisuals` y `WaveformPeaks` (Services/Media frente a Views/Timeline) y borrar el adaptador `Views/Shared/TimelineClipVisuals`.
     4. Dividir `ClipVisuals.cs`.
   - **D. Limpieza, documentación y scripts:**
     - retirar el visor y renombrar los estilos (punto 1);
     - poner al día el README (fases 3 y 4, cifras, cómo usar el editor), `docs/arquitectura/README.md`, este archivo y `app.md` (separar contrato y vistas);
     - `EditorNotification` con acción opcional, en lugar del `InfoBar` propio de ShortcutsView;
     - dividir `TimelineScheduler.cs` (1846 líneas) en parciales.
     - **Pruebas de tiempo real intermitentes bajo carga** (`TimelineHardwareTests.Cuts_with_pre_roll…60_hz`):
       - ritmo de la prueba con el reloj de pared;
       - categoría `TiempoReal` en una segunda pasada aparte en `probar.ps1`, escapando `&` como `^&`;
       - `SoftwareThreads = ProcessorCount/2` en Acceptance y caché ≥18.
3. **Después de corregir:** Daniel prueba la app. Luego viene la fase 5: efectos, LUT, blend, el render de los filtros y el audio con volumen y fundidos.

## Decisiones

- **Registro:** `docs/decisiones.md`, de D-001 a D-021.
- **Módulos:** `docs/propuesta-modulos.md`, aprobada. Núcleo en el exe, contenido en `.paquete` sin código, IA como procesos C#.
- **Fase 4:** la app final será un proyecto WinUI 3 nuevo y el visor se borra.
- **Pendientes para la interfaz:** decidir cómo funciona el ripple con enlazados de distinto largo y si se limpian los enlaces huérfanos.
- **Archivo:** la investigación y el sondeo están en `docs/archivo/`; el código del sondeo, en la etiqueta git `sondeo-interop-aot`.
