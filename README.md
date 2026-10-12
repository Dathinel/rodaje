# Rodaje

Editor de video nativo para Windows: rápido, por capas, con la comodidad de los editores móviles y la precisión de uno profesional. Un solo `Rodaje.exe` compilado con Native AOT, sin instalador y sin necesitar .NET instalado.

## Qué hace

- **Edición por capas** con tiempo exacto al cuadro: cortar, recortar, ripple, roll, slip, slide, insertar, sobrescribir, marcadores, enlazar audio y video, reemplazar y volver a enlazar medios. Todo se deshace con Ctrl+Z; autoguardado y recuperación tras un cierre inesperado.
- **Velocidad y reversa:** velocidad constante o por curva, congelar cuadro y reversa exacta, con el tono del audio conservado.
- **Efectos en la GPU:** corrección de color, LUT `.cube`, desenfoque, nitidez, viñeta, modos de mezcla, transformación con keyframes y transiciones.
- **Títulos y subtítulos:** texto con borde y sombra editable sobre el visor, plantillas y pistas de subtítulos, escritas a mano o generadas por la IA.
- **Exportación profesional:** MP4, MOV, MKV y WebM; H.264 y HEVC por GPU, AV1, VP9 y ProRes; AAC, Opus y PCM; normalización a LUFS, capítulos, dos pasadas, formatos verticales y verificación del archivo final.
- **IA local, por módulos:** subtítulos automáticos (Whisper), traducción, texto a voz, reducción de ruido, detección de silencios y de ritmo, ducking, estabilización y color automático. Todo corre en el equipo: nada se envía a internet.
- **Complementos:** transiciones, filtros y módulos se instalan y actualizan desde la app, con un catálogo firmado y la huella de cada archivo verificada.
- **Atajos configurables** con tres estilos de partida.

## Descargar y abrir

1. Descarga el zip de la última versión en [Releases](https://github.com/Dathinel/rodaje/releases): **base** (el editor) o **completa** (con complementos y modelos de IA).
2. Descomprímelo donde quieras y abre **`Rodaje.exe`**. No instala nada ni toca el registro; para quitarlo basta con borrar la carpeta.

**Requisitos:** Windows 10 u 11 de 64 bits. Una GPU con Direct3D 11 acelera la decodificación, los efectos y la exportación; sin ella todo funciona por CPU.

## Compilar

Todo el entorno (SDK de .NET 10 y FFmpeg LGPL, con versiones y huellas fijadas) se descarga dentro del proyecto, en `.entorno\`; no se instala nada fuera de la carpeta. Hace falta Visual Studio Build Tools con C++, porque Native AOT usa su enlazador.

| Para | Haz |
|---|---|
| Compilar y abrir el editor | Doble clic en `Rodaje.bat` (la primera vez prepara el entorno y publica en `dist\Rodaje\`) |
| Correr las pruebas | Doble clic en `Probar.bat`; `Probar.bat cobertura`, `aot` o `aceptacion` para las variantes |
| Armar los zip de distribución | `herramientas\distribuir.ps1` |

Variables de entorno opcionales en [docs/variables-entorno.md](docs/variables-entorno.md).

## Evidencias

Medido en un Ryzen 7 7435HS con RTX 3050 Laptop:

| Prueba | Resultado |
|---|---|
| Pruebas automáticas | Más de 4 000, en verde en Windows y Linux en cada cambio |
| Arranque | Ventana visible en **50 ms** en caliente y **215 ms** en frío |
| Salto exacto al cuadro | **2 864 de 2 864** saltos aleatorios en el cuadro exacto, en H.264 y HEVC, por GPU y por software |
| Sincronía audio/video | **0 cuadros de error en 10 minutos** |
| Exportación | 1 min 1080p de dos capas con efectos a H.264 por GPU en **9 s** (~200 fps) |
| Native AOT | **0 advertencias** |

## Documentación

- [Arquitectura por capas](docs/arquitectura/README.md): núcleo, medios, audio, reproducción, motor, render, exportación y app.
- [Formato de proyecto `.rodaje`](docs/formato-proyecto.md) ([esquema](schema/rodaje-proyecto.v5.schema.json)) y [formato `.paquete`](docs/formato-paquete.md)
- [Protocolo de módulos](docs/protocolo-modulos.md): cómo escribir un módulo de IA en cualquier lenguaje
- [Descarga de complementos](docs/descarga.md) y [arranque y distribución](docs/arranque-y-distribucion.md)
- [Decisiones de diseño](docs/decisiones.md) y [fondo legal](docs/legal.md)

## Licencia

[GNU GPLv3 con términos adicionales](LICENSE) (Sección 7): puedes usar, estudiar, modificar, redistribuir y vender Rodaje con el código fuente bajo la misma licencia, conservando el crédito visible en la interfaz y en las páginas de descarga o venta. Los complementos que se conectan por las interfaces públicas pueden llevar la licencia que elija su autor. El nombre «Rodaje» y sus logotipos no se licencian con el código ([TRADEMARK.md](TRADEMARK.md)).

Dependencias y licencias de terceros en [LICENCIAS.md](LICENCIAS.md) y [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt). Seguridad: [SECURITY.md](SECURITY.md). Contribuciones: [CONTRIBUTING.md](CONTRIBUTING.md).
