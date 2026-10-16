# Interfaz del inspector: revisión y propuesta

Revisión del panel derecho (Propiedades, Filtros, Velocidad, Máscaras, Seguimiento) sobre `main` a 14-oct, leyendo el XAML, su código y `docs/guia/`. Solo se leyó el código: la app no se ejecutó. Las rutas cortas son relativas a `src/Rodaje.App/Views/Inspector/`.

## 1. Incongruencias, de más a menos molestas

1. **Transformar está dos veces y se puede añadir una tercera.** Propiedades edita la transformación en cinco secciones (`Properties/PropertiesPanel.xaml:53-146`). La misma instancia sale también en la pila de Filtros, porque `Shows` solo excluye Máscara y Seguimiento (`Filters/FiltersPanel.xaml.cs:377`). Ahí aparece como «Transformar», con los mismos parámetros en filas sueltas: el volteo pasa a dos desplegables «Sí/No» (`Services/Inspector/EffectUi.cs:547,591`) en lugar de los botones de Propiedades, y no hay gráfica. Además, «Añadir filtro» ofrece otra Transformar (`Services/Inspector/EffectCatalog.cs:28`), que queda muerta con la nota «Solo se aplica la primera transformación activa» (`Filters/FiltersPanel.xaml.cs:39-40`). Por eso existe el aviso «Transformar está desactivado en Filtros» (`Properties/PropertiesPanel.xaml:51`): el interruptor que apaga Propiedades vive en otra pestaña. El usuario no sabe cuál de los dos editores manda, ni por qué una pestaña le habla de la otra.
2. **El visor cambia de herramienta según la pestaña abierta.** Las asas de transformación solo salen con Propiedades a la vista (`docs/guia/imagen.md:58`). Las del recorte salen con Filtros y el filtro elegido (`docs/guia/imagen.md:138`), las de máscara con Máscaras (`Masks/MasksPanel.xaml.cs:404`) y los puntos de seguimiento con Seguimiento (`Tracking/TrackingPanel.xaml.cs:136`). Si el usuario cambia de pestaña para mirar un valor, lo que estaba arrastrando en el visor desaparece, y nada en el visor dice qué modo está activo.
3. **El seguimiento está duplicado.** Máscaras tiene su propia sección «Seguimiento» con Atrás/Adelante (`Masks/MasksPanel.xaml:213-246`), y la pestaña Seguimiento repite los mismos cuatro botones (`Tracking/TrackingPanel.xaml:85-115`). Lo que sigue la máscara no aparece en «Seguimientos del clip», así que no tiene gráfica de confianza ni se puede refinar. Una pestaña corta con «Cancelar» (`MasksPanel.xaml:242`) y la otra con «Parar» (`TrackingPanel.xaml:107`), aunque las dos conservan lo seguido. A esto se suman dos caminos para estabilizar: «Estabilizar…» del menú del clip (`EffectUi.cs:154`) y «Estabilizar este clip en lo seguido» (`docs/guia/seguimiento.md:40`).
4. **Hay tres maneras de animar, cada una con su lenguaje.** Las filas de parámetros usan un rombo ◇/◆, anterior/siguiente y una curva por tramo (`Properties/ParameterRow.xaml:277-305`). La forma de una máscara usa otro ícono (`&#xE915;`) y sus propias flechas (`Masks/MasksPanel.xaml:197-209`). La velocidad no tiene keyframes: tiene «puntos» de una curva que se añaden con doble clic (`Speed/SpeedPanel.xaml:101-109`, `Speed/SpeedCurveControl.cs:17-19`), sin rombo ni marca en el clip, y no se puede ver en la Gráfica.
5. **La Gráfica solo sirve para Transformar.** `ShowGraph` siempre pasa la transformación (`Properties/PropertiesPanel.xaml.cs:459`). Los keyframes de un filtro (`Filters/FiltersPanel.xaml:153-251`), del volumen, de una máscara o de la velocidad no tienen gráfica. Además, la gráfica solo lista parámetros con dos keyframes o más (`PropertiesPanel.xaml.cs:351`); con uno solo el desplegable aparece deshabilitado, sin decir por qué, salvo el texto del lienzo (`Properties/KeyframeGraphControl.cs:180-181`).
6. **Hay botones de restablecer que no hacen lo que dicen.** La ayuda de «Restablecer todo» menciona «posición, escala, rotación y opacidad» (`Properties/PropertiesPanel.xaml:34`), pero restablece las 14 filas: también el ancla, la inclinación y el desenfoque de movimiento (`PropertiesPanel.xaml.cs:48,662-663`). El de «Posición y tamaño» no devuelve el volteo, aunque esté en esa misma sección: `ResetAsync` solo toca parámetros, no opciones (`PropertiesPanel.xaml:55-57`, `Services/Inspector/ClipPropertyEditor.cs:263-282`). «Opacidad y mezcla» y «Desenfoque de movimiento» no tienen botón propio.
7. **Una misma idea tiene nombres distintos según la pestaña.**
   - Transformar dice «Rotación» (±360°) y la máscara «Giro» (±180°) (`EffectUi.cs:198` frente a `:379`). La casilla de seguimiento también dice «Giro» (`MasksPanel.xaml:218`).
   - Transformar dice «Opacidad» y la máscara «Intensidad» (`EffectUi.cs:199` frente a `:381`).
   - La posición de Transformar va en px de la secuencia y el centro de una máscara en % de la imagen (`EffectUi.cs:195` frente a `:375`). La máscara vive en el espacio de la capa y sigue a la transformación (`Views/Player/PlayerView.Masks.cs:137-138`), pero ningún panel lo dice.
   - «Recortar» significa tres cosas: el filtro Recortar (`EffectUi.cs:98`), «Recortar la capa» de una máscara (`EffectUi.cs:561`) y «Recorte automático» de estabilizar (`Tracking/TrackingPanel.xaml:173`).
   - «Invertir» es la máscara al revés en Máscaras (`MasksPanel.xaml:157`) y la reproducción hacia atrás en Velocidad (`Speed/SpeedPanel.xaml:145`).
   - «Velocidad» es una pestaña y también el modo de la Gráfica que mide cuánto cambia un valor por segundo (`Properties/PropertiesPanel.xaml:170`).
   - «Mezcla» es el modo de mezcla en Propiedades (`PropertiesPanel.xaml:118`) y la proporción seco/húmedo en Filtros (`EffectUi.cs:310,319`).
