# Formatos compatibles

Qué abre Rodaje 1.0 y qué hace con cada formato, medido con la matriz de formatos (`tests/Rodaje.App.Tests/Matrix`) en un Ryzen 7 7435HS con RTX 3050 Laptop, FFmpeg LGPL 9.0.2, el 12-oct-2026.

## Cómo se prueba

Cada archivo se genera con el `ffmpeg.exe` del entorno (en la caché de pruebas, nunca en el repositorio). Los cuadros llevan su número dibujado y el audio son pitidos cada medio segundo. Sobre cada uno se comprueba esto:

1. **Importar.** La misma lectura que `ImportMediaAsync`: tamaño en pantalla, tasa, duración, audio y HDR.
2. **Miniaturas y forma de onda.**
3. **Reproducir y seek.** Se reproduce cuadro a cuadro y se hacen 17 saltos en desorden. En cada cuadro se lee el número dibujado.
4. **Reversa.** Los últimos 40 cuadros, por CPU y por GPU.
5. **Exportar.** Un proyecto con un corte, un tramo a 2× y otro en reversa se exporta a MP4 H.264 (NVENC) y a MKV FFV1 + FLAC. ffprobe comprueba cuadros, duración, audio y color, y se lee el número de cada cuadro exportado.

```powershell
$env:RODAJE_PRUEBAS_LARGAS = '1'
build\dotnet-local.cmd test tests\Rodaje.App.Tests --filter Categoria=Matriz
```

