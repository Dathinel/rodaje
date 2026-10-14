# Propuesta: interfaz de la línea de tiempo

Revisión de la línea de tiempo y sus herramientas: barra, modos (ripple, magnética, enlazados), cabeceras de pista, marcadores y regiones, edición de tres puntos, multicámara y atajos en los tres estilos. Se hizo leyendo el XAML, el código de interfaz y `docs/guia/`, sin ejecutar la app. Lo que depende de cómo se ve en pantalla se marca como «a comprobar en Windows».

## 1. Incongruencias, de la que más molesta a la que menos

1. **Tres maneras de no dejar huecos, con nombres que se cruzan.** La pista principal magnética, el ripple automático y la herramienta de ripple hacen casi lo mismo y aparecen juntos sin explicar en qué se diferencian (`src/Rodaje.App/Views/Timeline/TimelineView.xaml:34`, `:38`, `:40`). El desplegable «Ripple: pistas afectadas / todas / …» (`TimelineView.xaml:41-46`) está al lado del ripple automático, pero su `RippleScope` también rige la magnética, cerrar huecos y la herramienta (`Views/Timeline/Logic/MagneticEdits.cs:18`, `Logic/ClipDrag.cs:268`, `Services/EditorSession.Organize.cs:107`). Además, nunca se desactiva cuando el ripple automático está apagado. Las dos acciones tienen el mismo `ShortLabel` «Ripple» (`Services/Shortcuts/EditorShortcuts.cs:129` y `:139`), así que en la hoja rápida y en la vista esencial salen dos «Ripple». Qué hace Supr depende de cómo estén combinados los interruptores (`docs/guia/edicion.md:23`), y la barra no lo dice en ningún sitio.
2. **Dos «imanes» y una palabra para cuatro cosas.** El ajuste (*snapping*) se describe como «el imán que pega los clips» (`EditorShortcuts.cs:128`), mientras que la guía llama «el interruptor del imán» a la pista magnética (`docs/guia/edicion.md:59`). Para colmo, el botón de ajuste usa el glifo `E71B`, que en Segoe MDL2 es una cadena, el mismo concepto visual que «enlazar» (`TimelineView.xaml:39`). «Ajust-» significa cuatro cosas distintas: el ajuste a bordes (`EditorShortcuts.cs:128`), «Ajustar a la ventana» (`:146`), «Ajustar a rellenar» (`Views/Timeline/TimelineView.ThreePoint.cs:145`) y «Ajustar velocidad» (`TimelineView.ThreePoint.cs:97`).
3. **La misma tecla cambia de significado según el estilo.** N es *ajuste* en Rodaje y *roll* en Clásico; Y es *roll* en Rodaje y *slip* en Clásico; B es *ripple* en Rodaje y *cuchilla* en Ágil (`EditorShortcuts.cs:128`, `:135`, `:138-140`); Ctrl+G es *agrupar* en Rodaje y *crear compuesto* en Ágil (`TimelineView.Organize.cs:34`, `TimelineView.Compounds.cs:21`). Al cambiar de estilo, la memoria muscular hace otra cosa sin ningún aviso. En Ágil, nueve acciones que en Rodaje tienen tecla se quedan sin ella (región, ripple automático, cinco herramientas, agrupar y desagrupar), y el ripple automático está en la lista esencial (`EssentialShortcuts.cs:262`). Por eso la vista esencial de Ágil sale con un hueco.
4. **Alt no hace lo que dice la documentación.** Según `docs/guia/atajos.md:91` y `docs/guia/edicion.md:42`, «Alt arrastra sin ajuste». En el código, Alt sobre el cuerpo hace *slip* y sobre un borde cambia la velocidad (`TimelineView.Input.cs:400-409`), y con la herramienta de zoom aleja (`:194`). No hay ningún camino en el que Alt quite el ajuste (`Logic/ClipDrag.cs:138`). Quien siga la guía hará un *slip* sin querer.
5. **Cortar y cuchilla se ven igual; «recortar el inicio» tiene tres nombres.** El botón de acción *Cortar* y la herramienta *Cuchilla* usan el mismo glifo `E8C6` (`TimelineView.xaml:33` y `:51`). La misma acción se llama «Borrar a la izquierda del cursor» en el atajo (`EditorShortcuts.cs:109`), «Recortar el inicio hasta el cursor» en el menú (`Shell/TitleBar.xaml:171`) y «Recortar hasta el cursor» en la guía (`docs/guia/edicion.md:22`), y en la barra es un «[» de texto (`TimelineView.xaml:52`).
6. **Hay teclas escritas a mano en textos que no siguen al estilo.** Ocurre en «Añadir un marcador en el cursor (M)» y en «(R)», «(I y O)» (`Views/Markers/MarkersPanel.xaml:386-388`), en el estado vacío «Pulsa M (marcador) o R (región)» (`MarkersPanel.xaml:430`), en «Insertar (,)» y «Sobrescribir (.)» (`TimelineView.ThreePoint.cs:142-143`) y en «Pulsa 1 a N» (`Views/Library/LibraryView.Pro.cs:582`). En Ágil, R no hace nada, y cualquier tecla reasignada deja el texto mal. La barra principal sí lo hace bien: saca la tecla del registro (`TimelineView.xaml.cs:766-773`).
7. **La edición de tres puntos está escondida y nombra las cosas de otra manera.** Solo aparece en una barra de contexto que se muestra cuando hay un origen abierto (`TimelineView.ThreePoint.cs:117-120`). Reemplazar, Ajustar a rellenar y Superponer existen solo ahí o con su atajo: no están en ningún menú (`Shell/TitleBar.xaml:166-185`). Las marcas se rotulan «E» y «S» (`ThreePoint.cs:137`, `:151`), pero las teclas son I y O. Las teclas «,» y «.» tienen un doble significado que no se ve: con un borde elegido recortan un cuadro y, si no, insertan o sobrescriben (`ThreePoint.cs:46-47`).
8. **La multicámara no se ve en la línea de tiempo.** Se crea desde el menú de la biblioteca (`LibraryView.Pro.cs:46`) y se cambia de ángulo solo con 1 a 9 (`TimelineView.Organize.cs:44-64`). No hay indicador del ángulo activo ni del número de ángulos. Si la tecla no aplica, no pasa nada y nada lo explica (`Organize.cs:63`). La sincronización por sonido solo muestra un aviso de «tarda unos segundos», sin barra de avance (`LibraryView.Pro.cs:557`), aunque `NotificationProgress` existe (`Shell/MainWindow.xaml:301`). En la guía, el párrafo de multicámara se mezcla con «Pistas de video que traen su audio» (`docs/guia/edicion.md:67`).
9. **«Ignorar grupos» también ignora los enlaces.** El interruptor se llama «Ignorar grupos» y su abreviatura es «Sin grupos», pero su descripción y su efecto separan además el audio y el video enlazados (`TimelineView.xaml.cs:499`, `Logic/ClipDrag.cs:293`). «Agrupar» y «Enlazar» son dos conceptos distintos (`TimelineView.Organize.cs:34`, `TimelineView.ClipMenu.cs:30`), pero un mismo interruptor los apaga a los dos.
10. **Marcadores en tres sitios y con gestos que no coinciden.** Se añaden desde la barra (`TimelineView.xaml:55-56`), con M y R, y se gestionan en un panel que vive en la biblioteca, al otro lado de la ventana (`Views/Library/LibraryView.xaml:335`). En la regla, el clic derecho sobre un marcador lo borra sin preguntar (`TimelineView.Input.cs:674-677`), cuando en cualquier otro sitio el clic derecho abre un menú. Las regiones no se pueden mover ni editar desde la regla.
11. **La barra está saturada y su estilo es irregular.** Hay unos treinta controles en un `StackPanel` sin desborde (`TimelineView.xaml:26-60`); con poco ancho es probable que se recorten (a comprobar en Windows). Hay diez herramientas solo con ícono, y tres de ellas usan texto ASCII («]»», ««»», «[·]», `:34-36`). Justo encima hay otra barra que también se llama «Herramientas», pero con acciones de IA y análisis (`Views/Tools/ToolsBar.xaml:27`), mientras que en la página de atajos «Herramientas» son las de edición (`EditorShortcuts.cs:79`).
12. **La palabra «libre» tiene dos sentidos en la cabecera.** La principal dice «Principal · libre» cuando no es magnética, y las demás pistas de video siempre dicen «Video · libre» (`Views/Timeline/TrackHeaders.cs:436-437`).
13. **Los menús no reflejan el área.** En Ver están Acercar y Alejar, pero no Ajustar a la ventana ni Zoom a la selección (`TitleBar.xaml:220-221`). No hay ningún menú para la línea de tiempo, los marcadores, los tres puntos o la multicámara. Editar mezcla la edición con el análisis de IA (`TitleBar.xaml:179-184`). En los atajos, el zoom está en «Vista» y la herramienta de zoom en «Herramientas» (`EditorShortcuts.cs:142-147`).
14. **La línea de tiempo vacía no orienta.** Solo muestra el resumen «0 clips · duración» (`TimelineView.xaml.cs:276`); no hay un «arrastra un medio aquí».
15. **«Cerrar todos los huecos de la pista» puede actuar sobre varias pistas.** Desde el menú del clip, la acción afecta a todas las pistas de la selección (`TimelineView.Organize.cs:139`, `EditorSession.Organize.cs:98-100`), aunque el texto dice «de la pista».

