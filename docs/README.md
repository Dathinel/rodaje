# Índice de documentación de Rodaje

Mapa completo de la documentación técnica, manuales de usuario, arquitectura, formatos y marco legal de Rodaje.

---

## 1. Guía de usuario y manuales prácticos

Para editores, creadores de contenido y usuarios finales. Todos los manuales están actualizados con la interfaz moderna de Rodaje 1.0.1:

- **[Guía de inicio rápido](guia/README.md):** Visión global del flujo de trabajo y catálogo de capítulos.
- **[Instalación y requisitos](guia/instalar.md):** Instalador, versión portable y versión completa con modelos de IA; requisitos de Windows y GPU DirectX 11.
- **[Tu primer proyecto paso a paso](guia/primer-proyecto.md):** Importar, cortar, música bajo voz, rotulación y exportación a video vertical para redes en 10 minutos.
- **[Edición en la línea de tiempo](guia/edicion.md):** Operaciones al cuadro exacto, cortes (S), recorte ripple (Q/W), pista principal magnética, clips compuestos y edición de 3/4 puntos.
- **[Color, efectos e imagen](guia/imagen.md):** Corrección con ruedas de 3 vías, curvas, tablas LUT `.cube`, máscaras bezier, transformaciones animadas y modos de mezcla en GPU.
- **[Seguimiento de movimiento (Tracking)](guia/seguimiento.md):** Rastreo de puntos, esquinas y áreas aplicable a títulos, máscaras o estabilización.
- **[Audio y mezcla](guia/audio.md):** Volumen por clip, curvas de envolvente, ducking inteligente, ecualización, compresor y voz en off con sonoridad LUFS.
- **[Títulos y subtítulos](guia/titulos-y-subtitulos.md):** Generador de títulos vectoriales, subtitulado automático local con Whisper palabra a palabra y traducción.
- **[Inteligencia artificial local](guia/ia-local.md):** Módulos locales de IA (transcripción, eliminación de ruido, reencuadre vertical, quitar fondo) sin enviar datos a internet.
- **[Personalización](guia/personalizacion.md):** Tema (Grafito, Claro, Alto contraste, propios), acento, escala, editor de temas con contraste AA, espacios de trabajo, proyecto nuevo y perfil `.rodaje-perfil`.
- **[Exportación y renderizado](guia/exportar.md):** Preajustes de salida, exportación multipropósito, códecs acelerados por hardware y cola persistente.
- **[Rendimiento y aceleración gráfica](guia/rendimiento.md):** Optimización para GPU NVIDIA, AMD, Intel y perfiles de proxy para equipos modestos.
- **[Gestor de complementos](guia/complementos.md):** Instalación, actualización y verificación de firmas de paquetes modulares `.paquete`.
- **[Atajos de teclado y ratón](guia/atajos.md):** Lista completa de atajos para los tres estilos predefinidos (Rodaje, Ágil, Clásico).
- **[Solución de problemas y preguntas frecuentes](guia/solucion-de-problemas.md):** Diagnóstico ante fallos de inicio, códecs atípicos, volcados `.dmp` y recuperación de autoguardado.

---

## 2. Arquitectura interna y diseño de capas

Detalle de ingeniería de cada subsistema en `src/` (diseño sin dependencias circulares y Native AOT):

