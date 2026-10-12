# Tarea 60: cerrar la vigilancia de la cultura numérica

Rama `claude/cultura-vigilancia`, PR #54.

## Lo hecho

- **Porcentajes en `SpanishNumbers.Format`:** el formato `P` usaba los separadores de la invariante: «12.5 %».
  - Ahora `PercentDecimalSeparator` es «,» y `PercentGroupSeparator` es «.».
  - Los patrones quedan fijados en 0 (`PercentPositivePattern`) y 0 (`PercentNegativePattern`), que dan «35 %» y «-35 %».
  - Prueba nueva en `tests/Rodaje.App.Tests/Format/SpanishPercentTests.cs`.
- **`StabilizeDialog.cs`:** el acercamiento pasa de `{(result.Zoom - 1) * 100:0} %` a `{result.Zoom - 1:P0}`. Ahora las tres cifras de porcentaje del estado usan `P0` con el formato común; la línea 52 («… menos») ya lo usaba.
- **`TitleBar.xaml.cs:168,221`:** pasan a `SpanishNumbers.Format` con el nombre completo (`Rodaje.App.Services.Format.SpanishNumbers.Format`), así que no hace falta otro `using`. `KnownPending` de `CurrentCultureNumbersTests` queda vacío.
- **Vigilancia ampliada:** prueba nueva en `tests/Rodaje.App.Tests/Support/UnformattedNumbersTests.cs`.
  - **Qué recorre:** los `.cs` de `src/Rodaje.App/Views/` y `src/Rodaje.App/Services/`.
  - **Qué señala:** las interpolaciones con formato decimal o de miles (`{v:0.0}`, `{v:#,##0}`, `{v:F1}`, `{v:P0}`, `{v:N2}`, también dentro de `string.Format`), y los `ToString("…")` numéricos sin proveedor (`"0.0"`, `"#,##0"`, `F`, `P`, `N`, `G`, `E`, `C`).
  - **Cuándo vale:** si la **sentencia entera** lleva `SpanishNumbers.Format`, `InvariantCulture` o `NumberFormatInfo.InvariantInfo`. Mira hasta 6 líneas hacia atrás y hacia delante, porque el proveedor suele ir en la línea de `string.Create(…,`.
  - **Lista blanca explícita:**
    - Diagnósticos: `TimelineBenchmark.cs`, `LibraryScrollMeasurement.cs`, `MeasurementRun.cs` y `Metrics.cs`. Los dos últimos están en `Shell/` y `Diagnostics/`, fuera de lo que se recorre, pero se listan como pide la tarea.
    - El ayudante `Fmt($"…")` de `VoiceDialog.cs`, que formatea con `SpanishNumbers.Format`. Solo vale en ese archivo.
  - **Qué no cuenta:** `Trace.Write…`, `Debug.Write…` y `Debug.Assert` (registros, no textos para el usuario), las líneas con `Guid` (`{Guid.NewGuid():N}`, `ToString("N")`) y los comentarios.
  - **`KnownPending`:** existe, línea por línea, pero está vacía. Sobre el `origin/main` actual (654f361) no aparece ningún uso real.
  - **Casos propios de la detección:** uno por patrón (15 positivos), 12 casos que deben pasar, la sentencia en varias líneas (el proveedor antes o después, sin que tape la sentencia siguiente) y las listas explícitas. Otra prueba comprueba que de verdad lee los fuentes.

## Pendiente

- Nada de la tarea.
- **No se pudo comprobar** si la sesión local tenía abierto `TitleBar.xaml.cs`. El cambio son solo las dos líneas citadas; ningún PR abierto toca ese archivo.

## Decisiones

