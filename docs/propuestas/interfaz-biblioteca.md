# Revisión: biblioteca y paneles laterales

Revisión del código, sin ejecutar la app. Abarca el riel de la biblioteca (Medios, Audio, Texto, Efectos, Transiciones, Filtros, Módulos de IA y Marcadores). Las rutas parten de `src/Rodaje.App/` salvo que se indique otra cosa.

## 1. Incongruencias, de más a menos molestas

1. **Audio: la lista de medios ocupa el panel y los sonidos quedan apretados abajo.** `Views/Library/LibraryView.xaml:278-289`: la fila de `AudioGrid` es `*` y la de «Sonidos de paquetes» es `Auto`, con su propio `GridView` limitado a `MaxHeight="220"`. Aunque haya un solo audio importado, o ninguno, la cuadrícula se queda con todo el alto. Los sonidos se desplazan en una caja de 220 px dentro del panel, así que hay dos barras de desplazamiento y la rueda solo mueve una. Lo mismo pasa en Texto, Efectos, Transiciones y Filtros: «Mis …» va en otra caja con `MaxHeight="260"` (`:307-311`) debajo de la cuadrícula principal, que también es `*` (`:295, :299`).
2. **Los sonidos de paquetes no se pueden escuchar ni añadir con doble clic.** El `GridView` de sonidos (`LibraryView.xaml:288-289`) solo tiene `DragItemsStarting`: le faltan `DoubleTapped` y `ContextRequested`. Por eso «Escuchar», «Detener» y «Añadir en el cursor» (`LibraryView.Sounds.cs:17-22`) y el atajo de doble clic (`LibraryView.xaml.cs:501-505`) no se pueden alcanzar desde ningún sitio. Un sonido solo se puede arrastrar, y hay que hacerlo sin haberlo oído antes.
3. **«Filtros» y «Texto» nombran cosas distintas, con el mismo icono.** Filtros es, a la vez:
   - la categoría del riel (`LibraryView.xaml:163`, glifo E71C), con looks para aplicar;
   - el botón de búsqueda de Medios (`:209`, también E71C), que filtra por tipo, valoración o etiqueta;
   - la pestaña del panel derecho (`Views/Inspector/InspectorView.xaml:60-63`, E71C), con los ajustes del clip.

   Texto aparece en el riel (`LibraryView.xaml:160`, E8D2) y en el inspector (`InspectorView.xaml:46-49`, E8D2). Quien lee «abre Filtros» en la guía no sabe de cuál se habla (`docs/guia/imagen.md:11` frente a `docs/guia/audio.md:70`).
4. **Audio repite una parte de Medios, con menos funciones.** La pestaña Audio solo muestra los medios que tienen audio y no tienen video (`LibraryView.xaml.cs:166`), sin carpetas, búsqueda, filtros ni favoritos. Además, la selección es `Single` (`LibraryView.xaml:282`), mientras que Medios usa `Extended` (`:250`). Medios ya ofrece «Tipo › Audio» (`Views/Library/Logic/LibraryFilter.cs:115`). «Importar» sale también en Audio (`LibraryView.xaml.cs:281`), pero importa a Medios, y su ayuda dice «videos o audios» (`LibraryView.xaml:182`) aunque también entran imágenes (`LibraryFilter.cs:114`).
5. **Favoritos aparece dos veces y «Quitar todos los filtros» no quita todo.**
   - El interruptor de la estrella (`LibraryView.xaml:211`) y «Marca › Favoritos» del menú (`LibraryView.Pro.cs:225`) hacen lo mismo por dos caminos. Si se combinan (estrella encendida y «Rechazados»), la lista queda vacía sin explicar por qué.
   - «Quitar todos los filtros» (`LibraryView.Pro.cs:268-273`) no apaga la estrella ni borra la búsqueda.
   - `HasFilters` no cuenta `FavoritesOnly` (`LibraryFilter.cs:37`), así que el botón de filtros no se resalta cuando la estrella está encendida.
