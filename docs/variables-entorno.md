# Variables de entorno

Todas las variables que lee Rodaje empiezan con `RODAJE_`. Ninguna es obligatoria: sin ellas todo funciona con los valores por omisión. Las que lee el editor están juntas en [`EnvironmentVariables`](../src/Rodaje.Media/EnvironmentVariables.cs) (en `Rodaje.Media`, la capa más baja que las necesita); las de las pruebas, en [`Skip`](../tests/Rodaje.TestMedia/Skip.cs) y en los generadores de `tests/Rodaje.TestMedia`.

## Editor y módulos

| Variable | Para qué sirve | Quién la lee |
|---|---|---|
| `RODAJE_ENTORNO` | Usa otro `.entorno` (SDK de .NET, FFmpeg, modelos) en vez del de la carpeta del proyecto; por ejemplo, el del checkout principal desde un worktree, para no descargarlo dos veces | `Probar.bat`, `build\dotnet-local.cmd` y los `.ps1` de `build\` (a través de `comun.psm1`); `FfmpegLocator` (busca `<entorno>\ffmpeg\bin`); `FfmpegTool` de las pruebas; el módulo de traducción (busca `<entorno>\modelos`) |
| `RODAJE_FFMPEG` | Carpeta con las DLL de FFmpeg (o ruta de `ffmpeg.exe` para las pruebas). El editor la ignora si junto a su ejecutable hay una carpeta `ffmpeg\`: la distribución usa siempre el FFmpeg LGPL que trae | `FfmpegLocator` (Rodaje.Media); `FfmpegTool` (pruebas) |
| `RODAJE_PROXIES` | Carpeta de los proxies; sin ella, `%LOCALAPPDATA%\Rodaje\proxies` | `ProxyStore.DefaultDirectory` (Rodaje.Media) |
| `RODAJE_DATOS` | Carpeta de `ajustes.json`, `atajos.json`, `complementos\` (paquetes, módulos y elementos propios) y `cache\` de la app: miniaturas, formas de onda y `cache\shaders` (el bytecode compilado de los shaders de efectos; borrarlo solo obliga a recompilar en segundo plano). Sin ella, `%LOCALAPPDATA%\Rodaje`. Sirve para probar la app sin tocar los datos de siempre | `AppDataPaths.Root` (Rodaje.App); `ShaderCache.DefaultDirectory` (Rodaje.Render) |
| `RODAJE_MODELOS` | Carpeta de modelos de IA que los módulos miran antes que `modelos\` junto a su exe y que el almacén compartido | Módulos de audio, subtítulos, traducción, visión y voz (`ModelFiles`, `Transcriber`, `Voices`) |
| `RODAJE_VOZ_MODELO` | Otro archivo `.onnx` de Kokoro para el módulo de voz | `KokoroEngine` (módulo de voz) |
| `RODAJE_VOZ_HILOS`, `RODAJE_VOZ_PARALELO` | Hilos de ONNX Runtime por síntesis (por omisión, la mitad de los núcleos, de 1 a 16) y síntesis en paralelo (un quinto de los núcleos, de 1 a 3) | `KokoroEngine`, `Synthesizer` (módulo de voz) |

## Medidas y autoprueba

| Variable | Para qué sirve | Quién la lee |
|---|---|---|
| `RODAJE_MEDIR_BIBLIOTECA` | Archivo donde la biblioteca escribe su medida: espera a que carguen los medios, desplaza la cuadrícula despacio y de golpe, anota los intervalos entre cuadros y las portadas, y cierra la app | `LibraryScrollMeasurement` (Rodaje.App) |
| `RODAJE_MEDIR_LINEA` | Ruta de un informe: al abrir, la línea de tiempo arma un proyecto de 200 clips con el primer video abierto (o uno sintético), mide desplazamiento, zoom y arrastre, escribe el informe y cierra la app | `TimelineBenchmark` (Rodaje.App) |
| `RODAJE_AUTOPRUEBA_CAPTURAS` | Carpeta donde `--autoprueba` guarda en PNG la ventana (dibujada por XAML) y cuadros del visor | `SelfTestRun` (Rodaje.App) |

## Pruebas

| Variable | Para qué sirve | Quién la lee |
|---|---|---|
| `RODAJE_PRUEBAS_LARGAS` | Con `1`, corren las pruebas largas y las medidas en tiempo real (aceptación, carga, NVENC, reloj de audio) y las rápidas de aceptación hacen la medida completa. Con ella, `probar.ps1` sin `-Filtro` suma una quinta etapa: las pruebas de tiempo con criterio para código optimizado, en Release, y `app.ps1 -Autoprueba` (publica con Native AOT si cambió `src\`, abre la app, recorre las pestañas del inspector, reproduce y falla si no sale con 0 o deja `rodaje-error.txt`). `build\aceptacion.ps1` la pone sola | `Skip.LongRun` y `Skip.AudioDevice`; `probar.ps1` |
| `RODAJE_PRUEBAS_COMPLETAS` | Con `1`, corre además el clip de 10 minutos en tiempo real con el dispositivo de audio. La pone `aceptacion.ps1 -Completa` | `Skip.CompleteRun` |
| `RODAJE_PRUEBAS_AUDIO` | Con `1`, corre la prueba que hace sonar un tono de 3 s | `Skip.AudioTone` |
| `RODAJE_PRUEBAS_RED` | Con `1`, corren las pruebas contra el catálogo publicado en GitHub (`Categoria=Red`); la CI y `probar.ps1` las excluyen | `CatalogoRealTests` (Rodaje.Packages.Tests) |
| `RODAJE_COMPLEMENTOS` | Carpeta con los repositorios de contenido (`rodaje-filtros`, `rodaje-transiciones`) para la prueba de punta a punta; sin ella se busca una carpeta `Complementos` subiendo desde las pruebas, y si no está la prueba se omite | `ContentFactAttribute` (Rodaje.App.Tests) |
| `RODAJE_CODIFICADORES_GPL` | Ruta del `.paquete` de los codificadores GPL; sin ella se usa el de `dist\complementos` y, si no hay, las pruebas se omiten | `GplEncoderPackTests` (Rodaje.App.Tests), `GplAddOnExportTests` (Rodaje.Export.Tests) |
| `RODAJE_MEDIOS_LOCALES` | Carpeta con videos propios (`.mp4`): `ExportLocalMediaTests` (`Categoria=Local`) los pone uno tras otro en una secuencia 9:16 con su audio y exporta todos y todos menos el último a MP4 en `%TEMP%\rodaje-pruebas-locales`, verificados con ffprobe. Sin ella se omiten; los medios nunca se versionan | `LocalMediaFactAttribute` (Rodaje.Export.Tests) |
| `RODAJE_MEDIA_CACHE` | Carpeta de los medios de prueba generados; sin ella, `TestResults\media-cache` en la raíz del repositorio | `TestMediaGenerator.DefaultCacheDirectory` |
| `RODAJE_REGENERAR_ESQUEMA` | Con `1`, la prueba del esquema reescribe `schema/rodaje-proyecto.v6.schema.json` (el de la versión actual) desde el código en vez de solo compararlo | `ProjectSchemaTests` (Rodaje.Core.Tests) |
| `RODAJE_MODULO_AUDIO_IA`, `RODAJE_MODULO_ECO`, `RODAJE_MODULO_RAW`, `RODAJE_MODULO_SUBTITULOS`, `RODAJE_MODULO_TRADUCIR`, `RODAJE_MODULO_VISION`, `RODAJE_MODULO_VOZ` | Ruta de otro exe del módulo (por ejemplo, el publicado con Native AOT) para las pruebas que lo abren como proceso; sin ella, el compilado junto a las pruebas | Pruebas de cada módulo y de `Rodaje.Modules.Protocol` |
| `RODAJE_WHISPER_CPU` | Otro archivo de `modelos` para el modelo de la CPU en las pruebas de subtítulos, para comparar | `Corpus` (Rodaje.Module.Subtitles.Tests) |
| `RODAJE_EXPORTAR_HIJO` | Interna: la pone la prueba de matar la exportación para el proceso hijo que exporta | `ExportCancelAcceptanceTests` (Rodaje.Export.Tests) |

## Scripts de `build\`

| Variable | Para qué sirve | Quién la lee |
|---|---|---|
| `RODAJE_SIN_PAUSA` | Con `1`, `Probar.bat` no espera una tecla al final (para lanzarlo desde otro programa) | `Probar.bat` |
| `RODAJE_SDK_URL`, `RODAJE_SDK_SHA512` | Juntas, cambian la descarga del SDK por otro `.zip` con su huella; solo para probar `preparar-entorno.ps1` | `build\preparar-entorno.ps1` |
| `RODAJE_CERTIFICADO`, `RODAJE_CERTIFICADO_CLAVE` | Ruta a un `.pfx` (con su contraseña en la segunda) o huella de un certificado del almacén: firma los `Rodaje.exe`, los módulos de la completa, el instalador y su desinstalador. Sin ella todo sale sin firmar | `Comando-Firma` de `comun.psm1` (`distribuir.ps1`, `instalador.ps1`) |
| `RODAJE_REPO` | Raíz de otro checkout donde buscar el clip de prueba (`TestResults\media-cache`) si no está en este | `build\probar-instalador.ps1` |

Las variables `DOTNET_*` y `NUGET_PACKAGES` no son de Rodaje: las fijan `dotnet-local.cmd` y `Usar-DotnetLocal` de `comun.psm1` para que el SDK local no use nada global.
