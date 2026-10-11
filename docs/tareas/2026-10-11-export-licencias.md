# Tarea 21: licencias de la exportación

Rama `claude/export-licencias`, PR #15. Solo documentación: `LICENCIAS.md`, `docs/legal.md` y esta entrega. No es asesoramiento legal.

## Hecho

- **`LICENCIAS.md`, sección «Bibliotecas de la exportación (dentro de las DLL de FFmpeg)»:** por cada biblioteca externa que usa la exportación (SVT-AV1, libaom, libvpx, libopus, LAME, kvazaar, OpenH264, libwebp, zlib, encabezados de NVENC, AMF y libvpl): codificador de FFmpeg, commit que fija `scripts.d/` de BtbN (commit `6c9aec5f` de FFmpeg-Builds), licencia leída en el repositorio oficial en ese commit, y qué obliga. Más una nota sobre los codificadores propios de FFmpeg (ProRes, DNxHR, CineForm, FFV1, PNG, MJPEG, QT Animation, GIF, TIFF, EXR, AAC, FLAC, ALAC, PCM) y Media Foundation.
- **`docs/legal.md`, sección «Exportación: codificadores, licencias y patentes»:** tabla por `ExportCodec` y codificador (quién codifica, licencia, si se redistribuye, patentes), siete obligaciones de copyright y una revisión de quién cubre las patentes de H.264, HEVC y AAC.
- **`docs/legal.md`, «Lista de comprobación de la exportación»:** 13 puntos al final del documento, tras la lista general.
- Se comprobó qué hay de verdad en la compilación: `docs/ffmpeg-configure.txt` y `libavcodec/allcodecs.c` del commit `2a571b6068` de FFmpeg tienen todos los codificadores que nombran el contrato y el catálogo de la tarea 13. FLAC es el nativo, porque no hay `--enable-libflac`.

## Hallazgos que importan

1. **La mitigación de patentes no está demostrada, y AMD la contradice.** `legal.md` («Patentes de códecs»), D-028 y `fase-6.md` dicen que los fabricantes de la GPU y Microsoft «cubren» las patentes de H.264 y HEVC. El `LICENSE.txt` de AMF, en el commit que compila BtbN, dice lo contrario: AMD no concede licencia sobre AVC, HEVC, AAC ni MP3, y las regalías las paga quien lo usa. Intel dice que los códecs MPEG «may require licenses … including Intel». En lo de NVIDIA no se pudo leer el acuerdo vigente, y las páginas de Microsoft sobre Media Foundation no tratan el tema. No toqué el texto anterior ni D-028; lo anoté en la sección nueva.
2. **Obligaciones nuevas en los avisos:** el texto completo de la AOM Patent License 1.0 (SVT-AV1 y libaom), los `PATENTS` de Google (libvpx y libwebp), la atribución a LAME con enlace y el crédito al IJG que pide el `LICENSE.md` de FFmpeg (los usan DNxHR y ProRes por `fdctdsp`).
3. **El código fuente correspondiente** debe incluir también el de cada biblioteca externa, porque van estáticas dentro de `avcodec-63.dll` (LAME, que es LGPL, sobre todo).

## Sin verificar (marcado así en `legal.md`)

El entorno de la nube no pudo abrir ffmpeg.org, via-la.com, accessadvance.com, openh264.org, learn.microsoft.com ni aomedia.googlesource.com; las páginas de Microsoft se leyeron en su repositorio de GitHub (`MicrosoftDocs/win32`). Quedan sin verificar en la fuente primaria:
- las tarifas y exenciones de Via LA (AVC y AAC) y de Access Advance (HEVC);
- el texto vigente de la licencia de patentes del binario de Cisco OpenH264;
- la cláusula de patentes del acuerdo del SDK de NVIDIA;
- el fin del programa de licencias de MP3 (2017);
- si los pools de Sisvel (AV1 y VP9) alcanzan al software;
- las patentes o marcas de ProRes, DNxHR y CineForm;
- la licencia de libaom en su repositorio oficial (leída en el espejo `m-ab-s/aom`, sin el commit fijado).

## Pendiente

- **Decisión de Daniel (con alguien de licencias) antes de publicar:** quién cubre las patentes de H.264, HEVC y AAC al codificar con la GPU, con Media Foundation y con el `aac` de FFmpeg, a la vista de la nota de AMD. Puede acabar en una D nueva y en corregir «Patentes de códecs», D-028 y `fase-6.md` (caminos que esta tarea no podía tocar).
- Generar `THIRD-PARTY-NOTICES.txt` con todo lo de la tabla, que sigue sin existir.
- Leer en su fuente cada punto «sin verificar».

## Decisiones

- No se cambió ningún texto anterior de `legal.md` ni de `LICENCIAS.md`: solo se añadieron secciones. La contradicción con «Patentes de códecs» se explica en la sección nueva.
- Las versiones son los commits de `scripts.d/` de BtbN en el commit de la release fijada, no la última de cada proyecto.

## Choques

- La rama `claude/export-codificadores` (tarea 13, sin unir) declara en `ExportEncoderCatalog` las licencias `BSD-3-Clause-Clear` (SVT-AV1), `BSD-2-Clause` (libaom y OpenH264) y `BSD-3-Clause` (libvpx y kvazaar), que coinciden con lo verificado. Su texto «Controlador del fabricante» para el hardware no dice nada de patentes; si la interfaz llegara a afirmar que el hardware «cubre» las patentes, chocaría con el hallazgo 1.
- No toca código ni pruebas, así que no cambia el número de pruebas omitidas.

## Qué debe comprobar Daniel

- Abrir desde Windows los enlaces marcados «sin verificar» (sobre todo Via LA, Access Advance y el acuerdo de NVIDIA) y confirmar o corregir.
- Con la compilación instalada: `.entorno\ffmpeg\bin\ffmpeg.exe -hide_banner -encoders` debe listar `libsvtav1`, `libaom-av1`, `libvpx-vp9`, `libkvazaar`, `libopenh264`, `libopus`, `libmp3lame`, `libwebp_anim`, `h264_nvenc`, `h264_amf`, `h264_qsv`, `h264_mf`, `aac_mf`, `prores_ks`, `dnxhd`, `cfhd` y `ffv1`, y ningún `libx264`, `libx265` ni `libfdk_aac`.
- Decidir el punto de patentes de arriba antes de cualquier versión pública.
