# Fondo legal de Rodaje

Rodaje vive hoy en un repositorio privado, pero se construye como si fuera a publicarse mañana. Este documento explica la situación legal del programa, qué obligaciones traen sus dependencias y qué hay que comprobar, punto por punto, antes de hacer público el repositorio o de entregar un ejecutable a otra persona. No es asesoría legal: es el resultado de leer las licencias y las guías públicas de cada proyecto, con las fuentes citadas para que cualquiera pueda verificarlas.

## Licencia del código propio

Todo el código escrito para Rodaje se publica bajo la licencia MIT, con el aviso `Copyright (c) 2026 Daniel (Dathinel)` en el archivo `LICENSE` de la raíz. MIT permite a cualquiera usar, copiar, modificar y redistribuir el programa, incluso con fines comerciales, con la única condición de conservar el aviso de copyright y el texto de la licencia. Al ser tan permisiva, es compatible con enlazar dinámicamente bibliotecas LGPL, pero no con incorporar código GPL: si algún día se copiara código literal de un proyecto GPL, todo el programa distribuido tendría que pasar a GPL. Por eso la regla es estudiar el diseño de los proyectos GPL sin tomar su código, y detenerse a consultar si alguna vez se plantea lo contrario.

## Sala limpia

Los editores de código abierto estudiados (MLT, Kdenlive, Shotcut, OpenShot, Olive y LosslessCut, con sus licencias en `docs/archivo/investigacion.md`) se analizaron solo a nivel de arquitectura y comportamiento. No se copió, tradujo ni adaptó código de ninguno de ellos. De los programas comerciales que inspiran la experiencia de uso solo se toman ideas de producto: nunca se descompilan ni se analizan sus binarios, y nunca se extraen sus recursos (efectos, plantillas, fuentes, música, íconos). Todos los recursos que se incluyan en Rodaje deben ser propios o tener una licencia que permita redistribución y uso comercial, registrada en `LICENCIAS.md`.

## FFmpeg y la LGPL

