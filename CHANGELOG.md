# Historial de cambios de Rodaje

Todas las novedades, mejoras y correcciones de cada versión de Rodaje. Las fechas siguen la convención AAAA-MM-DD.

El formato sigue los principios de [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/) y este proyecto se adhiere a [Semantic Versioning](https://semver.org/lang/es/).

---

## [Sin publicar]

Lo que ya está en `main` para la 1.0.2. Buena parte sale de editar un video real para Instagram.

### Añadido
- **Personalización:** pestañas «Personalización» y «Temas» en Preferencias. Tema Grafito, Claro, Alto contraste o «Seguir a Windows»; acento de ocho colores o personalizado con ajuste automático a 3:1; escala de la interfaz (90–125 %) y tamaño del texto de la línea de tiempo. Editor de temas propios por grupos con vista previa en vivo, comprobación AA, duplicar, renombrar, eliminar, importar y exportar `.rodaje-tema`. Espacios de trabajo desde Preferencias, formato y carpeta de los proyectos nuevos, y perfil `.rodaje-perfil` con todo lo anterior más atajos y ratón; «Restablecer todo» con confirmación (D-056).
- **Imágenes fijas como clips:** PNG, JPEG, WebP, BMP y TIFF, también con transparencia. Duración inicial configurable en Preferencias (5 s por defecto) y recorte libre. Respeta la orientación EXIF y reduce a 8192 px las imágenes más grandes (#430, #431, #434).
- **Efecto Ken Burns** para fotos, con 4 preajustes en el menú del clip: acercar, alejar y desplazar a la izquierda o a la derecha (#434).
- **Captura de fotograma rápida:** botón de cámara en el visor y `Ctrl+Mayús+C`. Guarda en la carpeta `Capturas` del proyecto sin diálogo y ofrece añadirla a la biblioteca (#434).
- **Pistas:** eliminar desde el menú de la cabecera o con Supr, y reordenar arrastrando la cabecera o con Subir y Bajar pista (#437).
- **Selección múltiple:** los clips seleccionados se mueven juntos, y posición, escala, rotación, opacidad, volumen y fundidos se editan a la vez en el inspector; los valores distintos se muestran como «—» (#436).
- **Selección múltiple, parte 2:** restablecer, voltear, modo de mezcla, preajustes, velocidad fija, filtros, máscaras y estilo de texto se aplican a todos los clips seleccionados en un solo paso de deshacer, con «—» en los valores mixtos; la sombra de arrastre muestra el grupo entero (#448).
- **Gráfica de fotogramas clave:** pestaña «Gráfica» en el inspector (y en Ver › Paneles) con la curva Bézier del parámetro elegido: puntos arrastrables en tiempo y valor, asas en todas las claves seleccionadas, selección por recuadro, Supr, copiar y pegar, y la rampa de velocidad del clip como un parámetro más. Cada arrastre es un paso de deshacer (#443, #446).
- **HDR a SDR:** los videos PQ y HLG (BT.2020) se convierten a BT.709 con mapeo de tonos en la vista previa y en la exportación, con el parámetro animable «Brillo de referencia HDR» en Propiedades; antes salían con los colores apagados (#444, D-052).
- **10 bits de punta a punta:** las fuentes de 12 bits y 4:4:4 (ProRes 4444, FFV1, DNxHR 444) ya no se bajan a 10 bits al decodificar, las imágenes fijas de 16 bits (PNG, TIFF, EXR) entran en 16 bits en vez de 8, y el visor usa 10 bits con un monitor de 10 bits (Preferencias › Rendimiento › «Visor de 10 bits»). Medido en `docs/hdr.md` (#447, #451, D-053 propuesta).
- **Secuencias HDR (parte 1):** el formato de proyecto 15 guarda el espacio de color de la secuencia (`colorSpace`: `sdr-709`, `hdr-pq` o `hdr-hlg`; sin HDR se sigue guardando en 13 o 14). Una secuencia HDR se exporta en BT.2020 con PQ o HLG en HEVC Main 10, AV1 de 10 bits y ProRes, con el blanco SDR en 203 nits (BT.2408) y, en PQ, la pantalla de masterizado y MaxCLL/MaxFALL en el archivo. Las miniaturas y la tira de fotogramas de un medio HDR ya se ven convertidas a SDR. El compositor sigue en SDR: la selección en la interfaz, la mezcla en lineal y el visor HDR llegan en la parte 2 (D-054, `docs/hdr.md`).
- **Índice firmado:** tipo de entrada «app» para las herramientas que se descargan aparte (Subtitular, Rastrear, Lanzador); Rodaje las omite y sigue viendo solo complementos y módulos (#445).

### Cambiado
- **Video encima de otro:** su sonido va a la primera pista de audio libre, o a una nueva, sin tener que separarlo (#437).
- **Quitar silencios** cierra los huecos por defecto (#435).
- **Pegar atributos** copia también el estilo del texto y de los subtítulos (fuente, tamaño, color, contorno, fondo, animaciones) y las sombras, pero no las palabras (#435).
- **Ventanas estrechas:** el inspector se pliega por debajo de 788 px de ancho y la biblioteca por debajo de 544. Las pestañas del inspector se desplazan y la barra de título se compacta (#428).
- **Marcadores juntos en la regla:** sus nombres se recortan en lugar de pisarse. El nombre de la GPU se abrevia en la barra de título (#432).
- **Línea de tiempo:** ocupa el 42 % del alto de la ventana (mínimo 268 px, 3 pistas), con divisor de 8 px y pestaña (doble clic alterna 30 % y 55 %; Ver › Distribución). La rueda sube y baja por las pistas, Mayús+rueda desplaza en el tiempo, Ctrl+rueda hace zoom y Alt+rueda cambia el alto de pista; el estilo Clásico no cambia. Mayús+Z es «Ajustar a la secuencia» y el zoom se ajusta al abrir un proyecto. Las herramientas de IA pasan a un desplegable «Herramientas ▾». Con el arrastre cerca de los bordes la vista se desplaza sola. El inspector sin selección muestra la secuencia y «Formato del proyecto…» (#439, D-051).
- **Transición por defecto:** guarda defensiva si no hay transición configurada (#440).
- **Etiqueta de Preferencias** más clara: «Traer también el audio de los medios que caigan en estas pistas de video» (#440).
- **Formato de proyecto 14,** solo si el proyecto contiene fotos. Sin fotos se sigue guardando en el 13, que abre la 1.0.1 (D-050).
- **CI:** la de Windows publica la app y la arranca (autoprueba con WARP, 3 min), y `release.yml` crea la release en borrador solo al lanzarlo a mano (#442). Las pruebas intermitentes se han asentado: la instantánea del arnés de estrés ya no corre en paralelo y las fugas de hilos se cuentan por mediana (#449).

### Corregido
- **Cuadro numérico del inspector:** un valor largo (7051 en Escala) se ve entero y, tras confirmar, el cuadro muestra el valor realmente aplicado; con el foco en un cuadro de texto, Ctrl+A y Fin actúan sobre el texto y no sobre los clips (#452).
- **Cierre:** un refresco de complementos que terminaba después de cerrar la ventana podía tumbar la app (`ObjectDisposedException` en el reproductor) (#450).
- **Nombres con acentos dañados** («SubtÃ­tulos») en proyectos tocados con una herramienta externa se reparan al abrir (#450).
- **Barra de la línea de tiempo a 1366 px:** marcadores, regiones y los interruptores secundarios se pliegan en un botón «…» antes que Deshacer y Rehacer, que ya no desaparecen (#450).
- **Término único «fotograma»** en toda la interfaz (antes alternaba con «cuadro») (#450).
- **Exportación:** las últimas filas podían salir negras en algunos equipos. Las texturas intermedias del render se reutilizaban sin limpiar (#433).
- **Importación masiva:** ahora tiene un límite de análisis simultáneos y una ruta repetida ya no se analiza dos veces (#425).
- **Automatización del mezclador:** la de tipo «tocar» respeta una curva Mantener tras el retorno (#389).
- **Instalador:** ya no sale incompleto si la carpeta de la app estaba oculta (#427).
- **Archivos hostiles:** tope de 256 MB para el `.rodaje` y de 32 MB para los subtítulos, con mensaje en español (#440).
- **Menú y documentación:** separador duplicado y nombre huérfano en el menú, esquema v14 y texto del historial de la 1.0.1 sobre Preferencias (#440).
- **Paneles con desbordes:** Exportar › Avanzado, Complementos y Biblioteca (#429).

## [1.0.1] - 2026-10-16

Esta versión se centra en la estabilidad integral del motor de edición, la prevención de bloqueos ante fallos de hardware o archivos dañados, la compatibilidad hacia atrás del formato de proyecto, el blindaje de pruebas y la reorganización limpia de la interfaz (menús, preferencias y paneles contextuales).

### Añadido
- **Menú Secuencia y Formato del proyecto:** Nuevo menú de nivel superior para ajustar propiedades de la secuencia activa, resolución y tasa de fotogramas sin depender de atajos ocultos.
- **Ventana de Preferencias (`Ctrl+,`):** Diálogo centralizado para configurar las pistas de los proyectos nuevos, las carpetas de medios generados y de capturas, los proxies, la calidad de la vista previa y el estilo de teclado.
- **Acceso rápido a Reducción de ruido:** Integración directa en el menú contextual de clips de audio y en la barra de herramientas.
- **Diagnóstico local de incidencias:** Informe de fallo en `%LOCALAPPDATA%\Rodaje\fallos\` ante cierres imprevistos, sin telemetría externa; al volver a abrir, la app avisa y deja copiar el informe.
- **Edición de tres puntos en el menú Secuencia:** Insertar, Sobrescribir, Reemplazar, Ajustar a rellenar y Superponer desde el origen; además, Añadir pista de video y de audio y Cerrar todos los huecos.
- **Pestaña Transición en el inspector** y Seguimiento fuera de Máscaras.
- **Preescucha de sonidos en la biblioteca:** botón de reproducir en cada sonido y doble clic para añadirlo.
- **Batería de pruebas de contrato:** Nuevas pruebas exhaustivas para límites de VRAM y caché de fotogramas (`TimelineSessionOptions`), búfer circular de dinámica (`DynamicsMeterRing`), tasas AAC de Media Foundation y analizador atómico de progreso en exportaciones FFmpeg.

### Cambiado
- **Reorganización del menú Editar y Herramientas:** Las funciones de inteligencia artificial (transcripción, subtitulado, traducción, reencuadre) pasan a un espacio coherente sin sobrecargar la edición básica.
- **Gestión de tareas en segundo plano (`LongActions`):** Las acciones pesadas (quitar fondo, traducción, proxies) muestran estado visible, spinner de avance y soporte de cancelación inmediata sin congelar la interfaz WinUI.
- **Pista magnética y cierre de huecos:** El cierre de huecos ahora opera par a par protegiendo estrictamente la sincronía entre pistas de video y audio enlazado.
- **Diálogos de IA en un solo paso:** en Quitar fondo, Reencuadre, Estabilizar, Reducir ruido y Subtítulos automáticos, «Aplicar» está siempre activo: lanza el análisis, muestra el progreso y aplica al terminar.
- **Quitar silencios con IA:** usa la detección de voz (Silero VAD) cuando el módulo Audio con IA está instalado y dice qué método usó; sin módulo, mide el volumen.
- **Carpetas de los diálogos:** abrir, guardar y exportar empiezan en la carpeta del proyecto o en la última usada, y la recuerdan entre sesiones.
- **Un solo control de huecos** en la línea de tiempo y glosario unificado en toda la interfaz (fotograma, rotación, «…»).
- **Avisos de adaptación de proyectos antiguos:** Mensajes descriptivos en español cuando un proyecto de la versión 1.0.0 contiene parámetros normalizados al abrirse.

### Corregido
- **Pre-roll en secuencias 4K HEVC multicanal:** Se corrigió un defecto de intercambio de decodificadores cuando un clip cambiaba de pista, evitando esperas y cuadros congelados antes de cada corte.
- **Fuga de identificadores (`Event` / `Thread`):** Corrección en el ciclo de vida de hilos secundarios y descriptores Win32 al abrir y cerrar proyectos repetidamente.
- **Guardado seguro y atómico:** Escritura mediante archivo temporal y generación de respaldo `.bak` previo para impedir archivos `.rodaje` corruptos ante cortes de alimentación.
- **Tolerancia a archivos WAV corruptos:** Manejo seguro de archivos de audio truncados o con encabezados inconsistentes sin abortar el motor de sonido.
- **Desinstalar y cancelar:** cancelar ya no desinstala el módulo ni avisa «desinstalado».
- **Cabeceras de pista** que tapaban la barra de herramientas al desplazar la línea de tiempo, **panel de Trabajos** encima de los diálogos y **barra de las pestañas del inspector** que tapaba sus nombres.
- **Rutas largas en exportación:** Soporte completo para rutas UNC y carpetas con longitud superior a `MAX_PATH` (260 caracteres) en Windows.
- **Validación en listas del mezclador:** Corrección ante entradas nulas en listas de buses de audio y efectos maestros, reportando el elemento exacto.

---

## [1.0.0] - 2026-10-12

Lanzamiento inicial de Rodaje: editor de video nativo para Windows compilado con Native AOT sobre .NET 10.

### Características iniciales
- **Edición por capas:** Edición magnética, tres y cuatro puntos, slip, slide, roll, ripple y clips compuestos con soporte multinivel de deshacer (`Ctrl+Z`).
- **Reproducción y decodificación nativa:** Integración con DirectX 11 (D3D11VA) para aceleración por hardware de H.264 y HEVC; soporte por CPU para ProRes, DNxHD, AV1 y VP9.
- **Motor de color y efectos en GPU:** Corrección de color con ruedas de tres vías, curvas tonales, carga de tablas LUT `.cube`, máscaras bezier vectoriales y transiciones aceleradas.
- **Audio profesional:** Medición de sonoridad integrada en tiempo real (LUFS EBU R128), ecualización paramétrica, compresor, ducking automático de música y grabación de voz en off.
- **Inteligencia artificial local:** Subtitulado automático con Whisper, texto a voz, reducción de ruido neuronal, eliminación de silencios, reencuadre inteligente y soporte de CinemaDNG en local sin conexión a internet.
- **Exportación multiformato:** Exportación rápida por hardware (NVENC, AMF, QSV, Media Foundation) y software (libx264, libx265, libsvtav1, prores_ks) a MP4, MOV, MKV y WebM, con verificación posterior de integridad.
- **Ecosistema de complementos:** Sistema de paquetes modulares `.paquete` firmados digitalmente para efectos, filtros y módulos de IA independientes.
