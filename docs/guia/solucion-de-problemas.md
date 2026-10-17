# Preguntas frecuentes y solución de problemas

## La app no abre

- **SmartScreen bloquea el archivo.** Es el aviso de Windows para programas sin firma: «Más información» → «Ejecutar de todas formas». Detalle en [Instalar](instalar.md#el-aviso-de-smartscreen).
- **«No encuentro …\app\Rodaje.exe».** El portable se movió a medias: `Rodaje.exe` necesita la carpeta `app` al lado (puede estar oculta). Vuelve a descomprimir el zip entero.
- **Windows anterior a la versión 2004 (19041)** o de 32 bits: no es compatible. Actualiza Windows.
- **Se cierra al instante.** Mira `app\rodaje-error.txt` (abajo). Si el archivo dice que falta Direct3D 11 por hardware, la app sigue, pero decodifica en la CPU; si faltan las bibliotecas de FFmpeg, la carpeta `app\ffmpeg` está incompleta: vuelve a descomprimir o reinstala.
- **El visor dice «Error: …».** Lo que no deja arrancar el motor de video se muestra ahí, no en un diálogo. Copia el texto al informar.

## Un formato no se decodifica

- Rodaje lee lo que FFmpeg LGPL puede abrir: MP4, MOV, MKV, WebM, AVI, MTS, WMV, 3GP, MP3, WAV, M4A, FLAC, imágenes y más. H.264 y HEVC se decodifican por GPU si la tuya puede; ProRes, DNxHD, AV1, VP9 y los demás, por CPU.
- Al importar, si un archivo no se puede leer, el aviso «Un archivo no se importó» dice el motivo. Archivos dañados o con códecs raros: conviértelos antes a MP4 (H.264) o a ProRes.
- **CinemaDNG** no se importa como video normal: Archivo › Importar CinemaDNG... (necesita el módulo «RAW de cine»).
- **Va a saltos** con 4K, tasas altas o 10 bits: deja que Rodaje genere el proxy (**Archivo › Preferencias...** › pestaña **Rendimiento** › «Generar proxies al importar videos pesados», encendido de fábrica) o pídelo con clic derecho en el medio › Generar proxy. El porcentaje sale en la portada y en Trabajos. Si Trabajos dice «No se pueden generar: falta el ffmpeg.exe LGPL», reinstala.
- **Reversa de un video de más de 1080p**: pide proxy obligatorio; el panel Velocidad muestra su estado.

## Faltan medios al abrir un proyecto

Aviso «Falta un medio» o «Faltan N medios» con el botón **Volver a enlazar**. En el diálogo: «Buscar en carpeta…» (busca por nombre, y propone coincidencias con otro nombre: «¿Es este?…») o «Elegir…» por archivo, y «Confirmar». Los clips, efectos y keyframes no cambian. «Más tarde» deja el proyecto abierto con esas capas vacías.

## Faltan paquetes

«Faltan <paquetes>. Mientras no se instalen, sus LUT no se aplican y sus transiciones se ven como fundido cruzado.» Instálalos desde Complementos; el proyecto no se toca.

## La exportación falla

El trabajo en Trabajos muestra el motivo y un botón con la acción:

| Mensaje | Qué hacer |
|---|---|
| Falta un medio | Vuelve a enlazarlo (arriba) o quita el clip |
| No se pudo leer un cuadro | El archivo está dañado o el equipo no decodifica ese formato: genera su proxy o conviértelo |
| Falta contenido / Falta una fuente | Instala el paquete desde Complementos o pulsa «Exportar sin él» |
| Un efecto falló | «Exportar sin él» o quita el efecto del clip |
| La tarjeta gráfica se quedó sin memoria | Cierra otros programas que usen la GPU o elige «Bajo rendimiento» |
| Falta el codificador | No está el `ffmpeg.exe` LGPL de Rodaje: reinstala |
| El codificador no aceptó los ajustes / se detuvo | Elige otro codificador en Avanzado (por ejemplo, el de CPU) o vuelve a un preajuste. Mensajes típicos: «La GPU no admite estos ajustes de NVENC», «El codificador no admite 10 bits en esta GPU» |
| No hay espacio en el disco / No se puede escribir ahí | Libera espacio, cierra el archivo en el otro programa o elige otra carpeta |
| El archivo se escribió, pero no es lo que se pidió | La verificación con ffprobe encontró una diferencia; reintenta y, si se repite, informa del fallo con el texto completo |
| La exportación falló (algo inesperado) | Reintenta; los detalles quedan en `rodaje-error.txt` |

Si cerraste Rodaje con exportaciones pendientes, al abrir pregunta «Reanudar» o «Descartar».

## Rodaje se cerró de golpe

Al volver a abrir: «Hay una copia de «X» del dd/MM/yyyy a las HH:mm con cambios sin guardar» con **Recuperar**, que la guarda como «X (recuperado).rodaje». El autoguardado reside en `%LOCALAPPDATA%\Rodaje\Autoguardado`. Además, cada guardado manual genera automáticamente una copia de respaldo `.bak` previa en la misma carpeta del proyecto para blindar el archivo `.rodaje` ante cortes eléctricos.

Si avisa «Autoguardado desactivado» (no pudo preparar la carpeta), guarda a mano con `Ctrl+S`. Con carpetas sincronizadas en la nube (OneDrive, Google Drive), guardar puede fallar si el servicio bloquea temporalmente el archivo; el proyecto anterior permanece intacto. Guarda fuera de la carpeta sincronizada o reintenta.

## Dónde están los registros y volcados de error

- **`app\rodaje-error.txt`**, junto al ejecutable real: con el instalador, `%LOCALAPPDATA%\Programs\Rodaje\app\rodaje-error.txt`; con el portable, dentro de la carpeta `app` (oculta) junto a `Rodaje.exe`. Cada error inesperado se añade con su fecha y traza técnica.
- **Informes de fallo:** Si la aplicación se cierra de forma inesperada, deja un informe en `%LOCALAPPDATA%\Rodaje\fallos\` para facilitar el diagnóstico, sin recopilar contraseñas ni archivos personales. Al volver a abrirla, avisa y permite copiar el informe.
- **`TestResults\`** solo existe si compilas desde el código fuente y ejecutas la suite de pruebas.
- No hay telemetría ni registros en la nube: nada sale del equipo.

## Cómo informar un fallo

1. Reproduce el problema y anota los pasos.
2. Abre un issue con la plantilla **Informar un fallo** en https://github.com/Dathinel/rodaje/issues: versión de Rodaje (Ayuda › Acerca de Rodaje...), cómo lo instalaste, versión de Windows y GPU (la barra superior muestra la GPU y el nivel de DirectX), y el contenido de `app\rodaje-error.txt` si existe. Quita las rutas o datos que no quieras compartir.
3. Si pasa con un archivo concreto, di su formato (códec, resolución, fps) o adjúntalo si puedes.
4. Un problema de seguridad (un archivo preparado que haga ejecutar código, fugas de datos) no va en un issue público: sigue [SECURITY.md](../../SECURITY.md).

## Preguntas frecuentes

**¿Necesito instalar .NET, Visual C++ o FFmpeg?** No. Rodaje viene compilado a código nativo y trae sus bibliotecas LGPL de FFmpeg en `app\ffmpeg`.

**¿Funciona sin conexión?** Sí, todo. Solo Complementos usa internet, para bajar paquetes y módulos; la distribución completa los trae.

**¿Dónde se guardan mis proyectos?** Donde tú elijas al guardar (`.rodaje`, un JSON legible). Los videos no se copian: el proyecto los referencia por ruta. Si los mueves, Rodaje pide volver a enlazarlos.

**¿Puedo usar el mismo proyecto en otro PC?** Sí: copia el `.rodaje` y los medios; los medios generados (voces, audio sin ruido, máscaras) están en `%LOCALAPPDATA%\Rodaje\medios-generados` salvo que eligieras «Junto al proyecto».

**¿Puedo abrir dos ventanas?** Sí, abre `Rodaje.exe` otra vez. Mientras haya dos, Complementos no deja desinstalar ni actualizar.

**¿Por qué el visor baja la calidad al reproducir?** «Auto» reduce la resolución de la vista previa cuando la GPU no llega (la cabecera dice «Auto · ½») y vuelve a completa en pausa. La exportación compone siempre a resolución completa. Se fija en el botón de calidad del visor o en **Archivo › Preferencias...** (pestaña **Rendimiento**).

**¿Cómo quito Rodaje del todo?** Instalador: Configuración → Aplicaciones → Rodaje y contesta sí a borrar ajustes y complementos. Portable: borra la carpeta y `%LOCALAPPDATA%\Rodaje`.

**¿Puedo vender videos hechos con Rodaje?** Sí. La licencia cubre el programa, no lo que haces con él.