FFmpeg es la pieza con más obligaciones. Su licencia por defecto es la LGPL 2.1 o posterior, pero se convierte en GPL si se compila con `--enable-gpl` y en no redistribuible si se compila con `--enable-nonfree` (https://ffmpeg.org/legal.html). Rodaje usará las compilaciones `lgpl-shared` de BtbN (https://github.com/BtbN/FFmpeg-Builds). Según el archivo de variantes del propio proyecto (https://github.com/BtbN/FFmpeg-Builds/blob/master/variants/defaults-lgpl.sh), esas compilaciones usan `--enable-version3` y el archivo de licencia `COPYING.LGPLv3`, de modo que la licencia efectiva de las DLL distribuidas es la LGPL versión 3. Excluyen las bibliotecas que solo admiten GPL, como libx264 y libx265.

La página legal de FFmpeg ofrece una lista de comprobación para cumplir la LGPL. Aplicada a Rodaje significa lo siguiente:

1. **Ninguna compilación distribuida lleva `--enable-gpl` ni `--enable-nonfree`.** Se comprueba leyendo la línea de configuración que imprime `ffmpeg -version` o la que acompaña a la compilación, y verificando que no aparezcan bibliotecas GPL como libx264 o libx265. Además, `FfmpegLibrary` se niega a arrancar si `avcodec_license()` no empieza por «LGPL» o si la configuración de alguna biblioteca cargada lleva `--enable-gpl` o `--enable-nonfree`, y cuando junto al ejecutable hay una carpeta `ffmpeg\` ignora `RODAJE_FFMPEG`, así que la distribución solo carga el FFmpeg que trae. `FfmpegExecutable` aplica la misma regla al `ffmpeg.exe` de los proxies (ver más abajo).
2. **Enlace dinámico.** Las bibliotecas de FFmpeg se distribuyen como DLL separadas (`avcodec-*.dll`, `avformat-*.dll`, etc.) que Rodaje carga en tiempo de ejecución a través de FFmpeg.AutoGen. Nunca se enlazan estáticamente ni se empaquetan dentro del ejecutable de AOT.
3. **DLL sin renombrar ni ofuscar.** Se conservan los nombres originales, de modo que el usuario pueda reemplazarlas por otra compilación compatible, que es el derecho central de la LGPL.
4. **Código fuente correspondiente.** Junto a cada versión distribuida se ofrece el código fuente exacto de las DLL incluidas, o una oferta escrita de entregarlo. Como las compilaciones de BtbN se publican con etiqueta y fecha, se registra la URL exacta de la versión usada y su hash, y se archiva el tarball del código fuente correspondiente.
5. **Línea de configuración documentada.** Se incluye un archivo de texto con la línea de `configure` de la compilación distribuida.
6. **Crédito en "Acerca de".** El cuadro "Acerca de" de Rodaje menciona que el programa usa bibliotecas de FFmpeg bajo la LGPL, con enlace a https://ffmpeg.org y al código fuente.
7. **EULA y ingeniería inversa.** Si algún día Rodaje tiene términos de uso, no pueden prohibir la ingeniería inversa de la parte LGPL ni reclamar la propiedad de FFmpeg; deben indicar quiénes son sus autores. La licencia MIT actual no impone ninguna prohibición de ese tipo.
8. **Mismo cuidado con las bibliotecas que FFmpeg incluye.** Cada biblioteca externa compilada dentro de las DLL de BtbN tiene su propia licencia, que se revisa y se lista en `THIRD-PARTY-NOTICES.txt` en la versión en que FFmpeg se distribuya.

### Compilación fijada y su código fuente

Desde la fase 2, `herramientas\preparar-entorno.ps1` instala en `.entorno\ffmpeg` esta compilación exacta, verificando su huella:

- **Archivo:** `ffmpeg-n9.0.2-17-g2a571b6068-win64-lgpl-shared-9.0.zip`, de la release https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-09-30-13-08, con SHA-256 `7157177b8a6cb2174c1650ba8c71b363f2c78cba5330f88c4c02cf5b2b880646` (el de `checksums.sha256` de esa release, recalculado al descargar). Se eligió una release de fin de mes porque BtbN borra las diarias a las dos semanas y conserva las mensuales.
- **Licencia efectiva:** LGPL-3.0-or-later. `ffmpeg -buildconf` muestra `--enable-version3` y ni `--enable-gpl` ni `--enable-nonfree`; además desactiva explícitamente libx264, libx265, libxvid y libfdk-aac. La línea completa está en [`docs/ffmpeg-configure.txt`](ffmpeg-configure.txt).
- **Código fuente de FFmpeg:** la cadena de versión `n9.0.2-17-g2a571b6068` corresponde al commit `2a571b606854520cf89804d8030c8b328e621689` del repositorio oficial (17 commits después de la etiqueta `n9.0.2`, en la rama `release/9.0`): https://git.ffmpeg.org/ffmpeg.git y su espejo https://github.com/FFmpeg/FFmpeg/commit/2a571b606854520cf89804d8030c8b328e621689.
- **Scripts de compilación:** la etiqueta de la release apunta al commit `6c9aec5fc9a72ec3abedd1fa84db141fa18cf52b` de BtbN/FFmpeg-Builds (https://github.com/BtbN/FFmpeg-Builds/tree/autobuild-2026-09-30-13-08), que fija también las versiones de las bibliotecas externas compiladas dentro de las DLL.
- **Antes de distribuir:** archivar en un lugar propio el tarball de ese commit de FFmpeg y el de los scripts de BtbN (los enlaces de terceros pueden desaparecer), y ofrecerlos junto a `dist\` o mediante oferta escrita.

### `ffmpeg.exe` para los proxies

Desde la fase 3, `dist\` lleva también el `ffmpeg.exe` de esa misma compilación en `ffmpeg\`, junto a las DLL, más `avfilter-12.dll` y `avdevice-63.dll`, que solo él importa (`ffprobe.exe` y `ffplay.exe` no se copian). Rodaje lo ejecuta como un proceso aparte para generar los proxies de vista previa, igual que hace LosslessCut: le pasa los argumentos con `ProcessStartInfo.ArgumentList` y lee su avance de `-progress pipe:1`. Ejecutar un programa LGPL como proceso separado no lo enlaza con Rodaje, así que no cambia la licencia del programa; las obligaciones son las mismas que para las DLL: el exe va sin modificar ni renombrar, con su licencia, su código fuente correspondiente (es el mismo commit de FFmpeg y los mismos scripts de BtbN) y su línea de configure.

La guarda LGPL cubre también el exe. `FfmpegExecutable` solo acepta el `ffmpeg.exe` de la carpeta de la que `FfmpegLibrary` cargó las DLL, nunca uno del `PATH`, y antes de usarlo comprueba que `-buildconf` no lleve `--enable-gpl` ni `--enable-nonfree`, que `-L` declare la GNU Lesser General Public License y que su libavcodec sea la 63; si algo falla, los proxies quedan desactivados con el motivo en español. `herramientas\app.ps1` repite la comprobación de `-buildconf` antes de copiarlo a `dist\`.

Los proxies se codifican en H.264 con NVENC, después con Media Foundation y solo como último recurso con la OpenH264 compilada dentro de las DLL. Son archivos intermedios que se quedan en `%LOCALAPPDATA%\Rodaje\proxies` y nunca se exportan ni se entregan; la exportación usa siempre el original.

Las DLL incluyen bibliotecas externas con licencias propias (dav1d, libvpx, opus, openh264, SVT-AV1, kvazaar, libaom, freetype, harfbuzz, fribidi, libass, lame, zimg, zlib y otras; la lista sale de la línea de configure). Ninguna es GPL-only, pero el punto 8 de la lista anterior sigue pendiente. La OpenH264 de estas DLL está compilada desde el código fuente, así que no trae la licencia de patentes que Cisco concede solo a su binario oficial; para exportar H.264 se prefieren NVENC o Media Foundation (ver "Patentes de códecs").

Una advertencia específica del equipo de desarrollo: el FFmpeg 9.0 de gyan.dev instalado con winget es una compilación GPL. Sirve para pruebas y para preparar material, pero nunca debe copiarse a `dist\`, incluirse en un paquete ni entregarse junto con Rodaje.

## Patentes de códecs

Las licencias de software no resuelven las patentes. H.264/AVC y H.265/HEVC son formatos cubiertos por patentes administradas por consorcios: el programa AVC/H.264 de Via LA (https://www.via-la.com/licensing-2/avc-h-264/) y los programas de Access Advance para HEVC (https://accessadvance.com/), que además absorbió el antiguo programa HEVC/VVC de Via LA. La propia página legal de FFmpeg reconoce que no puede afirmar si su código usa algoritmos patentados y recuerda que el riesgo depende de la jurisdicción y del uso comercial.

Rodaje mitiga este riesgo de tres formas. Primero, la codificación H.264 y HEVC se delega en los codificadores por hardware de la GPU (por ejemplo NVENC) o en los de Media Foundation incluidos en Windows, cuyos fabricantes gestionan sus propias licencias de patentes. Segundo, se priorizan formatos libres de regalías como AV1, VP9 y Opus cuando el usuario no necesite un formato concreto. Tercero, el programa no se vende ni se distribuye comercialmente mientras no se haga una revisión legal específica. Nada de esto es asesoría legal: si Rodaje se distribuyera comercialmente, habría que consultar a un abogado de propiedad intelectual.

## Exportación: codificadores, licencias y patentes

Revisado el 11 de octubre de 2026 para la fase 6 (D-028, tarea 21 de la nube). **No es asesoramiento legal:** recoge lo que dicen las licencias y las páginas oficiales, con su enlace, y marca como «sin verificar» lo que no se pudo leer en la fuente primaria. Las licencias de copyright de cada biblioteca, con el commit que fija BtbN, están en `LICENCIAS.md`, sección «Bibliotecas de la exportación».

La exportación de la v1 no añade binarios: usa el mismo `ffmpeg.exe` y las mismas DLL LGPL de los proxies. Todo lo que puede codificar ya viaja hoy en `dist\`, aunque el usuario nunca lo elija, porque las bibliotecas externas van estáticas dentro de `avcodec-63.dll`. Que un codificador exista en la compilación se comprobó en la línea de configure (`docs/ffmpeg-configure.txt`) y en `libavcodec/allcodecs.c` del commit `2a571b6068` de FFmpeg; que funcione en el equipo lo decide la sonda de la tarea 13.

### Qué codifica cada cosa y qué obliga

| Códec (`ExportCodec`) | Codificador | Quién lo hace | Licencia del código | ¿Lo redistribuye Rodaje? | Patentes del formato |
|---|---|---|---|---|---|
| H.264 | `h264_nvenc`, `h264_amf`, `h264_qsv` | GPU (NVIDIA, AMD, Intel) | Encabezados MIT dentro de FFmpeg; el codificador es el controlador | Solo los encabezados compilados en `avcodec`; el controlador no | Pool AVC/H.264 de Via LA. Ver «Quién cubre las patentes» más abajo |
| H.264 | `h264_mf` | Media Foundation de Windows | Parte de Windows | No | Igual |
| H.264 | `libopenh264` (no por defecto) | CPU | BSD-2-Clause | Sí, dentro de `avcodec` | Sin cobertura: la licencia de Cisco es solo para su binario |
| HEVC | `hevc_nvenc`, `hevc_amf`, `hevc_qsv`, `hevc_mf` | GPU o Media Foundation | Igual que H.264 | Igual | Pool HEVC Advance de Access Advance y titulares fuera de pools |
| HEVC | `libkvazaar` (no por defecto) | CPU | BSD-3-Clause | Sí | Sin cobertura |
| AV1 | `libsvtav1`, `libaom-av1` | CPU | BSD 3-Clause Clear / BSD-2-Clause, más la AOM Patent License 1.0 | Sí | Licencia de patentes de AOM sin regalías; Sisvel ofrece además un pool de AV1 |
| AV1 | `av1_nvenc`, `av1_amf`, `av1_qsv` | GPU | Encabezados MIT | Solo encabezados | Igual que AV1 |
| VP9 | `libvpx-vp9` | CPU | BSD-3-Clause más la concesión de patentes de Google | Sí | Concesión de Google; Sisvel ofrece además un pool de VP9 |
| ProRes, DNxHR, CineForm, FFV1, MJPEG, PNG, QT Animation, GIF, TIFF, EXR | `prores_ks`, `dnxhd`, `cfhd`, `ffv1`, `mjpeg`, `png`, `qtrle`, `gif`, `tiff`, `exr` | CPU, código propio de FFmpeg | LGPL de FFmpeg (PNG, TIFF y EXR usan zlib) | Sí | Sin verificar (ver abajo) |
| WebP | `libwebp_anim` | CPU | BSD-3-Clause más la concesión de patentes de Google | Sí | Concesión de Google |
| AAC | `aac_mf` (preferido), `aac` | Media Foundation / código de FFmpeg | Windows / LGPL | `aac`, sí | Pool AAC de Via LA |
| Opus | `libopus` | CPU | BSD-3-Clause | Sí | Licencias sin regalías declaradas ante la IETF |
| MP3 | `libmp3lame` | CPU | LGPL (LAME) | Sí | Sin verificar (ver abajo) |
| FLAC, ALAC, PCM | `flac`, `alac`, `pcm_*` | CPU, código de FFmpeg | LGPL | Sí | Sin verificar; no se encontró ningún pool |

### Obligaciones de copyright que añade la exportación

1. **Avisos en `THIRD-PARTY-NOTICES.txt`** de cada biblioteca de la tabla de `LICENCIAS.md`: las BSD (SVT-AV1, libaom, libvpx, libopus, kvazaar, OpenH264, libwebp) piden reproducir el copyright, las condiciones y el descargo «en la documentación u otros materiales» del binario; las MIT (encabezados de NVENC, AMF y libvpl) piden incluir el aviso. Las fuentes son los archivos `LICENSE`/`COPYING` enlazados en `LICENCIAS.md`.
2. **AOM Patent License 1.0 completa** (SVT-AV1 y libaom): su cláusula 1.2.1.b exige, al distribuir en binario, incluir el texto de la licencia «in the documentation, legal notices, and/or other written materials». Tiene además una terminación defensiva (1.3): quien demande alegando que una implementación de AV1 infringe sus patentes pierde la licencia. Fuente: https://gitlab.com/AOMediaCodec/SVT-AV1/-/blob/master/PATENTS.md.
3. **Concesiones de patentes de Google** (`PATENTS` de libvpx y de libwebp): se incluyen junto a sus licencias. Fuentes: https://github.com/webmproject/libvpx/blob/main/PATENTS y https://github.com/webmproject/libwebp/blob/main/PATENTS.
4. **LAME:** su `LICENSE` pide reconocer que se usa LAME, con enlace a https://lame.sourceforge.io/, y enlazarla como biblioteca separada. Aquí va estática dentro de `avcodec-63.dll`, que es una DLL LGPL reemplazable; el código de LAME (revisión 6835) entra en el código fuente correspondiente que se ofrece con FFmpeg. Fuente: https://sourceforge.net/p/lame/svn/HEAD/tree/trunk/lame/LICENSE.
5. **IJG:** el `LICENSE.md` de FFmpeg (commit `2a571b6068`) dice que `jfdctfst.c`, `jfdctint_template.c` y `jrevdct.c` vienen de libjpeg y que, si solo se distribuyen ejecutables, hay que dar crédito al Independent JPEG Group en la documentación. `jfdctfst`/`jfdctint` se compilan con `fdctdsp`, que piden `dnxhd`, `prores_ks` y `prores_aw` (`configure`), y `jrevdct` con `idctdsp`. Fuente: https://github.com/FFmpeg/FFmpeg/blob/2a571b606854520cf89804d8030c8b328e621689/LICENSE.md.
6. **Código fuente correspondiente de FFmpeg:** ya era obligatorio por los proxies (sección «FFmpeg y la LGPL»); con la exportación no cambia, pero el archivo que se archive debe incluir también el código de cada biblioteca externa en el commit que fija `scripts.d/` de BtbN (commit `6c9aec5fc9a72ec3abedd1fa84db141fa18cf52b`), porque van dentro de la DLL.
7. **Ninguna biblioteca GPL ni nonfree:** la línea de configure lleva `--disable-libx264`, `--disable-libx265`, `--disable-libxvid` y `--disable-libfdk-aac`. El `LICENSE.md` de FFmpeg lista libx264, libx265 y libxvid entre las que obligan a `--enable-gpl`, y FDK AAC entre las incompatibles que piden `--enable-nonfree`. x264 y x265 quedan para un complemento aparte (D-028).

### Quién cubre las patentes de H.264, HEVC y AAC

Lo que dicen hoy `docs/legal.md` («Patentes de códecs»), D-028 y `docs/fase-6.md` es que, al codificar H.264 y HEVC con la GPU o con Media Foundation, «sus fabricantes gestionan sus propias licencias de patentes» o «las cubren». **Las fuentes primarias que se pudieron leer no lo confirman, y una lo contradice:**

- **AMD (AMF):** el `LICENSE.txt` del SDK de AMF, en el mismo commit que compila BtbN, empieza con una «Notice Regarding Standards»: AMD no concede licencia ni sublicencia sobre derechos de propiedad intelectual de estándares, «including … AVC/H.264; HEVC/H.265; AAC decode/FFMPEG; AAC encode/FFMPEG; VC-1; and MP3», y «you will pay any royalties due for such third party technologies». Fuente: https://github.com/GPUOpen-LibrariesAndSDKs/AMF/blob/6277e353fd625121a8f627b1d0540323ef372a49/LICENSE.txt.
- **Intel (QSV):** la documentación de `vpl-gpu-rt` dice que las implementaciones de códecs MPEG «may require licenses from various entities, including Intel Corporation». Fuente: https://github.com/intel/vpl-gpu-rt/blob/main/doc/HyperEncode_FeatureDeveloperGuide.md.
- **NVIDIA (NVENC):** **sin verificar.** Copias de terceros del acuerdo del SDK de NVIDIA dicen que obtener licencias de terceros para codificadores y decodificadores (menciona MPEG LA) es responsabilidad del licenciatario, pero no se pudo leer en el acuerdo vigente (https://developer.nvidia.com/nvidia-video-codec-sdk-license-agreement). Rodaje no usa el SDK de NVIDIA, solo los encabezados MIT de `nv-codec-headers` y el controlador del usuario.
- **Microsoft (Media Foundation):** las páginas de Microsoft sobre el codificador H.264, el HEVC y el AAC de Media Foundation describen perfiles y parámetros (H.264 Baseline y Main, High desde Windows 8; HEVC Main; AAC-LC) pero **no dicen nada** sobre si una aplicación que los llama hereda una licencia de patentes. Fuentes: https://learn.microsoft.com/windows/win32/medfound/h-264-video-encoder, https://learn.microsoft.com/windows/win32/medfound/codecobjects y https://learn.microsoft.com/windows/win32/medfound/aac-encoder. Las ediciones N de Windows no traen esos códecs hasta instalar el Media Feature Pack (https://support.microsoft.com/topic/78cfeea5-c7d9-4aa8-b38f-ee4df1392009).
- **Via LA, AVC/H.264:** su programa licencia a fabricantes de productos finales con codificadores o decodificadores; la tabla de tarifas da 0 USD por las primeras 100 000 unidades al año por grupo empresarial. **Sin verificar** en la página (el entorno de la nube no pudo abrirla; el dato viene de un resumen de búsqueda): https://www.via-la.com/licensing-2/avc-h-264/avc-h-264-license-fees.
- **Access Advance, HEVC Advance:** el resumen del programa de julio de 2026 está en https://accessadvance.com/wp-content/uploads/2025/07/HEVC-Advance-Program-Overview-July-2026.pdf. **Sin verificar** qué tarifa o exención aplicaría a un editor gratuito que llama a un codificador de la GPU o de Windows.
- **Via LA, AAC:** licencia a fabricantes de productos finales con codificador o decodificador AAC; según su FAQ, los flujos codificados en AAC no pagan por distribuirse. **Sin verificar** en la página: https://www.via-la.com/licensing-2/aac/.
- **OpenH264:** la licencia de patentes que paga Cisco cubre solo su binario descargado aparte por el usuario final, con atribución «OpenH264 Video Codec provided by Cisco Systems, Inc.». La OpenH264 de BtbN está compilada desde el código y no la tiene. **Sin verificar** en https://www.openh264.org/ (solo se leyeron copias del texto).
- **MP3:** Fraunhofer IIS y Technicolor dieron por terminado su programa de licencias de MP3 en abril de 2017. **Sin verificar** en una fuente primaria.
- **AV1 y VP9:** además de las concesiones sin regalías de AOM y de Google, Sisvel administra pools de patentes de AV1 y de VP9 dirigidos a dispositivos (https://www.sisvel.com/insights/sisvel-announces-first-licensees-of-av1-vp9-patent-pools/). **Sin verificar** si alcanzan al software.
- **ProRes, DNxHR, CineForm:** **sin verificar.** Los codificadores de FFmpeg son implementaciones propias del proyecto, no las de Apple, Avid o GoPro; no se buscó si sus dueños exigen licencia de patentes o de marca para codificar. Nombrar «ProRes» o «DNxHR» en la interfaz usa marcas ajenas (ver «Marcas»).
- **FFmpeg:** su página legal advierte que no puede asegurar que su código no use algoritmos patentados (https://ffmpeg.org/legal.html, ya citada arriba).

**Consecuencia para Rodaje:** la mitigación de «Patentes de códecs» (delegar H.264/HEVC en la GPU o en Windows) reduce el código propio que implementa formatos patentados, pero no está demostrado que traslade la licencia al editor, y AMD dice expresamente que no. Mientras Rodaje no se venda ni se distribuya comercialmente el riesgo práctico es bajo, pero antes de una versión pública hay que decidirlo con alguien de licencias. Es la decisión pendiente número 1 de la entrega de esta tarea.

## Modelos de inteligencia artificial

Solo se usan modelos cuya licencia permita expresamente la redistribución y el uso comercial, porque viajarán dentro de la carpeta del programa. Por cada modelo se registra en `LICENCIAS.md` su origen, su licencia, la URL de donde se descargó y su hash SHA-256, y el script de entorno verifica ese hash al descargarlo.

| Modelo | Uso previsto | Licencia | Estado | Fuente |
|---|---|---|---|---|
| Whisper (pesos de OpenAI) | Subtítulos automáticos | MIT, que cubre código y pesos | Apto | https://github.com/openai/whisper/blob/main/LICENSE |
| MODNet | Quitar fondo de personas | Apache-2.0 (el README extiende la licencia a "código, modelos y demos", salvo los GIF) | Candidato, confirmar pesos concretos | https://github.com/ZHKKKe/MODNet |
| U²-Net | Quitar fondo general | Apache-2.0 para el repositorio; los pesos no tienen licencia aparte y algunos se entrenaron con conjuntos de datos de terceros | Candidato, revisar conjuntos de datos | https://github.com/xuebinqin/U-2-Net |
| BiRefNet | Quitar fondo de alta calidad | MIT en el código y en la ficha de Hugging Face de los pesos | Candidato | https://huggingface.co/ZhengPeng7/BiRefNet |
| RMBG-1.4 y RMBG-2.0 de BRIA | Quitar fondo | Uso no comercial; lo comercial exige acuerdo aparte | Excluido | https://huggingface.co/briaai/RMBG-1.4 |

La licencia del repositorio no siempre cubre los pesos, y los pesos pueden heredar restricciones de los datos con que se entrenaron. Antes de incluir cualquiera de los candidatos hay que leer la licencia del archivo exacto que se va a descargar.

## Marcas

Rodaje no usa marcas ajenas en su código, su interfaz, sus íconos ni sus recursos. En particular, no aparecen CapCut, ByteDance, TikTok, Adobe, Photoshop ni Premiere en ningún elemento visible del programa. Los documentos de investigación pueden mencionar programas existentes de forma descriptiva, solo para explicar de dónde viene una idea. El propio nombre Rodaje se eligió para alejarse de "PhotoShot" y de Photoshop (ver `docs/decisiones.md`, D-001), y su registro debe verificarse antes de publicar.

## Privacidad y secretos

Rodaje no envía telemetría ni datos de uso a ningún servidor. Las funciones que usen servicios externos, como un asistente de lenguaje natural, serán opcionales, explicarán qué se envía y leerán su clave desde un archivo `.env` local. El `.env` está en `.gitignore` y nunca se sube; en el repositorio solo existe `.env.example`, con los nombres de las variables y sin valores.

## Avisos de terceros

`THIRD-PARTY-NOTICES.txt` contiene los avisos de licencia de todo componente de terceros que viaje dentro de `dist\`. Se regenera en cada versión, se copia junto a `Rodaje.exe` y debe acompañar siempre al ejecutable. `LICENCIAS.md` es la vista para desarrolladores: incluye también las dependencias que solo se usan para compilar o probar.

## Lista de comprobación antes de hacer público el repositorio

Esta lista se recorre completa, en orden, antes de cambiar la visibilidad del repositorio a pública o de entregar un ejecutable a terceros. Cada punto se marca con la fecha y el resultado.

- [ ] **Licencia.** `LICENSE` contiene el texto MIT con el aviso de copyright correcto y el año actualizado.
- [ ] **Sala limpia.** Se revisa que ningún archivo contenga código copiado de proyectos GPL o de terceros sin licencia compatible. Cualquier duda se resuelve antes de publicar.
- [ ] **Inventario de dependencias.** `LICENCIAS.md` lista todas las dependencias reales del proyecto (se compara con los archivos `packages.lock.json`), con su versión y licencia verificadas.
- [ ] **FFmpeg.** La compilación incluida es `lgpl-shared`, su línea de configuración no contiene `--enable-gpl` ni `--enable-nonfree`, las DLL y `ffmpeg.exe` vienen de esa misma compilación y conservan sus nombres, se archivó el código fuente exacto (o se redactó la oferta escrita), se registraron la URL y el hash de la compilación y el cuadro "Acerca de" da el crédito correspondiente.
- [ ] **Ningún binario GPL.** Se comprueba que `dist\` no contiene el FFmpeg de gyan.dev ni ningún otro binario GPL.
- [ ] **Avisos de terceros.** `THIRD-PARTY-NOTICES.txt` está regenerado para la versión y se copia junto a `Rodaje.exe`.
- [ ] **Licencias de Microsoft.** Se confirma que los binarios del Windows App SDK y, si se usan, de DirectML se redistribuyen según sus términos de licencia de software de Microsoft.
- [ ] **Modelos de IA.** Cada modelo incluido tiene licencia que permite redistribución y uso comercial, y su licencia, origen y hash están en `LICENCIAS.md`.
- [ ] **Patentes.** Se revisa que los perfiles de exportación por defecto usen codificadores del sistema o del hardware para H.264 y HEVC, y se mantiene la nota de patentes en la documentación.
- [ ] **Marcas.** Se busca en el código, la interfaz y los recursos cualquier marca ajena (CapCut, ByteDance, Adobe, etc.) y se elimina si aparece fuera de los documentos de investigación.
- [ ] **Nombre.** Se consulta "Rodaje" en las bases de marcas de la USPTO (https://tmsearch.uspto.gov), la EUIPO (https://euipo.europa.eu/eSearch/) y la Superintendencia de Industria y Comercio de Colombia (https://www.sic.gov.co), en las clases 9 y 42 de Niza.
- [ ] **Secretos.** Se escanea el historial completo del repositorio, no solo la última versión, con gitleaks (https://github.com/gitleaks/gitleaks), y se confirma que no aparece ningún `.env`, clave de API ni token. Si aparece alguno, se revoca la clave y se reescribe el historial antes de publicar.
- [ ] **Privacidad.** Se confirma que no hay telemetría y que toda función que envíe datos a un servicio externo es opcional y está explicada.
- [ ] **Políticas del repositorio.** Se quita la marca de "preparado para el futuro" de `SECURITY.md` y `CONTRIBUTING.md`, se activa el reporte privado de vulnerabilidades en GitHub y se revisa que el requisito de firma DCO esté vigente.

### Lista de comprobación de la exportación

Se recorre junto con la anterior antes de entregar un ejecutable que exporte. Cada punto se marca con la fecha y el resultado.

- [ ] **Bibliotecas reales.** Se comparan las bibliotecas de `ffmpeg -buildconf` de la compilación distribuida con la tabla «Bibliotecas de la exportación» de `LICENCIAS.md`, y los commits con los de `scripts.d/` de BtbN para esa release; si cambió la compilación, se vuelve a leer cada licencia.
- [ ] **Sin GPL ni nonfree.** `-buildconf` lleva `--disable-libx264`, `--disable-libx265`, `--disable-libxvid` y `--disable-libfdk-aac`, y ni `--enable-gpl` ni `--enable-nonfree`; `ffmpeg -encoders` no lista `libx264`, `libx265` ni `libfdk_aac`.
- [ ] **Avisos.** `THIRD-PARTY-NOTICES.txt` trae el copyright y la licencia de SVT-AV1, libaom, libvpx, libopus, LAME, kvazaar, OpenH264, libwebp, zlib, nv-codec-headers, AMF y libvpl.
- [ ] **AOM.** El texto completo de la Alliance for Open Media Patent License 1.0 va en los avisos (cláusula 1.2.1.b).
- [ ] **Google.** Los `PATENTS` de libvpx y libwebp van en los avisos.
- [ ] **LAME.** El cuadro «Acerca de» o los avisos dicen que se usa LAME, con enlace a https://lame.sourceforge.io/.
- [ ] **IJG.** Los avisos incluyen el crédito al Independent JPEG Group que pide el `LICENSE.md` de FFmpeg.
- [ ] **AMF.** La «Notice Regarding Standards» de AMD va en los avisos tal cual.
- [ ] **Código fuente.** El archivo del código fuente correspondiente incluye FFmpeg y cada biblioteca externa en el commit que fija BtbN (o la oferta escrita los cubre).
- [ ] **Valores por defecto.** `libkvazaar` y `libopenh264` siguen con `InDefaults = false` y ningún preajuste de fábrica los usa; la elección automática de H.264/HEVC solo toma codificadores de GPU o de Media Foundation, y sin ellos ofrece AV1 o VP9.
- [ ] **Patentes de H.264, HEVC y AAC.** Hay una decisión escrita (en `docs/decisiones.md`) sobre quién cubre las patentes al codificar con la GPU, con Media Foundation y con `aac`, tomada con alguien de licencias, a la vista de la nota de AMD de «Quién cubre las patentes».
- [ ] **Puntos «sin verificar».** Se leyó en la fuente primaria cada punto marcado «sin verificar» en «Exportación: codificadores, licencias y patentes», o se dejó constancia de por qué no aplica.
- [ ] **Marcas de formatos.** Los nombres ProRes, DNxHR, CineForm, NVENC, Quick Sync y AMF se usan solo para describir el formato o el hardware, sin logotipos ni sugerir respaldo.
