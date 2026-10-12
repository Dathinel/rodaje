# Tarea 21: licencias de la exportación

Rama `claude/export-licencias`, PR #15. Solo documentación: `LICENCIAS.md`, `docs/legal.md` y esta entrega. No es asesoramiento legal.

## Segunda vuelta (12-oct): evaluada contra la GPLv3 de D-032

La revisión pidió rehacerla sobre los `LICENCIAS.md` y `docs/legal.md` actuales de main, con una columna de compatibilidad con la GPLv3 por fila, el análisis de LAME y de la AOM Patent License 1.0, y sin «mientras Rodaje no se venda». Sin merge de main (lo prohíben las reglas de la nube):

1. Commit `LICENCIAS.md y legal.md: volver a las versiones de main`: `git checkout origin/main -- LICENCIAS.md docs/legal.md`. Tras él, los dos archivos son idénticos a los de main.
2. Commit siguiente: se vuelven a añadir encima solo las secciones de la exportación, con estos cambios:
   - **`LICENCIAS.md`:** «Bibliotecas de la exportación» con la columna «Compatibilidad con la GPLv3» y dos filas nuevas (codificadores propios de FFmpeg y Media Foundation, que antes iban en un párrafo). La cabecera de main decía que todo lo redistribuido era «MIT, BSD, Apache-2.0 o LGPL v3»; se amplió para nombrar zlib, IJG y LAME y para remitir a las licencias de patentes pendientes. La fila de FFmpeg remite a la sección nueva.
   - **`docs/legal.md`, «Compatibilidad con la GPLv3», punto g) nuevo:** análisis por biblioteca con citas literales de la GPLv3 (Secciones 10, 11 y 12, del `LICENSE` del repositorio), de la Library GPL 2 de LAME y de los archivos de patentes de AOM y de Google.
   - **`docs/legal.md`, «Exportación: codificadores, licencias y patentes»:** la tabla lleva la columna «Compatibilidad con la GPLv3»; la «Consecuencia» ya no dice «mientras Rodaje no se venda» y remite al punto d) (la GPLv3 permite vender) y a `pendientes-legales.md`.
   - **Lista de comprobación de la exportación:** un punto nuevo, «Compatibilidad con la GPLv3».

### Evaluación

| Biblioteca | Compatibilidad con la GPLv3 |
|---|---|
| SVT-AV1, libaom | Código BSD: sí. **AOM Patent License 1.0: pendiente del abogado.** Su terminación defensiva (1.3) alcanza demandas sobre cualquier implementación de AV1, más amplia que la de Apache-2.0 (que la FSF da por compatible). Es una concesión directa del titular al usuario (1.2) que Rodaje no impone, pero no se encontró ninguna valoración de la FSF. Si fuera una «further restriction», se quitan `libsvtav1` y `libaom-av1`. |
| LAME | Sí: «version 2 of the License, or (at your option) any later version» (encabezado de `lame.c`, r6835) → LGPL 2.1 o LGPL v3; además, la sección 3 de la Library GPL 2 permite pasar a la GPL. |
| libvpx, libwebp | Código sí; patentes de Google sí, a confirmar (terminación limitada a «these implementations», FSF `#WebM`). |
| libopus | Código sí; licencias de patentes de la IETF pendientes del abogado (no se leyeron). |
| kvazaar, OpenH264 (desde el código), zlib, encabezados MIT de NVENC/AMF/libvpl, ProRes/DNxHR y demás de FFmpeg (LGPL v3), IJG | Sí. |
| Binario de Cisco OpenH264 | No se usa; términos sin leer: pendiente del abogado si se llegara a usar. |
| Media Foundation, controladores de GPU | No se distribuyen; «System Libraries» o no, se confirma con el punto b) (poco peso). |

## Hecho en la primera vuelta (11-oct), que sigue en pie

- Por cada biblioteca externa de la exportación: codificador de FFmpeg, commit que fija `scripts.d/` de BtbN (commit `6c9aec5f` de FFmpeg-Builds), licencia leída en el repositorio oficial en ese commit, y qué obliga.
- Tabla por `ExportCodec` y codificador, obligaciones de copyright y revisión de quién cubre las patentes de H.264, HEVC y AAC.
- Se comprobó en `docs/ffmpeg-configure.txt` que la compilación lleva `--enable-libaom`, `libsvtav1`, `libvpx`, `libopus`, `libmp3lame`, `libkvazaar`, `libopenh264`, `libwebp`, `libvpl`, `amf` y `zlib`, con `--enable-version3`.

## Hallazgos que importan

