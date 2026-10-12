# Tarea 51: enganchar «Traducir pista» y el manifiesto de subtítulos

Rama `claude/traducir-enganche`. Dos commits, uno por punto.

## Hecho

1. **«Traducir pista» enganchada** (commit 48232b0):
   - `src/Rodaje.App/Shell/MainWindow.xaml.cs`: una línea,
     `_bindings.Add(Views.Translate.TranslateCommands.Bind(_router, _services));`, justo después de la de
     `SubtitleCommands` (en main estaba en la línea 144; la 135 que cita la tarea se movió con el PR #44).
   - `src/Rodaje.App/Views/Timeline/TimelineView.Analysis.cs`: «Traducir pista...» al final de `AddAnalysisItems`,
     después de «Subtítulos automáticos...». Invoca `tools.translateTrack` por el enrutador, como «Texto a voz».
2. **Manifiesto de subtítulos** (commit fe16688):
   - `modules/Rodaje.Module.Subtitles/paquete.json`: `"apps": ["rodaje"]`. Sin eso `ManifestReader` daba
     «Falta «apps».», `ModuleCatalog` descartaba el módulo y el comando de la tarea 37 (PR #44) avisaba «Hace falta el
     módulo "Subtítulos automáticos"».
   - `tests/Rodaje.Module.Subtitles.Tests/ManifestTests.cs` (nuevo): lee el `paquete.json` real con `ManifestReader`
     (sin errores, módulo del protocolo 1, función `subtitulos`) y comprueba `WorksIn("rodaje")`, el criterio de
     `ModuleCatalog`. Las dos fallaban antes del arreglo (se vio en Linux) y pasan después.
   - El `.csproj` de esas pruebas referencia `Rodaje.Paquetes` y su `packages.lock.json` lo anota (una entrada de
     proyecto). `dotnet restore Rodaje.slnx --locked-mode` pasa.

## Decisiones

- **Menú del clip, no de la pista.** La tarea pide el menú de la pista de subtítulos, pero el único menú en los caminos
  es el del clip (`AddAnalysisItems`, que llama `TimelineView.ClipMenu.cs`). La entrada se muestra siempre, como las
  de voz y subtítulos: el diálogo ya elige por defecto la pista del clip seleccionado y avisa si no hay nada que
  traducir o si falta el módulo.
- **Barra rápida sin tocar.** `QuickTools.State` sigue con «Próximamente» para `tools.autoSubtitles`, pero cambiarlo no
  es una línea: `QuickToolsTests` (línea 72) y la autoprueba `SelfTestRun.cs` (líneas 334–341) esperan
  «Próximamente», y habría que pasar si el módulo está instalado como con `analysis.noise`. «Traducir» no está en la
  barra. Queda pendiente.

## Pendiente

- Barra rápida: habilitar «Subtítulos automáticos» (con el módulo instalado y un clip con voz) y, si se quiere, añadir
  «Traducir pista»; actualizar `QuickToolsTests` y `SelfTestRun`.
- Un menú propio de la pista (cabecera) con «Traducir pista», si se quiere además del del clip.

## Pruebas

- Linux: `dotnet test tests/Rodaje.Module.Subtitles.Tests -p:EnableWindowsTargeting=true`: 6 correctas (las 2 nuevas
  incluidas), 6 omitidas y 1 fallo. El fallo es
  `SubtitleModuleTests.Audio_at_the_wrong_rate_is_rejected_without_closing_the_module`, que arranca
  `Rodaje.Module.Subtitles.exe` («Exec format error» en Linux); no es de esta rama y la CI de Linux no corre este
  proyecto.
- **Omitidas:** las mismas 6 antes y después (faltan el modelo de Whisper o el hardware). Esta rama no omite ninguna.
- La App WinUI no compila en Linux; los dos cambios de la app los compila el trabajo «Windows» de la CI.

## Posibles choques

- `MainWindow.xaml.cs`: una línea después de `SubtitleCommands`; otras ramas que añadan su `Bind` ahí chocan, se
  arregla a mano.
- `TimelineView.Analysis.cs`: tres líneas al final de `AddAnalysisItems`.
- `tests/Rodaje.Module.Subtitles.Tests/packages.lock.json`: una entrada.

## Qué comprobar en Windows

1. Con `modulo-subtitulos` instalado, «Complementos» lo muestra y «Subtítulos automáticos...» abre el diálogo (ya no
   avisa que falta el módulo).
2. Clic derecho en un clip de subtítulos → «Traducir pista...»: el diálogo elige esa pista; al traducir aparece la
   pista nueva encima con los mismos tiempos y un Ctrl+Z la quita.
3. Sin `modulo-traducir`, «Traducir pista...» avisa que falta el módulo y no hace nada más.

## CI (run 38240481635, sobre 1e91708)

- **Linux:** en verde.
- **Windows:** compila la solución entera, App WinUI incluida, con las dos entradas nuevas. `Rodaje.App.Tests` da
  966 correctas y 2 omitidas. `Rodaje.Module.Subtitles.Tests` da 7 correctas (las 2 nuevas incluidas) y 3 omitidas
  por falta del modelo, las mismas 3 que antes. Falla una sola prueba, ajena a esta rama:
  `Rodaje.TestServer.Tests.HandlerFaultTests.An_unexpected_exception_aborts_the_connection_and_is_recorded`
  («El cliente esperó al plazo: la conexión quedó colgada.», 9 s). Es del servidor HTTP de pruebas
  (`testing/Rodaje.TestServer`, tarea 61), depende de los tiempos del runner y en Linux pasó en la misma corrida.
  No la toqué.
