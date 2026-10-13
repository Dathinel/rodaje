Rodaje {{VERSION}} ({{FECHA}}): editor de video nativo para Windows, compilado de antemano (Native AOT). No necesita .NET ni nada instalado aparte.

## Qué trae

- **Edición por capas** al cuadro exacto: cuchilla, ripple, roll, slip, slide y rango; edición de tres y cuatro puntos con insertar, sobrescribir, reemplazar, ajustar y superponer; grupos, colores de clip, búsqueda y cierre de huecos, aviso y arreglo de desincronía; multicámara y sincronía de audio externo por forma de onda. Todo se deshace con Ctrl+Z, con autoguardado y recuperación.
- **Velocidad y reversa:** constante o por curva, congelar cuadro y reversa exacta, con el tono del audio conservado.
- **Color e imagen en la GPU:** ruedas, curvas (también de tono y saturación), corrección secundaria por tono/saturación/luma, monitores en tiempo real (forma de onda, desfile RGB, vectorscopio e histograma), LUT `.cube`, croma, desenfoques de lente, direccional y radial, grano, resplandor, aberración, reducción de ruido temporal, y pila de efectos con A/B y presets.
- **Transformación y animación:** ancla, inclinación y volteo, keyframes con curvas Bézier, rebote y elástico, trayectorias en el visor, desenfoque de movimiento, editor de gráficas y animaciones de un clic.
- **Máscaras y seguimiento:** pluma Bézier y formas, borde por vértice, combinaciones y máscara por efecto; tracker de 1, 2 o 4 puntos con precisión subpíxel para pegar títulos, anclar esquinas con perspectiva, pixelar caras o estabilizar.
- **Audio profesional:** mezclador con buses, envíos, paneo y automatización; compresor con sidechain, anticipación y multibanda, limitador de pico verdadero, puerta, de-esser, reverb, delay, saturación, ecualizadores, cambio de tono y medidores LUFS.
- **Títulos y subtítulos:** tipografía completa, degradados, varios bordes y sombras, texto en arco, animación por letra, palabra o línea, karaoke; subtítulos SRT/VTT escritos a mano o generados por la IA.
- **Transiciones:** 45 en el catálogo, con alineación, curva de avance, borde, desenfoque de movimiento y cruces de audio de potencia constante.
- **Exportación:** MP4, MOV, MKV, MXF y WebM; H.264, HEVC y AV1 por GPU (NVIDIA, AMD e Intel), VP9, ProRes, DNxHR y FFV1; copia directa sin recodificar cuando se puede; LUFS, capítulos, entrelazado, video 360 y verificación del archivo final; cola con varias exportaciones a la vez.
- **IA local que se adapta al equipo:** subtítulos (Whisper), traducción, texto a voz, reducción de ruido, silencios y ritmo, estabilización, color automático, quitar fondo y reencuadre. Elige modelo y memoria según la GPU y la RAM, y avisa el tiempo estimado. Nada se envía a internet.
- **Complementos:** transiciones, filtros, looks de cámara log, sonidos, preajustes profesionales, video 360, fuentes, títulos, zonas seguras y módulos de IA, con catálogo firmado y la huella de cada archivo verificada.
- **Biblioteca:** carpetas, colecciones inteligentes, valoración, etiquetas, subclips, metadatos de cámara y avisos de medios repetidos o sin conexión.
- **Flujo de trabajo:** proxies automáticos, aceleración según la GPU, espacios de trabajo, atajos configurables, segunda pantalla y recorrido inicial.

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
