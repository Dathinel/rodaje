# App: las vistas

Qué hace cada vista de `Rodaje.App` y cómo está hecha. Las reglas que todas cumplen (sesión, comandos, atajos, avisos) están en el [contrato](app.md).

## Biblioteca (`Views/Library`)

- **Medios.** Portada real, duración y resolución. Se importa con el botón, Ctrl+I o soltando archivos; un `.paquete` soltado se instala.
- **Uso.** Doble clic añade a la pista principal. Se arrastra a la línea de tiempo con el formato `rodaje/media-id` (el texto es el id del medio).
- **Menú contextual.** Añadir, generar proxy (con el avance sobre la portada), mostrar en el Explorador y quitar si no se usa.
- **Portadas.** Se piden solo para lo que la cuadrícula hace visible.
- **Otras categorías.** Efectos, Transiciones y Filtros traen lo del registro más lo de los paquetes y se arrastran con `rodaje/elemento`. Las transiciones llevan además `rodaje/transicion` (el texto es el tipo del motor o la referencia `paquete@versión/elemento`); la línea de tiempo solo acepta referencias de paquete por ese formato, porque en `rodaje/elemento` una referencia puede ser un filtro. Doble clic o el menú aplica el elemento al clip seleccionado. Texto llega con los títulos.
- **Medida.** Con `RODAJE_MEDIR_BIBLIOTECA=archivo` la vista mide el desplazamiento y cierra la app (`LibraryScrollMeasurement`).

## Reproductor (`Views/Player`) y medidores (`Views/Meters`)

