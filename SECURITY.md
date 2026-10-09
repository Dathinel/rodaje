# Política de seguridad

> **Preparado para una futura publicación.** Hoy el repositorio de FotoToma es privado y este documento todavía no está en vigor. Se deja escrito para que, el día que el repositorio se haga público, la forma de reportar problemas de seguridad ya esté definida. Al publicar, se quita este aviso y se activa el reporte privado de vulnerabilidades de GitHub (ver la lista de `docs/legal.md`).

## Versiones con soporte

FotoToma no tiene todavía versiones publicadas. Cuando las tenga, solo la versión más reciente recibirá correcciones de seguridad; las anteriores se consideran sin soporte y la recomendación será actualizar.

## Qué se considera un problema de seguridad

Un editor de video abre archivos que pueden venir de cualquier parte, así que el área de riesgo principal es la lectura de archivos: un video, una imagen o un archivo de proyecto preparado para provocar la ejecución de código, la lectura o escritura de archivos fuera de lo esperado o el agotamiento de memoria. También cuentan las fugas de información, como que el programa envíe datos o claves a un servicio sin que el usuario lo haya pedido, y los problemas en las DLL de terceros que FotoToma distribuye, como las de FFmpeg.

No se consideran problemas de seguridad los fallos que solo cierran el programa sin otra consecuencia, aunque se agradece reportarlos como errores normales.

## Cómo reportar

Por favor, no abras un issue público para un problema de seguridad. Usa el reporte privado de vulnerabilidades del repositorio en GitHub (pestaña "Security", botón "Report a vulnerability"). En el reporte incluye la versión de FotoToma, la versión de Windows, los pasos para reproducir el problema y, si es posible, un archivo de ejemplo que lo provoque.

Recibirás una primera respuesta en un plazo de siete días. Este es un proyecto personal mantenido por una sola persona, así que los tiempos de corrección dependerán de la gravedad, pero cada reporte se tomará en serio y se te mantendrá al tanto.

## Si el problema está en una dependencia

Si la vulnerabilidad está en FFmpeg, ONNX Runtime u otra biblioteca de terceros, repórtala también a ese proyecto. FotoToma actualizará la dependencia en cuanto exista una versión corregida, y gracias al enlace dinámico de FFmpeg cualquier usuario puede reemplazar esas DLL por una versión corregida sin esperar a una nueva versión del programa.

## Divulgación

Una vez corregido el problema, se publicará un aviso de seguridad en el repositorio describiendo el fallo, las versiones afectadas y la corrección, con el crédito a quien lo reportó si así lo desea.
