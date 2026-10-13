# Descarga de complementos y distribución

Cómo baja Rodaje sus paquetes, módulos y modelos, y cómo se publican. Decisión de fondo en D-031; formato en [formato-paquete.md](formato-paquete.md) y módulos en el [protocolo de módulos](protocolo-modulos.md).

## Cómo funciona

- **Índice firmado.** La app lee `indice.json` e `indice.json.sig` de la última release de `Dathinel/rodaje-catalogo` (`releases/latest/download/…`, sin pasar por la API de GitHub). La firma es ECDSA P-256 sobre SHA-256 de los bytes exactos, en DER y en base64. Se comprueba con la clave pública que va dentro del exe (`src/Rodaje.App/Services/Packages/clave-catalogo.txt`) **antes** de leer el JSON. No se acepta un índice con `generated` más viejo que el último visto (`descargas\indice-visto.txt`).
- **Orígenes.** Cada entrada solo puede apuntar a `https://github.com/Dathinel/<repo>/releases/download/<tag>/<archivo>` o a `https://huggingface.co/<repo>/resolve/<commit de 40 hex>/<archivo>`. Las redirecciones a los CDN se siguen sin fijar el host: lo que protege es el hash firmado.
- **Archivo.** `FileDownloader` baja a `<sha256>.paquete.parcial` en `%LOCALAPPDATA%\Rodaje\complementos\descargas`, calcula el SHA-256 al vuelo, reanuda con `Range` tras un corte, una cancelación o un apagón (y si la parte previa venía dañada, la baja entera una vez), nunca acepta más bytes que el tamaño publicado, y solo con hash y tamaño correctos vacía al disco y renombra. Si no coinciden, borra el archivo.
- **Instalación.** La de siempre (`PackageInstaller`): extracción aparte y cambio de nombre, con la recuperación tras apagón al arrancar. Lo descargado se borra después de instalar.
- **Modelos.** Después de instalar o actualizar un módulo desde el catálogo, `PackageService.EnsureModelsAsync` lee `module.models` de su `paquete.json` (`ModuleModels`) y baja cada modelo con `ModelStore` (la misma descarga: hash al vuelo y reanudación) a `complementos\modelos\<sha256>`, una vez por contenido. Luego lo deja donde el módulo lo busca, `<carpeta del módulo>\<file>` (por ejemplo `modulos\modulo-voz\modelos\voz\kokoro-v1.0-uint8.onnx`), con un enlace duro o, si no se puede, una copia. Complementos muestra el avance de cada modelo; si la descarga se corta o se cancela, el módulo queda instalado con «Bajar modelos», que sigue desde donde quedó. Al desinstalar se borran del almacén los modelos que ya no usa ningún módulo. La distribución completa trae cada modelo ya en `app\modulos\<id>\<file>`.
- **Módulos abiertos.** Desinstalar o actualizar un módulo cierra antes su proceso (`ModuleManager.CloseModuleAsync`, desde `PackageService`): sus trabajos se cancelan y el instalador reintenta unos instantes el cambio de nombre de la carpeta, por si Windows aún no soltó el exe.
- **Ids nuevos.** Los módulos que se probaron como `rodaje-audio-ia`, `rodaje-vision` y `rodaje-raw` se llaman `modulo-audio-ia`, `modulo-vision` y `modulo-raw`; lo instalado con el id viejo se migra al arrancar (`PackageService.MigrateRenamedModules`).
- **Sin clave** en el exe, la descarga queda desactivada y la página dice que se instale desde archivo.

## Estado

- **Clave**: la privada vive fuera del repositorio, en `%USERPROFILE%\.rodaje-claves\catalogo-privada.pem` de quien publica (`*.pem` está en `.gitignore`), con al menos dos respaldos físicos fuera del equipo; la pública, en `clave-catalogo.txt`. Si se pierde, los Rodaje ya publicados no aceptarán índices nuevos; si alguien la copia, se genera otra con `-NuevaClave -Reemplazar` y se publica una versión nueva de Rodaje.
- **Catálogo**: `Dathinel/rodaje-catalogo` (público, CC0, sin código), release `v6` (12-oct, 79 entradas) con todo lo publicado:

| Repositorio | Versiones | `.paquete` | Modelos |
|---|---|---|---|
| `rodaje-transiciones` | 1.0.0, 1.1.0 | 24 KB | — |
| `rodaje-filtros` | 1.0.0, 1.1.0 | 3,9 MB | — |
| `rodaje-fuentes` | 1.0.0 | 5,1 MB | — |
| `rodaje-titulos` | 1.0.0 | 25 KB | — |
| `rodaje-zonas` | 1.0.0 | 20 KB | — |
| `rodaje-camaras` | 1.0.0 | 1,9 MB | — |
| `rodaje-sonidos` | 1.0.0 | 15,7 MB | — |
| `rodaje-preajustes-pro` | 1.0.0 | 11 KB | — |
| `rodaje-360` | 1.0.0 | 9 KB | — |
| `modulo-subtitulos` | 1.0.0 y 1.1.0 | 38,9 MB | 2, 729 MB (Hugging Face); 1.1.0: 3, 786 MB (suma Whisper base) |
| `modulo-traducir` | 1.0.0, 1.0.1 | 61,2 MB | 1, 2,3 GB (Hugging Face) |
| `modulo-voz` | 1.0.0 | 7,7 MB | 39, 198 MB (Hugging Face y los léxicos misaki en su release) |
| `modulo-audio-ia` | 1.0.0 | 7,5 MB | 4, 10 MB (en su release: Silero VAD y DeepFilterNet3) |
| `modulo-vision` | 1.0.0 y 1.1.0 | 17,3 MB | 4, 211 MB (Hugging Face) |
| `modulo-raw` | 1.0.0 | 1,8 MB | — |
| `rodaje-codificadores-gpl` | 0.2.0 | 18,9 MB | — (su release adjunta la fuente correspondiente, 150 MB) |

  La 1.0.1 de `modulo-traducir` carga el modelo aunque la carpeta tenga «ñ» o tildes (página de códigos UTF-8 en su manifiesto: LLamaSharp pasa la ruta como texto ANSI).
  `rodaje-codificadores-gpl` (GPL-3.0-or-later, D-034) es un módulo con `protocol: 0`: el `ffmpeg.exe` GPL que Exportar usa para x264 y x265. Lo arma `build\armar-codificadores-gpl.ps1` (con sus avisos de licencia en `licencias/` y un `LEEME.md` con la fuente y la nota de patentes) y su release `v0.2.0` adjunta el `.paquete`, `rodaje-codificadores-gpl-0.2.0-fuente.tar` y el `.sha256` de cada uno.
  Cada repositorio de módulo (GPLv3 con los términos de Rodaje) trae README, `LICENCIAS.md`, `licencias/` y el manifiesto; su release `v1.0.0` adjunta el `.paquete`, el código con el que se compiló (`codigo-fuente-<id>-<commit>.zip`) y, en `modulo-audio-ia` y `modulo-voz`, los modelos que no están en Hugging Face con su licencia (MIT, Apache-2.0).
