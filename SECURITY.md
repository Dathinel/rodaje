# Política de seguridad

## Versiones con soporte

Solo la versión más reciente publicada en [Releases](https://github.com/Dathinel/rodaje/releases) recibe correcciones de seguridad. Las anteriores no tienen soporte: la recomendación es actualizar.

## Qué se considera un problema de seguridad

Un editor de video abre archivos que pueden venir de cualquier parte, así que el área de riesgo principal es la lectura de archivos: un video, una imagen o un archivo de proyecto preparado para provocar la ejecución de código, la lectura o escritura de archivos fuera de lo esperado o el agotamiento de memoria. También cuentan las fugas de información, como que el programa envíe datos o claves a un servicio sin que el usuario lo haya pedido; los complementos que se instalen sin pasar la verificación de firma y huella del catálogo; los módulos de IA, que se ejecutan como procesos aparte, y los problemas en las DLL de terceros que Rodaje distribuye, como las de FFmpeg.

No se consideran problemas de seguridad los fallos que solo cierran el programa sin otra consecuencia, aunque se agradece reportarlos como errores normales.

## Cómo reportar

Por favor, no abras un issue público para un problema de seguridad. Usa el reporte privado de vulnerabilidades del repositorio en GitHub (pestaña «Security», botón «Report a vulnerability»). Si ese botón no aparece, abre un issue que solo pida un canal privado, sin detalles del problema. En el reporte incluye la versión de Rodaje, la versión de Windows, los pasos para reproducir el problema y, si es posible, un archivo de ejemplo que lo provoque.

Recibirás una primera respuesta en un plazo de siete días. El proyecto tiene un solo mantenedor, así que el tiempo de corrección depende de la gravedad; cada reporte se toma en serio y se te mantiene al tanto.

## Si el problema está en una dependencia

Si la vulnerabilidad está en FFmpeg, ONNX Runtime u otra biblioteca de terceros, repórtala también a ese proyecto. Rodaje actualizará la dependencia en cuanto exista una versión corregida, y gracias al enlace dinámico de FFmpeg cualquier usuario puede reemplazar esas DLL por una versión corregida sin esperar a una nueva versión del programa.

## Divulgación

Una vez corregido el problema, se publicará un aviso de seguridad en el repositorio describiendo el fallo, las versiones afectadas y la corrección, con el crédito a quien lo reportó si así lo desea.
