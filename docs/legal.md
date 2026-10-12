# Fondo legal de Rodaje

Rodaje vive hoy en un repositorio privado, pero se construye como si fuera a publicarse mañana. Este documento explica la situación legal del programa, qué obligaciones traen sus dependencias y qué hay que comprobar, punto por punto, antes de hacer público el repositorio o de entregar un ejecutable a otra persona. No es asesoría legal: es el resultado de leer las licencias y las guías públicas de cada proyecto, con las fuentes citadas para que cualquiera pueda verificarlas.

## Licencia del código propio

Todo el código escrito para Rodaje se publica bajo la GNU General Public License v3 (texto oficial completo en `LICENSE`, de https://www.gnu.org/licenses/gpl-3.0.txt), con la cabecera `Copyright (C) 2026 Daniel (Dathinel)` y la URL oficial https://github.com/Dathinel/rodaje (decisión D-032, que sustituye a la PolyForm Noncommercial de D-030 y esta a la MIT de D-002; el repositorio nunca se publicó con ninguna de las dos). Al final de `LICENSE` van los términos adicionales de la Sección 7:

1. **Atribución en la interfaz** (7.b y 7.c): toda versión interactiva conserva, visible y con enlace funcional, el aviso «Basado en el editor de video de código abierto de Daniel (Dathinel) / Rodaje (https://github.com/Dathinel/rodaje)». En Rodaje lo cumplen el crédito fijo de la barra de título y el diálogo «Ayuda → Acerca de Rodaje»; las cadenas viven en `src/Rodaje.App/Services/UpstreamAttribution.cs`, una prueba las fija contra `LICENSE` y `app.ps1 -Autoprueba` falla si el crédito no se ve.
2. **Aviso en los canales de distribución y venta**: quien lo ofrezca o venda dice que deriva de un proyecto libre y gratuito y enlaza al repositorio oficial.
3. **Prohibición de retirar** el crédito y el enlace.
4. **Permiso adicional para complementos**: lo que se conecta por las interfaces públicas, puntos de extensión o IPC/CLI no se considera obra derivada y puede llevar cualquier licencia, también privativa y de pago.

Es una licencia de código abierto: cualquiera puede usar, estudiar, modificar, redistribuir y vender el programa, con el código fuente y bajo la misma licencia. El nombre «Rodaje», el logotipo, el icono y el material gráfico no se licencian con el código ([`TRADEMARK.md`](../TRADEMARK.md)): un derivado publicado debe usar nombre e íconos propios. Las dudas sobre el alcance de los términos adicionales están en «Compatibilidad con la GPLv3», más abajo.

Ser GPLv3 no cambia la regla con el código GPL ajeno: no se copia. El permiso para complementos y los términos de atribución solo los puede dar el titular del código, así que código GPL de terceros dentro de Rodaje los dejaría sin efecto sobre esa parte y quitaría a Daniel la titularidad única. Por eso se estudia el diseño de los proyectos GPL sin tomar su código, y se detiene el trabajo a consultar si alguna vez se plantea lo contrario.

## Sala limpia

Los editores de código abierto estudiados (MLT, Kdenlive, Shotcut, OpenShot, Olive y LosslessCut, con sus licencias en `docs/archivo/investigacion.md`) se analizaron solo a nivel de arquitectura y comportamiento. No se copió, tradujo ni adaptó código de ninguno de ellos. De los programas comerciales que inspiran la experiencia de uso solo se toman ideas de producto: nunca se descompilan ni se analizan sus binarios, y nunca se extraen sus recursos (efectos, plantillas, fuentes, música, íconos). Todos los recursos que se incluyan en Rodaje deben ser propios o tener una licencia que permita redistribución y uso comercial, registrada en `LICENCIAS.md`.

## Compatibilidad con la GPLv3

Análisis hecho el 11 de octubre de 2026 al pasar de la PolyForm Noncommercial a la GPLv3 con términos adicionales (D-032), leyendo `LICENSE` y las licencias de las dependencias. No cambia el código. Las citas en inglés son literales de cada licencia (la GPLv3 de `LICENSE`, la LGPL v3 y el `license.txt` del Windows App SDK). Todo lo que sigue son **pendientes para el abogado** (ver [`pendientes-legales.md`](pendientes-legales.md)): no se redacta ningún texto legal nuevo aquí.

### a) FFmpeg (LGPL v3) con la GPLv3

**Qué dicen las licencias.** Las DLL y el `ffmpeg.exe` distribuidos son LGPL v3 (`--enable-version3`). La LGPL v3 se define como la GPLv3 más permisos adicionales: «This version of the GNU Lesser General Public License incorporates the terms and conditions of version 3 of the GNU General Public License, supplemented by the additional permissions listed below». Su sección 4 («Combined Works») pide que los términos de la obra combinada no restrinjan la modificación de la biblioteca ni «reverse engineering for debugging such modifications». La GPLv3 no prohíbe nada de eso: concede la modificación (Sección 2) y su Sección 3 renuncia a las medidas anti-elusión.

**Conclusión de trabajo.** Compatible: la LGPL v3 es compatible con la GPLv3 por construcción, y la versión 2.1 de la LGPL (la licencia por defecto de FFmpeg) también lo es por su opción de pasar a la GPL (LGPL 2.1, sección 3). Siguen en pie las obligaciones de siempre (DLL sin renombrar, código fuente u oferta, línea de configure, crédito) y una condición: ningún texto añadido (por ejemplo, el que exija el Windows App SDK, punto b) puede prohibir la ingeniería inversa de la combinación con FFmpeg. El párrafo de `THIRD-PARTY-NOTICES.txt` sigue marcado como pendiente de revisión.

### b) Windows App SDK: cláusula 3.c frente al copyleft de la GPLv3

**Qué dice Microsoft.** La prohibición de copyleft está en la cláusula 3.c.ii de los términos del Windows App SDK (`license.txt` del paquete `Microsoft.WindowsAppSDK.WinUI` 2.3.9; resumida en `LICENCIAS.md`, que la cita junto a la 3.b): «You may not: [...] modify or distribute the source code of any distributable code so that any part of it becomes subject to any license that requires that the distributable code, any other part of the software, or any of Microsoft's other intellectual property be disclosed or distributed in source code form, or that others have the right to modify it.» La 3.b.ii pide además «require distributors and external end users to agree to terms that protect it and Microsoft at least as much as this agreement», y la 3.b.iii una indemnización. Rodaje se distribuye autocontenido con unas 240 DLL, `.pri` y `.winmd` del SDK junto a `Rodaje.exe`. Matiz para el abogado: la 3.c.ii habla de modificar o distribuir el **código fuente** de lo redistribuible, y Rodaje solo distribuye los binarios sin modificar.

**Qué dice la GPLv3.** La Sección 6 obliga a dar el «Corresponding Source» del ejecutable, definido en la Sección 1 como «all the source code needed to generate, install, and (for an executable work) run the object code», pero que «does not include the work's System Libraries». Y: «The "System Libraries" of an executable work include anything, other than the work as a whole, that (a) is included in the normal form of packaging a Major Component, but which is not part of that Major Component, and (b) serves only to enable use of the work with that Major Component, or to implement a Standard Interface for which an implementation is available to the public in source code form. A "Major Component", in this context, means a major essential component (kernel, window system, and so on) of the specific operating system (if any) on which the executable work runs, or a compiler used to produce the work, or an object code interpreter used to run it.»

**Dudas para el abogado.**
1. Si el Windows App SDK cuenta como System Library: sirve para usar el sistema de ventanas de Windows (un Major Component), pero no viene con Windows (se distribuye por NuGet y en este caso dentro de la carpeta de Rodaje). El código fuente de WinUI es MIT en GitHub, pero los binarios del paquete llevan los términos de Microsoft.
2. Si no lo es, Daniel (único titular) puede añadir un permiso adicional de enlace en `LICENSE` (Sección 7, «additional permissions»), como el del término 4 para complementos, para el Windows App SDK, la proyección del Windows SDK y, si llega, DirectML. **No se redacta aquí.** Ojo: un permiso adicional lo puede quitar quien redistribuya (Sección 7: «When you convey a copy of a covered work, you may at your option remove any additional permissions from that copy»), y entonces esa copia ya no se podría distribuir con las DLL.
3. Si la GPLv3, aunque no se aplique a las DLL, «somete» igualmente esos archivos a copyleft en el sentido de la 3.c.ii al distribuirlos juntos, y qué texto de usuario final cumple la 3.b sin restringir los derechos de la GPLv3 (Sección 10: «You may not impose any further restrictions on the exercise of the rights granted or affirmed under this License») ni la LGPL de FFmpeg. La lista de cláusulas de Microsoft que debería cubrir ese texto (archivos solo licenciados, ingeniería inversa limitada a esos archivos, sin quitar avisos, exclusión de garantías que proteja a Microsoft, aviso de datos de uso 2.a, exportación, marcas, transmisión a redistribuidores, aceptación sin instalador, alcance de la indemnización) se mantiene del análisis anterior.

### c) Término adicional 2 (tiendas y páginas de venta): ¿cabe en la Sección 7?

**Qué permite la GPLv3.** La Sección 7 solo admite términos adicionales de una lista cerrada. Los usados son: «b) Requiring preservation of specified reasonable legal notices or author attributions in that material or in the Appropriate Legal Notices displayed by works containing it» y «c) Prohibiting misrepresentation of the origin of that material, or requiring that modified versions of such material be marked in reasonable ways as different from the original version». Y añade: «All other non-permissive additional terms are considered "further restrictions" within the meaning of section 10. If the Program as you received it, or any part of it, contains a notice stating that it is governed by this License along with a term that is a further restriction, you may remove that term.»

