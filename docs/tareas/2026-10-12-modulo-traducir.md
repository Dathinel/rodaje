# Tarea 38: Traducir subtítulos (módulo)

Rama `claude/modulo-traducir`, PR #39. Fase 7, agente F.

## Hecho

- **`modules/Rodaje.Module.Translate/`** (`modulo-traducir`, protocolo 1, función `traducir`):
  - Recibe los subtítulos `{id, text}` (en los parámetros o, si no caben en un mensaje, en un búfer de bytes) y devuelve
    `{source, target, device, segments: [{id, text}], untranslated}`, con los mismos ids y en el mismo orden
    (`docs/fase-7.md`, «Formatos»). Los tiempos no viajan: los conserva la app.
  - **Motor:** Qwen3-4B-Instruct-2507 Q4_K_M (Apache-2.0) en llama.cpp vía **LLamaSharp 0.27.0** (MIT, abril de 2026)
    con `LLamaSharp.Backend.Cpu` y `LLamaSharp.Backend.Vulkan.Windows`. La biblioteca nativa se elige al cargar el
    primer modelo: Vulkan si hay `vulkan-1.dll`, si no CPU (AVX512/AVX2/AVX/sin AVX). Con `device: auto` sube todas las
    capas a la GPU; si no cabe, repite con 18 y luego en la CPU (la «descarga parcial» de la fase). `vramMb` 3000.
  - **Cómo traduce:** lotes de 8 subtítulos seguidos (hasta 1000 caracteres) con los 2 anteriores como contexto,
    numerados `[n]` en el prompt ChatML de Qwen; la respuesta se lee por número. Lo que el modelo junta, salta o repite
    se pide suelto. Temperatura 0. Un subtítulo vacío sale vacío sin pasar por el modelo; si el modelo no da nada,
    queda el original y su id va en `untranslated`.
  - **Líneas:** cada traducción se parte en líneas de ≤42 caracteres (`maxLineChars`), contadas como elementos de
    texto, con el mínimo de líneas y el corte más parejo (igual criterio que `SubtitleEdits.Wrap`). Una palabra más
    larga que la línea (URL, japonés o chino sin espacios) se corta.
  - **Entrada desconfiada:** idiomas de una lista de 20 (`pt`→`pt-BR`, `zh`→`zh-Hans`), hasta 5000 subtítulos, ids
    únicos de ≤64 caracteres, textos de ≤500, búferes solo de bytes. Todo error es `invalid-parameters` con el motivo en
    español; si se pide un `model` que no existe, no se busca otro.
  - `paquete.json`, `LICENCIAS.md` (con las dependencias de LLamaSharp) y `README.md` del módulo.
  - **Sin AOT:** LLamaSharp no declara soporte; `PublishAot=false` y se publica autocontenido con JIT, como prevé D-031
    (pregunta 3 de la fase). `RuntimeIdentifier` win-x64 fijo, como audio-ia (lockfile sin depender del anfitrión).
- **App, archivos nuevos** (la acción «Traducir pista»):
  - `Services/Translate/SubtitleTrackTranslation.cs` (sin WinUI): pistas traducibles (las de subtítulos primero; la del
    clip de texto seleccionado por defecto), parámetros por tandas de 400, lectura del resultado que exige exactamente
    los ids pedidos y limpia caracteres de control, y el comando que crea la pista de subtítulos «Subtítulos (Inglés)»
    justo encima de la original con copias de los clips (mismos tiempos, estilo, caja y efectos, sin enlace) en un solo
    paso del historial. La pista original no cambia.
  - `Services/Translate/ModuleSubtitleTranslator.cs`: un trabajo de Trabajos (cancelable, con avance) que llama al
    módulo por tandas.
  - `Services/EditorSession.Translate.cs`: `TranslatableTrack()` y `AddTranslatedTrack(...)`.
  - `Views/Translate/TranslateDialog.cs` (pista, idioma del original o «Detectar», destino; avisa de los textos sin
    traducir) y `Views/Translate/TranslateCommands.cs` (acción `tools.translateTrack`, lista para enlazar).
- **`herramientas/preparar-modelos.ps1`:** `-Traductor` baja el modelo aparte (2,5 GB, no entra en el zip completo).
- **`tests/Rodaje.Module.Translate.Tests/`** y una línea por proyecto en `Rodaje.slnx`.

## Pruebas

En Linux (`dotnet test tests/Rodaje.Module.Translate.Tests -p:EnableWindowsTargeting=true --filter
"Categoria!=SoloWindows&Categoria!=TiempoReal&Categoria!=Rendimiento"`): **36 correctas, 0 fallos, 2 omitidas**.

- Con un modelo falso (corren en todas partes): lotes, ids y orden, contexto, vacíos, reintento de lo que el modelo
  junta o salta, original cuando no hay respuesta, líneas de ≤42, tope de caracteres por lote, cancelación entre lotes,
  JSON del resultado, prompt y lectura de la respuesta, idiomas, validación de la entrada, manifiesto (lo lee
  `ManifestReader` como módulo del protocolo 1), y la pista traducida de la app con deshacer (compila aquí
  `SubtitleTrackTranslation.cs` enlazado, sin tocar `Rodaje.App.Tests`).
- **Con el modelo** (`[ModelFact]`): frases conocidas es→en con sus palabras clave, es→pt-BR y en→es, y el criterio de
  la fase: 100 subtítulos es→en en ≤90 s con los mismos ids y líneas ≤42 (categoría `Rendimiento`). Corpus propio:
  el guion de un video de viaje escrito para esta prueba (`Corpus.cs`).
