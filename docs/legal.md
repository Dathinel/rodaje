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

## Compatibilidad de la licencia de Rodaje

Análisis hecho el 11 de octubre de 2026 al pasar de MIT a PolyForm Noncommercial 1.0.0, leyendo el texto de `LICENSE` y las licencias de las dependencias. No cambia el código. Las citas entre comillas son traducciones libres; vale el texto en inglés. Las conclusiones son de trabajo y quedan **pendientes de revisión legal** (ver [`pendientes-legales.md`](pendientes-legales.md)).

### FFmpeg (LGPL): reemplazar las DLL y depurar la combinación

**Qué exige la LGPL.** La LGPL 2.1, sección 6, permite distribuir un programa que use la biblioteca «bajo los términos que elijas, siempre que esos términos permitan modificar la obra para uso propio del cliente y la ingeniería inversa para depurar esas modificaciones». Las DLL que se distribuyen son LGPL v3 (`--enable-version3`), cuya sección 4 («Combined Works») pide lo mismo: términos que, en conjunto, no restrinjan de hecho la modificación de las partes de la biblioteca contenidas en la obra combinada ni la ingeniería inversa para depurar esas modificaciones. Por eso Rodaje carga las DLL por enlace dinámico y sin renombrarlas, de modo que se puedan reemplazar.

**Qué dice la PolyForm Noncommercial.** Ninguna cláusula prohíbe la ingeniería inversa, el desensamblado ni el reemplazo de componentes. Las que importan:

- *Copyright License*: concede licencia para «todo lo que pudieras hacer con el software que de otro modo infringiría los derechos de autor del licenciante, para cualquier fin permitido».
- *Changes and New Works License*: concede licencia para «hacer cambios y obras nuevas basadas en el software para cualquier fin permitido».
- *Noncommercial Purposes* y *Personal Uses*: todo fin no comercial es permitido, incluido el uso personal para estudio, experimentos y pruebas.
- *Fair Use*: los términos no limitan los derechos de uso legítimo que dé la ley.
- *Definitions*: la licencia cubre solo «el software que el licenciante pone a disposición bajo estos términos», es decir, el código de Daniel; no se extiende a las DLL de FFmpeg, que siguen bajo la LGPL.

**Conclusión de trabajo.** Compatible. Quien usa Rodaje con un fin permitido puede reemplazar las DLL de FFmpeg, modificar la combinación para su propio uso y depurarla por ingeniería inversa: la PolyForm lo cubre con las cláusulas citadas y no contiene ninguna prohibición en contra. Quien quiere un uso comercial no tiene licencia de Rodaje, pero eso no restringe la modificación de FFmpeg: es la misma situación que la de cualquier programa no libre que enlaza FFmpeg, que la LGPL admite; las DLL siguen siendo LGPL y se pueden reemplazar o usar por separado. Dos condiciones para que siga así: (1) ningún texto añadido (EULA, términos del Windows App SDK, cuadro «Acerca de») puede prohibir la ingeniería inversa o la modificación de la combinación con FFmpeg; (2) la cláusula *Violations*, que termina la licencia de Rodaje, no afecta a los derechos que la LGPL da sobre FFmpeg, y conviene que los avisos lo digan. `THIRD-PARTY-NOTICES.txt` ya lleva un párrafo en ese sentido, pendiente de revisión legal.

### Windows App SDK: ¿basta la PolyForm como términos de usuario final?

**Qué exige Microsoft.** La cláusula 3.b de los términos del Windows App SDK (`license.txt` de los paquetes, resumido en `LICENCIAS.md`) pide, para cada archivo redistribuido: (i) que la aplicación añada funcionalidad principal significativa; (ii) que «distribuidores y usuarios finales externos acepten términos que protejan [ese código] y a Microsoft al menos tanto como este acuerdo»; y (iii) que el desarrollador indemnice a Microsoft por reclamaciones relacionadas con la distribución o el uso de la aplicación, salvo las basadas solo en el código redistribuible sin modificar.

**Conclusión de trabajo: no basta, hace falta un texto complementario.** La PolyForm solo licencia el software de Daniel (*Definitions*) y su exclusión de responsabilidad (*No Liability*) protege solo al licenciante. No dice nada de los archivos de Microsoft ni protege a Microsoft. Peor: concede a cualquiera, para fines no comerciales, el derecho a redistribuir (*Distribution License*) con la única condición de pasar los términos y el `Required Notice` (*Notices*), así que un redistribuidor no quedaría obligado por los términos de Microsoft que exige 3.b.ii. Hace falta un acuerdo de usuario final (EULA) o unos términos complementarios que acompañen a la distribución y que también obliguen a los redistribuidores. **No se redacta aquí:** es trabajo del abogado.

Cláusulas que ese texto debería cubrir, como lista para el abogado (**pendiente de revisión legal**):

1. Los archivos del Windows App SDK que viajan en `dist\` son de Microsoft, se licencian y no se venden, y conservan sus propios términos; la PolyForm no se aplica a ellos.
2. Prohibición de ingeniería inversa, descompilación y desensamblado **limitada a esos archivos** (equivalente a la 4.b de Microsoft), redactada de forma que no alcance a FFmpeg ni a la combinación de Rodaje con FFmpeg, para no chocar con la LGPL.
3. Prohibición de eludir sus limitaciones técnicas, de quitar los avisos de Microsoft y de redistribuirlos fuera de Rodaje o como producto independiente (4.a, 4.c y 4.e).
4. Exclusión de garantías y de responsabilidad que proteja también a Microsoft y a sus proveedores, no solo a Daniel.
5. Aviso de recopilación de datos (2.a): el software de Microsoft puede enviar datos de uso, cómo desactivarlo y el enlace a la declaración de privacidad.
6. Cumplimiento de las leyes de exportación (5).
7. Sin uso de marcas de Microsoft que sugiera respaldo (3.c.i).
8. Obligación de los redistribuidores de pasar estos mismos términos a quienes reciban copias.
9. Cómo se acepta el texto sin instalador (por ejemplo, un diálogo en el primer arranque) y si la aceptación vale en Colombia.
10. Fuera del EULA, para Daniel: el alcance real de la indemnización de 3.b.iii para una persona que distribuye gratis y sin ánimo de lucro.

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