6. **Los filtros no tienen el mismo alcance.** Valoración, Marca, Etiqueta y colección buscan en toda la biblioteca (`IsGlobal`, `LibraryFilter.cs:34`). Tipo y «Esconder rechazados» filtran solo la carpeta abierta (`:37`). Todos están en el mismo menú y nada indica que actúan sobre conjuntos distintos.
7. **Lo propio y lo de los paquetes se presenta de tres maneras.** En Texto, Efectos, Transiciones y Filtros, lo propio va aparte, bajo «Mis …» (`LibraryElements.cs:84-91`). En Audio, los sonidos propios quedan bajo el título «Sonidos de paquetes» (`LibraryView.xaml:287`), marcados con «Mío» (`Services/Packages/LibraryElements.cs:111`). El título «Mis sonidos» (`:89`) existe pero no se usa. Lo del motor («Rodaje») y lo de los paquetes van en la misma cuadrícula, sin separación ni orden visible (`LibraryElements.cs:100-117`): solo cambia la línea pequeña de origen de la ficha (`LibraryView.xaml:145`).
8. **Organizar la biblioteca no se puede deshacer.** Carpetas, favoritos, valoraciones, etiquetas, rechazos y colecciones se guardan aparte del proyecto (`LibraryView.Folders.cs:43-60`) y quedan fuera del historial. «Borrar colección» (`LibraryView.Pro.cs:258-265`) y «Eliminar carpeta» (`LibraryView.Folders.cs:219-234`) actúan sin confirmar y sin Ctrl+Z. En cambio, «Quitar del proyecto» y los marcadores sí pasan por el historial (`Views/Markers/MarkersPanel.xaml:5`), y la guía promete que «Ctrl+Z deshace cualquier paso» (`docs/guia/primer-proyecto.md:28`).
9. **El aviso de carpetas vigiladas puede no aparecer.** `WatchedBanner` está dentro de `MediaPanel` (`LibraryView.xaml:261`), y ese panel se oculta mientras la biblioteca está vacía (`LibraryView.xaml.cs:268`). En un proyecto sin medios, o desde cualquier otra pestaña, los medios nuevos no se anuncian.
10. **Los estados vacíos no siguen una misma regla.**
    - La biblioteca muestra el logo y un texto (`LibraryView.xaml:338-341`); Marcadores, solo un texto (`MarkersPanel.xaml:75-76`).
    - Texto, Efectos, Transiciones y Filtros siempre tienen algo del motor, así que nunca llegan a su mensaje vacío (`LibraryView.xaml.cs:289`). Quien no instaló paquetes no sabe que existen, salvo por el enlace del pie (`LibraryView.xaml:303-306`).
    - El enlace del pie dice «transiciones, filtros y efectos» incluso en Texto.
    - Módulos de IA explica que se instalan desde Complementos, pero no ofrece un botón para hacerlo (`LibraryView.xaml:331-332`).
11. **Algunos controles aparecen donde no corresponden y otros no se ven.**
    - El botón Complementos de la cabecera sigue visible en Marcadores (`LibraryView.xaml:185`).
    - Marcadores trae su propia barra, con otro relleno (12,8 frente a 8,8; `MarkersPanel.xaml:19`), dentro de un panel que se llama biblioteca.
    - Las filas de marcadores tienen botones visibles de editar y quitar (`MarkersPanel.xaml:66-69`); las fichas de la biblioteca solo responden al clic derecho, y en Efectos, Transiciones, Filtros y Texto el menú ni siquiera aparece si el elemento no se puede aplicar (`LibraryView.xaml.cs:513-514`).
    - El riel solo tiene iconos (`LibraryView.xaml:158-165`), mientras que las pestañas del inspector llevan texto.
12. **El doble clic en un estilo de texto hace cosas distintas sin avisar.** Si hay un título seleccionado, le cambia el estilo; si no, crea uno nuevo (`Services/Effects/ElementApplier.cs:69-78`). El menú contextual sí distingue «Añadir texto en el cursor» de «Aplicar al texto seleccionado» (`LibraryView.xaml.cs:527-531`), pero el doble clic no.
13. **No se ve el progreso de lo que tarda.**
    - Importar solo marca la sesión como ocupada (`Services/EditorSession.Files.cs:202`): no hay contador en la biblioteca.
    - El progreso del proxy es una etiqueta de 9 px en la ficha (`LibraryView.xaml.cs:753-767`), y en la vista de lista no aparece.
    - Las miniaturas de transiciones aparecen de golpe, sin un marcador de posición mientras se cargan (`LibraryView.Transitions.cs:152-186`).

## 2. Propuesta de organización

Reglas comunes a todas las categorías:

- Una sola zona con desplazamiento por categoría. Las secciones se pueden plegar, cada una ocupa el alto que necesita y ninguna lleva `MaxHeight`.
- El mismo orden en todas: **Mío → Rodaje → cada paquete** (con su nombre como encabezado) y, al final, la tarjeta «Conseguir más».
- Cada tarjeta responde igual: doble clic hace la acción principal, clic derecho abre el menú, se puede arrastrar y, al pasar el ratón, aparece un botón ▸ para escuchar o ver la vista previa.
- El riel se llama «Recursos» en las ayudas, y sus categorías usan nombres que no coinciden con las pestañas del inspector: Filtros pasa a llamarse **Looks**, y Texto, **Estilos de texto**. El botón de Medios se llama «Filtrar» y usa otro glifo (E16E).
- Marcadores sale de la biblioteca: va a una pestaña de la línea de tiempo o del panel derecho, porque pertenece a la secuencia y no es un recurso.

