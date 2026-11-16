# FFmpeg LGPL v3: Linux x64 y macOS arm64 (D-066, fase 2)

La compilación se pide a mano en `.github/workflows/ffmpeg-lgpl.yml`, con `destino=linux64`,
`macarm64` o `los-dos`. Solo produce artefactos durante 90 días; no publica releases.
La compilación real y la instalación deben pasar en CI antes de considerar los binarios aptos.

FFmpeg: `2a571b606854520cf89804d8030c8b328e621689` (release/9.0),
`--extra-version=rodaje`. Paquetes:

- `ffmpeg-n9.0.2-17-g2a571b6068-rodaje1-linux64-lgpl-shared-9.0.tar.xz`
- `ffmpeg-n9.0.2-17-g2a571b6068-rodaje1-macarm64-lgpl-shared-9.0.tar.xz`

Cada paquete tiene una carpeta interior del mismo nombre sin `.tar.xz`, con `bin/ffmpeg`,
`bin/ffprobe`, las siete bibliotecas compartidas de FFmpeg, `configure.txt`, `auditoria.txt`
y los avisos de licencia. Se conserva también el contenido que empaqueta BtbN en Linux.
Cada artefacto binario trae el tar, su `.sha256` y ambos informes. La fuente va en un artefacto
separado, `rodaje-ffmpeg-lgpl-fuente-linux-x64.tar.xz` o
`rodaje-ffmpeg-lgpl-fuente-osx-arm64.tar.xz`, con su `.sha256`.

## Imagen Linux y receta Windows

Scripts de [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds/tree/6c9aec5fc9a72ec3abedd1fa84db141fa18cf52b):
`6c9aec5fc9a72ec3abedd1fa84db141fa18cf52b`, tag `autobuild-2026-09-30-13-08`.

Imagen fijada:

```text
ghcr.io/btbn/ffmpeg-builds/base-linux64@sha256:fa86b2845524170871766de2061b54974e1ef0ac5b4bdc2401b494c3f46d5968
```

Verificada el 2026-11-16 con `docker buildx imagetools inspect` y la API OCI de GHCR;
es el índice que apuntaba a `latest` durante la consulta. Su manifiesto `linux/amd64` es
`sha256:4852c800a40494226a37cce8400b36902b92642c3659baf377cb61f81e0c2e0c`.
La configuración OCI (`sha256:16f8503d0f35c2f37c201fa69f64ae74030cd1612925a44e54e439f91c2358b7`)
indica creación `2026-10-31T18:14:36.570791247Z`.

**Límite de procedencia:** no está comprobado que sea la imagen del tag de septiembre ni la
más próxima de todo el historial. Es la imagen de ejecución próxima que se pudo verificar:
la API de versiones de paquetes devolvió 403 por falta del alcance `read:packages`;
el registro público solo enumeró `latest`, `cache`, `latest-x86` y `cache-x86`.
La compilación no necesita ese permiso ni resolver `latest`.

Linux aplica las mismas exclusiones que Windows: etapas `chromaprint` y `zvbi`, plugins
`fast_float` y `threaded` de lcms2. Conserva el límite de memoria de 8 GiB y la construcción
de una etapa por vez. Después de compilar fija con `patchelf` `$ORIGIN` en las bibliotecas
y `$ORIGIN/../lib` en los ejecutables para permitir también cargar las bibliotecas desde Rodaje.
El auditor no admite dependencias dinámicas adicionales a la lista solicitada.

`compilar-ffmpeg-lgpl.sh` admite un primer argumento opcional `win64|linux64`.
Sin él, sigue usando `win64`, el mismo volumen, `/entrada`, `/salida`, los mismos argumentos
de BtbN y la copia de `artifacts/*.zip`. Solo Linux admite las tres variables de rutas
`RODAJE_FFMPEG_TRABAJO`, `RODAJE_FFMPEG_ENTRADA` y `RODAJE_FFMPEG_SALIDA`.
El `.ps1` de Windows no cambia; tampoco sus pins, comprobaciones ni empaquetado.
No se afirma haber reproducido aquí la huella del ZIP: eso requeriría volver a compilar Windows.

## Fuentes de Mac

El manifiesto ejecutable `build/ffmpeg-lgpl-fuentes.txt` fija nombre, versión, SHA-256, URL oficial
y licencia. Las huellas se calcularon descargando los archivos completos, no a partir del nombre.
Se contrastaron los textos de licencia dentro de los tarballs. La versión de Kvazaar trae
también componentes ISC; se preservan sus avisos. Las concesiones de patentes de los proyectos
se conservan junto a los avisos de copyright.