- **[Arquitectura por capas](arquitectura/README.md):** Filosofía de diseño, reglas de aislamiento entre capas, pruebas y directrices de compilación.
  - **[Capa Núcleo (`Rodaje.Core`)](arquitectura/nucleo.md):** Aritmética de tiempo racional (`RationalTime`), modelo de datos inmutable, árbol de pistas y pila multinivel de deshacer (`UndoStack`).
  - **[Capa Medios (`Rodaje.Media`)](arquitectura/medios.md):** Integración con FFmpeg LGPL, decodificación por hardware (D3D11VA), lectura de fotogramas al cuadro exacto y generador de proxies.
  - **[Capa Audio (`Rodaje.Audio`)](arquitectura/audio.md):** Motor de sonido WASAPI de baja latencia, reloj maestro de sincronía audiovisual, cambio de velocidad con preservación de tono y mezcla por buses.
  - **[Capa Reproducción (`Rodaje.Playback`)](arquitectura/reproduccion.md):** Planificador de fotogramas, bucle de sincronización con la tasa de refresco del monitor y gestión de pausas.
  - **[Capa Motor (`Rodaje.Engine`)](arquitectura/motor.md):** Sesión de línea de tiempo (`TimelineSession`), presupuesto de VRAM, decodificadores dinámicos y pre-roll multicapa.
  - **[Capa Render (`Rodaje.Render`)](arquitectura/render.md):** Composición acelerada en Direct3D 11, pipeline de color lineal / Rec.709, shaders HLSL, renderizado de máscaras y transformaciones geométricas.
  - **[Capa Exportación (`Rodaje.Export`)](arquitectura/exportacion.md):** Coordinador de exportación por tuberías, codificadores por GPU (NVENC/AMF/QSV/MF) y software, y verificación con `ffprobe`.
  - **[Capa Aplicación (`Rodaje.App`)](arquitectura/app.md) y [Vistas](arquitectura/app-vistas.md):** Interfaz gráfica WinUI 3, desacoplamiento de lógica en `Logic/`, diseño de controles y enlace de datos.
- **[Arranque y distribución](arranque-y-distribucion.md):** Medición de arranques en frío (215 ms) y caliente (50 ms), recortador AOT, empaquetado portable e instalador.
- **[Pruebas a mano](pruebas-a-mano.md):** Lo que se repasa con la app abierta antes de dar por buena una función o de publicar, porque depende de la red, de la ventana, del navegador o de medios reales.

---

## 3. Formatos, protocolos y extensibilidad

- **[Matriz de formatos compatibles](formatos.md):** 37 combinaciones de contenedores, códecs de video, audio e imágenes verificadas de punta a punta.
- **[HDR, 10 bits y RAW](hdr.md):** Profundidad medida en cada punto de la cadena y plan por fases (HDR → SDR, 10 bits de punta a punta, trabajar en HDR, RAW).
- **[Formato de proyecto `.rodaje`](formato-proyecto.md):** Especificación del archivo JSON de proyecto, marcas de tiempo en ticks racionales y [esquema JSON](../schema/rodaje-proyecto.v15.schema.json).
- **[Formato modular `.paquete`](formato-paquete.md):** Empaquetado zip para plantillas, filtros, transiciones, tablas LUT y módulos ejecutables.
- **[Protocolo de módulos de IA](protocolo-modulos.md):** Comunicación interproceso mediante tuberías nombradas y memoria compartida para integrar modelos en cualquier lenguaje.
- **[Descarga y seguridad del catálogo](descarga.md):** Infraestructura de catálogo firmado mediante claves criptográficas para distribución segura de complementos.
- **[Variables de entorno](variables-entorno.md):** Opciones de depuración, carpetas de datos personalizadas y ajustes de laboratorio.

---

## 4. Decisiones de diseño y marco legal

- **[Decisiones de diseño (D-001 a D-051)](decisiones.md):** Bitácora oficial de decisiones técnicas, elecciones de arquitectura y compromisos de estabilidad. Lo aún sin publicar está en el [CHANGELOG](../CHANGELOG.md), sección «Sin publicar».
- **[Marco legal y licencias](legal.md):** Justificación de la licencia GPLv3, compatibilidad con FFmpeg LGPL y directrices de marcas.
- **[Guía para contribuir](../CONTRIBUTING.md):** Convención de commits, flujo de Pull Requests y Developer Certificate of Origin ([DCO 1.1](DCO.txt)).
- **[Configuración de compilación de FFmpeg](ffmpeg-configure.txt):** Opciones exactas de compilación LGPL sin componentes privativos ni no libres.
- **[Crates de Rust en dependencias](crates-ffmpeg.md):** Auditoría de dependencias estáticas incluidas en bibliotecas auxiliares (rav1e, librsvg).
- **Textos de licencia en la raíz:** [LICENSE](../LICENSE), [TRADEMARK.md](../TRADEMARK.md), [LICENCIAS.md](../LICENCIAS.md) y [THIRD-PARTY-NOTICES.txt](../THIRD-PARTY-NOTICES.txt).
