# App: las vistas

Qué hace cada vista de `Rodaje.App` y cómo está hecha. Las reglas que todas cumplen (sesión, comandos, atajos, avisos) están en el [contrato](app.md).

## Biblioteca (`Views/Library`)

- **Medios.** Portada real, duración y resolución. Se importa con el botón, Ctrl+I o soltando archivos; un `.paquete` soltado se instala.
- **Uso.** Doble clic añade a la pista principal. Se arrastra a la línea de tiempo con el formato `rodaje/media-id` (el texto es el id del medio).
- **Menú contextual.** Añadir, generar proxy (con el avance sobre la portada), mostrar en el Explorador y quitar si no se usa.
- **Portadas.** Se piden solo para lo que la cuadrícula hace visible.
- **Otras categorías.** Texto, Efectos, Transiciones y Filtros se arrastran con `rodaje/elemento`.
- **Medida.** Con `RODAJE_MEDIR_BIBLIOTECA=archivo` la vista mide el desplazamiento y cierra la app (`LibraryScrollMeasurement`).

## Reproductor (`Views/Player`) y medidores (`Views/Meters`)

- **Cabecera.** Proporción (con resolución y cuadros por segundo en la ayuda), aviso de lanzadera («▶ ×4») y de «Proxy» cuando una capa en pantalla sale de un proxy. También zoom (Ajustar, 50 %, 100 %), márgenes seguros (acción 93 %, títulos 90 %, EBU R 95) y pantalla completa (F, doble clic o el botón; Esc sale).
- **Transporte.** Código de tiempo, cuadro anterior, reproducir o pausar, cuadro siguiente y duración.
- **Lanzadera J/K/L.** L reproduce y luego dobla hasta ×8, J va hacia atrás, K detiene. A ×1 reproduce de verdad; a otras velocidades `PlayerEngine` hace un seek por refresco en pausa, sin audio.
- **Ediciones mientras reproduce.** El hilo de render toma la última copia del proyecto después de esperar el vsync, justo antes de componer: 10 ms de mediana entre `Reload` y el cuadro presentado (`edicion.a_present` en `app.ps1 -Medir`).
- **Medidores.** Barras L/R con la escala IEC: verde hasta −18 dB, amarillo hasta −6 y rojo encima.
  - Muestran la franja del RMS (300 ms), la raya del pico retenido, un aviso de saturación que queda encendido hasta hacer clic y la lectura del mayor pico.
  - Se dibujan con `SpriteVisual` en `CompositionTarget.Rendering`, sin reservas por cuadro.
  - La caída (12, 20 o 40 dB/s) se elige con clic derecho. Ver [Audio](audio.md#medidores-de-nivel-metering).

## Línea de tiempo (`Views/Timeline`)

- **Barra de herramientas.**
  - Herramientas: normal (V), envolvente (E), selección y zoom (Z).
  - Interruptores: pista principal magnética, ajuste (N), ripple automático con su modo, fundido cruzado automático, bloquear envolventes e ignorar grupos. Se guardan en `ajustes.json`.
  - Acciones: cortar (S), recortar inicio y final, borrar, marcador (M), región (R), deshacer, rehacer y zoom.
- **Lienzo.**
  - Regla con marcas que se adaptan al zoom, marcadores y regiones.
  - Cabeceras con nombre y los interruptores de ocultar, silenciar y bloquear.
  - Clips con el color de su tipo, nombre, tira de película o forma de onda, rombos de keyframes, envolvente de volumen y contorno de selección.
- **Interacción.**
  - Clic y Ctrl+clic seleccionan; arrastrar en vacío selecciona con un recuadro y marca un rango.
  - Arrastrar un clip lo mueve sin pisar nada en pistas libres, con ajuste a bordes, cursor, marcadores y regiones. En la principal magnética lo reordena; al sacarlo o meterlo cierra o abre el hueco, también en las pistas de los enlazados.
  - Los bordes recortan y Alt+arrastrar hace slip.
  - Todo arrastre es un gesto: los pasos se ejecutan en vivo (se prueban antes sobre una copia), se funden y quedan como un paso del historial; Esc lo revierte.
  - Rueda desplaza, Mayús+rueda cambia de pistas, Ctrl+rueda hace zoom alrededor del puntero.
- **Soltar medios.** En la principal magnética se inserta en el corte más cercano, con el sonido en la primera pista de audio. En las demás pistas tapa lo que haya (con Ctrl, inserta).
- **Dibujo.** `TimelineRenderer` recicla elementos de XAML y solo arma los clips de una ventana de tres anchos de pantalla. Durante un zoom continuo lo dibujado se estira (`BitmapCache`) y se rearma nítido 150 ms después. La lógica sin WinUI (`Views/Timeline/Logic`) se prueba en `tests/Rodaje.App.Tests/TimelineLogic`.
- **Medida** (`RODAJE_MEDIR_LINEA=informe.txt`, 200 clips, RTX 3050 Laptop a 60 Hz):
  - Desplazarse: 16,7 ms por cuadro de mediana (el código propio, 0,2 ms).
  - Zoom continuo: 21,8 ms estirando y 31,8 ms rearmando; el resto es el dibujo de XAML.
  - Arrastrar un borde hasta que el motor publica la secuencia editada: 0,8 ms de mediana.

## Inspector (`Views/Inspector`)

- **Pestañas.** Propiedades, Filtros, Historial y Trabajos, cada una en su carpeta.
- **Propiedades.** Los parámetros animables del clip en el cursor (`Services/Inspector/ClipPropertyEditor`). Sin keyframes se cambia el valor fijo; con keyframes, cambiar el valor crea o ajusta el keyframe del cuadro del cursor. Las curvas entre keyframes salen de `KeyframeEase`.
- **Restablecer y quitar keyframes.** Cada parámetro tiene su botón de restablecer y Transformación, «Restablecer todo». Si hay keyframes, un aviso dice cuántos se borrarán antes de hacerlo; «Quitar keyframes» va aparte, en el menú contextual del parámetro. Todo es un paso del historial (D-024).
- **Guardar como transición.** Guarda los ajustes en `%LOCALAPPDATA%\Rodaje\complementos\propios`.
- **Filtros.** La pila del clip: activar, reordenar, quitar y añadir.
- **Historial.** Cada paso del deshacer; un clic vuelve a ese estado.
- **Trabajos.** Proxies en curso y terminados.

## Atajos (`Views/Shortcuts`)

- **Lista.** Estilo de teclado (Rodaje, Ágil, Clásico), búsqueda y grupos en dos columnas (orden de `ShortcutGroups.Order`). Cada fila muestra el nombre, una frase sencilla y sus teclas, con Cambiar, añadir, quitar y volver a la de fábrica.
- **Captura.** `KeyCapture` suspende el router y se queda con cada tecla hasta soltarla, para que no lance una acción ni active el botón con el foco. Rechaza lo reservado (`ReservedKeys`: tecla Windows, Alt+F4, Alt+Tab, Tab, Impr Pant...) y Esc la cancela.
- **Cambios.** Cada cambio pasa por `ShortcutCustomizer` y su aviso sale en la barra de la ventana con «Deshacer».
- **Búsqueda.** `ShortcutSearch` busca sin tildes ni mayúsculas por texto o por tecla («s», «ctrl+b», «ñ»).
- **Ver teclado.** Dibuja un teclado ISO español (`KeyboardGeometry`) con la acción de cada tecla en el color de su grupo. Cada posición física pide su tecla virtual a la distribución activa; Ctrl, Mayús y Alt cambian de capa y un clic filtra la lista.
- **Hoja rápida.** `CheatSheet` junta los 15 atajos más útiles y los guarda en Documentos como HTML imprimible o PNG.

## Complementos (`Views/Plugins`)

Contenido y módulos de IA con su estado, «Funciona en», enlace al repositorio, Instalar desde archivo y Desinstalar. La descarga desde GitHub queda para la fase 7, detrás de `IPackageDownloader`.
