# Tarea 42: números con coma decimal en toda la App

Rama `claude/analisis-cultura`.

## Lo hecho

- **Formato común:** `src/Rodaje.App/Services/Format/SpanishNumbers.cs` con `SpanishNumbers.Format`, un `NumberFormatInfo` de solo lectura con coma decimal y punto de miles. La app corre con `InvariantGlobalization`: no se puede pedir «es-ES» y `CurrentCulture` es la invariante, que da «0.5».
- **`SpeedMath` y `TitleEditor`:** ya no definen su propio `NumberFormatInfo`; los dos usan el común. `TitleEditor.WeightName` también lo usa en lugar de `CurrentCulture`, aunque el número es entero.
- **Sitios con `CultureInfo.CurrentCulture` corregidos:**
  - `AnalysisUi.Db/Ms/Sec`, el BPM de `RhythmDialog`, `StabilizeDialog` (estado y deslizador), `SilenceDialog`, `DuckingDialog`, `NoiseReductionDialog` y `AutoColorAction`.
  - Los dos mensajes de `SubtitleStyles.Legibility`, entre ellos «hace falta 4,5:1».
  - El resumen del estilo en `TextPanel.xaml.cs:106`.
  - El `Fmt` de `VoiceDialog.cs`: velocidad «1,25×», tono y segundos.
- **Pruebas nuevas:**
  - `tests/Rodaje.App.Tests/Format/SpanishNumbersTests.cs`: comprueba el formato con la cultura invariante en el hilo, igual que en la app publicada. Mira que sea de solo lectura y que `SpeedMath`, `TitleEditor` y el aviso de contraste lo usen.
  - `tests/Rodaje.App.Tests/Support/CurrentCultureNumbersTests.cs`: recorre los `.cs` de `src/Rodaje.App` y falla si una línea formatea con `CultureInfo.CurrentCulture`. Se salta los comentarios y los usos que no son números (`ToLower`/`ToUpper`). Prueba también la propia regla de detección, y que de verdad lee los fuentes.

## Pendiente

- `src/Rodaje.App/Shell/TitleBar.xaml.cs:168` y `:221` siguen con `CurrentCulture`. Formatean enteros pequeños (cantidad de pistas y «Video N»), que salen igual en la invariante, y están fuera de los caminos de la tarea.
  - La prueba de vigilancia los lista línea por línea como pendientes conocidos, así que cualquier otro uso en ese archivo falla.
  - Para cerrarlo basta cambiarlos a `SpanishNumbers.Format` y quitar las dos entradas de `KnownPending`.
- Los `StringComparer.CurrentCultureIgnoreCase` (`EditorSession.Titles.cs`, `ModuleCatalog.cs`, `PackageService.cs`) y el `ToLower(CurrentCulture)` de `TextPanel.Rows.cs` no formatean números; no se tocan.

## Decisiones

- El formato común se pasa como `IFormatProvider`: `string.Create(SpanishNumbers.Format, $"…")` y `ToString(fmt, SpanishNumbers.Format)`. No hace falta un `CultureInfo`, que con `InvariantGlobalization` no se puede crear para «es».
- Lo que se guarda o se lee de archivos sigue en `InvariantCulture`; solo cambian los textos para el usuario.

## Pruebas en Linux

Con `dotnet test tests/Rodaje.App.Tests -p:EnableWindowsTargeting=true -p:RuntimeIdentifier=linux-x64 --filter "Categoria!=SoloWindows&Categoria!=TiempoReal&Categoria!=Rendimiento"`:

| | Total | Pasan | Fallan | Omitidas |
|---|---|---|---|---|
| `main` | 878 | 845 | 29 | 4 |
| Esta rama | 885 | 852 | 29 | 4 |

- **Fallos:** las 29 son las mismas en las dos ramas, por la plataforma: módulos `.exe`, Job Object, voz y rutas de Windows.
- **Omitidas:** son las 4 de siempre en `main` (contenido de paquetes, medios y modelos de IA que no están en Linux); no se omite ninguna nueva.
- **Las 7 pruebas nuevas pasan.**
- **Sin verificar en Linux:** los archivos de `Views/` (diálogos de análisis, `TextPanel`, `VoiceDialog`) no compilan aquí. Los verifica el trabajo «Windows» de la CI.

## Posibles choques

- `AnalysisUi.cs`, los diálogos de análisis, `VoiceDialog.cs`, `TextPanel.xaml.cs`, `SubtitleStyles.cs`, `SpeedMath.cs` y `TitleEditor.cs` cambian solo en el `using` y en la línea del formato.
- Si otra rama añade en ellos un `CultureInfo.CurrentCulture` para un número, la prueba de vigilancia lo señalará al unir.

## Qué comprobar en Windows

- **Silencios, ritmo, ducking, ruido, estabilizar y color automático:** los valores con decimales salen con coma, por ejemplo «0,5 s» y «Tempo: 120,5 BPM».
- **Texto a voz:** la velocidad sale como «1,25×» y la duración como «Listo: 3,2 s de audio».
- **Subtítulo con poco contraste:** el aviso dice «hace falta 4,5:1».
