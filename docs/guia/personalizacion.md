# Personalización

Todo lo que se puede ajustar al gusto está en **Archivo › Preferencias…**, pestañas **Personalización** y **Temas**. Se guarda en `ajustes.json` y se aplica sin reiniciar (la escala, al pulsar Guardar). Cancelar deshace la vista previa.

## Apariencia

- **Tema:** Grafito (el de fábrica), Claro, Alto contraste, los propios y «Seguir a Windows» (Claro u Grafito según el modo de Windows al arrancar). Con el contraste alto de Windows activo mandan los colores del sistema y el tema no se toca.
- **Color de acento:** ocho de la paleta, «Personalizado…» con selector o «Del tema». Si el acento no llega a 3:1 (AA) sobre los paneles, Rodaje lo oscurece o aclara hasta llegar y lo avisa debajo.
- **Escala de la interfaz:** 90, 100, 110 o 125 %. Los diálogos y menús flotantes quedan al 100 %.
- **Texto de la línea de tiempo:** pequeño, normal o grande (nombres de clips, pistas y regla).

## Temas

- Los de serie no se editan: **Duplicar** crea una copia propia y de ahí se edita. Cada color tiene un selector y se ve al momento en la propia app; se guarda al pulsar Guardar.
- Grupos: ventana, paneles, bordes, texto, acento y selección, línea de tiempo (clips de video, audio y título, marcadores) y fondo del visor. El fondo de la línea de tiempo es el de la ventana y el cursor va con el acento.
- Debajo del editor, la **comprobación AA** lista las parejas texto/fondo que no llegan (texto 4,5:1; bordes y acento 3:1).
- **Renombrar**, **Eliminar** (pide confirmación), **Restablecer** (vuelve a los colores de Grafito), **Importar** y **Exportar** `.rodaje-tema` (JSON con versión). Los propios viven en `%LOCALAPPDATA%\Rodaje\temas\<nombre>.rodaje-tema`; uno dañado se ignora con aviso y la app arranca en Grafito.
- Los controles de WinUI siguen el tema en claro u oscuro y toman el acento; no toman un tinte de color en los grises.

## Espacio de trabajo

Elegir una distribución de paneles (Edición, Color, Audio o las propias), guardar la actual con nombre o volver a «Edición». Es lo mismo que **Ver › Espacio de trabajo**; cada perfil recuerda los anchos de los paneles, el alto de la línea de tiempo y las pestañas visibles.

## Proyecto nuevo

Pistas de video y de audio (1 a 8), proporción, resolución y cuadros por segundo de la secuencia, y la carpeta que proponen los diálogos de guardar proyecto.

## Perfil

**Exportar perfil…** escribe un `.rodaje-perfil` (JSON con versión) con la apariencia, el tema propio activo, los espacios, el proyecto nuevo y los cambios de teclas y ratón de los tres estilos. **Importar perfil…** lo aplica al pulsar Guardar; lo que viene de fuera se limpia (nombres sin caracteres de control, carpetas solo con ruta completa, valores acotados). **Restablecer todo…** pide confirmación y vuelve a fábrica todo lo anterior; los temas propios guardados no se borran.
