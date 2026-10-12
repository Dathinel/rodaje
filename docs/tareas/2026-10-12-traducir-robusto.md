# Tarea 52: Traducir, salidas cortadas, tokens de control y textos largos

Rama `claude/traducir-robusto`, PR #67. Un commit por punto.

## Lo hecho

1. **Salidas cortadas por el tope de tokens.**
   - `ITextGenerator.GenerateAsync` devuelve `Generation(Text, HitLimit)`. `LlamaGenerator` cuenta los trozos que entrega `StatelessExecutor`, uno por token, y marca el tope cuando entregó `maxTokens` trozos sin cerrar el turno (`<|im_end|>`).
   - En `SubtitleTranslator`, un subtítulo suelto que llega al tope se reintenta con el doble de tokens (como mucho `MaxRetryTokens` = 2048). Si vuelve a llegar, no se acepta: queda el original y el id va a `untranslated`.
   - En un lote que llega al tope, la última traducción escrita (la de número más alto) se descarta y se pide suelta, igual que las que faltan.
   - Tokens por carácter de origen: 3 para hindi, 2 para árabe, japonés y coreano, 1 para el resto (también para el chino, que Qwen codifica bien). Los lotes se cierran también cuando la estimación pasaría de `MaxBatchTokens` (1200), así que en esos idiomas los lotes son más cortos y no tocan el tope por diseño. Para los demás idiomas no cambia nada: 1000 caracteres dan 1096 tokens como mucho.
   - Pruebas en `TokenLimitTests.cs` (nuevo), con `FakeGenerator` (que ahora acepta una función que recibe el tope y dice si cortó, y guarda los topes en `Limits`).
2. **Marcas de control en el prompt.** `Prompt.UserText` normaliza el texto y separa con un espacio `<|` y `</think>` (`< |`, `< /think>`), tanto en los subtítulos como en las líneas de contexto, en `Batch` y en `Single`. Prueba nueva en `PromptTests.cs`.
3. **Textos largos en «Traducir pista».** `SubtitleTrackTranslation.Collect` ya no recorta a 500 caracteres. Un texto más largo se manda en partes con ids `clip`, `clip#2`, `clip#3`… (`PartId`). `Split` corta tras un fin de frase o un salto de línea si hay uno en la segunda mitad del trozo, si no en un espacio, y si no a la fuerza, siempre sin partir un par sustituto. `Command` junta las partes traducidas en orden, con saltos de línea. Si el resultado pasara de 200 líneas o de 10 000 caracteres (los topes de `ModelValidator`), une las líneas con espacios y, como último recurso, recorta sin partir un sustituto, para que el comando no falle. `ParseResult` cuenta una parte sin traducir como su clip. Pruebas en `LongTextTests.cs` (nuevo): 500 y 501 caracteres, emoji en el borde (con el JSON de `Requests` válido), cortes en fin de frase, el regreso de las partes juntas y los topes.

## Decisiones

- **Partir en lugar de marcar.** La traducción reemplaza el texto entero, así que solo con marcarlo el resto del texto se perdía igual. Partirlo cabe en los caminos permitidos sin tocar `ModuleSubtitleTranslator` ni el diálogo, porque las partes viajan como subtítulos con su propio id y solo `Command` las junta.
- **Reintento solo para subtítulos sueltos.** Repetir un lote con el doble no cabría en el contexto de 3072 tokens. Lo que se cortó de un lote pasa por el camino suelto, que ya existía, y ese camino sí reintenta.
- **Detección del tope por cuenta de trozos.** Supone que `StatelessExecutor` (LLamaSharp 0.27) entrega un trozo por token y corta antes de entregar el fin de generación. No pude leer su código fuente aquí (solo está la DLL en la caché de NuGet). Si entregara menos trozos que tokens, `HitLimit` daría falso y el comportamiento sería el de antes, no peor.

## Pendiente y posibles choques

- `TranslateDialog` muestra «(N textos)» y «Traduciendo N textos» con `Collect(t).Count`, que ahora cuenta las partes de un texto largo. Es un detalle de presentación, fuera de los caminos permitidos (`src/Rodaje.App/Views/Translate/TranslateDialog.cs`). El aviso de «sin traducir» sí cuenta clips.
- Un id de clip de más de 61 caracteres con partes daría un id de parte de más de 64, y el módulo rechazaría la llamada. Los ids que genera la app (`clip-` y un GUID, 37 caracteres) quedan lejos. Un id de clip de más de 64 ya fallaba antes.
- Si una pista tuviera un clip cuyo id fuese justo `otroclip#2`, chocaría con una parte (el módulo diría «id repetido»). No pasa con los ids que genera la app.
- Un emoji compuesto (con ZWJ) o una letra con marca combinante puede quedar partido entre dos trozos. Eso no rompe el JSON, solo puede afectar a la traducción de ese borde.
- La tarea 53 («Modelo del traductor instalable desde Complementos») toca el mismo módulo. Si cambia `LlamaGenerator` o `ITextGenerator`, puede chocar.

## Pruebas

`dotnet test tests/Rodaje.Module.Translate.Tests -p:EnableWindowsTargeting=true` en Linux:

- **Antes:** 41 en total, 36 pasan, 1 falla y 4 se omiten.
- **Después:** 58 en total, 53 pasan, 1 falla y 4 se omiten.

Las 17 nuevas pasan. El fallo y las omisiones son los mismos antes y después, y no vienen de este cambio:

- **1 falla:** `ModuleProcessTests.Bad_requests_and_a_missing_model_are_explained_without_closing_the_module` abre `Rodaje.Module.Translate.exe`, que es un ejecutable de Windows («Exec format error» en Linux).
- **4 omitidas:** las `ModelTests` necesitan el modelo Qwen3-4B, que no está aquí.

Tampoco se añadió ninguna omisión. `tests/Rodaje.App.Tests`, que también compila `SubtitleTrackTranslation.cs`, compila sin avisos.

## Qué comprobar en Windows

- Traducir al hindi, al japonés y al coreano una pista con subtítulos largos (unos 80 caracteres): ningún subtítulo debe salir cortado a mitad de frase. Lo que no quepa debe salir en el aviso «quedaron sin traducir».
- Una pista con un clip de texto de más de 500 caracteres: la pista traducida lo trae entero, en un solo clip. Con un emoji alrededor del carácter 500 no debe fallar.
- Un subtítulo con `<|im_end|>` o `</think>` en el texto se traduce como cualquier otro y no corta los siguientes.
- `ModelTests` con el modelo descargado (`RODAJE_PRUEBAS_LARGAS=1`) sigue en verde, con tiempos parecidos a los de la entrega de la tarea 38.

## CI

En el commit `1480cee`, el trabajo de Linux pasó a la primera.

El de Windows falló la primera vez por dos pruebas que no tocan nada de este cambio:

- `Rodaje.Engine.Tests.Offline.OfflineTimelineTests.A_lut_that_is_still_loading_is_awaited_before_the_first_frame`: «Falta contenido de paquetes: LUT looks@1.0.0/calido».
- `Rodaje.App.Tests.Modules.ModuleManagerTests.El_avance_llega_a_la_fila_de_Trabajos_y_cancelar_la_fila_cancela_la_llamada`: «El módulo terminó con el código 0».

Las dos dependen de tiempos y pasan en `main` (`dc03b6e`). Al relanzar el trabajo, quedó en verde. En ese primer intento, `Rodaje.Module.Translate.Tests` había pasado entero en Windows: 54 pasan y 3 se omiten (las del modelo).