```
┌ Riel ┐┌ Cabecera: [Título de la categoría]  [Acción principal]  [⋯] ┐
│ Medios ││ Medios:  Buscar ▢  [Filtrar ▾ (n)] [☆] [▤]                     │
│ Audio  ││          Todos los medios › Boda › Exteriores                  │
│ Estilos││  ▸ Carpetas (3)                                                 │
│ Efectos││  ▾ Medios (42)            ← cuadrícula, una sola barra          │
│ Trans. ││  ─ aviso: 5 medios nuevos en carpetas vigiladas [Importar] [✕]  │
│ Looks  │└──────────────────────────────────────────────────────────────────┘
│ IA     │ Audio:     [Importar audio]
└────────┘  ▾ Música y efectos del proyecto (1)   ← filtro de Medios con tipo
                                                     Audio, mismas carpetas
            ▾ Mis sonidos (2)       ▸ escuchar  ⤓ añadir en el cursor
            ▾ Sonidos · Paquete X (24)
            ▸ Conseguir más sonidos en Complementos
          Efectos, Transiciones, Looks y Estilos de texto:
            Buscar ▢
            ▾ Mis transiciones (1)   (si está vacía: «Guarda una desde el
                                       inspector con Guardar como transición»)
            ▾ Rodaje (18)
            ▾ Paquete «Básicas» (30)
            ▸ Conseguir más en Complementos
          Módulos de IA: tarjeta por módulo con [Instalar] o [Abrir]
```

Estados vacíos: todos con el mismo bloque (logo atenuado, una frase y un botón con la acción, nunca solo texto). Si una sección queda vacía, su encabezado se muestra con una ayuda breve; no se oculta.

Deshacer: la organización (carpetas, marcas, colecciones) se guarda en su propio historial dentro del panel, con «Deshacer» en un aviso de unos segundos. Si eso no se hace, al menos «Eliminar carpeta» y «Borrar colección» deben pedir confirmación.

Filtros: un solo menú «Filtrar» donde la estrella es un atajo visible del mismo estado `Mark = Favorite`, no un segundo filtro. «Quitar filtros» limpia todo, incluida la búsqueda. Todos los filtros tienen el mismo alcance, toda la biblioteca, y la barra de ruta lo muestra («Resultados en toda la biblioteca»).

## 3. Qué probar con pruebas de lógica (sin WinUI)

Conviene sacar a funciones puras lo que hoy decide `UpdateVisibility` y `RefreshPackages`, y probarlo en `tests/Rodaje.App.Tests/Library/`:

- **Secciones por categoría.** `LibraryElements.Sections(category, installed)` devuelve las secciones en orden: Mío, Rodaje y cada paquete. Hay que comprobar que en Audio los sonidos propios quedan en «Mis sonidos» y no entre los de paquetes, y que ningún elemento sale dos veces (incluidas las LUT que ya usa un filtro).
- **Estado del panel.** Una función que, con los conteos (medios, audios, sonidos, propios, carpetas) y la consulta, devuelva qué secciones se ven y qué mensaje vacío corresponde. Debe cubrir: biblioteca vacía con aviso de carpetas vigiladas pendiente (el aviso tiene que verse), Audio con 0 o 1 medio y N sonidos, y categorías con solo lo del motor (tiene que aparecer «Conseguir más»).
- **Acciones de cada elemento.** Una tabla `(ItemKind, ¿hay selección?) → acción principal, opciones del menú`. Todo elemento que se puede arrastrar también tiene que tener acción con doble clic y menú; el sonido tiene que tener «Escuchar».
- **Filtros.** «Quitar filtros» devuelve una `LibraryQuery` igual a la vacía, con estrella y búsqueda incluidas. La estrella y `Mark = Favorite` producen la misma consulta. Todos los filtros tienen el mismo alcance; hoy hay que fijar `IsGlobal` contra `HasFilters` para que no se separen sin querer.
- **Nombres sin choque.** Una prueba que compare los nombres y glifos del riel con los de las pestañas del inspector y falle si se repiten (hoy fallaría con Texto y Filtros).
- **Deshacer de la organización.** Si se adopta el historial propio: eliminar una carpeta y deshacer deja el mismo `LibraryOrganization` (mismos hijos, medios y revisión lógica); lo mismo para borrar y restaurar una colección.
