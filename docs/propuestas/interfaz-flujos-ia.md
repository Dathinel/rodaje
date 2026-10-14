# Interfaz de las funciones de IA y los flujos largos: revisión

Alcance: subtítulos automáticos, quitar fondo, reducir ruido, reencuadre, texto a voz, traducir pista y color automático. Cómo se lanzan, cómo muestran el avance y la cancelación, dónde queda el resultado y si se encuentran. Revisión de lectura sobre `main` (2aac9bd): código de `src/Rodaje.App` y `docs/guia/`. No se ejecutó la app, así que lo que depende de cómo se ve en pantalla está marcado como «a comprobar en Windows».

## 1. Incongruencias, de la que más molesta a la que menos

1. **Traducir promete que la tarea sigue si cierras el diálogo, y no es así.** Mientras traduce, el diálogo dice «… (sigue en Trabajos si cierras)» (`src/Rodaje.App/Views/Translate/TranslateDialog.cs:121`), pero al cerrarlo `DialogWork.CloseAsync` cancela la tarea (`TranslateDialog.cs:152`, `Services/LongActions/DialogWork.cs:3-8`). Quien se fía del mensaje pierde la traducción sin que nada se lo advierta.

2. **Quitar fondo y Reencuadre no siguen el flujo de las demás acciones largas.** Las demás pasan por `LongActions.RunAsync` (`Views/Analysis/AnalysisCommands.cs:53-54`, `Views/Voice/VoiceCommands.cs:57`, `Views/Translate/TranslateCommands.cs:25`). Estas dos, en cambio, llaman al diálogo directamente (`Views/Vision/VisionCommands.cs:28-29,37`). En consecuencia:
   - el botón de Herramientas no gira ni marca el porcentaje (`Views/Tools/ToolsBar.xaml.cs:135-149` solo atiende a `LongActions`);
   - la barra de avisos no muestra el avance;
   - el diálogo no se protege contra un segundo lanzamiento.

   Lo mismo con el arranque: Reducir ruido y Subtítulos empiezan a analizar en cuanto se abre el diálogo (`NoiseReductionDialog.cs:143`, `AutoSubtitlesDialog.cs:205`), mientras que Quitar fondo pide pulsar «Empezar» y Reencuadre, «Analizar» (`VisionDialogs.cs:90,214`).

3. **Reencuadre cambia el formato de la secuencia sin avisar antes.** Si el formato elegido no coincide con el del proyecto, Aplicar también cambia el tamaño de la secuencia (`VisionDialogs.cs:252-261`). Solo se entera después, por el aviso final (`:264-265`); el diálogo no lo dice antes de aplicar.

   Además, la «Suavidad» queda activa después de analizar (`VisionDialogs.cs:212`: no está entre lo que se desactiva en `:270-272`), pero moverla ya no cambia nada: el plan se calculó con el valor de antes.

4. **El menú del clip ofrece todo a cualquier clip y le falta Reducir ruido.** `TimelineView.Analysis.cs:9-42` añade todas las acciones sin mirar el tipo de clip ni si el módulo está instalado. Así, un clip de audio ofrece «Quitar fondo…» y un video sin subtítulos, «Traducir pista…». La barra de Herramientas sí atenúa el botón y explica por qué (`QuickTools.State`); los menús no usan esa regla.

   Además, `AnalysisCommands.Menu` (`AnalysisCommands.cs:20-28`) no incluye `ReduceNoise`, aunque la guía dice que está en el menú del clip (`docs/guia/ia-local.md:41`). Y en el mismo bloque aparece «Máscaras» (`VisionCommands.cs:20`), que abre una pestaña: no es una acción.

5. **Cada punto de entrada tiene una lista distinta.**

   | Punto de entrada | Qué tiene y qué le falta |
   |---|---|
   | Herramientas (`QuickTools.cs:24-39`) | 13 botones; falta Traducir pista |
   | Editar (`Shell/TitleBar.xaml:58-63`) | 6 acciones; faltan Quitar fondo, Reencuadre, Subtítulos automáticos, Estabilizar y Color automático |
   | Menú del clip | Tiene Traducir y Máscaras; falta Reducir ruido |

   Nadie puede saber qué hay en cada sitio sin probarlos todos.

6. **El aviso final no es igual en todas las acciones: unas traen Deshacer y otras no, y casi ninguna dice dónde quedó el resultado.**

   | Acción | Aviso final |
   |---|---|
   | Color automático (`AutoColorAction.cs:54`) | Trae «Deshacer» |
   | Subtítulos automáticos (`AutoSubtitlesDialog.cs:230`) | Sin «Deshacer»: `LongActionService.cs:146` solo lo añade si la acción no avisó |
   | Quitar fondo (`VisionDialogs.cs:134`) | Sin «Deshacer», con el resultado |
   | Reencuadre (`VisionDialogs.cs:264`) | Sin «Deshacer», con el resultado |
   | Reducir ruido, Traducir pista, Texto a voz | El genérico «Listo: <historial>» con Deshacer; nada de dónde quedó el medio, la pista o el clip nuevos |

   La guía promete «Al terminar queda un aviso con **Deshacer**» para todas (`ia-local.md:48`).