8. **El modo de mezcla figura como filtro, pero se edita en otro sitio.** «Modo de mezcla» sale en la pila de Filtros con su nombre como nota. Al elegirlo solo se ve el texto «El modo se elige en Propiedades, sección «Opacidad y mezcla»» (`Filters/FiltersPanel.xaml.cs:354`). Se puede añadir otro, que tampoco se aplica (`FiltersPanel.xaml.cs:39`).
9. **El orden de la pila no siempre cuenta, pero siempre se puede arrastrar.** La pila de Filtros dice «Se aplican de arriba abajo» (`Filters/FiltersPanel.xaml:39`) y deja arrastrar todas las filas (`FiltersPanel.xaml.cs:285`). Sin embargo, Transformar, Recortar, Anclaje de esquinas y Modo de mezcla se aplican en su etapa, estén donde estén (`src/Rodaje.Engine/Timeline/EffectPlan.cs:98`). El usuario mueve Transformar por encima del desenfoque y no cambia nada.
10. **Guardar lo propio tiene tres nombres.** La animación de Propiedades se guarda «como transición…», con el nombre por defecto «Mi transición» (`Properties/PropertiesPanel.xaml:39`, `PropertiesPanel.xaml.cs:22`), aunque su sección se llama «Animaciones» (`PropertiesPanel.xaml:148`) y una transición es otra cosa, que se edita en la misma pestaña. Filtros dice «Guardar como preset», que queda en «Mis filtros» (`Filters/FiltersPanel.xaml:42-43`), y Texto dice «Guardar como estilo» (`docs/guia/complementos.md:86`).
11. **«Filtros» contiene también los efectos.** La biblioteca separa Efectos y Filtros (`docs/guia/imagen.md:11`), pero la pestaña se llama Filtros y su «Añadir filtro» ofrece Transformar, Recortar, Croma o Estabilizar… (`Filters/FiltersPanel.xaml:30,37`).
12. **La pestaña Propiedades cambia de contenido sin cambiar de nombre.** Con una transición seleccionada, la pestaña sigue llamándose «Propiedades» pero muestra otro panel (`InspectorView.xaml.cs:129-130`). Son 9 pestañas que en un panel estrecho pasan a íconos (`InspectorView.xaml:3-4`).
13. **El audio de un clip queda repartido en tres pestañas.** Volumen y fundidos están en Propiedades. El preajuste «Voz y música» de Propiedades escribe un ecualizador y un compresor que se ajustan en Filtros (`Properties/PropertiesPanel.xaml:205-210`), y el compresor tiene además su pestaña Dinámica (`docs/guia/audio.md:57`).
14. **Algunos estados vacíos y avisos de progreso no dan la información que hace falta.**
    - En Máscaras y Seguimiento, el mismo texto vacío sirve para «no hay clip» y para «el cursor está fuera del clip» (`Masks/MasksPanel.xaml:28`, `Tracking/TrackingPanel.xaml:21`); no dice cuál de las dos cosas pasa.
    - El deslizador de Velocidad llega a 100×, pero la curva solo va de 0,1× a 10× (`Speed/SpeedPanel.xaml:58-61` frente a `SpeedCurveControl.cs:17`).
    - «Normalizar la sonoridad del clip» normaliza en realidad todos los clips de audio seleccionados (`PropertiesPanel.xaml:193`, `PropertiesPanel.xaml.cs:115`) y, mientras mide, no se puede cancelar.

