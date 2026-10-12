# Tarea 37: subtítulos automáticos en la app (rama `claude/subtitulos-ui`, PR #44)

Tres sesiones: la primera dejó el formato (`generator.words`), la segunda el diálogo y las pruebas sin compilar, y la tercera (esta) lo compiló, lo probó, lo registró como comando y escribió esta entrega.

## Lo hecho

- **Formato 5, `generator.words`** (commit fc0ffec): `GeneratorWord` (inicio, fin y texto en el origen del clip, como los keyframes), validación (`ModelValidator.Words.cs`: sin espacios ni caracteres de control, de 1 a 100 caracteres, en orden, ±360 000 s, hasta 2000 por clip, y cuentan para el tope de texto del proyecto), clon y `SameContent`, esquema v5 y `docs/formato-proyecto.md`. **Sin migración:** es un campo opcional del formato 5. Pruebas en `tests/Rodaje.Core.Tests/Model/GeneratorWordsTests.cs`.
- **Lógica sin WinUI** (`src/Rodaje.App/Views/Subtitles/Logic/AutoSubtitles.cs`, se compila también en `Rodaje.App.Tests`):
  - Elige el clip (el audio enlazado, como «Reducir ruido», o un video con sonido).
  - Baja el tramo `[SourceIn, SourceOut)` a f32 mono de 16 kHz con `ModuleMedia.WriteAudioAsync`.
  - Llama a `subtitulos` con un búfer de salida de 1 MiB más 4 KiB por segundo, por `ModuleManager.RunAsync` (es un trabajo con avance y cancelación).
  - Lee el JSON del búfer (`{"bytes": n}`) o el que viene en línea.
  - Pasa cada palabra a la secuencia con `ClipAudioMap.ToTimeline` (`ClipTimeMap`, con velocidad y reversa). Descarta lo que cae después del tramo, reparte por largo las palabras que faltan, parte las palabras que traen espacios y acorta las de más de 100 caracteres.
  - Arma un solo paso del historial: `SubtitleEdits.AddSubtitles` (lo mismo que hace `Edits.AddSubtitles`) y, dentro de ese paso, las palabras de cada subtítulo en `generator.words`.
- **Diálogo** (`Views/Subtitles/AutoSubtitlesDialog.cs`): idioma (detectar, español, inglés o portugués), avance y cancelación, y la lista de subtítulos con sus tiempos y la barra del clip (un clic lleva el cursor a ese subtítulo). También «Transcribir de nuevo» con otro idioma. Al aplicar va por `ModuleProposals` (`Show` y luego `Apply`, un solo Ctrl+Z); si cancela, el proyecto no cambia.
- **Comando** (`Views/Subtitles/SubtitleCommands.cs`): registra `tools.autoSubtitles`, el mismo id de `QuickTools.AutoSubtitles`. «Subtítulos automáticos...» del menú del clip (`TimelineView.Analysis.cs`) lo invoca, y `MainWindow.xaml.cs` lo une con una línea.
- **Números para el usuario:** con coma decimal (`AutoSubtitles.Numbers`, igual a `SpanishNumbers.Format` de main). La vigilancia `CurrentCultureNumbersTests` de main rechaza `CultureInfo.CurrentCulture` en `src/Rodaje.App`.

## La CI roja de fc0ffec (job 114691679504)

Esta rama no la causó. Falló una sola prueba: `Rodaje.Modules.Protocol.Tests.ProcessTests.A_module_that_ignores_cancel_is_killed_after_the_grace_period`, que tardó 4608 ms. Es una prueba de tiempos que depende de la carga del runner (tarea 47) y esta rama no toca `Rodaje.Modules.Protocol`. Lo demás pasó: 1378 de Core, Media, App y el resto.

## Pendiente

- **Tarea 51 (otra rama), necesaria para que el comando funcione:** `modules/Rodaje.Module.Subtitles/paquete.json` no tiene `"apps"`. `ManifestReader` lo deja vacío, `ModuleCatalog` (línea 129) descarta los módulos con `!manifest.WorksIn(AppId)`, y entonces `FindByFunction("subtitulos")` da null. Mientras tanto el diálogo solo avisa «Hace falta el módulo "Subtítulos automáticos"». No lo toqué.
- **Barra rápida:** `QuickTools.State` (`Views/Analysis/Logic/QuickTools.cs`, en main línea 61) sigue devolviendo «Próximamente» para `tools.autoSubtitles`. El archivo está fuera de los caminos de la tarea. El cambio que falta es una línea: el mismo criterio que `analysis.noise` con el módulo de subtítulos. Mientras tanto, la acción ya está registrada.
- **Al unir con main:** se puede cambiar `AutoSubtitles.Numbers` por `SpanishNumbers.Format`.
- **Vista previa en la línea de tiempo:** la propuesta se aplica en cuanto se pulsa «Aplicar». La vista previa es la del diálogo (lista y barra), porque la línea de tiempo todavía no dibuja `ModuleProposals.Pending`.
- **Edición por palabra:** fuera de esta tarea. `generator.words` ya queda guardado para hacerla.

## Decisiones

- **Tiempos de las palabras:** se usan `ClipAudioMap`/`ClipTimeMap` sin ajustar al cuadro, y no `ModuleTime.ToSequence`, porque ese ajusta al cuadro y descarta lo que cae fuera del clip. Las palabras guardan su tiempo exacto. El ajuste al cuadro lo hace `SubtitleEdits.Layout` al partir.
- **Reparto de palabras entre subtítulos:** las palabras se reparten siguiendo el orden de la partición. Cada subtítulo se queda con tantas palabras como su texto. Esto se cumple porque las palabras ya llegan limpias y sin espacios.
- **Invocación desde el menú:** el menú invoca el comando por el enrutador, como «Texto a voz», en vez de abrir el diálogo directamente.

## Posibles choques

- `MainWindow.xaml.cs`: una línea después de la de `VoiceCommands`. Otras ramas que agreguen ahí su propia línea van a chocar, pero se arregla a mano en un momento.
- `TimelineView.Analysis.cs`: tres líneas al final de `AddAnalysisItems`.
- `Rodaje.App.Tests.csproj`: un `Compile Include` de `Views/Subtitles/Logic`.

## Pruebas

- Linux: `Rodaje.App.Tests` compila con `-p:EnableWindowsTargeting=true` y las 14 pruebas de `AutoSubtitlesTests` pasan. `Rodaje.Core.Tests`: 1379 de 1380. La que falla es `ProjectFileTests.Save_TempLockedByAnotherProgram_ThrowsAndKeepsExistingFile`, que depende del bloqueo de archivos de Windows; en la CI de Linux queda fuera por su categoría. La app WinUI no compila en Linux (el XamlCompiler es un .exe), así que el diálogo solo se comprueba en la CI de Windows.
- Pruebas omitidas: esta rama no omite ninguna. Antes y después se omiten las mismas, por hardware, modelos o pruebas largas.

## Qué debe comprobar Daniel en Windows

Hace falta tener `modulo-subtitulos` instalado con `"apps"` (tarea 51).

1. Clic derecho en un video con voz → «Subtítulos automáticos...».
2. Comprobar que avanza, que se puede cancelar y que «Transcribir de nuevo» funciona con otro idioma.
3. Comprobar que la lista y la barra coinciden con la voz.
4. Pulsar «Aplicar»: los subtítulos deben quedar en una pista de subtítulos, y un Ctrl+Z debe quitarlos todos.
5. Guardar, abrir el `.rodaje` y confirmar que los subtítulos traen `words`.
6. Repetir con un clip a 2× y otro en reversa.
