# Tarea 35: limpiezas de la revisión en bloque

Rama `claude/limpiezas-bloque-1`, PR #36. Sale de los hallazgos menores y del M10 de la revisión en bloque del 11-oct (commit `30b0a731`). El informe de esa revisión no está en el repositorio, así que se trabajó con el texto de la tarea y se comprobó cada punto en el código de `origin/main`.

## Lo hecho (un commit por punto)

1. **Cortes en el cursor.** Se quitan `TrimStartToPlayhead`, `TrimEndToPlayhead` y `TrimToPlayhead` de `EditorSession.Editing.cs`. Ningún atajo ni vista los usaba: Q/W van a `DeleteLeft/RightOfPlayhead` desde la tarea de cortes. Las tres pruebas de `EditingAtPlayheadTests` que los usaban pasan a `DeleteLeftOfPlayhead`/`DeleteRightOfPlayhead`, con los mismos resultados esperados. Se renombraron a `BorrarALaIzquierda_…` y `BorrarALaDerecha_…`. El `cref` de `EditorSession.Cuts.cs` que apuntaba al método borrado ahora apunta a `SplitAtPlayhead`, que actúa sobre los mismos clips.
2. **Pegar atributos** (`ClipAttributes.cs`).
   - **Velocidad 1×:** el destino recibe tono `true` y muestreo `Nearest` también cuando el origen va a 1×. Antes el `if (speed is not null)` lo saltaba. En la práctica ya quedaba bien, porque `Retime` normaliza a `null` una velocidad identidad, pero ahora es explícito y tiene prueba.
   - **Keyframes:** se recortan a la duración del clip destino (`TrimToLength`). Los que caen antes de 0 o después del final se quitan. En el borde que cruzaban entra un keyframe con el valor que tenía la animación ahí (`Parameter.Evaluate`), así lo que se ve dentro del clip no cambia. Es exacto con interpolación lineal o constante; con suavizado, el último tramo se acorta.
   - Pruebas nuevas en `tests/Rodaje.Core.Tests/Editing/Links/PasteAttributesLimitsTests.cs`.
3. **Zoom de la línea de tiempo.**
   - `TimelineView` se suscribe a `Unloaded` y para la animación de zoom (`StopZoomAnimation`), así no queda enganchada a `CompositionTarget.Rendering` con la vista fuera.
   - `ZoomFit.Whole` ya no repite la cuenta: calcula el encuadre con un `TimelineViewport` aparte y `FitTo`. Da los mismos valores (la prueba `AjustarALaVentana_CoincideConFitToDelViewport` sigue pasando).
4. **Plantillas.**
   - `ProjectTemplate.Clean` quita también las marcas bidi, con `SafeText.IsBidiControl` de `Rodaje.Paquetes`, que la App ya referencia. Vale para el nombre, el id y los nombres y roles de las pistas.
   - `ProjectTemplateStore.Save` rechaza con `ArgumentException` una plantilla propia con el nombre de una de fábrica (sin distinguir mayúsculas). La barra de título ya atrapa esa excepción y la muestra como aviso.
   - Pruebas nuevas en `tests/Rodaje.App.Tests/Templates/TemplateNameTests.cs`.
5. **`docs/protocolo-modulos.md`** puesto al día con el `ModuleManager`:
   - el estado (la app ya abre módulos) y el `appVersion` que manda la app;
   - el `ping` de vigilancia, ya hecho;
   - `log` y `cancellable`, que siguen sin uso;
   - una tabla con las reglas del `ModuleManager`: procesos, plazo de 30 s para abrir, ocioso de 2 min, ping, Job Object, VRAM, vista previa y fallos;
   - dónde busca módulos `ModuleCatalog` y los campos `vramMb`/`runtime` que lee aparte.

## Pendiente

- **Confirmar al borrar una plantilla propia.** El borrado está en `src/Rodaje.App/Shell/TitleBar.xaml.cs` (`OnDeleteTemplateClick`, que llama a `_templates.Remove(id)` sin preguntar). Ese archivo queda fuera de los caminos de la tarea, y `IEditorDialogs` no tiene una confirmación genérica que la sesión pueda usar. Queda para una tarea que permita tocar `Shell/TitleBar.xaml.cs`: un `ContentDialog` con `DialogGate.ShowAsync`, como el de «Guardar como plantilla».

## Decisiones

- Al recortar los keyframes al pegar, se conserva el valor en el borde (keyframe nuevo) en vez de solo descartar. Así un destino más corto no salta al último valor del origen.
- El nombre de fábrica solo se rechaza al guardar. Un `plantillas.json` editado a mano con ese nombre se sigue leyendo, para no perder datos en silencio.
- El mensaje del rechazo no lleva `paramName`, para que el aviso no muestre «(Parameter 'template')». Ya hay un precedente en `ExportSettingsValidator`.

## Pruebas (Linux, dotnet 10.0.401)

- `Rodaje.Core.Tests` con `Categoria!=SoloWindows&Categoria!=TiempoReal&Categoria!=Rendimiento`: 1328 de 1328 pasan.
- `Rodaje.App.Tests` compila en Linux con `-p:EnableWindowsTargeting=true -p:RuntimeIdentifier=linux-x64`.
  - Las de esta tarea pasan: plantillas, `EditingAtPlayheadTests`, `ZoomFitTests` y vista, 269 de 269 con el filtro.
  - La suite entera con el filtro da 30 fallas en Linux. Son de plataforma (tuberías y rutas de Windows, módulos, voz, miniaturas, exportación) y son las mismas que en `origin/main`, salvo `TitleEditorTests.BordesYConvertirEnKeyframes`. Esa falla por la cultura (`0.5` frente a `0,5`) y la arregló en main el PR #35 (`UiThread.cs`, `TitleEditor.cs`), que se unió después de que saliera esta rama. No es de este cambio.
- **Pruebas omitidas:** ninguna nueva. En App.Tests en Linux se omitían 4 antes y se omiten 4 después.
- Los `packages.lock.json` regenerados en Linux se deshicieron.

## Choques posibles

- `origin/main` avanzó durante la tarea (PR #35: `ModuleCall.cs`, `ModuleConnection.cs`, `MainWindow.xaml.cs`, `TitleEditor.cs`). Ninguno de esos archivos se toca aquí. La guía de módulos cita el `StartTimeout` de 30 s que trajo ese PR, así que esa fila es correcta en main aunque no en la base de esta rama.
- La tarea 34 (`claude/autoguardado-pruebas`) toca `Services/Autosave/`: no se tocó.

## Qué comprobar en Windows

- La CI de Windows compila la App (`TimelineView.xaml.cs` con el nuevo `Unloaded`, y `ProjectTemplate.cs` con `using Rodaje.Paquetes`).
- **A mano:**
  - Q/W siguen igual.
  - «Ajustar a la ventana» y «Zoom a la selección» animan igual que antes.
  - Guardar una plantilla llamada «Cuadrado 1:1» muestra el aviso de nombre de fábrica.
  - Pegar atributos de un clip largo con keyframes de transformación en uno corto deja la animación cortada en su final.