1. **La mitigación de patentes no está demostrada, y AMD la contradice.** `legal.md` («Patentes de códecs»), D-028 y `fase-6.md` dicen que los fabricantes de la GPU y Microsoft gestionan o «cubren» las patentes de H.264 y HEVC. El `LICENSE.txt` de AMF dice que AMD no concede licencia sobre AVC, HEVC, AAC ni MP3. Con la GPLv3 cualquiera puede vender Rodaje, así que el riesgo no se acota a un uso no comercial.
2. **La AOM Patent License 1.0 es el único punto de compatibilidad abierto** con peso (SVT-AV1 y libaom van en la DLL que ya se distribuye).
3. **Obligaciones nuevas en los avisos:** el texto completo de la AOM Patent License 1.0, los `PATENTS` de Google, la atribución a LAME y el crédito al IJG.
4. **El código fuente correspondiente** debe incluir el de cada biblioteca externa, porque van estáticas dentro de `avcodec-63.dll`.

## Sin verificar (marcado así en los documentos)

El entorno de la nube no pudo abrir gnu.org, openh264.org, aomedia.googlesource.com, via-la.com, accessadvance.com ni learn.microsoft.com. Quedan sin verificar en la fuente primaria:
- las anclas de compatibilidad de la lista de la FSF (leídas en los datos de `spdx/fsf-api`, rama `gh-pages`), y si la entrada `#WebM` evalúa también `PATENTS`;
- las condiciones de las licencias de patentes de Opus ante la IETF;
- los términos del binario de Cisco OpenH264;
- la licencia de libaom en su repositorio oficial (leída en el espejo `m-ab-s/aom`);
- las tarifas de Via LA y Access Advance, la cláusula de patentes del SDK de NVIDIA, el fin del programa de MP3, el alcance de los pools de Sisvel y las patentes o marcas de ProRes, DNxHR y CineForm.

Sí se leyeron en la fuente en esta vuelta: `PATENTS.md` y `LICENSE.md` de SVT-AV1 en el commit fijado (GitLab), `PATENTS` de libvpx en el commit fijado, y `COPYING`, `LICENSE` y `libmp3lame/lame.c` de LAME en la revisión 6835 (SourceForge).

## Pendiente

- **Para el abogado** (cuando se haga la consulta de `pendientes-legales.md`): la terminación defensiva de AOM frente a la Sección 10; las licencias de Opus ante la IETF; quién cubre las patentes de H.264, HEVC y AAC. `pendientes-legales.md` no está en los caminos de esta tarea: conviene añadir allí una casilla para el punto g) de `legal.md`.
- Generar `THIRD-PARTY-NOTICES.txt` con todo lo de la tabla.
- Leer en su fuente cada punto «sin verificar».

## Decisiones

- No se cambió el texto anterior de main salvo la cabecera de `LICENCIAS.md` y una frase de la fila de FFmpeg, que la sección nueva contradecía.
- La compatibilidad se evalúa en una columna por fila, más el punto g) para lo que necesita citas; las licencias de patentes se separan de las de copyright.
- Las versiones son los commits de `scripts.d/` de BtbN en la release fijada, no la última de cada proyecto.

## Choques

- La rama tiene su base antes de D-030 y D-032 y no se hizo merge de main; `git diff origin/main -- LICENCIAS.md docs/legal.md` muestra solo lo añadido por esta tarea. Al unir, si main tocó esos archivos después de `origin/main` del 12-oct, el choque se resuelve tomando main y volviendo a aplicar las secciones de la exportación.
- La rama `claude/export-codificadores` (tarea 13) declara en `ExportEncoderCatalog` licencias que coinciden con las verificadas. Si la interfaz llegara a afirmar que el hardware «cubre» las patentes, chocaría con el hallazgo 1.
- No toca código ni pruebas: no cambia el número de pruebas omitidas.

## Qué debe comprobar Daniel

- Abrir desde Windows https://www.gnu.org/licenses/license-list.html y confirmar las anclas de la columna de compatibilidad (`#clearbsd`, `#FreeBSD`, `#ModifiedBSD`, `#WebM`, `#LGPLv2.1`, `#LGPLv3`, `#ZLib`, `#Expat`, `#ijg`, `#apache2`).
- Abrir los enlaces marcados «sin verificar» (sobre todo Via LA, Access Advance, el acuerdo de NVIDIA y openh264.org).
- Con la compilación instalada: `.entorno\ffmpeg\bin\ffmpeg.exe -hide_banner -encoders` debe listar `libsvtav1`, `libaom-av1`, `libvpx-vp9`, `libkvazaar`, `libopenh264`, `libopus`, `libmp3lame`, `libwebp_anim`, `h264_nvenc`, `h264_amf`, `h264_qsv`, `h264_mf`, `aac_mf`, `prores_ks`, `dnxhd`, `cfhd` y `ffv1`, y ningún `libx264`, `libx265` ni `libfdk_aac`.
- Llevar al abogado el punto g) de `legal.md` junto con los demás de «Compatibilidad con la GPLv3».
