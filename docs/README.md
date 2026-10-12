# Documentación de Rodaje

Cómo está hecho Rodaje, sus formatos y su fondo legal. Para usarlo o compilarlo basta con el [README](../README.md) de la raíz.

## Arquitectura

- [Arquitectura por capas](arquitectura/README.md): mapa de los proyectos de `src/`, reglas comunes, scripts y pruebas.
  - [Núcleo](arquitectura/nucleo.md): tiempo exacto, modelo del proyecto, operaciones de edición y deshacer.
  - [Medios](arquitectura/medios.md): FFmpeg, decodificación por GPU, salto exacto al cuadro y proxies.
  - [Audio](arquitectura/audio.md): salida WASAPI, reloj maestro, velocidad y mezcla.
  - [Reproducción](arquitectura/reproduccion.md): qué cuadro se muestra en cada refresco.
  - [Motor](arquitectura/motor.md): varias capas, decodificadores, memoria de video y mezcla.
  - [Render](arquitectura/render.md): composición en Direct3D 11, color y geometría.
  - [Exportación](arquitectura/exportacion.md): orquestación, codificación y verificación.
  - [App](arquitectura/app.md) y [vistas](arquitectura/app-vistas.md): la interfaz WinUI 3.
- [Arranque y distribución](arranque-y-distribucion.md): tiempos de arranque, lanzador, instalador y zip.

## Formatos y extensiones

- [Formato de proyecto `.rodaje`](formato-proyecto.md), con su [esquema JSON](../schema/rodaje-proyecto.v6.schema.json).
- [Formato `.paquete`](formato-paquete.md): complementos de contenido y módulos.
- [Protocolo de módulos](protocolo-modulos.md): cómo escribir un módulo de IA en cualquier lenguaje.
- [Descarga de complementos](descarga.md): catálogo firmado, índice y claves.
- [Variables de entorno](variables-entorno.md) que leen la app, las pruebas y los scripts.

## Decisiones y legal

- [Decisiones de diseño](decisiones.md) (D-001 a D-034).
- [Fondo legal](legal.md) y [pendientes legales antes de publicar](pendientes-legales.md).
- [Contribuciones externas](contribuciones.md).
- [Configuración de FFmpeg](ffmpeg-configure.txt) de la compilación LGPL que se distribuye, y los [crates de Rust](crates-ffmpeg.md) que rav1e y librsvg meten en sus DLL.
- En la raíz: [LICENSE](../LICENSE), [TRADEMARK.md](../TRADEMARK.md), [LICENCIAS.md](../LICENCIAS.md) y [THIRD-PARTY-NOTICES.txt](../THIRD-PARTY-NOTICES.txt).