- **Prueba contra lo publicado** (`--filter Categoria=Red` con `RODAJE_PRUEBAS_RED=1`; la CI y `probar.ps1` la excluyen porque necesita red):
  - `CatalogoRealTests` (`Rodaje.Packages.Tests`) baja el índice real, verifica la firma con la clave del repo e instala transiciones y filtros; una firma o un índice alterados se rechazan.
  - `CatalogoRealExtremoAExtremoTests` (`Rodaje.App.Tests`) hace lo de la página de Complementos con `PackageService`: instala transiciones y filtros 1.0.0 (con el hash del índice) y los actualiza a 1.1.0 desde el catálogo; instala fuentes, títulos y zonas; aplica el estilo «Karaoke pop» (Montserrat, resaltado `#FFD400`); registra las fuentes en la colección privada y comprueba que DirectWrite resuelve «Montserrat» y «Nunito» (antes de registrarlas no las encontraba); instala `modulo-audio-ia`, baja sus 4 modelos y detecta la voz de un archivo de prueba; lo desinstala con su proceso abierto y desinstala el resto. Además: cada uno de los seis módulos se instala y su proceso saluda con su id y la 1.0.0; una descarga cortada al 40 % sigue con `Range` pidiendo solo lo que falta; y cada uno de los 50 modelos del índice responde con el tamaño firmado. Tiempos y tamaños en `TestResults\catalogo-real.txt`.
  - `CatalogoRealExtremoAExtremoTests.Nuevos.cs` usa todo el catálogo en un proyecto real: instala los nueve complementos de contenido; aplica el look S-Log3 de `rodaje-camaras` (su LUT da 0,430 al gris del 18 %), el «Paneo de 360°» de `rodaje-360` (keyframes de guiñada), el filtro «Cine», una transición en el corte, el estilo «Karaoke pop» con su fuente y un juego de zonas; suelta un sonido de `rodaje-sonidos`; exporta con «Emisión HD 1080i25 (EBU R128)» de `rodaje-preajustes-pro` (ProRes entrelazado, `field_order=tb`, 25 fps) y con «Video 360 4K (H.264)» (3840×1920 con metadatos esféricos), y desinstala todo. Otra prueba instala `modulo-traducir` 1.0.0, lo actualiza a 1.0.1 y baja los modelos de los seis módulos (3,5 GB), y cada uno hace su función: subtítulos, traducción, voz, reducción de ruido, quitar fondo, reencuadre y CinemaDNG.
  - `CatalogoRealExtremoAExtremoTests.CodificadoresGpl.cs` instala `rodaje-codificadores-gpl` desde el catálogo, comprueba sus avisos en `licencias/` y que la fuente del `LEEME.md` esté publicada con esa huella, exporta un proyecto con x264 y x265 en 8 y 10 bits con audio Opus y MP3 por su `ffmpeg.exe`, verifica cada archivo con el ffprobe LGPL (códec, `pix_fmt`, cuadros, audio y la firma del codificador en el archivo) y lo desinstala. Corrida del 12-oct (catálogo `v6`, 25 s): instalar, 18,9 MB en 1,2 s; 181 cuadros 1280×720 en 2,3–2,4 s con x264 y 3,4–4,1 s con x265.
  - Corrida del 12-oct, 22:40 (catálogo `v4`, 6 de 6 en 9 min 10 s): los nueve de contenido se instalan en 0,3–1,2 s cada uno (sonidos, 15,7 MB, en 1,2 s); exportar el proyecto en 1080i25 tarda 45,9 s (170,6 MB) y el 360 4K, 14,4 s; los modelos de los seis módulos bajan en 4 min 42 s (Qwen, 2,3 GB, en 3 min 55 s), y subtítulos tarda 85 s, traducir 30 s, voz 6,9 s, ruido 2,7 s, quitar fondo 9,9 s, reencuadre 4 s y CinemaDNG 0,9 s, incluido abrir cada módulo.
  - Corrida del 12-oct (Wi-Fi doméstica): transiciones 1.0.0 → 1.1.0 en 0,5 s y filtros en 0,8 s; fuentes (5,1 MB) en 1,2 s; `modulo-audio-ia` con sus modelos (7,5 + 10,4 MB) en 3,7 s y la detección de voz, abriendo el módulo, en 4,7 s; los seis módulos instalados y saludando en 17 s; la descarga cortada en 2,1 de 5,1 MB terminó pidiendo 3,1 MB.

## Publicar una versión nueva

1. **Paquete de contenido**: sube `version` en su `paquete.json`, `empaquetar.ps1` (deja `dist\<id>-<versión>.paquete`), commit y push, y `gh release create v<versión> dist\<id>-<versión>.paquete` en su repo.
   **Módulo**: sube `version` en su `paquete.json` y en su `Program.cs`, `build\empaquetar-modulo.ps1 -Modulo <id>` (deja `dist\modulos\<id>-<versión>.paquete`: Native AOT, o autocontenido si la biblioteca no admite AOT, como Traducir con LLamaSharp; sin modelos) y `gh release create v<versión>` en su repo con el `.paquete`, el código (`git archive`) y los modelos que no salen de Hugging Face. `module.entry` va en la raíz del paquete y cada modelo en `modelos/…`, junto al exe.
