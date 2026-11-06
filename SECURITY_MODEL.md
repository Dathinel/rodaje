# Modelo de amenazas: Dathinel (Rodaje y herramientas de IA)

Basado en el código de los 24 repositorios, en `rodaje/SECURITY.md` (escrito por Daniel) y en las respuestas de Daniel del 5-nov-2026. No hay repositorio de infraestructura: son apps de escritorio para Windows, sin servidor propio; lo más parecido son los workflows de GitHub Actions.
Complementa a `SECURITY.md` (qué es un problema de seguridad y cómo reportarlo): aquí se describe el sistema, sus límites de confianza y qué riesgos se priorizan, para que las revisiones y los escaneos de seguridad partan de él. Las rutas que empiezan por otro repositorio (`lanzador/`, `modulo-*`…) son de los demás repositorios de Dathinel.


## Resumen del sistema y límites de confianza

- **Rodaje** (`Dathinel/rodaje`): editor de video nativo para Windows (.NET 10, Native AOT, WinUI 3). Se instala por usuario, sin permisos de administrador (`%LOCALAPPDATA%\Rodaje`). Lee medios con FFmpeg LGPL (DLL distribuidas), proyectos `.rodaje` (JSON, `src/Rodaje.Core/Serialization`, esquemas en `schema/`), LUT `.cube`, subtítulos y RAW/CinemaDNG.
- **Complementos**: los `.paquete` son zip con `paquete.json` (`src/Rodaje.Packages`). Los de contenido no traen código (`rodaje-transiciones`, `-filtros`, `-fuentes`, `-titulos`, `-zonas`, `-camaras`, `-sonidos`, `-preajustes-pro`, `-360`). Los módulos sí traen un ejecutable (`module.entry`).
- **Catálogo firmado** (`Dathinel/rodaje-catalogo`): `indice.json` + `indice.json.sig` (ECDSA P-256). La clave pública va en el exe (`src/Rodaje.App/Services/Packages/clave-catalogo.txt`) y la privada en el equipo de Daniel (`%USERPROFILE%\.rodaje-claves`). Las descargas solo vienen de `github.com/Dathinel/*/releases` o de `huggingface.co/<repo>/resolve/<commit>`, se comprueban por SHA-256 y tamaño (`src/Rodaje.Packages/Download`). El índice no puede ser más viejo que el último visto.
- **Módulos de IA** (`modulo-*`, `rodaje/modules`, `rodaje-codificadores-gpl`): procesos aparte que Rodaje abre y con los que habla por una tubería con nombre `CurrentUserOnly` (JSON enmarcado, 1 MiB máx.) y memoria compartida (`src/Rodaje.Modules.Protocol`, `docs/protocolo-modulos.md`). Lo que devuelven es una propuesta que se aplica como un paso del historial.
- **Herramientas de IA** (`lanzador`, `subtitular`, `rastrear`): apps WinUI 3 + CLI. El Lanzador baja e instala Subtitular y Rastrear desde el catálogo firmado (`Lanzador.Core/Instalador.cs`) en `%LOCALAPPDATA%\Dathinel`. Las tres buscan actualizaciones en la API pública de GitHub y reutilizan los módulos de Rodaje.
- **Sensores-Teoria**: prácticas de la materia (ESP32, Python, visión). `_lanzador/lanzador.py` levanta un servidor HTTP en 127.0.0.1 y ejecuta procesos locales.
- **Proceso y build**: `rodaje-proceso` (notas privadas, scripts de agentes y de la nube), `build/*.ps1` y `generar.ps1`/`empaquetar.ps1` de cada repo, y los workflows `.github/workflows` (CI, nocturna, release en borrador con `contents: write`).

Límites de confianza: (1) archivo externo → proceso del editor o herramienta; (2) red/GitHub/Hugging Face → índice y descargas; (3) editor ↔ proceso de módulo (tubería y memoria compartida); (4) otros procesos del mismo usuario/equipo → tubería, carpetas de datos; (5) repositorio y PR → CI y releases.

## Componentes

| Componente | Código que cubre |
|---|---|
| Editor | `rodaje/src` salvo `Rodaje.Packages` y `Rodaje.Modules.Protocol`: Core, Media, Render, Engine, Export, Audio, Analysis, Playback, App, Launcher; `rodaje/schema` |
| Paquetes y catálogo | `rodaje/src/Rodaje.Packages`, `rodaje/src/Rodaje.App/Services/Packages`, `Services/Updates`, `rodaje-catalogo` y el contenido de los nueve repos `rodaje-*` de contenido |
| Módulos de IA | `rodaje/src/Rodaje.Modules.Protocol`, `rodaje/src/Rodaje.App/Services/Modules`, `rodaje/modules`, `modulo-*`, `rodaje-codificadores-gpl` |
| Herramientas de IA | `lanzador`, `subtitular`, `rastrear` (sus `src/`) |
| Build y publicación | `.github/workflows` y `.github/actions` de cada repo, `build/*.ps1`, `generar.ps1`/`empaquetar.ps1`/`probar.ps1` de los repos de contenido, `rodaje-proceso`, `Dathinel/Dathinel` |
| Sensores-Teoria | `Dathinel/Sensores-Teoria` entero |