- **Por el protocolo** (`SoloWindows`): errores explicados sin cerrar el módulo, modelo que falta, búfer de audio
  rechazado; y con el modelo, traducir con búferes de entrada y salida.

**Omitidas.** Antes: el proyecto no existía (0). Después:
- Linux: 2 omitidas por «falta el modelo Qwen3-4B-Instruct-2507-Q4_K_M.gguf en .entorno\modelos» y excluidas por el
  filtro 2 `SoloWindows` (tubería con nombre y memoria compartida de Windows) y 1 `Rendimiento` (reloj de pared).
- CI de Windows: 3 omitidas por la misma falta del modelo (la CI no baja modelos) y 1 excluida por `Rendimiento`.

La App WinUI no compila en Linux (XamlCompiler.exe). `Services/**` sí, dentro de `Rodaje.App.Tests` (compila sin
errores). El diálogo y los comandos se comprobaron compilando aparte con el paquete de WinUI 2.3.9 y los mismos
analizadores, con sustitutos de `EditorServices`, `EditorSession` y `ShortcutRouter`: sin errores ni avisos.

## Pendiente

1. **Enganchar la acción** (toca archivos existentes, fuera de los caminos): en `Shell/MainWindow.xaml.cs`, junto a
   la línea 135, `_bindings.Add(Views.Translate.TranslateCommands.Bind(_router, _services));`, y una entrada de menú
   como la de voz en `Views/Timeline/TimelineView.Analysis.cs` (menú de la pista de subtítulos o del clip de texto),
   y si se quiere en la barra de herramientas (`QuickTools`, donde «Subtítulos automáticos» sigue «Próximamente»).
2. **Huella del modelo sin verificar.** La nube no llega a Hugging Face (el proxy lo bloquea; tampoco espejos ni el
   registro de Ollama). La SHA-256 `3605803b…c67e597` sale de un espejo de terceros que dice ser copia exacta del
   archivo de unsloth; la URL va a `resolve/main` y falta `size` en `paquete.json`. Hay que bajarlo una vez, comprobar
   la huella, fijar la URL al commit y anotar el tamaño. Si la huella no coincide, el script borra la descarga.
3. **Medir en el equipo de Daniel** el criterio de 100 subtítulos en ≤90 s y la VRAM real (¿cabe entero con el
   editor abierto?).
4. **Opus-MT** como opción ligera (CPU, ~300 MB por par) no se hizo: no hacía falta y también vive en Hugging Face.

## Decisiones

- **LLamaSharp 0.27.0** como pide la fase: MIT, paquetes de backend de 36 MB (CPU) y 20 MB (Vulkan Windows); con el
  RID win-x64 solo se copian las DLL de Windows (84 MB en `bin`). Se usa `LLamaSharp.Backend.Vulkan.Windows` y no
  `LLamaSharp.Backend.Vulkan`, que arrastra también la de Linux.
- **El módulo no conoce tiempos ni clips:** solo ids y textos. Así la app conserva los tiempos exactos (los del cuadro)
  y el resultado es fácil de comprobar.
- **Pista nueva, nunca reescribir la original**, justo encima, con el rol de subtítulos y en un paso del historial
  (la IA no edita, D-031).
- **Contexto de 3072 tokens** y lotes de 8: el KV cabe en ~450 MB y el prompt más la respuesta nunca pasan del contexto.

## Posibles choques

- `Rodaje.slnx`: una línea en `/modules/` y otra en `/tests/`.
- `herramientas/preparar-modelos.ps1`: el parámetro `-Traductor`, la entrada y un bloque al final.
- El PR #34 (NU1004) fija el RID de Echo, Voice y Subtitles; este módulo ya nace con `RuntimeIdentifier` win-x64.
- Visto de paso: el `paquete.json` de `modulo-subtitulos` no tiene `"apps": ["rodaje"]`, que `ManifestReader` exige
  («Falta «apps»»), así que el catálogo de módulos lo descartaría. Este módulo sí lo trae.

## Qué comprobar en Windows

1. `herramientas\preparar-modelos.ps1 -Traductor` (verifica la huella; ver «Pendiente» 2).
2. `dotnet test tests\Rodaje.Module.Translate.Tests` con el modelo: frases conocidas, otros pares y, sin filtro, la de
   100 subtítulos en ≤90 s (anota tiempo y dispositivo de la salida).
3. Que el módulo arranque con Vulkan y, con la GPU ocupada, baje capas a la CPU sin fallar.
4. Tras enganchar la acción: traducir una pista de subtítulos generada por «Subtítulos automáticos», ver la pista
   nueva encima con los mismos tiempos y deshacerla con un solo Ctrl+Z.

## CI

- Linux en verde en todos los empujes.
- Windows compila la solución entera, App WinUI incluida (con el diálogo nuevo). `Rodaje.Module.Translate.Tests` dio 37 correctas y 3 omitidas por falta del modelo.
- Las dos primeras corridas de Windows fallaron por pruebas de tiempo ajenas a este cambio, una distinta cada vez:
  - `TimelineEffectsTests.Effect_evaluation_itself_reserves_nothing`: 3760 bytes reservados en vez de 0.
  - `OfflineTimelineTests.Cancelling_while_a_decoder_is_busy_returns_promptly_and_releases_everything`: 2041 ms desde cancelar.
  - `ModuleManagerTests.Una_llamada_larga_que_contesta_el_ping_no_se_toma_por_colgada`: el eco se tomó por colgado.
- La tercera corrida quedó en verde sin cambios.