## 2. Propuesta de organización

**Glosario único.** Cada concepto tiene un solo nombre, y ese nombre se usa en la barra, los menús, los atajos y la guía:

| Concepto | Nombre | Se deja de usar |
|---|---|---|
| Pegar a bordes, cursor y marcadores | **Imán** (ícono de imán) | «Ajuste», ícono de cadena |
| Principal sin huecos | **Principal magnética** | «el interruptor del imán» |
| Correr lo que sigue | **Ripple** (un modo, ver abajo) | ripple automático y herramienta, como cosas separadas |
| Partir en el cursor / partir con clic | **Cortar** / **Cuchilla**, cada uno con su ícono | el mismo glifo para los dos |
| Q / W | **Recortar hasta el cursor (izquierda/derecha)** | «Borrar a la izquierda…» |
| Cambiar la velocidad para llenar | **Llenar con velocidad** | «Ajustar a rellenar», «Ajustar velocidad» |
| Mover sin los enlazados ni el grupo | **Editar suelto** | «Ignorar grupos» |
| Marca de entrada y de salida | **Entrada / Salida (I / O)** | «E / S» |

**Barra en tres bloques con nombre**, y lo poco frecuente en desplegables:

```
┌ Herramienta ────────────────┐┌ Huecos y pegado ──────────────────────┐┌ Acciones ─────────────┐┌ Vista ──────────┐
│ [Normal][Cuchilla][Rango]   ││ Huecos: (Dejar | Principal | Ripple ▾)││ Cortar  [ ]  Borrar   ││ − ━━●━━ +  ⤢  ⌖ │
│ [Recorte ▾: Ripple·Roll·    ││   └ Ripple ▾: pistas afectadas / todas ││ Marcador  Región      ││ 00:01:23:12     │
│   Slip·Slide]               ││     / + marcadores y regiones          ││                       ││                 │
│ [Más ▾: Selección·Envolv.·  ││ [Imán]  [Editar suelto]               ││                       ││                 │
│   Zoom]                     ││ Más ▾: Fundido auto · Bloq. envolv.   ││                       ││                 │
└─────────────────────────────┘└───────────────────────────────────────┘└───────────────────────┘└─────────────────┘
Barra de contexto (solo si aplica):
  Origen: «toma3.mp4»  Entrada 00:00:02:00  Salida 00:00:05:10  [V→V1▾][A→A1▾]  Insertar · Sobrescribir · Reemplazar · Superponer · Llenar con velocidad
  Multicámara «Multicámara 1»: [1 Cam A] [2 Cam B] [3 Cam C]   ← el ángulo activo, resaltado
Cabecera de pista: nombre · ojo/altavoz · candado · etiqueta «Principal · magnética» | «Principal» | «Video» | «Audio»
```

