<p align="center">
  <img src="assets/marca/rodaje.svg" alt="Logotipo de Rodaje" width="128">
</p>

<h1 align="center">Rodaje</h1>

<p align="center">
  Editor de video nativo para Windows: rápido, por capas, con la comodidad de los editores modernos y la precisión de uno profesional.<br>
  Compilado de antemano con Native AOT: sin dependencias pesadas externas ni necesidad de instalar .NET.
</p>

<p align="center">
  <a href="LICENSE"><img src="https://img.shields.io/badge/licencia-GPLv3-E8833A?style=flat-square&labelColor=1D1D1D" alt="Licencia GPLv3"></a>
  <a href="https://github.com/Dathinel/rodaje/releases/latest"><img src="https://img.shields.io/badge/versi%C3%B3n-1.0.1-E8833A?style=flat-square&labelColor=1D1D1D" alt="Versión 1.0.1"></a>
  <img src="https://img.shields.io/badge/Windows-x64-E8833A?style=flat-square&labelColor=1D1D1D" alt="Windows x64">
  <img src="https://img.shields.io/badge/.NET-10-E8833A?style=flat-square&labelColor=1D1D1D&logo=dotnet&logoColor=white" alt=".NET 10">
  <a href="https://github.com/Dathinel/rodaje/actions/workflows/ci.yml"><img src="https://img.shields.io/github/actions/workflow/status/Dathinel/rodaje/ci.yml?branch=main&style=flat-square&labelColor=1D1D1D&label=CI" alt="Estado de la CI"></a>
</p>

## Descargar