- **Cabecera.** Proporción (con resolución y cuadros por segundo en la ayuda), aviso de lanzadera («▶ ×4») y de «Proxy» cuando una capa en pantalla sale de un proxy. También calidad, zoom (Ajustar, 50 %, 100 %), márgenes seguros (acción 93 %, títulos 90 %, EBU R 95) y pantalla completa (F, doble clic o el botón; Esc sale).
- **Calidad de la vista previa.** Auto, Completa, Mitad o Cuarto, en la cabecera y en Ver (`ajustes.json`). Auto (`PreviewQualityGovernor`) mide la composición en la GPU cada 50 ms al reproducir; si el p95 de un segundo pasa de 10 ms baja un escalón, hasta un cuarto, y en pausa vuelve a completa. La cabecera dice «Auto · ½» mientras está bajada. La exportación compone siempre a resolución completa.
- **Transporte.** Código de tiempo, cuadro anterior, reproducir o pausar, cuadro siguiente y duración.
- **Lanzadera J/K/L.** L reproduce y luego dobla hasta ×8, J va hacia atrás, K detiene. A ×1 reproduce de verdad; a otras velocidades `PlayerEngine` hace un seek por refresco en pausa, sin audio.
- **Ediciones mientras reproduce.** El hilo de render toma la última copia del proyecto después de esperar el vsync, justo antes de componer: 10 ms de mediana entre `Reload` y el cuadro presentado (`edicion.a_present` en `app.ps1 -Medir`).
- **Escenario de efectos.** `app.ps1 -Medir N -Escenario efectos` arma sobre el clip de prueba la principal cortada en 5 con 4 transiciones y dos capas encima, todas con color y otro efecto, y al final mueve el deslizador de exposición 30 veces reproduciendo (`edicion.deslizador_a_present`, desde antes de ejecutar el comando).
- **Medidores.** Barras L/R con la escala IEC: verde hasta −18 dB, amarillo hasta −6 y rojo encima.
  - Muestran la franja del RMS (300 ms), la raya del pico retenido, un aviso de saturación que queda encendido hasta hacer clic y la lectura del mayor pico.
  - Se dibujan con `SpriteVisual` en `CompositionTarget.Rendering`, sin reservas por cuadro.
  - La caída (12, 20 o 40 dB/s) se elige con clic derecho. Ver [Audio](audio.md#medidores-de-nivel-metering).

## Línea de tiempo (`Views/Timeline`)

- **Barra de herramientas.**
  - Herramientas: normal (V), envolvente (E), selección y zoom (Z).
  - Interruptores: pista principal magnética, ajuste (N), ripple automático con su modo, fundido cruzado automático, bloquear envolventes e ignorar grupos. Se guardan en `ajustes.json`.
  - «Fundido auto» viene apagado. Encendido, cada corte sin transición de la pista principal muestra su símbolo y **un clic** en él crea un `rodaje.crossfade` de 0,5 s. No se crean solos al editar: llenarían el historial y cambiarían cortes que no se tocaron.
  - Acciones: cortar (S), recortar inicio y final, borrar, marcador (M), región (R), deshacer, rehacer y zoom.
- **Lienzo.**
  - Regla con marcas que se adaptan al zoom, marcadores y regiones.
  - Cabeceras con nombre y los interruptores de ocultar, silenciar y bloquear.
  - Clips con el color de su tipo, nombre, tira de película o forma de onda, rombos de keyframes, envolvente de volumen y contorno de selección.
  - Indicador de velocidad delante del nombre (`SpeedBadge`): «2,0×», «◀ 0,5×», «❚❚ Congelado» o «Curva»; a 1× no aparece. Deja a la derecha el hueco de la mini curva de la 5c.2.
  - Transiciones: un bloque con una X sobre su corte, del ancho de su duración (al menos 12 px) y con una manija en cada borde.
  - Cabeceras de audio con el volumen de la pista («Volumen −3,0 dB»).
- **Interacción.**
  - Clic y Ctrl+clic seleccionan; arrastrar en vacío selecciona con un recuadro y marca un rango.
  - Arrastrar un clip lo mueve sin pisar nada en pistas libres, con ajuste a bordes, cursor, marcadores y regiones. En la principal magnética lo reordena; al sacarlo o meterlo cierra o abre el hueco, también en las pistas de los enlazados.
  - Los bordes recortan y Alt sobre el cuerpo hace slip (el origen corre a la velocidad media del clip).
  - **Alt + arrastrar un borde estira** cambiando la velocidad (`DragOperation.StretchStart`/`StretchEnd`): el mismo material dura lo arrastrado, pegado al cuadro y a los puntos de ajuste, entre 0,1× y 100× y sin pasar a los vecinos salvo con ripple. Va con los enlazados (comparten velocidad), en vivo y como un paso «Cambiar velocidad»; un globo sobre la fila dice velocidad y duración («0,5× · 0:20.000»). En un congelado es un recorte.
  - El margen de cada borde al recortar se mide con el mapeo (`ClipDrag.Room`): a 2× un segundo de línea de tiempo gasta dos de origen.
  - Transiciones: un clic la selecciona (y vacía la selección de clips) y Supr la quita. Arrastrar una manija cambia la duración, siempre centrada en el corte, en cuadros enteros por lado y sin pasar el clip más corto; queda como un paso del historial.
  - Envolvente (E, en clips de audio y con las envolventes sin bloquear): un clic pone un punto de `rodaje.audio.gainDb` y lo arrastra, agarrar un punto lo mueve sin pasar a sus vecinos y el clic derecho lo quita. La altura es lineal en amplitud: 0 dB a media altura, +6 dB arriba y −60 dB (silencio) abajo.
  - Volumen de pista: se arrastra el texto de la cabecera a los lados (0,25 dB por píxel, con Mayús 0,05); doble clic vuelve a 0 dB. Va en `Track.Effects` como `rodaje.audio`; con keyframes fija el del cursor.
  - Todo arrastre es un gesto: los pasos se ejecutan en vivo (se prueban antes sobre una copia), se funden y quedan como un paso del historial; Esc lo revierte.
  - Rueda desplaza, Mayús+rueda cambia de pistas, Ctrl+rueda hace zoom alrededor del puntero.
- **Velocidad desde el teclado** (`EditorSession.Speed`, sobre la selección o el clip principal del cursor): «Velocidad del clip…» (Ctrl+R) selecciona y avisa con `SpeedPanelRequested` para que el inspector abra su pestaña; «Más rápido» y «Más lento» (Ctrl+Alt+. y Ctrl+Alt+,) recorren `SpeedSteps` de 0,1× a 100×; «Congelar cuadro» (Mayús+R; R sola sigue siendo Región) mete 3 s del cuadro del cursor y lo selecciona; «Invertir» no tiene tecla. Iguales en los tres estilos; con ripple en la principal magnética.
- **Comandos de velocidad.** `Services/SpeedCommands` (`SetSpeed`, `RateStretch`, `FreezeFrame`, `SetReverse`) es el doble provisional de los de `Edits` del núcleo: solo velocidad constante, en diezmilésimas, sin reflejar keyframes al invertir. Al unir el núcleo cada método llama al suyo.
- **Soltar medios.** En la principal magnética se inserta en el corte más cercano, con el sonido en la primera pista de audio. En las demás pistas tapa lo que haya (con Ctrl, inserta).
- **Soltar transiciones.** Caen en el corte entre clips pegados más cercano (hasta 28 px) de la pista bajo el puntero, con 0,5 s. Si el corte ya tiene una, cambia el tipo y conserva la duración. Las de paquete son `rodaje.preset` con la referencia en su opción `preset` (`TransitionPlanning`).
- **Mapeo.** Todo lo que pasa del origen a la línea de tiempo usa `ClipTimeMap` (`Logic/ClipMapping`): los keyframes y la envolvente van en el tiempo de los efectos (el virtual en un congelado) y se buscan por su lugar en la línea de tiempo; la onda pide el tramo del origen visible y cada punto toma su tramo con el mapeo (reversa y curvas incluidas); las miniaturas se separan en el origen según la velocidad.
- **Dibujo.** `TimelineRenderer` recicla elementos de XAML y solo arma los clips de una ventana de tres anchos de pantalla. Durante un zoom continuo lo dibujado se estira (`BitmapCache`) y se rearma nítido 150 ms después. La lógica sin WinUI (`Views/Timeline/Logic`) se prueba en `tests/Rodaje.App.Tests/TimelineLogic`.
- **Medida** (`RODAJE_MEDIR_LINEA=informe.txt`, 200 clips, RTX 3050 Laptop a 60 Hz):
  - Desplazarse: 16,7 ms por cuadro de mediana (el código propio, 0,2 ms).
  - Zoom continuo: 21,8 ms estirando y 31,8 ms rearmando; el resto es el dibujo de XAML.
  - Arrastrar un borde hasta que el motor publica la secuencia editada: 0,8 ms de mediana.
  - Estirar con Alt + borde, cada paso con 203 clips (prueba `CadaPasoDeEstirarTardaMenosDe16ms`, `TiempoReal`): 0,8 ms de mediana y 5,5 ms de p95.

## Inspector (`Views/Inspector`)

- **Pestañas.** Propiedades, Velocidad, Filtros, Historial y Trabajos, cada una en su carpeta. Con una transición seleccionada (`Session.SelectedTransitionId`), Propiedades muestra la transición. Si los nombres no caben en el ancho del panel, las pestañas pasan a ícono y solo la elegida conserva su nombre (todas con ayuda emergente).
- **Propiedades.** Los parámetros animables del clip en el cursor (`Services/Inspector/ClipPropertyEditor`), en secciones plegables (`InspectorSection`): «Posición y tamaño» (posición X e Y en dos filas seguidas, escala y rotación), «Opacidad y mezcla» (opacidad y el modo de mezcla del clip, 13 modos) y «Audio» (volumen y fundidos de entrada y salida). Plegada, cada sección resume sus valores en la cabecera («X −120 · Y 0 · 100 % · 0°») y dice cuántos keyframes tiene; si está abierta o no se guarda en `ajustes.json` (`InspectorSections`). Cada parámetro va en una fila (`ParameterRow`): etiqueta de ancho fijo con puntos suspensivos y ayuda, deslizador, número con su unidad, rombo de keyframe y restablecer, estos dos atenuados hasta pasar el ratón; por debajo de 340 px de ancho el deslizador baja a una segunda línea. Con keyframes aparece debajo la navegación (anterior y siguiente), la curva del keyframe del cursor y cuántos hay. Sin keyframes se cambia el valor fijo; con keyframes, cambiar el valor crea o ajusta el keyframe del cuadro del cursor. Las curvas entre keyframes salen de `KeyframeEase`. Los keyframes van en el tiempo del clip (`EffectCommands.KeyframeTime`, sobre `Clip.ToEffectTime`): a 2× el del cursor cae en el doble de origen, la navegación vuelve con `TimelineOfKeyframe` y el medio cuadro de tolerancia se escala con la velocidad.
- **Velocidad** (`Views/Inspector/Speed`, lógica en `Services/Inspector/SpeedEditor` y `SpeedMath`). Actúa sobre el primer clip de video seleccionado (o el de audio) y sus enlazados.
  - Deslizador logarítmico de 0,1× a 100× (1× en un tercio, 10× en dos) con imanes en 0,25×, 0,5×, 1×, 2×, 4× y 10×; redondea a centésimas, o a décimas desde 10×. Al lado, la velocidad escrita («1,5», «150 %», «x2»), y debajo botones rápidos.
  - Duración resultante: «5,0 s → 3,3 s», a la izquierda lo que dura a 1×. Escribir una duración da la velocidad exacta (la fracción más cercana que admite el núcleo).
  - «Mantener tono», activo por defecto. «Congelar» mete 3 s del cuadro del cursor (solo en video y con el cursor sobre el clip). «Invertir» y «Curva» se ven apagados con «Próximamente» (5c.2 y 5c.3).
  - Aviso de los límites del sonido si el clip suena: por encima de 8× se oye granuloso y por debajo de 0,25× metálico y con eco; en los dos casos sigue sonando.
  - Historial: arrastrar el deslizador es un gesto (en vivo, un paso, Esc lo revierte); escribir, los botones, el tono y congelar son un paso cada uno. Restablecer vuelve a 1× (D-024): se apaga si ya lo está y, si hay una curva, pregunta antes.
  - Las ediciones pasan por `ISpeedCommands` (`SpeedCommands.Default`), la costura con `Edits.SetSpeed`, `RateStretch` y `FreezeFrame` del núcleo.
- **Restablecer y quitar keyframes.** Cada parámetro tiene su botón de restablecer; «Posición y tamaño» y «Audio» restablecen su sección con el botón de su cabecera, y el menú «⋯» de la cabecera del panel trae «Restablecer todo» (toda la transformación) y «Guardar como transición…». Si hay keyframes, un aviso dice cuántos se borrarán antes de hacerlo; «Quitar keyframes» va aparte, en el menú contextual del parámetro. Todo es un paso del historial (D-024).
- **Guardar como transición.** Guarda los ajustes en `%LOCALAPPDATA%\Rodaje\complementos\propios`.
- **Filtros.** La pila del clip: activar, reordenar, quitar y añadir (sin el `rodaje.speed` reservado); el modo de mezcla está en Propiedades. Abajo, los parámetros del filtro elegido con keyframes, restablecer y quitar keyframes como en Propiedades; en Color y LUT, la LUT de un paquete o «Cargar .cube…». Nombres, unidades y pasos salen de `EffectUi` y los rangos del registro (`ParameterSpec.For`).
- **Audio.** Volumen con keyframes (de −60 a +24 dB) y fundidos de entrada y salida.
- **Transición.** Tipo (propia o de paquete), duración (hasta el tope del núcleo), parámetros, dirección del empuje y quitar; todo con `Edits.SetTransition*`.
- **Historial.** Cada paso del deshacer; un clic vuelve a ese estado.
- **Trabajos.** Proxies en curso y terminados.

## Atajos (`Views/Shortcuts`)

- **Dos vistas.** «Esencial» (la de fábrica) muestra el teclado solo con los atajos de `EssentialShortcuts` (reproducir, J/K/L, cuadros, inicio y final, cortar, borrar, ripple, deshacer, rehacer, marcador, zoom, guardar e importar) y su lista en columnas que se adaptan al ancho. «Avanzado» es todo lo de abajo. La elección se guarda en `ajustes.json` (`ShortcutsAdvanced`).
- **Teclado que cabe.** El dibujo va en un `Viewbox` que solo encoge: con el panel estrecho se ve entero y en proporción.
- **Lista.** Estilo de teclado (Rodaje, Ágil, Clásico), búsqueda y grupos en dos columnas (orden de `ShortcutGroups.Order`). Cada fila muestra el nombre, una frase sencilla y sus teclas, con Cambiar, añadir, quitar y volver a la de fábrica.
- **Captura.** `KeyCapture` suspende el router y se queda con cada tecla hasta soltarla, para que no lance una acción ni active el botón con el foco. Rechaza lo reservado (`ReservedKeys`: tecla Windows, Alt+F4, Alt+Tab, Tab, Impr Pant...) y Esc la cancela.
- **Cambios.** Cada cambio pasa por `ShortcutCustomizer` y su aviso sale en la barra de la ventana con «Deshacer».
- **Búsqueda.** `ShortcutSearch` busca sin tildes ni mayúsculas por texto o por tecla («s», «ctrl+b», «ñ»).
- **Ver teclado.** Dibuja un teclado ISO español (`KeyboardGeometry`) con la acción de cada tecla en el color de su grupo. Cada posición física pide su tecla virtual a la distribución activa; Ctrl, Mayús y Alt cambian de capa y un clic filtra la lista.
- **Hoja rápida.** `CheatSheet` junta los 15 atajos más útiles (entre ellos «Velocidad del clip…» y «Congelar cuadro») y los guarda en Documentos como HTML imprimible o PNG.

## Complementos (`Views/Plugins`)

Contenido y módulos de IA con su estado, «Funciona en», enlace al repositorio, Instalar desde archivo y Desinstalar. La descarga desde GitHub queda para la fase 7, detrás de `IPackageDownloader`.