- **Los huecos se controlan con un solo control de tres posiciones:** *Dejar hueco*, *Principal magnética* (la de fábrica) y *Ripple en todas*. El ámbito de ripple cuelga de ese control y se aplica a todo lo que corre. La herramienta de ripple pasa a ser «Recorte ripple» dentro de *Recorte*: es un gesto de recorte, no un modo.
- **Undo y Redo salen de la barra**, porque ya están en Editar, en Ctrl+Z y en los laterales del ratón. La barra rápida de IA pasa a llamarse **«Rápidas»** o **«IA y análisis»**.
- **Menú nuevo, «Línea de tiempo»:** herramientas, huecos, imán, marcadores y regiones (anterior/siguiente, ir al panel), tres puntos (Entrada, Salida, Insertar…), multicámara (ángulos 1 a 9) y zoom (todo lo de la vista). El análisis de IA sale de Editar y pasa a «Herramientas IA».
- **Marcadores:** el panel se puede abrir desde la línea de tiempo, como pestaña del panel derecho o desde la nueva entrada del menú. En la regla, el clic derecho abre un menú (Editar, Quitar) en vez de borrar, y las regiones se pueden mover y estirar desde la regla.
- **Atajos, con una regla:** una tecla nunca significa dos acciones distintas en dos estilos, o queda libre o hace lo mismo. Por ejemplo, en Clásico el roll sería Mayús+Y y el *slip* Mayús+D; en Ágil la cuchilla sería C, y Ctrl+G sería agrupar. Toda acción esencial tiene tecla en los tres estilos. Ningún texto escribe una tecla a mano: todos la piden al registro, como hace `Tip()` (`TimelineView.xaml.cs:768`).
- **Alt**, una vez decidido qué hace (*slip* y estirar), se corrige en la guía. Si además se quiere mover sin imán, se le da otro modificador, por ejemplo Mayús mientras se arrastra.
- **Línea vacía:** un texto centrado dice «Arrastra aquí un medio de la biblioteca, o pulsa Ctrl+I para importar».
- **Multicámara:** el avance de la sincronización usa `LongActionService` y su barra. Una tecla de ángulo que no aplica avisa: «El cursor no está sobre una multicámara».