7. **El avance aparece en tres sitios que no se ven a la vez.**
   - En el diálogo, que es modal.
   - En la barra de avisos, tapada por ese modal, y que solo muestra la última acción (`LongActionService.cs:53-63`, `Shell/MainWindow.xaml.cs:721-770`).
   - En Trabajos, solo para los trabajos de módulos.

   Traducir solo muestra un anillo sin porcentaje (`TranslateDialog.cs:69`), aunque su trabajo tiene avance en Trabajos. Texto a voz informa del avance con valor nulo (`VoiceDialog.cs:147`).

8. **Trabajos abre con un estado vacío engañoso.** El panel empieza por «Proxies» y por «No hay proxies en preparación…» (`Views/Inspector/Jobs/JobsPanel.xaml:24,30-31`) aunque haya trabajos de IA en marcha. La sección «Módulos de IA» queda abajo y se oculta cuando no hay nada (`ModuleJobsView.cs:35,72`). Las acciones largas que no usan módulos (Color automático, Quitar silencios, Estabilizar) no pasan nunca por Trabajos.

9. **Hay dos ajustes de «calidad» que no se conocen entre sí.** Uno es Ver › Calidad de la IA, con Automática, Rápida y Máxima (`TitleBar.xaml:93-97`). El otro es el desplegable «Calidad» de Quitar fondo, con Normal y Alta (`VisionDialogs.cs:80-82`). El diálogo no dice cuál de las tres globales está activa ni enlaza con ella, aunque cambia el modelo y la resolución (`ia-local.md:31`).

10. **Texto a voz nunca se atenúa.** `QuickTools.State` no recibe el módulo de voz y para `TextToSpeech` devuelve `Yes` (`QuickTools.cs:72`; `ToolsBar.xaml.cs:159-162`). Sin el módulo, el botón parece disponible y el aviso llega después de pulsarlo (`VoiceDialog.cs:33-36`). Quitar fondo, Subtítulos y Reducir ruido sí se atenúan.

11. **A ninguna de estas acciones se le puede asignar un atajo.** No están en `EditorShortcuts.Catalog` (`Services/Shortcuts/EditorShortcuts.cs:94`, registrado en `MainWindow.xaml.cs:96`) y nadie las registra aparte. Por eso no salen en el panel de atajos, y `ToolsBar.xaml.cs:178` busca siempre una tecla que no existe. Los comentarios que dicen «Sin atajo por omisión» o «un atajo futuro» (`SubtitleCommands.cs:9`) hacen pensar que sí se puede.

    Los identificadores, además, mezclan prefijos: `tools.`, `vision.`, `voice.`, `analysis.` y `audio.` (`QuickTools.cs:17-22`, `TranslateDialog.cs:23`).

12. **Nombres que no coinciden:**
    - «Grabar voz» en Herramientas frente a «Grabar voz en off…» en los menús y la cabecera (`QuickTools.cs:38`; `docs/guia/audio.md:90`).
    - «Reducir ruido» (audio con IA) frente a «Reducción de ruido» (filtro de imagen, `docs/guia/imagen.md:34`).
    - Botones con verbos distintos para pasos equivalentes: Empezar, Analizar, Transcribir de nuevo, Traducir, Insertar en el cursor, Aplicar y Quitar.
    - La ayuda de Color automático dice «Corrige exposición y contraste» (`QuickTools.cs:31`), pero también cambia la temperatura y el matiz (`AutoColorAction.cs:50-52`).
    - Los avisos de módulo que falta dicen «Complementos › Módulos de IA» (`QuickTools.cs:64`), mientras que la guía dice «Instálalo desde Complementos» (`ia-local.md:44`) y habla también de una categoría «Módulos de IA» en la biblioteca (`ia-local.md:42`).

13. **La guía tiene un hueco y una ruta que no coincide con el código.** `docs/guia/audio.md:67` es un encabezado vacío, «Bajar música bajo la voz (ducking)», que repite el de `:78`; el enlace de `ia-local.md` lleva al vacío. `titulos-y-subtitulos.md:52` dice «Clic derecho en un subtítulo», pero la opción sale en cualquier clip (incongruencia 4).

14. **La vista previa de las propuestas no llega a verse.** `ModuleProposals` existe para dibujar la propuesta antes de aplicarla. Sin embargo, `Show` y `Apply` van seguidos (`VisionDialogs.cs:308-311`, `AutoSubtitlesDialog.cs:225-228`), y Reducir ruido, Texto a voz y Traducir no la usan. La persona usuaria no lo nota directamente, pero es la razón de que cada diálogo resuelva a su manera la revisión del resultado.

## 2. Propuesta de organización

**Un único catálogo de acciones de IA y largas.** Sería una lista sin WinUI, como `QuickTools`, con estos datos por acción:
- id;
- nombre;
- grupo;
- módulo que necesita;
- tipo de clip al que se aplica;
- verbo del botón principal;
- dónde queda el resultado.

De ese catálogo saldrían la barra, el menú de la barra de título, el menú del clip y el panel de atajos. Las reglas de `QuickTools.State` servirían también para atenuar las opciones de los menús.