**Duda.** Los términos 1 y 3 (aviso en la interfaz y prohibición de quitarlo) encajan bien en 7(b) y 7(c), y también la marca: 7(e) permite «Declining to grant rights under trademark law for use of some trade names, trademarks, or service marks», que respalda `TRADEMARK.md`. El término 2 obliga a poner texto en la **descripción del producto, la página de venta o el proceso de descarga**, que no son parte del material ni de sus «Appropriate Legal Notices»: puede exceder 7(b) y ser una «further restriction» que cualquier receptor puede eliminar. Si es así, el riesgo no es que la licencia caiga, sino que ese término no se pueda hacer cumplir. También conviene revisar si «sin obstrucciones» y «en la ventana o diálogo de Acerca de, barra de estado o pantalla de inicio» del término 1 son «reasonable» en el sentido de 7(b).

### d) La GPLv3 permite vender el programa

Distinto de la PolyForm: la Sección 4 dice «You may charge any price or no price for each copy that you convey, and you may offer support or warranty protection for a fee», y la Sección 10 impide añadir restricciones. Cualquiera puede vender Rodaje o un derivado, siempre que entregue el código fuente correspondiente bajo la GPLv3 (Sección 6), conserve la atribución de los términos 1 y 3 y, para un derivado publicado, use otro nombre e íconos (`TRADEMARK.md`). Lo que Daniel conserva es el nombre, la marca visual y el crédito; no la exclusividad de venta. Es la decisión que Daniel tomó el 11-oct (D-032).