- **`PercentPositivePattern = 0`, no 2:** la tarea pedía 2 para obtener «35 %», pero en .NET el patrón 2 es «%n» («%35»). El que da «35 %» es el 0, que es también el de la invariante. Se fija igual, junto con el negativo (0: «-35 %»), para no depender de ella, y la prueba comprueba el texto.
- **Sentencia entera y no solo la línea:** casi todos los usos correctos llevan el proveedor en la línea anterior. Si se mirara línea a línea, habría decenas de falsos positivos.
- **Registros fuera:** un `Trace.WriteLine` no lo ve el usuario. El de `ModuleConnection.cs:189` (`{…TotalSeconds:0.#} s`) se deja pasar por esa regla y no por la lista de pendientes.
- **Enteros sin decimales (`{v:0}`, `{n}`) no se señalan:** salen igual en las dos culturas.

## Posibles choques con otros PR abiertos

Se probó copiando a esta rama sus archivos nuevos o cambiados de `Views/` y `Services/` y corriendo la vigilancia:

- **#44 (subtítulos automáticos, `claude/subtitulos-ui`): la rompería.**
  - La línea es `src/Rodaje.App/Views/Subtitles/Logic/AutoSubtitles.cs:308`: `return string.Create(Numbers, $"{minutes}:{… / 10.0:00.0}");`.
  - Usa `AutoSubtitles.Numbers`, su copia de `SpanishNumbers.Format`, y la vigilancia solo reconoce el formato común.
  - La entrega de #44 ya propone cambiar `AutoSubtitles.Numbers` por `SpanishNumbers.Format` al unir. Con eso pasa sin tocar la vigilancia.
  - Hace falta una tarea nueva (o hacerlo al unir #44): reemplazar `Numbers` por `SpanishNumbers.Format` en `AutoSubtitles.cs` y en `AutoSubtitlesDialog.cs` (líneas con `string.Create(AutoSubtitles.Numbers, …)`) y borrar la propiedad.
- **#47 (reenlazar, `Views/Relink/`, `Services/Relink/`) y #46 (exportar fotograma, `Views/Export/StillExportCommand.cs`):** pasan.
- **#52, #53, #48 y #22:** no tocan `Views/` ni `Services/` de la App con formatos numéricos.
- **Ningún PR abierto toca** `TitleBar.xaml.cs`, `StabilizeDialog.cs`, `SpanishNumbers.cs` ni `CurrentCultureNumbersTests.cs`.

## Pruebas en Linux

Con `dotnet test tests/Rodaje.App.Tests -p:EnableWindowsTargeting=true -p:RuntimeIdentifier=linux-x64`:

- **Las afectadas:** `UnformattedNumbers`, `CurrentCultureNumbers`, `SpanishPercent` y `SpanishNumbers`. Pasan 40 de 40, entre ellas la vigilancia real sobre `Views/` y `Services/`.
- **Suite completa sin `SoloWindows`, `TiempoReal` ni `Rendimiento`:**

  | | Total | Pasan | Fallan | Omitidas |
  |---|---|---|---|---|
  | `origin/main` (654f361) | 895 | 860 | 29 | 6 |
  | Esta rama | 928 | 893 | 29 | 6 |

  - Las 33 pruebas nuevas pasan.
  - Las 29 que fallan son las mismas en las dos ramas, por la plataforma (módulos `.exe`, Job Object, voz y rutas de Windows).
- **Omitidas:** son 6 antes y 6 después, las mismas en las dos ramas. La causa es que en Linux no hay FFmpeg («sin FFmpeg»: contenido de paquetes, CinemaDNG, medios de módulos y audio IA). No se omite ninguna prueba nueva.
- **`TitleBar.xaml.cs` y `StabilizeDialog.cs` no compilan en Linux.** Los verifica el trabajo «Windows» de la CI.

## Qué comprobar en Windows

- **Estabilizar:** el estado dice, por ejemplo, «(35 % menos). Acercamiento: 8 %. Cuadros con movimiento medido: 97 %», con espacio antes de «%».
- **Barra de título → proyecto nuevo:** los menús de cantidad de pistas («1», «2», …) y de audio enlazado («Video 1», …) siguen iguales.