## 3. Qué se puede probar sin WinUI

Todo esto cabe en `tests/Rodaje.App.Tests` (`Shortcuts/`, `TimelineLogic/`). Para algunas pruebas, primero hay que sacar a catálogos estáticos los registros de acciones que hoy viven en la vista (`TimelineView.Organize.cs`, `TimelineView.ThreePoint.cs`, `TimelineView.Compounds.cs`).

- **Coherencia entre estilos:** para cada tecla que existe en dos estilos, la acción es la misma; si no, la combinación tiene que estar en una lista de excepciones con su motivo. Hoy esta prueba fallaría con N, Y, B y Ctrl+G.
- **Esenciales completos:** cada acción de `EssentialShortcuts.Ids` tiene tecla en los tres estilos.
- **Etiquetas únicas:** no hay dos `ShortLabel` iguales dentro de un mismo grupo ni entre los esenciales (hoy, «Ripple» dos veces).
- **Sin teclas escritas a mano:** una prueba que lea los `.xaml` y los `.cs` de `src/Rodaje.App/Views` y rechace textos de interfaz con «(X)» o «Pulsa X» que no vengan del registro.
- **Guía frente a catálogo:** leer las tablas de `docs/guia/atajos.md` y compararlas con las teclas de fábrica de cada estilo.
- **Matriz de huecos:** probar cada combinación de borrar, recortar o mover × principal u otra pista × magnética × ripple × herramienta × ámbito, con el hueco esperado y lo que se corre (`ClipDrag`, `MagneticEdits`, `TimelinePlanning`).
- **Gesto según los modificadores:** extraer `TrimGesture` y la elección de `DragOperation` (`TimelineView.Input.cs:400-429`) a una función pura (golpe, herramienta, modificadores) → operación. Así se fija qué hace Alt, Ctrl+Alt y Mayús, y la guía puede citarlo.
- **«,» y «.»:** una función pura (borde elegido, origen, marcas) → recortar, insertar o nada.
- **Multicámara:** resolver la multicámara bajo el cursor y si el ángulo *n* aplica, con el mensaje cuando no aplica.
- **Destino de «Cerrar huecos»:** qué pistas se cierran según la selección (`EditorSession.Organize.cs:98-100`) y que el texto del menú lo diga.
- **Etiqueta de la cabecera:** una función pura (rol, principal, magnética, bloqueada) → texto, sin el «libre» ambiguo.
