# Personalización

Todo lo que se puede ajustar al gusto está en **Archivo › Preferencias…**, pestañas **Personalización** y **Temas**. Se guarda en `ajustes.json` y se aplica sin reiniciar (la escala, al pulsar Guardar). Nada se confirma hasta pulsar **Guardar**: Cancelar deshace la vista previa, los temas editados, borrados o renombrados, el perfil importado y «Restablecer todo».

## Apariencia

- **Tema:** Grafito (el de fábrica), Claro, Alto contraste, los propios y «Seguir a Windows» (Claro u Grafito según el modo de Windows). Con «Seguir a Windows», si Windows cambia de modo con Rodaje abierto (Configuración › Personalización › Colores), la app cambia al momento, sin reiniciar; con Preferencias abierto, la vista previa manda hasta que se cierre. Con el contraste alto de Windows activo mandan los colores del sistema y el tema no se toca.
- **Color de acento:** ocho de la paleta, «Personalizado…» con selector o «Del tema». Si el acento no llega a 3:1 (AA) sobre los paneles, Rodaje lo oscurece o aclara hasta llegar y lo avisa debajo.
- **Escala de la interfaz:** 90, 100, 110 o 125 %. La ventana entera (barra de título, paneles, línea de tiempo) se dibuja a esa escala y la zona por la que se arrastra la ventana (el nombre del proyecto en la barra de título) se calcula con ella, así que coincide con lo que se ve a cualquier escala. Los diálogos (Preferencias, Exportar, avisos) y los menús flotantes quedan al 100 %: WinUI los dibuja fuera de la raíz escalada, y así una escala grande no deja un diálogo más ancho que la ventana.
- **Texto de la línea de tiempo:** pequeño, normal o grande (nombres de clips, pistas, regla y marcadores). Se aplica al pulsar Guardar a todo lo que ya está dibujado, sin esperar a tocar el proyecto.

## Temas

- El tema que se elige o se edita aquí es el de la app (el desplegable de Personalización lo sigue). Los de serie no se editan: **Duplicar** crea una copia propia y de ahí se edita. Cada color tiene un selector y se ve al momento en la propia app.
- Grupos: ventana, paneles, bordes, texto, acento y selección, línea de tiempo (fondo de las pistas de video y de audio, clips de video, audio y título, marcadores) y fondo del visor. El fondo de la línea de tiempo es el de la ventana y el cursor va con el acento.
- **Pistas de video y de audio:** cada tipo tiene su color de fondo (filas y cabeceras); de fábrica, el de los paneles. La pista principal conserva su tinte del acento y la cabecera seleccionada, el de la selección. El texto principal sobre ellas entra en la comprobación AA.
- Debajo del editor, la **comprobación AA** lista las parejas texto/fondo que no llegan (texto 4,5:1; bordes y acento 3:1).
- **Renombrar**, **Eliminar** (pide confirmación), **Restablecer** (vuelve a los colores de Grafito), **Importar** y **Exportar** `.rodaje-tema` (JSON con versión). Los archivos se escriben o se borran al pulsar Guardar. Los propios viven en `%LOCALAPPDATA%\Rodaje\temas\<nombre>.rodaje-tema`; uno dañado se ignora con aviso y la app arranca en Grafito.
- **Versiones del `.rodaje-tema`:** la 1 (hasta la 1.0.2) no tenía los colores de las pistas; al leer uno, o un `.rodaje-perfil` que lo lleve dentro, las pistas toman el color de los paneles (lo que se veía) y el tema pasa a la versión 2 al guardarlo. Un archivo de una versión más nueva se ignora con aviso.
- Los controles de WinUI (botones, campos, desplegables, menús, diálogos, deslizadores) siguen el tema entero: cada gris de fábrica se deriva del color del tema de su nivel (fondo, barra, panel, control, al pasar, pulsado, borde, texto), así que un tema de paneles azulados tiene los controles azulados, y toman el acento. Con Grafito quedan exactamente como de fábrica.

## Espacio de trabajo

Elegir una distribución de paneles (Edición, Color, Audio o las propias), guardar la actual con nombre o volver a «Edición». Es lo mismo que **Ver › Espacio de trabajo**; cada perfil recuerda los anchos de los paneles, el alto de la línea de tiempo y las pestañas visibles.

## Proyecto nuevo

Pistas de video y de audio (1 a 8), proporción, resolución y cuadros por segundo de la secuencia, y la carpeta que proponen los diálogos de guardar proyecto. Con un formato distinto del de fábrica (16:9, 1080p, 30), el primer video importado no lo cambia; con el de fábrica, la secuencia sigue tomando el tamaño y la cadencia del primer video, como siempre.

## Perfil

**Exportar perfil…** escribe un `.rodaje-perfil` (JSON con versión) con la apariencia, el tema propio activo, los espacios, el proyecto nuevo y los cambios de teclas y ratón de los tres estilos. **Importar perfil…** lo muestra al momento y lo aplica al pulsar Guardar; lo que viene de fuera se limpia (nombres sin caracteres de control, carpetas solo con ruta completa, valores acotados). **Restablecer todo…** pide confirmación y vuelve a fábrica todo lo anterior al pulsar Guardar; los temas propios guardados no se borran. **Exportar perfil…** escribe lo que muestran los controles sin guardar nada en los ajustes.