| Componente | Versión | Licencia | SHA-256 | URL oficial |
|---|---|---|---|---|
| OpenH264 | 2.6.0 | BSD-2-Clause | `558544ad358283a7ab2930d69a9ceddf913f4a51ee9bf1bfb9e377322af81a69` | [tar.gz](https://github.com/cisco/openh264/archive/refs/tags/v2.6.0.tar.gz) |
| dav1d | 1.5.4 | BSD-2-Clause | `686616b7c69eb88d44459391ab25cac13b6647a3b288835c5784e71c1514a5c5` | [tar.xz](https://downloads.videolan.org/pub/videolan/dav1d/1.5.4/dav1d-1.5.4.tar.xz) |
| libvpx | 1.15.2 | BSD-3-Clause | `26fcd3db88045dee380e581862a6ef106f49b74b6396ee95c2993a260b4636aa` | [tar.gz](https://github.com/webmproject/libvpx/archive/refs/tags/v1.15.2.tar.gz) |
| Opus | 1.5.2 | BSD-3-Clause | `65c1d2f78b9f2fb20082c38cbe47c951ad5839345876e46941612ee87f9a7ce1` | [tar.gz](https://downloads.xiph.org/releases/opus/opus-1.5.2.tar.gz) |
| LAME | 3.100 | LGPL-2.0-or-later | `ddfe36cab873794038ae2c1210557ad34857a4b6bdc515785d1da9e175b1da1e` | [tar.gz](https://downloads.sourceforge.net/project/lame/lame/3.100/lame-3.100.tar.gz) |
| SVT-AV1 | 3.1.0 | BSD-3-Clause-Clear | `3999586c261dc3d8690fd1489fc74da4e0fdff9159c8ce2b76ddfac001ad96d3` | [tar.gz](https://gitlab.com/AOMediaCodec/SVT-AV1/-/archive/v3.1.0/SVT-AV1-v3.1.0.tar.gz) |
| Kvazaar | 2.3.2 | BSD-3-Clause | `35e506a6d2be9ae343f17787eb80fa214492b276e9c88b6e93749050ae380070` | [tar.xz](https://github.com/ultravideo/kvazaar/releases/download/v2.3.2/kvazaar-2.3.2.tar.xz) |
| libwebp | 1.6.0 | BSD-3-Clause | `e4ab7009bf0629fd11982d4c2aa83964cf244cffba7347ecd39019a9e38c4564` | [tar.gz](https://storage.googleapis.com/downloads.webmproject.org/releases/webp/libwebp-1.6.0.tar.gz) |
| FFmpeg | `2a571b606854520cf89804d8030c8b328e621689` | LGPL-3.0-or-later con esta configuración | `f02fc0809a13f37e3d4e1f74254d07e035a5ef01a6d91e727b915b1764d97540` | [tar.gz](https://github.com/FFmpeg/FFmpeg/archive/2a571b606854520cf89804d8030c8b328e621689.tar.gz) |
| BtbN (solo Linux) | `6c9aec5fc9a72ec3abedd1fa84db141fa18cf52b` | MIT | `81da4f7c878dc0f60e6b8d0049a112e32178e20f025d468fe4e99e77b2dfe0ec` | [tar.gz](https://github.com/BtbN/FFmpeg-Builds/archive/6c9aec5fc9a72ec3abedd1fa84db141fa18cf52b.tar.gz) |

Mac usa `macos-15` con comprobación de `uname -m == arm64`, el SDK de Xcode y un objetivo
mínimo macOS 13.0. Homebrew aporta nasm, meson, ninja, cmake y pkg-config, nunca bibliotecas
del producto. Las ocho dependencias se instalan estáticas con PIC en un prefijo privado;
`PKG_CONFIG_LIBDIR` queda limitado a él. FFmpeg desactiva la autodetección y activa solo las
ocho dependencias, VideoToolbox y AudioToolbox, además de sus componentes internos LGPL.
Los `install_name` son `@rpath` y las rutas de carga `@loader_path/../lib`.
La firma ad-hoc de los Mach-O no usa identidad de Apple ni secretos.
libvpx usa `arm64-darwin24-gcc` (su destino `arm64-darwin-gcc` selecciona iOS);
LAME recibe explícitamente `aarch64-apple-darwin` porque su `config.guess` de 2017
identifica Darwin moderno como x86_64. Se desactiva su ensamblador nasm, que es de x86.
OpenH264 genera su `.pc` estático con `-lc++`, en vez del `-lstdc++` predeterminado.

El tarball de FFmpeg no lleva `.git`: la receta escribe `VERSION=n9.0.2-17-g2a571b6068` antes
de compilar. Este cambio, los ajustes de `.pc` de Kvazaar y todos los comandos de construcción
están en los scripts incluidos. La fuente contiene los nueve tarballs exactos, `config.log`,
`config.h`, `config.mak`, versiones de herramientas, scripts, workflow, ambos informes y
`LEEME.txt` con las huellas de cada archivo. En Linux incluye los tarballs de las etapas
referenciadas por `Dockerfile.generado`, FFmpeg y BtbN originales, la referencia e inspección
de la imagen y la receta de transformación.

**Pregunta para la revisión de licencias antes de publicar:** ¿qué revisión de patentes se
aplicará a OpenH264 compilado desde fuentes por Rodaje y a la cláusula de patentes de
BSD-3-Clause-Clear de SVT-AV1? Esta receta registra las licencias de copyright y conserva
sus avisos; no decide esa cuestión ni afirma cobertura de los binarios propios por Cisco.

## Ejecutar y comprobar

En un anfitrión dedicado con las herramientas del workflow, sin cambiar `build/preparar-entorno.ps1`:

```bash
# Linux x64 (Docker, buildx, patchelf, readelf, curl, tar, xz y PowerShell 7):
bash build/compilar-ffmpeg-lgpl-linux.sh "$PWD/salida-ffmpeg/linux64"
bash build/probar-ffmpeg-lgpl.sh "$PWD/salida-ffmpeg/linux64/ffmpeg-n9.0.2-17-g2a571b6068-rodaje1-linux64-lgpl-shared-9.0.tar.xz" linux "$PWD/prueba-linux"

# macOS arm64 (herramientas anteriores de Homebrew, Xcode, xz y PowerShell 7):
bash build/compilar-ffmpeg-lgpl-mac.sh "$PWD/salida-ffmpeg/macarm64"
bash build/probar-ffmpeg-lgpl.sh "$PWD/salida-ffmpeg/macarm64/ffmpeg-n9.0.2-17-g2a571b6068-rodaje1-macarm64-lgpl-shared-9.0.tar.xz" mac "$PWD/prueba-mac"
```

Cada compilación requiere un `salida/trabajo` nuevo, para no mezclar prefijos o informes.
Mac requiere que la ruta de compilación no contenga espacios. La prueba de reubicación sí
introduce un espacio en el nombre final para comprobar la carga desde esa ubicación.
Para reutilizar una fuente correspondiente descomprimida, exportar `RODAJE_FFMPEG_FUENTES`
con su ruta absoluta. Se verifican las mismas huellas; Linux también reutiliza `etapas/`.
La receta BtbN puede descargar etapas no utilizadas: solo se empaquetan las que monta su Dockerfile.

`probar-ffmpeg-lgpl.sh` calcula `RODAJE_FFMPEG_SHA256`, comprueba el sidecar, fija
`RODAJE_FFMPEG_URL` al tar local y llama a `pwsh build/preparar-entorno.ps1 -SoloFFmpeg`.
Usa `RODAJE_ENTORNO` temporal. Después mueve la carpeta, elimina del entorno las variables
de búsqueda de bibliotecas, audita, comprueba las ABI esperadas y los codificadores
`libopenh264`, `libsvtav1`, `libvpx-vp9`, `libopus`, `libmp3lame`, `libkvazaar`, `libwebp`,
`prores_ks`, `ffv1`, `aac`, y `h264_videotoolbox` en Mac. Genera un cuadro FFV1 y lo lee con
ffprobe, sin pantalla ni GPU. No ejecuta ffplay.

`auditar-ffmpeg.sh` inspecciona todos los ELF/Mach-O, no solo los dos programas. Rechaza
arquitecturas equivocadas, dependencias fuera de la lista, referencias propias ausentes,
enlaces externos, rutas de carga absolutas, licencia distinta de LGPL v3 y cualquiera de
las trece opciones prohibidas de Windows. Un fallo conserva un informe con `ERROR`;
solo el éxito escribe `RESULTADO: OK`. El informe enumera las dependencias observadas.
Esto no detecta por sí solo código GPL enlazado estáticamente ni dependencias cargadas por
`dlopen`: las exclusiones y la revisión de fuentes son necesarias además de esta auditoría.

Comprobaciones sin compilar:

```bash
for f in build/compilar-ffmpeg-lgpl*.sh build/ffmpeg-lgpl-comun.sh build/auditar-ffmpeg.sh build/probar-*ffmpeg*.sh; do bash -n "$f"; done
python -c "import yaml,sys; yaml.safe_load(open(sys.argv[1]))" .github/workflows/ffmpeg-lgpl.yml
bash build/probar-auditoria-ffmpeg.sh
```

La prueba del auditor usa salidas simuladas de las herramientas y ejecutables; comprueba
las decisiones de aceptación/rechazo, no el resultado de una compilación real.
La CI ejecuta también ShellCheck sobre los scripts nuevos. El workflow usa únicamente
`contents: read`, no utiliza secretos y publica los artefactos solo después de pasar la instalación.

Validación local del 2026-11-16: sintaxis de los siete scripts, YAML y restricciones del workflow,
42 casos del auditor, las diez huellas de descargas y el empaquetado de una fuente simulada
(estructura, informes y huellas internas de `LEEME.txt`). La receta Windows anterior y la
generalizada produjeron árboles preparados de BtbN idénticos byte a byte; con Docker simulado,
también coincidieron las órdenes y los archivos seleccionados/copias. No había ShellCheck
disponible en el equipo. No se compilaron binarios ni se lanzó la CI; Claude debe subir la rama
y ejecutar el workflow para comprobar ambos RID.
