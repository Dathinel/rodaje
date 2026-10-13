# Fondo legal de Rodaje

El repositorio de Rodaje (https://github.com/Dathinel/rodaje) es público y la versión 1.0.0 está lista para publicarse. Este documento explica la licencia del programa, qué obligaciones traen sus dependencias y qué hay que comprobar antes de entregar un ejecutable a terceros. No es asesoría legal: resume las licencias y las guías públicas de cada proyecto, con las fuentes citadas. Lo que falta resolver está en [pendientes-legales.md](pendientes-legales.md); las licencias de cada dependencia, en [`LICENCIAS.md`](../LICENCIAS.md).

## Licencia del código propio

El código de Rodaje se publica bajo la GNU General Public License, **solo versión 3** (`GPL-3.0-only`, también en `Directory.Build.props`), con el texto oficial completo en [`LICENSE`](../LICENSE), la cabecera `Copyright (C) 2026 Dathinel` y la URL oficial https://github.com/Dathinel/rodaje (D-032). Al final de `LICENSE` van los términos adicionales de la Sección 7:

1. **Atribución en la interfaz** (7.b y 7.c): toda versión interactiva conserva, visible y con enlace funcional, el aviso «Basado en el editor de video de código abierto de Dathinel / Rodaje (https://github.com/Dathinel/rodaje)». Rodaje lo cumple con el crédito fijo de la barra de título («Código abierto upstream: Rodaje») y el diálogo «Ayuda → Acerca de Rodaje». Las cadenas están en `src/Rodaje.App/Services/UpstreamAttribution.cs`; una prueba las fija contra `LICENSE` y `build\app.ps1 -Autoprueba` falla si el crédito no se ve.
2. **Aviso en los canales de distribución y venta:** quien ofrezca o venda el programa dice que deriva de un proyecto libre y gratuito y enlaza al repositorio oficial.
3. **Prohibición de retirar** el crédito y el enlace.
4. **Permiso adicional para complementos:** lo que se conecta por las interfaces públicas, puntos de extensión o IPC/CLI no se considera obra derivada y puede llevar cualquier licencia, también privativa y de pago.

Es una licencia de código abierto: cualquiera puede usar, estudiar, modificar, redistribuir y vender el programa, con el código fuente y bajo la misma licencia. El nombre «Rodaje», el logotipo, el icono y el material gráfico no se licencian con el código ([`TRADEMARK.md`](../TRADEMARK.md)): un derivado publicado debe usar nombre e íconos propios.

Que Rodaje sea GPLv3 no permite copiar código GPL ajeno. El permiso para complementos y los términos de atribución solo los puede dar el titular, así que código GPL de terceros dentro de Rodaje los dejaría sin efecto sobre esa parte y rompería la titularidad única. Por eso de los proyectos GPL se estudia el diseño, sin tomar su código.

## Sala limpia

Los editores de código abierto estudiados (MLT, Kdenlive, Shotcut, OpenShot, Olive y LosslessCut) se analizaron solo a nivel de arquitectura y comportamiento. No se copió, tradujo ni adaptó código de ninguno. De los programas comerciales que inspiran la experiencia de uso solo se toman ideas de producto: nunca se descompilan ni se analizan sus binarios, ni se extraen sus recursos (efectos, plantillas, fuentes, música, íconos). Todo recurso incluido en Rodaje es propio o tiene una licencia que permite redistribución y uso comercial, registrada en `LICENCIAS.md`.

## FFmpeg y la LGPL

FFmpeg es la pieza con más obligaciones. Su licencia por defecto es la LGPL 2.1 o posterior; pasa a GPL con `--enable-gpl` y a no redistribuible con `--enable-nonfree` (https://ffmpeg.org/legal.html). Rodaje usa una compilación propia de la variante `lgpl-shared` de BtbN («Compilación fijada»), que según su archivo de variantes (https://github.com/BtbN/FFmpeg-Builds/blob/master/variants/defaults-lgpl.sh) llevan `--enable-version3` y `COPYING.LGPLv3`: la licencia efectiva es la **LGPL v3**, sin libx264 ni libx265.

La lista de comprobación de FFmpeg, aplicada a Rodaje:

1. **Sin `--enable-gpl` ni `--enable-nonfree`.** `FfmpegLibrary` se niega a arrancar si `avcodec_license()` no empieza por «LGPL» o si la configuración de alguna biblioteca cargada lleva esas opciones. Si junto al ejecutable hay una carpeta `ffmpeg\`, ignora `RODAJE_FFMPEG`: la distribución solo carga el FFmpeg que trae.
2. **Enlace dinámico.** Las bibliotecas de FFmpeg van como DLL separadas (`avcodec-63.dll`, `avformat-63.dll`, etc.) que Rodaje carga en tiempo de ejecución con FFmpeg.AutoGen. Nunca se enlazan estáticamente ni se empaquetan dentro del ejecutable AOT.
3. **DLL sin renombrar ni ofuscar**, para que el usuario pueda reemplazarlas por otra compilación compatible.
4. **Código fuente correspondiente** de las DLL incluidas, o una oferta escrita de entregarlo.
5. **Línea de configuración** de la compilación distribuida en [`ffmpeg-configure.txt`](ffmpeg-configure.txt).
6. **Crédito en «Acerca de»:** «Usa bibliotecas de FFmpeg (https://ffmpeg.org) bajo la LGPL v3». Falta el enlace al código fuente archivado.
7. **Sin términos que prohíban la ingeniería inversa** de la parte LGPL ni reclamen la propiedad de FFmpeg. La GPLv3 de Rodaje no impone nada de eso.
8. **Licencias de las bibliotecas que FFmpeg incluye**, revisadas y listadas en `THIRD-PARTY-NOTICES.txt` (ver «Exportación» y los pendientes).

### Compilación fijada

Las DLL, `ffmpeg.exe` y `ffprobe.exe` son una compilación propia: `build\compilar-ffmpeg-lgpl.ps1` corre en Docker la variante `win64-lgpl-shared` de BtbN/FFmpeg-Builds con los mismos scripts y el mismo FFmpeg que la release `autobuild-2026-09-30-13-08` de BtbN, pero **sin chromaprint** (BtbN la enlaza con fftw3, GPL-2.0-or-later), **sin libzvbi** (`src/packet-830.*` y `src/pdc.*` son GPL-2.0-only, incompatibles con la GPLv3) y **sin los plugins `fast_float` y `threaded` de Little CMS** (GPL-3.0-or-later; el núcleo de lcms2 es MIT). Un escaneo de cabeceras de cada etapa activa no encontró más código GPL compilado: lo que aparece está en herramientas, pruebas o ejemplos que no se compilan, en módulos de gnulib relicenciados a LGPL, o en etapas apagadas en la variante LGPL. `build\preparar-entorno.ps1` instala esa compilación en `.entorno\ffmpeg` y verifica su huella:

- **Archivo:** `ffmpeg-n9.0.2-17-g2a571b6068-rodaje1-win64-lgpl-shared-9.0.zip`, SHA-256 `573d569b6221f97b0f3482b7c65c3b58b97c56ce66ca1100fb84086a79d00b88`. Se aloja en la release `ffmpeg-n9.0.2-17-g2a571b6068-rodaje1` de `Dathinel/rodaje` (pendiente de subir, con su `.sha256` y la fuente); mientras, `preparar-entorno.ps1` lo toma de `.entorno\ffmpeg-lgpl\`.
- **Licencia efectiva:** LGPL-3.0-or-later. `ffmpeg -buildconf` muestra `--enable-version3`, ni `--enable-gpl` ni `--enable-nonfree`, ni chromaprint ni libzvbi; el script de compilación rechaza el resultado si aparece alguna de esas opciones o una biblioteca GPL.
- **Código fuente de FFmpeg:** commit `2a571b606854520cf89804d8030c8b328e621689` de la rama `release/9.0` (https://github.com/FFmpeg/FFmpeg/commit/2a571b606854520cf89804d8030c8b328e621689).
- **Scripts de compilación:** commit `6c9aec5fc9a72ec3abedd1fa84db141fa18cf52b` de BtbN/FFmpeg-Builds, con los cambios que aplica `build\compilar-ffmpeg-lgpl.sh` (etapas quitadas, imagen `base-win64` y FFmpeg fijados, `--extra-version=rodaje`, menos trabajos en paralelo).
- **Fuente correspondiente:** `rodaje-ffmpeg-lgpl-fuente.tar` (2,1 GB, SHA-256 `81dd3e47394d3cacf597e7bdf8dbf4698b48bed07da3a444c704ca69354726ba`): el tarball de cada etapa tal como lo montó el Dockerfile generado, FFmpeg, los scripts de BtbN y los propios, con huellas. Se publica junto al zip.
### `ffmpeg.exe` para proxies y exportación

La distribución lleva el `ffmpeg.exe` de la misma compilación en `ffmpeg\`, junto a las DLL, más `avfilter-12.dll` y `avdevice-63.dll`, que solo él importa (`ffprobe.exe` y `ffplay.exe` no se copian). Rodaje lo ejecuta como proceso aparte para los proxies y la exportación: argumentos con `ProcessStartInfo.ArgumentList`, cuadros por la entrada estándar y avance por `-progress`. Ejecutar un programa LGPL como proceso separado no lo enlaza con Rodaje; las obligaciones son las mismas que para las DLL: sin modificar ni renombrar, con su licencia, su fuente y su línea de configure.

`FfmpegExecutable` solo acepta el `ffmpeg.exe` de la carpeta de la que se cargaron las DLL, nunca uno del `PATH`, y comprueba que `-buildconf` no lleve `--enable-gpl` ni `--enable-nonfree`, que `-L` declare la LGPL y que su libavcodec sea la 63; si algo falla, los proxies y la exportación se desactivan con el motivo en español. `build\app.ps1` repite la comprobación antes de copiarlo a `dist\`.

Los proxies se codifican en H.264 con NVENC, después con Media Foundation y, como último recurso, con la OpenH264 compilada en las DLL. Son archivos intermedios en `%LOCALAPPDATA%\Rodaje\proxies`: nunca se exportan ni se entregan.

El FFmpeg de gyan.dev que se usa en desarrollo es una compilación GPL: nunca se copia a `dist\`, a un paquete ni a una distribución.

## Complementos con otras licencias

Van aparte del programa, cada uno en su propio proceso, y se instalan desde el gestor de complementos.

**`rodaje-codificadores-gpl`** (lo arma `build\armar-codificadores-gpl.ps1`; publicado el 12-oct en `Dathinel/rodaje-codificadores-gpl`, release `v0.2.0`, y en el catálogo `v6`). Empaqueta un `ffmpeg.exe` estático propio (D-034), compilado por `build\compilar-ffmpeg-gpl.ps1` en Docker con los scripts de BtbN en el mismo commit que la compilación LGPL (`6c9aec5`) y la cadena de herramientas `base-win64` de BtbN fijada por huella: FFmpeg en el commit `2a571b6` con `--enable-gpl --enable-version3`, x264, x265, libopus y libmp3lame (los codificadores de audio de la exportación que no son nativos de FFmpeg) y libiconv, que pide LAME. Ninguna otra biblioteca externa: el script de armado rechaza el exe si `-buildconf` activa algo fuera de esa lista. Dentro del paquete van el texto de la GPLv3, la línea de configure, los avisos de cada pieza en `licencias/` (FFmpeg, x264, x265 con json11, libopus, LAME, libiconv, mingw-w64 con su runtime y winpthreads, mingw-std-threads y la GCC Runtime Library Exception 3.1; todos sacados de la fuente que se compiló menos el de GCC, fijado por huella en `build\codificadores-gpl\`) y un `LEEME.md` con la URL y la huella de la fuente correspondiente y la nota de patentes.

- **Copyright: compatible.** El exe es GPL-3.0-or-later y corre como proceso aparte por una tubería (la misma frontera que el `ffmpeg.exe` LGPL), así que editor y exe son obras separadas. `FfmpegExecutable.OpenGplAddOn` acepta GPL o LGPL v3 o posterior, rechaza `nonfree` y exige la serie de libavcodec de Rodaje; el editor nunca carga sus bibliotecas.
- **Código fuente correspondiente (GPLv3, Sección 6): publicada.** `rodaje-codificadores-gpl-<versión>-fuente.tar` va en la misma release que el `.paquete`, con su `.sha256` (6(d): acceso equivalente desde el mismo sitio): un tarball por pieza (FFmpeg, scripts de BtbN, mingw-w64 con su CRT y winpthreads, mingw-std-threads, libiconv con gnulib, x264, x265, libopus con sus modelos y LAME), exactamente el árbol que se compiló, más los scripts de compilación, la línea de configure y las huellas. libgcc y libstdc++ entran estáticas bajo la excepción de bibliotecas de ejecución de GCC; el compilador no se redistribuye (es la imagen pública de BtbN, citada por huella).
- **Patentes de H.264 y HEVC: pendiente del abogado.** x264 y x265 codifican en software formatos con pools de patentes (Via LA para AVC; Access Advance y Via LA para HEVC), sin la mitigación parcial de la GPU o de Windows. Por eso nunca son la opción por defecto (`InDefaults = false`): solo aparecen en «Avanzado» con la nota «Complemento GPL», y el complemento se instala aparte por decisión de quien lo usa.
- **Nota de patentes en el paquete** (informativa, no es consejo legal). El `LEEME.md` del paquete y el `LICENCIAS.md` de su repositorio dicen que H.264/AVC y H.265/HEVC son formatos con patentes que se licencian en pools (Via LA; Access Advance), que las licencias de copyright del paquete no conceden licencias de patentes y que codificar con x264 o x265 puede requerir una según el país y el uso, en especial si es comercial; que por eso Rodaje no lo trae por defecto ni lo elige solo y cada quien decide si lo instala y responde por su uso; y que Opus tiene licencias sin regalías declaradas ante la IETF y el programa de licencias de MP3 terminó en 2017, sin que sea una garantía.

**`modulo-raw`:** LibRaw 0.22.2 bajo la LGPL-2.1 (a elegir con la CDDL-1.0), DLL oficial sin modificar, compatible con la GPLv3 (LGPL-2.1, sección 3). Se entregan su `LICENSE.LGPL`, `COPYRIGHT` y su código fuente. Los SDK de Blackmagic RAW, RED y ARRI tienen licencias propias y no entran en la v1; ProRes RAW solo existe en Windows con el decodificador propio de FFmpeg, con una cuestión de patentes de Apple sin revisar ([`modules/Rodaje.Module.Raw/LICENCIAS.md`](../modules/Rodaje.Module.Raw/LICENCIAS.md)).

## Exportación: codificadores, licencias y patentes

La exportación no añade binarios: usa el mismo `ffmpeg.exe` y las mismas DLL LGPL de los proxies. Todo lo que puede codificar viaja en la distribución aunque el usuario nunca lo elija, porque las bibliotecas externas van estáticas dentro de `avcodec-63.dll`. Las licencias de cada biblioteca, con el commit que fija BtbN, están en `LICENCIAS.md`, «Bibliotecas de la exportación».

| Códec | Codificador | Quién lo hace | Licencia del código | ¿Lo redistribuye Rodaje? | Patentes del formato | Compatibilidad con la GPLv3 |
|---|---|---|---|---|---|---|
| H.264 | `h264_nvenc`, `h264_amf`, `h264_qsv` | GPU (NVIDIA, AMD, Intel) | Encabezados MIT; el codificador es el controlador | Solo los encabezados, dentro de `avcodec` | Pool AVC/H.264 de Via LA (ver «Quién cubre las patentes») | Encabezados MIT: sí |
| H.264 | `h264_mf` | Media Foundation | Parte de Windows | No | Igual | No se distribuye |
| H.264 | `libopenh264` (no por defecto) | CPU | BSD-2-Clause | Sí | Sin cobertura: la licencia de Cisco es solo para su binario | Sí |
| HEVC | `hevc_nvenc`, `hevc_amf`, `hevc_qsv`, `hevc_mf` | GPU o Media Foundation | Igual que H.264 | Igual | HEVC Advance (Access Advance) y titulares fuera de pools | Igual que H.264 |
| HEVC | `libkvazaar` (no por defecto) | CPU | BSD-3-Clause | Sí | Sin cobertura | Sí |
| AV1 | `libsvtav1`, `libaom-av1` | CPU | BSD-3-Clause-Clear / BSD-2-Clause, más la AOM Patent License 1.0 | Sí | Licencia de AOM sin regalías; Sisvel ofrece además un pool | Código sí; licencia de patentes **pendiente** (punto e) |
| AV1 | `av1_nvenc`, `av1_amf`, `av1_qsv` | GPU | Encabezados MIT | Solo encabezados | Igual que AV1 | Encabezados MIT: sí |
| VP9 | `libvpx-vp9` | CPU | BSD-3-Clause más la concesión de patentes de Google | Sí | Concesión de Google; Sisvel ofrece además un pool | Sí, a confirmar en gnu.org |
| ProRes, DNxHR, CineForm, FFV1, MJPEG, PNG, QT Animation, GIF, TIFF, EXR | `prores_ks`, `dnxhd`, `cfhd`, `ffv1`, `mjpeg`, `png`, `qtrle`, `gif`, `tiff`, `exr` | CPU, código de FFmpeg | LGPL de FFmpeg (PNG, TIFF y EXR usan zlib) | Sí | Sin verificar | Sí (LGPL v3 y licencia del IJG) |
| WebP | `libwebp_anim` | CPU | BSD-3-Clause más la concesión de Google | Sí | Concesión de Google | Igual que VP9 |
| AAC | `aac_mf` (preferido), `aac` | Media Foundation / FFmpeg | Windows / LGPL | `aac`, sí | Pool AAC de Via LA | `aac`: sí |
| Opus | `libopus` | CPU | BSD-3-Clause | Sí | Licencias sin regalías declaradas ante la IETF | Código sí; patentes **pendientes** |
| MP3 | `libmp3lame` | CPU | Library GPL 2 o posterior (LAME) | Sí | Sin verificar | Sí |
| FLAC, ALAC, PCM | `flac`, `alac`, `pcm_*` | CPU, código de FFmpeg | LGPL | Sí | No se encontró ningún pool | Sí |

### Obligaciones de copyright de la exportación

1. **Avisos en `THIRD-PARTY-NOTICES.txt`** de cada biblioteca: las BSD (SVT-AV1, libaom, libvpx, libopus, kvazaar, OpenH264, libwebp) piden reproducir copyright, condiciones y descargo; las MIT (encabezados de NVENC, AMF y libvpl), incluir el aviso.
2. **AOM Patent License 1.0 completa** (SVT-AV1 y libaom): su cláusula 1.2.1.b exige incluir el texto al distribuir en binario (https://gitlab.com/AOMediaCodec/SVT-AV1/-/blob/master/PATENTS.md).
3. **`PATENTS` de libvpx y libwebp** junto a sus licencias.
4. **LAME:** reconocer que se usa, con enlace a https://lame.sourceforge.io/. Va estática dentro de `avcodec-63.dll`, una DLL LGPL reemplazable, y su código (revisión 6835) entra en la fuente correspondiente de FFmpeg.
5. **IJG:** `jfdctfst.c`, `jfdctint_template.c` y `jrevdct.c` vienen de libjpeg; si solo se distribuyen ejecutables, hay que dar crédito al Independent JPEG Group en la documentación (`LICENSE.md` de FFmpeg en el commit fijado).
6. **AMD:** la «Notice Regarding Standards» del SDK de AMF va en los avisos tal cual.
7. **Sin bibliotecas GPL ni nonfree en las DLL:** la línea de configure lleva `--disable-libx264`, `--disable-libx265`, `--disable-libxvid` y `--disable-libfdk-aac`.

### Quién cubre las patentes de H.264, HEVC y AAC

Las licencias de software no resuelven las patentes. Rodaje reduce la exposición así: H.264 y HEVC se codifican por GPU o con Media Foundation, se priorizan formatos sin regalías (AV1, VP9, Opus) cuando no se necesita uno concreto, y los codificadores por software de formatos con pools quedan fuera de los valores por defecto. Pero **no está demostrado que codificar con la GPU o con Windows traslade la licencia al editor**, y una fuente lo contradice:

- **AMD (AMF):** el `LICENSE.txt` del SDK de AMF (commit `6277e353` que compila BtbN) dice que AMD no concede licencia sobre estándares, «including … AVC/H.264; HEVC/H.265; AAC decode/FFMPEG; AAC encode/FFMPEG; VC-1; and MP3», y que «you will pay any royalties due for such third party technologies».
- **Intel (QSV):** la documentación de `vpl-gpu-rt` dice que los códecs MPEG «may require licenses from various entities, including Intel Corporation».
- **NVIDIA (NVENC):** sin verificar en el acuerdo vigente. Rodaje solo usa los encabezados MIT de `nv-codec-headers` y el controlador del usuario.
- **Microsoft (Media Foundation):** la documentación de los codificadores H.264, HEVC y AAC no dice si una aplicación que los llama hereda una licencia de patentes. Las ediciones N de Windows no traen esos códecs sin el Media Feature Pack.
- **Via LA (AVC y AAC) y Access Advance (HEVC):** licencian a fabricantes de productos finales; las tarifas y exenciones para un editor gratuito que llama a un codificador del sistema están sin verificar en la fuente primaria.
- **OpenH264:** la licencia de patentes que paga Cisco cubre solo su binario descargado por el usuario final; la OpenH264 de BtbN está compilada desde el código y no la tiene.
- **MP3:** el programa de licencias de Fraunhofer y Technicolor terminó en 2017 (sin verificar en fuente primaria).
- **AV1 y VP9:** además de las concesiones de AOM y Google, Sisvel administra pools dirigidos a dispositivos; sin verificar si alcanzan al software.
- **ProRes, DNxHR, CineForm:** los codificadores son implementaciones propias de FFmpeg; sin verificar si Apple, Avid o GoPro exigen licencia de patentes. Sus nombres se usan solo para describir el formato.

El autor no distribuye Rodaje comercialmente. La GPLv3 permite que terceros lo vendan, y la exposición a patentes de cada distribuidor es suya (Sección 11). Antes de una distribución comercial hay que consultarlo con alguien de propiedad intelectual.

## Compatibilidad con la GPLv3

Las citas en inglés son literales de cada licencia. Todo lo marcado como pendiente está en [pendientes-legales.md](pendientes-legales.md).

### a) FFmpeg (LGPL v3)

Compatible: la LGPL v3 «incorporates the terms and conditions of version 3 of the GNU General Public License, supplemented by the additional permissions listed below», y la LGPL 2.1 se puede pasar a la GPL (sección 3). Su sección 4 pide que los términos de la obra combinada no restrinjan la modificación de la biblioteca ni «reverse engineering for debugging such modifications»; la GPLv3 no restringe nada de eso. Condición: ningún texto añadido (por ejemplo, el que pida el Windows App SDK, punto b) puede prohibir esa ingeniería inversa. El párrafo de `THIRD-PARTY-NOTICES.txt` que lo explica está marcado como pendiente de revisión legal.

### b) Windows App SDK

Rodaje se distribuye con unas 240 DLL, `.pri` y `.winmd` del Windows App SDK sin modificar. Sus términos (`license.txt` de `Microsoft.WindowsAppSDK.WinUI` 2.3.9) dicen en la cláusula 3.c.ii: «You may not: [...] modify or distribute the source code of any distributable code so that any part of it becomes subject to any license that requires that the distributable code [...] be disclosed or distributed in source code form, or that others have the right to modify it». La 3.b.ii pide «require distributors and external end users to agree to terms that protect it and Microsoft at least as much as this agreement», y la 3.b.iii una indemnización.

La GPLv3 excluye del «Corresponding Source» las «System Libraries»: lo que se incluye en la forma normal de empaquetar un «Major Component» (kernel, sistema de ventanas…) y sirve solo para usar la obra con él. Dudas:

1. Si el Windows App SDK cuenta como System Library: sirve al sistema de ventanas de Windows, pero no viene con Windows. Su fuente (WinUI) es MIT en GitHub, pero los binarios llevan los términos de Microsoft. La 3.c.ii habla del **código fuente** de lo redistribuible, y Rodaje solo distribuye binarios.
2. Si no lo es, el titular puede añadir en `LICENSE` un permiso adicional de enlace para el Windows App SDK, la proyección del Windows SDK y DirectML. Quien redistribuya podría quitarlo de su copia (Sección 7), y entonces esa copia no podría ir con las DLL.
3. Qué texto de usuario final cumple la 3.b sin añadir restricciones a la GPLv3 (Sección 10) ni a la LGPL de FFmpeg. El instalador ya muestra, junto a `LICENSE` y `TRADEMARK.md`, un aviso sobre los binarios de Microsoft; su redacción está pendiente de revisión.

### c) Términos adicionales y Sección 7

La Sección 7 solo admite términos de una lista cerrada; los usados son 7(b), conservar avisos y atribuciones, y 7(c), no tergiversar el origen. 7(e) permite negar derechos de marca, lo que respalda `TRADEMARK.md`. Los términos 1 y 3 encajan en 7(b) y 7(c). El término 2 obliga a poner texto en la **descripción del producto, la página de venta o el proceso de descarga**, que no forman parte del material: puede ser una «further restriction» que cualquier receptor puede eliminar («you may remove that term»). Si es así, la licencia no cae, pero ese término no se puede hacer cumplir. También conviene confirmar que «sin obstrucciones» y la lista de lugares del término 1 son «reasonable».

Otros puntos:

- **Activos reservados.** Si el logotipo y el icono viajan dentro del ejecutable, forman parte de lo que se redistribuye: revisar cómo se combinan con la GPLv3 (por ejemplo, exigir solo que un derivado los reemplace).
- **Permiso para complementos.** Como todo permiso adicional, quien redistribuya una versión modificada puede quitarlo de su copia; no afecta a la versión oficial.
- **«Appropriate Legal Notices»** (Sección 5.d): «Acerca de Rodaje» muestra el copyright, que no hay garantía, que se puede redistribuir y dónde leer la licencia.
- **Venta.** La Sección 4 permite cobrar por cada copia y la 10 impide añadir restricciones: cualquiera puede vender Rodaje o un derivado con su código fuente, la atribución y otro nombre. El titular conserva el nombre, la marca visual y el crédito, no la exclusividad de venta.
- **Código fuente de cada versión.** El repositorio público sirve de enlace al código, pero la Sección 6 pide el código que corresponde a cada ejecutable entregado (por ejemplo, una etiqueta por versión), además de la fuente de FFmpeg.

### d) Licencias de copyright de las bibliotecas

Ninguna biblioteca de FFmpeg entra en el código GPLv3 de Rodaje: van dentro de una DLL LGPL v3 que se carga en tiempo de ejecución. Aun así, todas las revisadas son compatibles con la GPLv3 según la lista de la FSF: BSD-3-Clause (libvpx, libopus, kvazaar, libwebp), BSD-3-Clause-Clear (SVT-AV1), BSD-2-Clause (libaom, OpenH264), zlib, MIT (encabezados de NVENC, AMF y libvpl) y la licencia del IJG. LAME es Library GPL 2 «or (at your option) any later version», compatible a través de la LGPL 2.1 o de su propia sección 3, que permite pasar a la GPL. Rodaje no modifica LAME.

Las demás bibliotecas de las DLL están en `LICENCIAS.md`, «Otras bibliotecas dentro de las DLL de FFmpeg». Las que admiten la GPL lo hacen en doble licencia o con «o posterior» (FreeType, GMP, Mbed TLS). **El resto está sin revisar** (librist, libbluray, Fontconfig, dependencias de librsvg y libplacebo, entre otras), así que todavía no se puede afirmar que ninguna sea solo GPL.

### e) Licencias de patentes

- **AOM Patent License 1.0 (SVT-AV1 y libaom): pendiente.** La concede cada titular directamente al usuario (1.2), y tiene una terminación defensiva (1.3): quien demande alegando que **cualquier** implementación de AV1 infringe sus patentes pierde la licencia. Rodaje no la impone; solo reproduce su texto. La GPLv3 tiene un mecanismo parecido y la FSF acepta la Apache-2.0, pero el alcance de AOM es más amplio. Hay que confirmar que no es una «further restriction» (Sección 10) ni un conflicto de la Sección 12. Si lo fuera, se quitan `libsvtav1` y `libaom-av1` (queda AV1 por GPU). La condición de ofrecer las patentes propias no aplica si el titular no tiene patentes esenciales de AV1.
- **Concesión de Google (libvpx, libwebp): compatible, a confirmar.** Su terminación se limita a demandas sobre esas implementaciones, como Apache-2.0; la FSF marca la «License of WebM» como compatible.
- **Opus ante la IETF: pendiente.** El `COPYING` de libopus remite a declaraciones de Xiph.Org, Microsoft y Broadcom (https://datatracker.ietf.org/ipr/1524/, `/1914/`, `/1526/`) que no se pudieron leer.
- **OpenH264:** la compilada desde el código es BSD-2-Clause. El binario de Cisco no se usa; si se usara, sus términos están pendientes.
- **Media Foundation y controladores de la GPU:** Rodaje no los distribuye. Media Foundation viene con Windows (caso típico de «System Libraries»); los controladores son opcionales y los carga FFmpeg.

## Modelos de inteligencia artificial

Los modelos no viajan en el ejecutable base: los instala el gestor de complementos o los trae la distribución completa, cada uno con su SHA-256 fijado (`build\preparar-modelos.ps1` y el `paquete.json` de cada módulo). Solo se usan modelos cuya licencia permita redistribuirlos y usarlos comercialmente. El detalle está en el `LICENCIAS.md` de cada módulo.

| Modelo | Uso | Licencia | Notas | Módulo |
|---|---|---|---|---|
| Whisper large-v3-turbo y small (ggml) | Subtítulos | MIT (pesos de OpenAI) | small para equipos sin GPU | [Subtitles](../modules/Rodaje.Module.Subtitles/LICENCIAS.md) |
| Qwen3-4B-Instruct-2507 (GGUF Q4_K_M) | Traducción | Apache-2.0 | Se descarga aparte (unos 2,5 GB); huella y URL por fijar a un commit | [Translate](../modules/Rodaje.Module.Translate/LICENCIAS.md) |
| Kokoro-82M v1.0 y sus voces | Texto a voz | Apache-2.0 | Datos de entrenamiento no todos públicos | [Voice](../modules/Rodaje.Module.Voice/LICENCIAS.md) |
| DeepFilterNet3 | Reducción de ruido | MIT o Apache-2.0 (se elige MIT) | En CPU | [AudioAi](../modules/Rodaje.Module.AudioAi/LICENCIAS.md) |
| Silero VAD v6.2.3 | Detección de voz | MIT | | [AudioAi](../modules/Rodaje.Module.AudioAi/LICENCIAS.md) |
| MODNet | Quitar fondo (por defecto) | Apache-2.0 | | [Vision](../modules/Rodaje.Module.Vision/LICENCIAS.md) |
| BiRefNet_lite (fp16) | Quitar fondo, «calidad alta» | MIT | Entrenado con DIS5K, de uso no comercial: **pendiente** | [Vision](../modules/Rodaje.Module.Vision/LICENCIAS.md) |
| YuNet 2023mar | Rostros | MIT | Entrenado con WIDER FACE | [Vision](../modules/Rodaje.Module.Vision/LICENCIAS.md) |
| RT-DETRv2-R18 | Personas | Apache-2.0 | Entrenado con COCO 2017 | [Vision](../modules/Rodaje.Module.Vision/LICENCIAS.md) |

**Descartados por su licencia:** RMBG-1.4 y 2.0 de BRIA (uso no comercial), NLLB-200 (CC-BY-NC), las voces de Piper derivadas de lessac (licencia de sus datos) y espeak-ng (GPL; el español y el portugués usan fonemizadores propios). Los runtimes (ONNX Runtime, Whisper.net y whisper.cpp, LLamaSharp y llama.cpp) son MIT; DirectML, que usa el módulo de visión, se redistribuye bajo la licencia de software de Microsoft. CUDA no se redistribuye.

La licencia de un repositorio no siempre cubre los pesos, y los pesos pueden heredar restricciones de sus datos de entrenamiento: antes de incluir un modelo se lee la licencia del archivo exacto que se descarga.

## Catálogo y descargas

El gestor de complementos lee un índice publicado en `Dathinel/rodaje-catalogo` (público, CC0, sin código) y firmado con ECDSA P-256; la clave pública va dentro del exe y la privada nunca se versiona. Cada entrada solo puede apuntar a releases de `github.com/Dathinel` o a Hugging Face fijado por commit, y cada archivo se comprueba con su SHA-256 antes de instalarse ([descarga.md](descarga.md)). Los paquetes de contenido (`rodaje-transiciones`, `rodaje-filtros`) son CC0. Cada módulo publicado lleva su `LICENCIAS.md`.

## Marcas

Rodaje no usa marcas ajenas en su código, su interfaz, sus íconos ni sus recursos: no aparecen CapCut, ByteDance, TikTok, Adobe, Photoshop ni Premiere en ningún elemento visible, salvo el uso descriptivo de los nombres de redes en las zonas seguras del visor (sin logotipos) y los nombres de formatos y hardware (ProRes, DNxHR, NVENC…). El registro de decisiones puede mencionar programas existentes para explicar de dónde viene una idea. El nombre «Rodaje» está reservado en `TRADEMARK.md`, pero no registrado.

## Privacidad y secretos

Rodaje no envía telemetría ni datos de uso. La red solo se usa para descargar complementos y modelos desde el gestor. Si alguna función llega a usar servicios externos, será opcional, explicará qué se envía y leerá su clave desde un `.env` local, que está en `.gitignore`. La clave privada del catálogo vive fuera del repositorio.

## Avisos de terceros

`THIRD-PARTY-NOTICES.txt` contiene los avisos de los componentes que viajan con el programa; `build\app.ps1` lo copia junto a `Rodaje.exe` y debe acompañar siempre al ejecutable. Hoy está incompleto (ver pendientes). `LICENCIAS.md` es la vista para desarrolladores e incluye también lo que solo se usa para compilar o probar.

## Lista de comprobación antes de distribuir

Se recorre completa antes de entregar un ejecutable a terceros. Cada punto se marca con la fecha y el resultado.

### Programa

- [ ] **Licencia.** `LICENSE` tiene la cabecera de copyright con el año al día, el texto literal de la GPLv3 (SHA-256 del tramo GPL `3972dc9744f6499f0f9b2dbf76696f2ae7ad8af9b23dde66d6af86c9dfb36986`, el de https://www.gnu.org/licenses/gpl-3.0.txt) y los términos adicionales revisados; `TRADEMARK.md` está al día; el instalador muestra los dos.
- [ ] **Crédito.** El exe publicado muestra «Código abierto upstream: Rodaje» en la barra de título y el aviso completo en «Acerca de Rodaje», y el enlace abre https://github.com/Dathinel/rodaje.
- [ ] **Código fuente.** Cada ejecutable señala el código de su versión exacta (etiqueta de git), además de la fuente de FFmpeg.
- [ ] **Sala limpia.** Ningún archivo contiene código copiado de proyectos GPL o de terceros sin licencia compatible.
- [ ] **Inventario.** `LICENCIAS.md` lista todas las dependencias reales (comparadas con los `packages.lock.json`), con versión y licencia verificadas.
- [ ] **FFmpeg.** La compilación es `lgpl-shared`, sin `--enable-gpl` ni `--enable-nonfree`; las DLL y `ffmpeg.exe` vienen de esa misma compilación y conservan sus nombres; la fuente está archivada o hay oferta escrita; «Acerca de» da el crédito con enlace a la fuente.
- [ ] **Ningún binario GPL** en la distribución base (ni el FFmpeg de gyan.dev ni otro).
- [ ] **Avisos de terceros.** `THIRD-PARTY-NOTICES.txt` está completo para la versión y va junto a `Rodaje.exe`.
- [ ] **Microsoft.** Los binarios del Windows App SDK, WebView2, la proyección del Windows SDK y, en el módulo de visión, DirectML se redistribuyen según sus términos (punto b).
- [ ] **Modelos de IA.** Cada modelo de la distribución completa tiene licencia que permite redistribución y uso comercial, con licencia, origen y hash en el `LICENCIAS.md` de su módulo.
- [ ] **Catálogo.** El índice publicado está firmado con la clave cuya pública lleva el exe, y la privada tiene respaldo fuera del equipo.
- [ ] **Marcas.** No hay marcas ajenas fuera del uso descriptivo; el nombre se buscó en USPTO (https://tmsearch.uspto.gov), EUIPO (https://euipo.europa.eu/eSearch/) y SIC Colombia (https://www.sic.gov.co), clases 9 y 42 de Niza.
- [ ] **Secretos.** El historial completo se escaneó con gitleaks (https://github.com/gitleaks/gitleaks) sin encontrar `.env`, claves ni tokens; si aparece alguno, se revoca y se reescribe el historial.
- [ ] **Privacidad.** No hay telemetría y toda función que envía datos fuera es opcional y está explicada.
- [ ] **Políticas del repositorio.** `SECURITY.md` y `CONTRIBUTING.md` al día, reporte privado de vulnerabilidades activado en GitHub y requisito DCO vigente.

### Exportación

- [ ] **Bibliotecas reales.** Las de `ffmpeg -buildconf` coinciden con «Bibliotecas de la exportación» de `LICENCIAS.md`, y sus commits con los de `scripts.d/` de BtbN; si cambió la compilación, se vuelve a leer cada licencia.
- [ ] **Sin GPL ni nonfree.** `-buildconf` lleva `--disable-libx264`, `--disable-libx265`, `--disable-libxvid` y `--disable-libfdk-aac`; `ffmpeg -encoders` no lista `libx264`, `libx265` ni `libfdk_aac`.
- [ ] **Avisos.** `THIRD-PARTY-NOTICES.txt` trae copyright y licencia de cada biblioteca de las DLL (con la cita de FreeType que pide la FTL), el texto completo de la AOM Patent License 1.0, los `PATENTS` de libvpx y libwebp, el crédito a LAME con su enlace, el del Independent JPEG Group y la «Notice Regarding Standards» de AMD.
- [ ] **Código fuente.** El archivo de la fuente correspondiente incluye FFmpeg y cada biblioteca externa en el commit que fija BtbN, o la oferta escrita los cubre.
- [ ] **Valores por defecto.** `libkvazaar` y `libopenh264` siguen con `InDefaults = false` y ningún preajuste de fábrica los usa; la elección automática de H.264/HEVC solo toma codificadores de GPU o de Media Foundation y, sin ellos, ofrece AV1 o VP9.
- [ ] **Patentes.** Hay una decisión escrita en [decisiones.md](decisiones.md) sobre quién cubre las patentes de H.264, HEVC y AAC, tomada con alguien de licencias.
- [ ] **Compatibilidad.** Están respondidos los puntos pendientes de «Licencias de patentes» (AOM, Opus, OpenH264) y confirmadas en https://www.gnu.org/licenses/license-list.html las anclas de compatibilidad de `LICENCIAS.md`, incluida la de WebM.
- [ ] **Marcas de formatos.** ProRes, DNxHR, CineForm, NVENC, Quick Sync y AMF se usan solo para describir el formato o el hardware, sin logotipos ni sugerir respaldo.

### Complementos

- [ ] **`rodaje-codificadores-gpl`.** `rodaje-codificadores-gpl-<versión>-fuente.tar` adjunto a la misma release que el `.paquete` con su `.sha256` (hecho en la 0.2.0), avisos en `licencias/` (hecho) y riesgo de patentes de x264 y x265 revisado (pendiente).
- [ ] **Módulos.** Cada módulo publicado lleva su `LICENCIAS.md`, el aviso de cada modelo y biblioteca, y licencia elegida para su repositorio.