La última versión es la **[1.0.1](https://github.com/Dathinel/rodaje/releases/latest)** (16-oct-2026). Hay tres descargas: el **instalador** (recomendado, por usuario y sin permisos de administrador), el **portable** (descomprimir y abrir) y el **completo** (portable con los complementos y los módulos de IA ya dentro). Detalle en [Instalar Rodaje](docs/guia/instalar.md).

Lo nuevo de la 1.0.1: interfaz reorganizada (menús Herramientas y Secuencia, Preferencias con `Ctrl+,`, inspector con pestañas Clip, Transición y Seguimiento), funciones de IA que se aplican con un solo clic, «Quitar silencios» con detección de voz y muchas correcciones de estabilidad. Todo en el [historial de cambios](CHANGELOG.md).

## Qué es y qué hace

Rodaje es un editor de video no lineal (NLE) de alto rendimiento enfocado en la agilidad de montaje y el respeto a los recursos del sistema:

- **Montaje al fotograma exacto:** Cortar (S), recortar (Q/W), ripple, roll, slip, slide, edición de 3 y 4 puntos, clips compuestos y pista principal magnética. Todo cambio tiene soporte multinivel de deshacer (`Ctrl+Z`).
- **Aceleración por GPU:** Composición completa y corrección de color en Direct3D 11 con ruedas de 3 vías, curvas, tablas LUT `.cube`, máscaras bezier, transformaciones animadas con fotogramas clave y transiciones fluidas.
- **Títulos y subtítulos vectoriales:** Creación de rótulos sobre el visor con estilos configurables, plantillas y subtitulado automático local por IA con marca de tiempo precisa palabra por palabra.
- **Audio profesional:** Medición de sonoridad LUFS en tiempo real (EBU R128), ducking inteligente (bajar música bajo la voz), ecualización, compresor y grabación directa de voz en off.
- **IA local sin nube:** Subtitulado con Whisper, traducción, texto a voz, reducción de ruido neuronal, eliminación de silencios y reencuadre vertical. Todo se ejecuta en tu hardware: nada sale de tu equipo.
- **Exportación flexible y fiable:** Salida a MP4, MOV, MKV y WebM (H.264/HEVC por GPU, AV1, VP9, ProRes), cola persistente en segundo plano y verificación automática con `ffprobe`.

## Cómo se usa

1. **Crear o abrir:** Al iniciar, elige una plantilla (16:9 horizontal, 9:16 vertical para redes) o pulsa `Ctrl+N`.
2. **Importar medios:** Arrastra videos, fotos o canciones a la biblioteca o pulsa `Ctrl+I`.
3. **Editar en la línea de tiempo:** Arrastra clips a las pistas. Corta con `S`, elimina el sobrante con `Supr`, o recorta rápidamente con `Q` y `W`.
4. **Ajustar propiedades:** Selecciona un clip para modificar volumen, color, velocidad o efectos en el inspector de la derecha. Las funciones de IA (quitar fondo, reencuadre, subtítulos, reducir ruido) están en el menú **Herramientas**.
5. **Exportar:** Pulsa `Ctrl+E`, elige tu resolución y códec, y pulsa **Exportar**.

Consulta la [Guía de inicio paso a paso](docs/guia/primer-proyecto.md) para un tutorial completo de 10 minutos.

## Cómo se compila y prueba

El entorno de compilación (SDK de .NET 10 y bibliotecas LGPL de FFmpeg) se descarga automáticamente de forma aislada en `.entorno\`; no se instala nada en el sistema global.

### Requisitos
- Windows 10 (versión 19041 o posterior) o Windows 11 de 64 bits (x64).
- Visual Studio Build Tools 2022 con la carga «Desarrollo para el escritorio con C++» (necesario únicamente para la publicación con Native AOT).
- Tarjeta gráfica con soporte para DirectX 11.

### Pasos rápidos

| Tarea | Comando / Acción |
|---|---|
| **Compilar y abrir el editor** | Doble clic en `Rodaje.bat` (prepara el entorno en el primer inicio y publica en `dist\Rodaje\`) |
| **Correr toda la suite de pruebas** | Doble clic en `Probar.bat` (más de 5 000 pruebas automatizadas) |
| **Medir cobertura de código** | `Probar.bat cobertura` (Core ≥ 99 %, Export ≥ 90 %) |
| **Comandos de dotnet** | `build\dotnet-local.cmd <argumentos>` (ejemplo: `build\dotnet-local.cmd test`) |
| **Generar instalador y paquetes** | `powershell -ExecutionPolicy Bypass -File build\distribuir.ps1` |

## Cifras y evidencias técnicas

Medidas en banco de pruebas estándar (AMD Ryzen 7 7435HS, NVIDIA RTX 3050 Laptop GPU, Windows 11):

| Métrica / Prueba | Cifra comprobada | Evidencia |
|---|---|---|
| **Arranque en frío (Native AOT)** | **215 ms** (interfaz completa usable en ~750 ms) | [Arranque y distribución](docs/arranque-y-distribucion.md) |
| **Arranque en caliente** | **50 ms** hasta ventana visible | [Arranque y distribución](docs/arranque-y-distribucion.md) |
| **Precisión de salto de fotograma** | **2 864 de 2 864** saltos aleatorios en el cuadro exacto | `tests/Rodaje.Media.Tests/VideoDecoderAccuracyTests.cs` |
| **Sincronía audiovisual** | **0 cuadros de deriva** en reproducción de 10 minutos | Pruebas de integración de reloj WASAPI / D3D11 |
| **Velocidad de exportación** | 1 min 1080p con 2 capas y efectos a H.264 en **9 segundos** (~200 fps) | Exportador GPU NVENC con buffer de cuadro directo |
| **Matriz de compatibilidad de formatos** | **37 de 37** formatos de video/audio/imagen validados | [Matriz de formatos](docs/formatos.md) |
| **Suite de pruebas automáticas** | **Más de 5 000 pruebas** verdes en Windows y Linux | CI de GitHub Actions (`ci.yml`) |
| **Advertencias de compilación AOT** | **0 advertencias** (AOT y recortador limpios) | Compilación estricta con Native AOT |

## Documentación

- **[Guía de usuario completa](docs/guia/README.md):** [Instalación](docs/guia/instalar.md), [Primer proyecto](docs/guia/primer-proyecto.md), [Edición](docs/guia/edicion.md), [Color e imagen](docs/guia/imagen.md), [Audio](docs/guia/audio.md), [Títulos y subtítulos](docs/guia/titulos-y-subtitulos.md), [IA local](docs/guia/ia-local.md), [Exportación](docs/guia/exportar.md), [Atajos](docs/guia/atajos.md) y [Solución de problemas](docs/guia/solucion-de-problemas.md).
- **[Historial de cambios](CHANGELOG.md):** Novedades de la versión 1.0.1 y versiones previas.
- **[Cómo contribuir](CONTRIBUTING.md):** Entorno de trabajo, convención de commits, firma DCO y flujo de pull requests.
- **[Arquitectura interna](docs/README.md):** Documentación técnica detallada de cada capa (`Core`, `Media`, `Engine`, `Audio`, `Playback`, `Render`, `Export`, `App`).

## Licencia

Publicado bajo licencia [GNU General Public License v3.0 con términos adicionales](LICENSE) (Sección 7): código abierto para estudiar, modificar y redistribuir con crédito conservado. Los complementos que interactúan mediante las interfaces públicas pueden conservar la licencia que defina su autor.

Marcas y logotipos sujetos a [TRADEMARK.md](TRADEMARK.md). Avisos de terceros en [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
