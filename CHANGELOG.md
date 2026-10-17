# Historial de cambios de Rodaje

Todas las novedades, mejoras y correcciones de cada versión de Rodaje. Las fechas siguen la convención AAAA-MM-DD.

El formato sigue los principios de [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/) y este proyecto se adhiere a [Semantic Versioning](https://semver.org/lang/es/).

---

## [1.0.1] - 2026-10-16

Esta versión se centra en la estabilidad integral del motor de edición, la prevención de bloqueos ante fallos de hardware o archivos dañados, la compatibilidad hacia atrás del formato de proyecto, el blindaje de pruebas y la reorganización limpia de la interfaz (menús, preferencias y paneles contextuales).

### Añadido
- **Menú Secuencia y Formato del proyecto:** Nuevo menú de nivel superior para ajustar propiedades de la secuencia activa, resolución y tasa de fotogramas sin depender de atajos ocultos.
- **Ventana de Preferencias (`Ctrl+,`):** Diálogo centralizado para configurar carpetas de trabajo, caché de previsualización, límites de VRAM y comportamiento de autoguardado.
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