Las tablas de cada corrida quedan en `TestResults\matriz\` (`tabla.md`, `ia.md` y `autor.md`). Para usar otros videos reales, `RODAJE_VIDEOS_AUTOR` apunta a su carpeta; se abren solo para leer.

## Video

En todos los formatos de esta tabla funcionan la importación, las miniaturas, la forma de onda, la reproducción, los 17 de 17 saltos al cuadro exacto, la reversa por CPU y por GPU y las dos exportaciones, sin perder ningún cuadro.

| Contenedor | Video | Audio | Decodifica | Reproducción |
|---|---|---|---|---|
| MP4 | H.264 8 bits 4:2:0 con B, 720p30 | AAC estéreo 48 kHz | GPU | 403 fps |
| MP4 | H.264 | MP3 mono 44,1 kHz | GPU | 329 fps |
| MP4 | H.264 sin audio | — | GPU | 329 fps |
| MP4 | H.264 3840×2160 a 60 | AAC | GPU | 71 fps |
| MP4 | H.264 1080×1920 (vertical) | AAC | GPU | 195 fps |
| MP4 | H.264 con rotación de 90° en metadatos | AAC | GPU | 230 fps |
| MP4 | H.264 VFR de teléfono (marcas cada 1/90000 s) | AAC | GPU | 172 fps |
| MP4 | H.264 VFR con marcas repetidas | AAC | GPU | 305 fps |
| MP4 | HEVC Main 10 | AAC | GPU | 213 fps |
| MP4 | HEVC Main 10 HDR10 (PQ, BT.2020) | AAC | GPU | 75 fps |
| M4V | H.264 | AAC | GPU | 223 fps |
| MOV | H.264 | PCM 16 bits | GPU | 172 fps |
| MOV | H.264 a 23,976 | AAC | GPU | 185 fps |
| MOV | H.264 High 4:4:4 | AAC | CPU | 84 fps |
| MOV | HEVC Main 10 HLG | AAC | GPU | 78 fps |
| MOV | ProRes 422 HQ 1080p (10 bits) | PCM 24 bits | CPU | 14 fps |
| MOV | ProRes 4444 con alfa (12 bits) | PCM 24 bits | CPU | 19 fps |
| MKV | H.264 | FLAC 96 kHz | GPU | 363 fps |
| MKV | H.264 | AAC 5.1 | GPU | 339 fps |
| MKV | HEVC 8 bits | Opus | GPU | 445 fps |
| MKV | AV1 (SVT-AV1) | Opus | CPU (dav1d) | 179 fps |
| WebM | VP9 | Opus | GPU | 211 fps |
| MXF | DNxHR HQ 1080p25 4:2:2 | PCM 16 bits | CPU | 30 fps |
| AVI | MJPEG 4:2:0 | PCM 16 bits | CPU | 139 fps |
| MPEG-TS | MPEG-2 | AC-3 5.1 | GPU | 283 fps |
| MP4 (teléfono del autor) | HEVC Main 10 1080×1920 | AAC 44,1 kHz | GPU | 534–695 fps |

En los videos del autor, que no llevan número dibujado, cada salto se compara con el cuadro que entrega `ffmpeg.exe`: la diferencia media es de 0,38 niveles o menos.

## Audio y lo que no entra

| Formato | Resultado |
|---|---|
| WAV PCM 16 bits 48 kHz y 24 bits 96 kHz, FLAC mono 44,1 kHz, MP3 44,1 kHz, M4A AAC, Opus en Ogg, AC-3 5.1 | Importa, dibuja la forma de onda y exporta: se remuestrea a 48 kHz estéreo y el 5.1 se mezcla a estéreo |
| PNG, JPEG, WebP y secuencias de imágenes | No se importan, con el motivo: «es una imagen fija y Rodaje todavía no usa imágenes como clips» |
| H.264 4:2:2 | No se pudo generar: el FFmpeg LGPL no trae x264 y NVENC no codifica 4:2:2. El 4:2:2 se probó con ProRes y DNxHR |

## IA y análisis

Cada medio lleva 12 s de voz real: *El clavo*, de Alarcón, leído para LibriVox, de dominio público. Primero se abre en una sesión de verdad. Después pasa por cada herramienta, y cada resultado entra como un solo paso del historial. Al final se deshace todo y se comprueba que el proyecto vuelve a ser idéntico al de partida.

Se probó en MP4 H.264/AAC, MKV HEVC 10 bits/Opus, WebM VP9/Opus, MOV ProRes 422 y 4444 (con PCM a 96 kHz), MXF DNxHR, AVI MJPEG/MP3 mono, MP4 rotado con AAC 5.1, WAV a 96 kHz y FLAC mono. Todas las herramientas aceptan todos esos formatos. Las de imagen se omiten en los que solo tienen audio.

| Herramienta | Resultado | Tiempo |
|---|---|---|
| Subtítulos (Whisper large-v3-turbo, GPU) | 14 a 18 palabras en 12 s | 4–6 s |
| Reducir ruido (DeepFilterNet3) | 12 s limpios | 3–8 s |
| Voz y ducking (Silero VAD) | 4 tramos, 7,1 s de voz, 8 puntos de ganancia | < 1 s |
| Ritmo | 120 por minuto en la pista de pitidos | < 0,5 s |
| Quitar silencios | 5 silencios, 4,4 s | < 0,3 s |
| Estabilizar | 300–361 cuadros | 12–27 s |
| Color automático | 16 muestras | 1–3 s |
| Quitar fondo (MODNet, 3 s) | 75–90 cuadros de máscara | 8–18 s |
| Reencuadre a 9:16 (RT-DETR, 3 s) | 18 cuadros analizados | 3–8 s |
| Traducción es→en (Qwen3 4B) | Los subtítulos de la pista | 16 s (97 s la primera carga) |
| Texto a voz (Kokoro) | 4,8 s de voz a 24 kHz | 8–13 s |
| CinemaDNG sintético (25 DNG) | ProRes 422 HQ, que después se estabiliza | 1–3 s |

## Límites conocidos

- **HDR (PQ y HLG).** No hay mapeo de tonos: se decodifica y se exporta como BT.709, con los colores apagados. Desde esta versión, la importación lo avisa.
- **Intermedios en la CPU.** ProRes, DNxHR, H.264 4:4:4 y AV1 se decodifican en la CPU. En 1080p, ProRes y DNxHR van a 14–30 fps, así que para editarlos con fluidez conviene usar proxies. La GPU del equipo decodifica AV1, pero FFmpeg usa dav1d.
- **VFR muy irregular.** La secuencia toma la tasa mediana medida, por ejemplo 90000/3061 (29,4 fps). Cada índice muestra el cuadro más cercano y puede repetir o saltar uno.
- **MKV con audio más largo que el video.** El número de cuadros sale de la duración del contenedor: los índices de más repiten el último cuadro.
- **`ReverseFrameReader` con D3D11VA.** Si lo retenido no cabe en el pool del decodificador, entrega cuadros reciclados. La app lo dimensiona en `OfflineReaders`; quien lo use directamente debe pasar `MaxRetained`.