2. **Índice**: `build\distribuir.ps1 -SoloIndice -Complementos <carpeta con <repo>\dist\>` deja `dist\distribucion\indice.json` con **todas** las versiones de cada `dist\` (de `..\Complementos\<repo>` y de `dist\modulos`) y los modelos de cada módulo. La app solo lee el índice de la última release, así que **cada índice lista todo lo publicado**, no solo lo nuevo, y su `generated` es posterior al anterior. Si en el equipo faltan `dist\` de algún repositorio, se bajan los `.paquete` del índice anterior (comprobando su SHA-256), se pasan todos con `-Paquetes` en el mismo orden más los nuevos, y se comprueba que el índice nuevo contenga cada entrada del anterior sin cambios (así se armó `v6`).
3. **Firma**: `build\firmar-indice.ps1 -Indice dist\distribucion\indice.json` firma y comprueba que la clave pública del repo corresponde a la privada.
4. **Catálogo**: `gh release create v<n+1> indice.json indice.json.sig -R Dathinel/rodaje-catalogo`. Las releases viejas se pueden dejar: la app solo toma la última y rechaza índices más viejos que el último visto.
5. **Comprobar**: `build\dotnet-local.cmd test tests\Rodaje.Packages.Tests --filter Categoria=Red` con `RODAJE_PRUEBAS_RED=1`.

**Distribución completa.** `distribuir.ps1` sin opciones publica la app (Native AOT: el lanzador `Rodaje.exe` más la carpeta `app\`) y deja además el instalador, `Rodaje-<versión>-portable.zip`, `Rodaje-<versión>-completo.zip`, `SHA256SUMS.txt` y las notas (ver [arranque-y-distribucion.md](arranque-y-distribucion.md#versión-e-instalador-v1)); la completa trae la versión más nueva de cada `.paquete` extraída en `app\paquetes\<id>` o `app\modulos\<id>`, y cada modelo de un módulo en `app\modulos\<id>\<file>` (de `.entorno\modelos` o bajado y verificado). Los módulos de `modules\` que no llegaron como `.paquete` se publican ahí mismo, sin repetir los que sí. Un modelo de más de 2 GiB (Qwen, 2,3 GB) queda fuera (límite de un asset de GitHub) y se baja desde el gestor. Los zip y el instalador van a la release de `rodaje` con `build\publicar-release.ps1`.

## Pruebas

`tests/Rodaje.App.Tests/Packages/ModuleInstallTests` (servidor local): instalar un módulo baja sus modelos a su carpeta (y reinstalar no baja nada); un modelo cortado deja el módulo instalado y se reanuda con `Range`; un modelo con otro hash no se coloca; actualizar un módulo que trabaja cancela el trabajo y cierra el proceso, y desinstalarlo abierto también, borrando sus modelos del almacén; los ids viejos se migran. `PackageServiceTests` comprueba que cada módulo de `modules\` está en el catálogo, en 1.0.0, con el exe en la raíz y modelos de orígenes permitidos.

`tests/Rodaje.Packages.Tests/Download`, con un servidor HTTP propio en 127.0.0.1 (sockets reales): 200 archivos con un byte cambiado y 100 índices con un hash cambiado rechazados y borrados; 1000 índices o firmas alterados rechazados; cortes de red con reanudación (`Range`, solo se piden los bytes que faltan); cancelar y seguir con otro proceso; parcial dañado; servidor sin `Range`; redirecciones; un apagón en cada etapa (descarga, extracción, entre apartar la versión vieja y poner la nueva) deja siempre una versión completa; firmas hechas por `firmar-indice.ps1` aceptadas por el programa.