## Puntos de entrada y quién los alcanza

- Abrir/importar video, audio, imagen, RAW, subtítulos, LUT o un proyecto `.rodaje` (cualquiera que le pase un archivo al usuario).
- Instalar un `.paquete` desde archivo o desde el catálogo; elementos propios en `complementos\propios`.
- Índice del catálogo, descargas de paquetes, modelos y apps; consulta de versiones en la API de GitHub (red, GitHub, Hugging Face).
- Tubería con nombre y memoria compartida de los módulos (procesos locales del mismo usuario).
- Variables de entorno (`RODAJE_DATOS`, `RODAJE_ENTORNO`, `RASTREAR_MODULOS`…) y carpetas de búsqueda de módulos y FFmpeg (usuario local).
- Workflows de GitHub Actions (PRs, `workflow_dispatch`).
- Servidor local de Sensores-Teoria en 127.0.0.1 (páginas web abiertas en el mismo equipo).

## Activos (Daniel los marcó todos como críticos)

1. El equipo del usuario: que no se ejecute código ajeno (Editor, Paquetes, Módulos, Herramientas).
2. La clave privada de firma del catálogo y el proceso de publicación (`build/firmar-indice.ps1`, `distribuir.ps1`, `publicar-release.ps1`, releases).
3. Los proyectos y medios del usuario (lectura o escritura fuera de lo esperado, pérdida de datos).
4. Los secretos de GitHub Actions (`GITHUB_TOKEN` con `contents: write` en los workflows de release).

## Atacantes que importan (Daniel: todos)

- Quien prepara un video, imagen, proyecto o `.paquete` malicioso para que el usuario lo abra.
- Quien compromete o intercepta el catálogo y las descargas (red, cuenta de GitHub, Hugging Face).
- Otro programa o usuario en el mismo equipo (tubería de los módulos, carpetas de datos, variables de entorno).
- Cadena de suministro: CI, releases, dependencias (FFmpeg, ONNX Runtime, whisper.cpp, llama.cpp).

## Controles existentes

- `SafePaths` y `SafeText` en paquetes (sin `..`, unidades, ADS, nombres reservados, bidi; UTF-8 estricto), extracción aparte y cambio de nombre.
- Firma ECDSA del índice antes de leer el JSON, antirretroceso por `generated`, orígenes permitidos, SHA-256 y tamaño máximo en cada descarga.
- Tubería `CurrentUserOnly`, nombre con GUID, marco máx. 1 MiB, tipos validados.
- Workflows con `permissions: contents: read` por defecto; la release sale en borrador y solo Daniel la publica.
- Instalación por usuario, sin administrador.

## Fuera de alcance y riesgo aceptado (Daniel)

- Fallos que solo cierran el programa sin otra consecuencia (`rodaje/SECURITY.md`). El agotamiento de memoria provocado por un archivo sí cuenta.
- **Sensores-Teoria** es material académico: lo que salga ahí importa poco.
- **Scripts de desarrollo y de la nube** (`build/*.ps1`, `rodaje-proceso`) solo corren en el equipo de Daniel: los hallazgos que solo alcanza quien ya controla sus entradas locales importan poco. Siguen contando los que tocan la clave de firma o corren en GitHub Actions con permisos de escritura (`release.yml` ejecuta `build\distribuir.ps1`).

## Decisiones de Daniel sobre confianza

- Un módulo de IA de terceros que el usuario instala desde archivo es de confianza una vez instalado: instalarlo es aceptar que ejecute código. Lo que solo alcanza un módulo ya instalado (incluido mandar mensajes maliciosos por la tubería desde ese módulo) no es una amenaza. Sí cuenta que un `.paquete` o una descarga instale o ejecute un módulo sin pasar por la instalación del usuario o por la firma y la huella del catálogo, y que otro proceso suplante a un módulo en la tubería.
- Los workflows no reciben PRs de forks: solo Daniel hace push y PRs. Lo que solo alcanza un PR de un tercero en CI importa poco.

## Preguntas abiertas

- Ninguna por ahora.