Lo que sí es coherente y conviene conservar: las secciones plegables (`InspectorSection`), que recuerdan si quedaron abiertas y muestran un resumen; la fila de parámetro igual en todas partes; y los cambios de deslizador que se funden en un solo paso del historial (`src/Rodaje.Core/Commands/Edits.Effects.cs:43-61`).

## 2. Propuesta de organización

La idea central: **un editor por cosa y una pestaña por tarea**. La pila del clip es el índice de todo lo que el clip tiene, y cada elemento se edita en un solo lugar.

```
Panel derecho
├─ Clip                     (antes «Propiedades»; con una transición: pestaña «Transición»)
│   ├─ Transformar          posición, escala, ancho/alto, rotación, voltear   [↺ incluye voltear]
│   │   └─ Ancla e inclinación (plegada)
│   ├─ Opacidad y modo de fusión                                             [↺]
│   ├─ Desenfoque de movimiento (plegada)                                    [↺]
│   ├─ Animaciones          baldosas · «Guardar animación…»
│   └─ Audio                volumen, fundidos, sonoridad (no se repite el preajuste)
│
├─ Efectos                  (antes «Filtros»; toda la pila del clip)
│   ├─ Fijos, siempre arriba y sin asa: Transformar ▸ Clip · Recortar · Anclaje
│   ├─ Pila ordenable        color, desenfoque, LUT…  «se aplican de arriba abajo»
│   ├─ Máscaras ▸ pestaña Máscaras (fila de resumen, sin editor aquí)
│   └─ Detalle del elegido   filas de parámetros
│   «Añadir» sin Transformar ni Modo de mezcla (únicos por clip)
│
├─ Velocidad                velocidad, duración, tono · Curva con rombos ◆ · congelar · al revés
│
├─ Máscaras                 herramientas, lista y Forma/Borde/Forma animada (◆ como en todo)
│   └─ «Seguir esta máscara…» ▸ abre Seguimiento con un seguimiento nuevo ya enlazado
│
├─ Seguimiento              (el único lugar donde se sigue)
│   ├─ Seguimientos del clip (también los de máscara, con su icono)
│   ├─ Seguir · Confianza · Refinar
│   └─ Aplicar a…           otro clip · máscara · zona · estabilizar
│
├─ Animación (o tira inferior, desplegable)
│   └─ Gráfica de CUALQUIER parámetro con keyframes: transformación, filtros, máscara,
│      volumen y curva de velocidad. Modos «Valor» y «Ritmo» (no «Velocidad»).
│
└─ Historial · Trabajos     (sin cambios)
```

Reglas que lo sostienen:

- **Un solo editor de Transformar.** En Efectos aparece como fila fija con «Editar en Clip ▸». El ojo de esa fila se repite en la cabecera de Clip, así que se apaga y se enciende desde ahí. Nada único por clip (Transformar, modo de fusión) se puede añadir dos veces.
- **El visor sigue a lo elegido, no a la pestaña.** Las asas muestran lo último que se eligió (transformación, recorte, máscara, puntos de seguimiento) y una etiqueta en una esquina del visor dice el modo («Transformar», «Máscara 2», «Seguimiento 1»). Esc vuelve a Transformar.
- **Un solo lenguaje para los keyframes.** El rombo ◇/◆, anterior/siguiente y la curva por tramo valen para filas, forma de máscara y velocidad. La curva de velocidad son keyframes de `rate`, que se ven en el clip y en la Gráfica.
- **Un vocabulario.** Rotación (no Giro), Opacidad (no Intensidad), «Modo de fusión» para el modo y «Mezcla» solo para seco/húmedo, «Al revés» para la reproducción hacia atrás e «Invertir» solo para máscaras. «Recortar» queda solo para el filtro; la máscara pasa a «Cortar la capa» o «Limitar efectos». Y «Guardar animación…», «Guardar look…», «Guardar estilo…» según lo que se guarda.
- **Las unidades se dicen donde importan.** En Máscaras, una línea: «La máscara se mueve con la imagen del clip; X e Y en % de la imagen».
- **Los restablecer hacen lo que dicen.** Cada sección tiene el suyo, la ayuda enumera lo que toca, y el volteo vuelve con «Posición y tamaño».

## 3. Qué probar con pruebas de lógica, sin WinUI

Todo esto vive o puede vivir en `Services/Inspector/` y en `Rodaje.Core`, y se prueba en `tests/Rodaje.App.Tests/Inspector/` junto a `EffectUiTests`, `ClipPropertyEditorTests` e `InspectorModelTests`.

- **Lo que se puede añadir.** `EffectCatalog.For(Video)` no ofrece tipos únicos por clip (Transformar, Modo de mezcla) cuando el clip ya los tiene. Si se decide que nunca se ofrecen, también se prueba.
- **Qué lista la pila.** La función que decide qué filas lista la pila y cuáles son fijas o se pueden arrastrar (hoy `Shows` y el `true` de `FiltersPanel.xaml.cs:285`): las etapas Transform y Blend nunca se pueden arrastrar, y Mask y Track nunca salen.
- **Arrastrar solo lo que cuenta.** Mover un efecto de etapa fija no cambia el `EffectPlan` resultante. Así se demuestra que el orden no importa, y que esconder el asa no pierde nada.
- **Restablecer.** `ResetAsync` de «Posición y tamaño» deja `flipX`/`flipY` en `off`. Restablecer todo deja la transformación igual que una nueva. La lista de lo que toca cada restablecer coincide con el texto de su ayuda: el texto se arma desde las etiquetas de `EffectUi`.
- **Vocabulario.** Una prueba recorre `EffectUi` y comprueba que la misma magnitud (rotación, opacidad, posición) tiene la misma etiqueta y unidad en todos los efectos, salvo excepciones declaradas. También comprueba que «Mezcla», «Invertir» y «Recortar» no se usen con dos sentidos.
- **Gráfica.** `GraphParameterChoice.Build` acepta parámetros de cualquier efecto (no solo Transformar) y explica por qué uno con un solo keyframe no se dibuja. Su texto vacío se elige en lógica, no en el control.
- **Velocidad como keyframes.** Pasar los puntos de la curva de velocidad a keyframes de `rate` y volver deja la misma duración y el mismo mapa de tiempo (ida y vuelta exacta, de 2 a 64 puntos). El rango de la curva coincide con el del deslizador, o el límite se declara en un solo sitio.
- **Seguimiento único.** Seguir una máscara crea una entrada de seguimiento enlazada (un `Track` con destino la máscara) y deja keyframes iguales a los del camino actual de `MasksPanel`. Aplicar y deshacer es un paso del historial.
- **El visor sigue a lo elegido.** Una función pura decide el modo del visor a partir de la selección del inspector (transformación, recorte, máscara o seguimiento) y no de la pestaña visible; se prueba con todas las combinaciones de selección.
- **Estados vacíos.** Los textos vacíos de cada pestaña distinguen «sin clip», «clip de audio» y «cursor fuera del clip». Se eligen en una función de lógica y se prueba cada caso.