### e) El enlace del crédito apunta a un repositorio privado

El término 1 exige «un enlace interactivo directo y funcional» a https://github.com/Dathinel/rodaje, y la app lo cumple con el crédito de la barra de título y el diálogo «Acerca de». Hoy el repositorio es privado: para cualquiera que no sea Daniel el enlace da 404, así que el enlace solo funciona de verdad al hacerlo público. Mientras sea privado, Rodaje solo se comparte con personas cercanas; antes de entregar un ejecutable a terceros hay que publicar el repositorio (o al menos el código fuente correspondiente, que la Sección 6 exige igualmente).

### f) Otros puntos anotados al revisar

- **Versión de la GPL.** `LICENSE` lleva el texto de la versión 3 pero no dice «solo la versión 3» ni «o cualquier versión posterior». La Sección 14 dice: «If the Program does not specify a version number of the GNU General Public License, you may choose any version ever published by the Free Software Foundation». Los términos adicionales hablan de «GNU GPLv3», lo que probablemente basta, y `Directory.Build.props` declara `GPL-3.0-only`; confirmar si la cabecera debe decirlo de forma expresa.
- **Activos con todos los derechos reservados.** `TRADEMARK.md` reserva el logotipo, el icono y el material gráfico. Si esos archivos viajan dentro del ejecutable o de `dist\`, forman parte de lo que se redistribuye: revisar cómo se combina con la GPLv3 (por ejemplo, ponerlos aparte del código o exigir solo que un derivado los reemplace).
- **Permiso para complementos frente a quien redistribuye.** Como todo permiso adicional, quien redistribuya una versión modificada puede quitar el término 4 de su copia (Sección 7). No afecta a la versión de Daniel.
- **«Appropriate Legal Notices».** La Sección 5(d) exige que cada interfaz interactiva de una versión modificada los muestre: aviso de copyright, que no hay garantía, que se puede redistribuir bajo la licencia y cómo ver la licencia (Sección 0). El diálogo «Ayuda → Acerca de Rodaje» ya los incluye junto al crédito (`UpstreamAttribution`).

## FFmpeg y la LGPL

FFmpeg es la pieza con más obligaciones. Su licencia por defecto es la LGPL 2.1 o posterior, pero se convierte en GPL si se compila con `--enable-gpl` y en no redistribuible si se compila con `--enable-nonfree` (https://ffmpeg.org/legal.html). Rodaje usará las compilaciones `lgpl-shared` de BtbN (https://github.com/BtbN/FFmpeg-Builds). Según el archivo de variantes del propio proyecto (https://github.com/BtbN/FFmpeg-Builds/blob/master/variants/defaults-lgpl.sh), esas compilaciones usan `--enable-version3` y el archivo de licencia `COPYING.LGPLv3`, de modo que la licencia efectiva de las DLL distribuidas es la LGPL versión 3. Excluyen las bibliotecas que solo admiten GPL, como libx264 y libx265.

La página legal de FFmpeg ofrece una lista de comprobación para cumplir la LGPL. Aplicada a Rodaje significa lo siguiente:

1. **Ninguna compilación distribuida lleva `--enable-gpl` ni `--enable-nonfree`.** Se comprueba leyendo la línea de configuración que imprime `ffmpeg -version` o la que acompaña a la compilación, y verificando que no aparezcan bibliotecas GPL como libx264 o libx265. Además, `FfmpegLibrary` se niega a arrancar si `avcodec_license()` no empieza por «LGPL» o si la configuración de alguna biblioteca cargada lleva `--enable-gpl` o `--enable-nonfree`, y cuando junto al ejecutable hay una carpeta `ffmpeg\` ignora `RODAJE_FFMPEG`, así que la distribución solo carga el FFmpeg que trae. `FfmpegExecutable` aplica la misma regla al `ffmpeg.exe` de los proxies (ver más abajo).
2. **Enlace dinámico.** Las bibliotecas de FFmpeg se distribuyen como DLL separadas (`avcodec-*.dll`, `avformat-*.dll`, etc.) que Rodaje carga en tiempo de ejecución a través de FFmpeg.AutoGen. Nunca se enlazan estáticamente ni se empaquetan dentro del ejecutable de AOT.
3. **DLL sin renombrar ni ofuscar.** Se conservan los nombres originales, de modo que el usuario pueda reemplazarlas por otra compilación compatible, que es el derecho central de la LGPL.
4. **Código fuente correspondiente.** Junto a cada versión distribuida se ofrece el código fuente exacto de las DLL incluidas, o una oferta escrita de entregarlo. Como las compilaciones de BtbN se publican con etiqueta y fecha, se registra la URL exacta de la versión usada y su hash, y se archiva el tarball del código fuente correspondiente.
5. **Línea de configuración documentada.** Se incluye un archivo de texto con la línea de `configure` de la compilación distribuida.
6. **Crédito en "Acerca de".** El cuadro "Acerca de" de Rodaje menciona que el programa usa bibliotecas de FFmpeg bajo la LGPL, con enlace a https://ffmpeg.org y al código fuente. Desde el 11-oct «Ayuda → Acerca de Rodaje» ya lo menciona; falta el enlace al código fuente archivado.
7. **EULA y ingeniería inversa.** Si algún día Rodaje tiene términos de uso, no pueden prohibir la ingeniería inversa de la parte LGPL ni reclamar la propiedad de FFmpeg; deben indicar quiénes son sus autores. La GPLv3 de Rodaje no impone ninguna prohibición de ese tipo (análisis en «Compatibilidad con la GPLv3»).
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

Rodaje mitiga este riesgo de tres formas. Primero, la codificación H.264 y HEVC se delega en los codificadores por hardware de la GPU (por ejemplo NVENC) o en los de Media Foundation incluidos en Windows, cuyos fabricantes gestionan sus propias licencias de patentes. Segundo, se priorizan formatos libres de regalías como AV1, VP9 y Opus cuando el usuario no necesite un formato concreto. Tercero, Daniel no vende ni distribuye comercialmente el programa mientras no se haga una revisión legal específica. La GPLv3 sí permite que terceros lo vendan, y su Sección 11 trata las licencias de patentes que cada distribuidor obtenga; la exposición de quien distribuya es asunto suyo, pero conviene que los avisos lo recuerden. Nada de esto es asesoría legal: si Rodaje se distribuyera comercialmente, habría que consultar a un abogado de propiedad intelectual.

## Modelos de inteligencia artificial

Solo se usan modelos cuya licencia permita expresamente la redistribución y el uso comercial, porque viajarán dentro de la carpeta del programa. Por cada modelo se registra en `LICENCIAS.md` su origen, su licencia, la URL de donde se descargó y su hash SHA-256, y el script de entorno verifica ese hash al descargarlo.

| Modelo | Uso previsto | Licencia | Estado | Fuente |
|---|---|---|---|---|
| Whisper (pesos de OpenAI) | Subtítulos automáticos | MIT, que cubre código y pesos | Apto | https://github.com/openai/whisper/blob/main/LICENSE |
| MODNet | Quitar fondo de personas | Apache-2.0 (el README extiende la licencia a "código, modelos y demos", salvo los GIF) | Candidato, confirmar pesos concretos | https://github.com/ZHKKKe/MODNet |
| U²-Net | Quitar fondo general | Apache-2.0 para el repositorio; los pesos no tienen licencia aparte y algunos se entrenaron con conjuntos de datos de terceros | Candidato, revisar conjuntos de datos | https://github.com/xuebinqin/U-2-Net |
| BiRefNet | Quitar fondo de alta calidad | MIT en el código y en la ficha de Hugging Face de los pesos | Candidato | https://huggingface.co/ZhengPeng7/BiRefNet |
| RMBG-1.4 y RMBG-2.0 de BRIA | Quitar fondo | Uso no comercial; lo comercial exige acuerdo aparte. Incompatible con la GPLv3, que permite el uso comercial | Excluido | https://huggingface.co/briaai/RMBG-1.4 |

La licencia del repositorio no siempre cubre los pesos, y los pesos pueden heredar restricciones de los datos con que se entrenaron. Antes de incluir cualquiera de los candidatos hay que leer la licencia del archivo exacto que se va a descargar.

## Marcas

Rodaje no usa marcas ajenas en su código, su interfaz, sus íconos ni sus recursos. En particular, no aparecen CapCut, ByteDance, TikTok, Adobe, Photoshop ni Premiere en ningún elemento visible del programa. Los documentos de investigación pueden mencionar programas existentes de forma descriptiva, solo para explicar de dónde viene una idea. El propio nombre Rodaje se eligió para alejarse de "PhotoShot" y de Photoshop (ver `docs/decisiones.md`, D-001), y su registro debe verificarse antes de publicar.

## Privacidad y secretos

Rodaje no envía telemetría ni datos de uso a ningún servidor. Las funciones que usen servicios externos, como un asistente de lenguaje natural, serán opcionales, explicarán qué se envía y leerán su clave desde un archivo `.env` local. El `.env` está en `.gitignore` y nunca se sube; en el repositorio solo existe `.env.example`, con los nombres de las variables y sin valores.

## Avisos de terceros

`THIRD-PARTY-NOTICES.txt` contiene los avisos de licencia de todo componente de terceros que viaje dentro de `dist\`. Se regenera en cada versión, se copia junto a `Rodaje.exe` y debe acompañar siempre al ejecutable. `LICENCIAS.md` es la vista para desarrolladores: incluye también las dependencias que solo se usan para compilar o probar.

## Lista de comprobación antes de hacer público el repositorio

Esta lista se recorre completa, en orden, antes de cambiar la visibilidad del repositorio a pública o de entregar un ejecutable a terceros. Cada punto se marca con la fecha y el resultado.

- [ ] **Licencia.** `LICENSE` contiene la cabecera de copyright con el año actualizado, el texto literal de la GNU GPLv3 (SHA-256 del tramo GPL `3972dc9744f6499f0f9b2dbf76696f2ae7ad8af9b23dde66d6af86c9dfb36986`, el de https://www.gnu.org/licenses/gpl-3.0.txt) y los términos adicionales revisados por el abogado; `TRADEMARK.md` está al día; los pendientes de [`pendientes-legales.md`](pendientes-legales.md) están resueltos.
- [ ] **Crédito upstream.** El exe publicado muestra «Código abierto upstream: Rodaje» en la barra de título y el aviso completo en «Ayuda → Acerca de Rodaje», y el enlace abre https://github.com/Dathinel/rodaje, que ya es público (antes de serlo el enlace da 404).
- [ ] **Código fuente.** Cada ejecutable entregado va acompañado del código fuente correspondiente de Rodaje o de un enlace a él (GPLv3, Sección 6), además del de FFmpeg.
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