```
Barra de título
└─ Herramientas                     (menú nuevo; sustituye al bloque de Editar)
   ├─ Audio:          Quitar silencios… · Detectar ritmo… · Bajar música bajo la voz… · Reducir ruido…
   ├─ Imagen:         Estabilizar… · Color automático · Balance de blancos automático
   │                  Quitar fondo… · Reencuadre automático…
   ├─ Texto y voz:    Subtítulos automáticos… · Traducir pista… · Texto a voz… · Grabar voz en off…
   └─ Calidad de la IA ›  Automática · Rápida · Máxima      (sale de Ver)

Barra Herramientas (encima de la línea de tiempo)
  [Audio ▸ 4] | [Imagen ▸ 5] | [Texto y voz ▸ 4]   mismos grupos, con separadores; incluye Traducir

Menú del clip
  … edición … ─ Herramientas ›   solo las del tipo de clip; las que no aplican, atenuadas con el motivo

Cada diálogo de IA (mismo esqueleto)
  ┌ Título = nombre del catálogo ─────────────────────────────┐
  │ 1. Opciones             (se bloquean mientras analiza)     │
  │    En este equipo: ~40 s (GPU · …)   Calidad: Automática ▸ │
  │ 2. [Analizar]           mismo verbo en todas               │
  │    ▓▓▓▓▓▓░░ 62 %  Transcribiendo…                          │
  │ 3. Revisar              lista, A/B, marcas en la línea      │
  │    «Al aplicar: crea la pista Subtítulos (ES)»             │
  │    «Cambiará la secuencia a 9:16»   ← efectos laterales    │
  │                         [Cancelar]  [Aplicar]               │
  └──────────────────────────────────────────────────────────┘
  Cerrar = cancelar, y el diálogo lo dice.

Al terminar (siempre igual)
  Aviso: «Subtítulos automáticos: 48 subtítulos en la pista Subtítulos.»  [Mostrar] [Deshacer]

Trabajos (Ver › Trabajos)
  En marcha    ← toda acción larga (con o sin módulo), con su porcentaje y Cancelar
  Terminados   ← con [Mostrar] y Limpiar
  Proxies · Exportaciones   ← debajo, plegables
  Vacío: «No hay nada en marcha.»
```

Decisiones que hacen falta (en conflicto con lo actual):
- Mover las acciones del menú Editar a un menú Herramientas.
- Elegir si «Cerrar» cancela siempre (lo más simple, y como funciona hoy) o si algunas acciones siguen en Trabajos. En ese caso, el diálogo tendría que ofrecer un botón «Seguir en segundo plano», no un texto.

Con el cambio de menú, la guía `ia-local.md` («Dónde están») se reduce a una línea.

## 3. Qué conviene probar sin WinUI

- **El catálogo coincide en todos los puntos de entrada.**
  - Todo id de `QuickTools.All`, de `AnalysisCommands.Menu` y de `VisionCommands.Menu`, más los de Traducir y Texto a voz, está registrado en el catálogo de atajos.
  - Las acciones del clip incluyen `analysis.noise`.
  - Los `Tag` del menú de `TitleBar.xaml` (leído como XML) son ids del catálogo.
  - El mismo id tiene el mismo nombre en todos los sitios.
- **`QuickTools.State` para cada acción:**
  - sin el módulo, Texto a voz y Traducir se marcan como no disponibles, con el motivo;
  - Traducir sin pistas de texto, también;
  - el mismo estado vale para el menú del clip.
- **El aviso final**, con un doble de `EditorSession`: toda acción que cambia el proyecto deja un aviso con «Deshacer», aunque haya dado su propio aviso. Es justo el caso que hoy falla por `notices == 0` en `LongActionService.cs:146`.
- **El flujo de un diálogo como estado puro.** Habría que extraer un `AiFlow` con los estados Opciones → Analizando → Listo → Aplicado, Cancelado o Error, sin WinUI, y comprobar:
  - las opciones quedan bloqueadas mientras analiza y después;
  - si se cambia una opción después de analizar, el resultado queda «desactualizado» y Aplicar se desactiva;
  - el texto y el estado de los botones en cada paso;
  - que cerrar en Analizando cancela, con el «Cancelando…» que ya cubre `DialogWorkTests`.
- **Los textos dicen lo que pasa:** ningún texto de diálogo que cancela al cerrar menciona «sigue en Trabajos» (Traducir hoy no lo cumple).
- **Reencuadre:** la decisión de cambiar el formato (`w * H != h * W`) y el aviso previo salen de una función pura, que se prueba con 16:9 → 9:16, 9:16 → 9:16 y 4:5 → 1:1.
- **Avance:** cada acción con un trabajo de módulo informa de un avance numérico al `LongActionScope` cuando el trabajo lo da, con un `ModuleJob` simulado y `ModuleActivity.Describe`. Traducir y Texto a voz hoy no lo hacen.
- **La guía**: los anclajes `*.md#…` de `docs/guia/` llevan a secciones con contenido, lo que detectaría el encabezado vacío de `audio.md:67`.
