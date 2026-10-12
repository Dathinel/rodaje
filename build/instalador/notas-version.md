Rodaje {{VERSION}} ({{FECHA}}): editor de video nativo para Windows, compilado de antemano (Native AOT). No necesita .NET ni nada instalado aparte.

## Qué trae

- **Edición por capas** al cuadro exacto: cortar, recortar, ripple, roll, slip, slide, insertar y sobrescribir, marcadores, audio y video enlazados, clips compuestos, edición de tres y cuatro puntos. Todo se deshace con Ctrl+Z, con autoguardado y recuperación tras un cierre inesperado.
- **Velocidad y reversa:** constante o por curva, congelar cuadro y reversa exacta, con el tono del audio conservado.
- **Imagen en la GPU:** corrección de color con ruedas y curvas, comparación antes/después, LUT `.cube`, croma, máscaras, recorte con proporción, desenfoque, nitidez, viñeta, modos de mezcla, transformación con keyframes y transiciones.
- **Audio:** volumen por clip y mezcla con LUFS, ecualizador, compresor, ducking y voz en off.
- **Títulos y subtítulos** editables sobre el visor, con plantillas; escritos a mano o generados por la IA, con el tiempo de cada palabra para corregirlos.
- **Exportación:** MP4, MOV, MKV y WebM; H.264 y HEVC por GPU, AV1, VP9 y ProRes; AAC, Opus y PCM; normalización a LUFS, capítulos, formatos verticales y verificación del archivo final; varias versiones a la vez y cola que sobrevive a cerrar la app.
- **IA local por módulos:** subtítulos (Whisper), traducción, texto a voz, reducción de ruido, silencios y ritmo, estabilización, color automático, quitar fondo y reencuadre automático, importar CinemaDNG. Nada se envía a internet.
- **Complementos:** transiciones, filtros y módulos desde la app, con catálogo firmado y la huella de cada archivo verificada.
- **Atajos configurables** de teclado y ratón, con tres estilos de partida.
- **Flujo de trabajo:** proxies automáticos, espacios de trabajo, pantalla completa y segunda pantalla, carpetas vigiladas, zonas seguras de redes y recorrido inicial.

## Descargas

| Archivo | Para quién | Tamaño |
|---|---|---:|
{{ARTEFACTOS}}

- **Instalador:** por usuario y sin permisos de administrador, en `%LOCALAPPDATA%\Programs\Rodaje`. Crea el acceso en Inicio (y, si se elige, en el escritorio), abre los `.rodaje` con Rodaje y lo añade a «Abrir con» de videos. Se desinstala desde Configuración → Aplicaciones; tus proyectos no se tocan y los ajustes solo se borran si lo eliges.
- **Portable:** descomprime y abre `Rodaje.exe`. No toca el registro; para quitarlo, borra la carpeta.
- **Completo:** el portable con los complementos y los modelos de IA ya dentro. Los modelos de más de 2 GiB se bajan desde Complementos.

Comprueba la descarga con `SHA256SUMS.txt` (`Get-FileHash <archivo>` en PowerShell).{{AVISO_FIRMA}}

## Requisitos

- Windows 10 versión 2004 (19041) o posterior, u 11, de 64 bits (x64).
- GPU con DirectX 11.
- Recomendado: GPU NVIDIA, AMD o Intel con decodificación de video por hardware (H.264 y HEVC). Sin ella, la decodificación va por CPU.

## Licencia

Software libre bajo la GNU GPLv3 con términos adicionales (Sección 7): este programa deriva de un proyecto libre y gratuito, https://github.com/Dathinel/rodaje. El nombre «Rodaje» y sus logotipos no se licencian con el código (`TRADEMARK.md`). Licencias de terceros en `THIRD-PARTY-NOTICES.txt`.
