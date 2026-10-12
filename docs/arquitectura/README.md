# Arquitectura de Rodaje

Rodaje es un editor de video nativo para Windows escrito en C# sobre .NET 10. Se entrega como un ejecutable Native AOT autocontenido, en zip portable o con instalador por usuario. El código se reparte en capas, cada una en su propio proyecto y apoyada solo en las de abajo. Cada documento describe una capa tal como está hoy, con sus archivos, su lógica y cómo se prueba.

```mermaid
flowchart TB
    Launcher["Lanzador: Rodaje.Launcher<br/>Rodaje.exe → app\Rodaje.exe"]
    App["App: Rodaje.App<br/>interfaz WinUI 3"]
    Export["Exportación: Rodaje.Export<br/>plan, trabajos, ffmpeg.exe"]
    Analysis["Análisis: Rodaje.Analysis<br/>silencios, ritmo, voz, movimiento, color"]
    Engine["Motor: Rodaje.Engine<br/>ClipPlayer, TimelineSession, AudioMixer"]
    Render["Render: Rodaje.Render<br/>compositor Direct3D 11"]
    Audio["Audio: Rodaje.Audio<br/>WASAPI, reloj maestro"]
    Media["Medios: Rodaje.Media<br/>FFmpeg LGPL, D3D11VA, proxies"]
    Playback["Reproducción: Rodaje.Playback<br/>programador, caché, composición"]
    Core["Núcleo: Rodaje.Core<br/>tiempo, modelo, ediciones, .rodaje"]
    Paquetes["Paquetes: Rodaje.Packages<br/>formato .paquete, sin dependencias"]
    Modulos["Módulos: Rodaje.Modules.Protocol<br/>protocolo de IA, sin dependencias"]
    Launcher -.->|"abre"| App
    App --> Export
    App --> Analysis
    App --> Engine
    App --> Render
    App --> Paquetes
    App --> Modulos
    Export --> Engine
    Export --> Render
    Export --> Media
    Analysis --> Core
    Engine --> Audio
    Engine --> Media
    Engine --> Playback
    Render --> Media
    Render --> Playback
    Audio --> Playback
    Media --> Playback
    Media --> Core
    Playback --> Core
```

| Capa | Documento | En una frase |
|---|---|---|
| Núcleo | [nucleo.md](nucleo.md) | Tiempo exacto en ticks, modelo del proyecto con efectos, transiciones, velocidad y clips compuestos, unas 50 operaciones de edición con deshacer por instantáneas y el formato `.rodaje` |
| Medios | [medios.md](medios.md) | FFmpeg LGPL verificado, GPU compartida con D3D11VA, seek exacto al cuadro, reversa, análisis de archivos, proxies y salida a `ffmpeg.exe` |
| Audio | [audio.md](audio.md) | Salida WASAPI que da el reloj maestro y sobrevive a la pérdida del dispositivo; medidores, LUFS, ecualizador, compresor y captura de voz en off |
| Reproducción | [reproduccion.md](reproduccion.md) | Qué cuadro se muestra en cada refresco, siguiendo al audio sin bloquear nunca |
| Motor | [motor.md](motor.md) | Varias capas con pre-roll, varios decodificadores por medio, presupuesto de VRAM, velocidad y reversa, títulos, mezcla, proxies y el motor fuera de línea |
| Render | [render.md](render.md) | Composición en Direct3D 11 sin copias: color, efectos, máscaras, croma, ruedas y curvas, transiciones, títulos y conversión para exportar |
| Exportación | [exportacion.md](exportacion.md) | La orquestación: plan, audio con LUFS, motor fuera de línea → compositor → conversor → `ffmpeg.exe`, verificación, modos de rendimiento y cancelación |
| Análisis | Sin documento propio | `src/Rodaje.Analysis`: DSP y visión propios sobre el núcleo para quitar silencios, detectar ritmo, bajar la música bajo la voz, estabilizar, seguir regiones (máscaras) y color automático; la app le pasa el audio y los cuadros |
| Paquetes | [formato-paquete.md](../formato-paquete.md) | Manifiesto, instalación segura, catálogo y descarga firmada de los complementos, para compartir con otros programas |
| Módulos | [protocolo-modulos.md](../protocolo-modulos.md) | El protocolo de los módulos de IA (tubería con JSON y memoria compartida), con el cliente y el anfitrión en C# |
| App | [app.md](app.md) | El contrato de la interfaz WinUI 3: `EditorSession`, comandos y eventos, atajos, ajustes, avisos, miniaturas, complementos y módulos de IA |
| Vistas | [app-vistas.md](app-vistas.md) | Qué hace cada vista: biblioteca, reproductor y medidores, línea de tiempo, inspector, exportar, herramientas, atajos, complementos y flujo de trabajo |
| Lanzador | [arranque-y-distribucion.md](../arranque-y-distribucion.md) | `src/Rodaje.Launcher`: el `Rodaje.exe` de la carpeta, que abre `app\Rodaje.exe` con los mismos argumentos y oculta `app\` la primera vez |

## Núcleo, contenido e IA

El ejecutable trae solo el código del motor y un conjunto fijo de primitivas: transformar, opacidad, recorte y modos de mezcla; color y LUT; desenfoque y máscara; texto con borde y sombra; fundidos; ganancia y fundidos de audio. Lo demás va fuera y se instala o no (D-031):

```
Rodaje.exe        lanzador; el programa va en app\
app\paquetes\     contenido sin código: transiciones, filtros, estilos de texto, plantillas
app\modulos\      IA local: procesos aparte con sus modelos
```

- **Paquetes** ([formato-paquete.md](../formato-paquete.md)): un zip con `paquete.json` y sus recursos, nunca código, con su licencia declarada. Un proyecto guarda `paquete@versión/elemento`; si el paquete falta, el proyecto abre igual, avisa y ofrece instalarlo.
- **Módulos de IA** ([protocolo-modulos.md](../protocolo-modulos.md)): procesos aparte que hablan por una tubería con JSON y reciben cuadros y audio por memoria compartida. Un modelo que se queda sin memoria cierra su módulo, no el editor; el ejecutable sigue pequeño y AOT, y cada módulo lleva su licencia. **La IA no edita:** lo que devuelve se convierte en comandos del núcleo, se ve antes de aplicarse y se deshace con Ctrl+Z.
- **Distribución:** instalador y zip portable (el editor), zip completo (con paquetes, módulos y modelos en `app\`) y el gestor de complementos de la app, que baja lo mismo desde un catálogo firmado ([descarga.md](../descarga.md), [arranque-y-distribucion.md](../arranque-y-distribucion.md)).
- **Sin plugins con código dentro del proceso:** Native AOT no carga ensamblados en tiempo de ejecución; lo que necesite código va como módulo.

## Reglas que atraviesan todas las capas

- **La interfaz no toca el modelo.** Todo cambio es un comando del núcleo (`Edits` + `UndoStack`), así que todo se deshace con Ctrl+Z, también lo que haga la IA.
- **El audio manda.** El video sigue al reloj de la salida de audio: si el equipo no alcanza, se pierde un cuadro, nunca una muestra.
- **Nada espera en el camino de render.** `Tick` muestra lo mejor que hay; abrir, decodificar y cerrar archivos ocurre en otros hilos.
- **Un solo dispositivo D3D11** compartido por FFmpeg y el compositor: los cuadros decodificados se muestrean sin copiarlos.
- **Compatible con Native AOT** desde el primer día: nada de reflexión; JSON e interop COM generados en compilación; advertencias tratadas como errores.
- **FFmpeg LGPL en proceso**, cargado por ruta absoluta y verificado; los codificadores GPL (x264, x265) solo como `ffmpeg.exe` aparte, en su complemento ([`legal.md`](../legal.md)).
- **Lo que falta no rompe.** Sin GPU se decodifica en CPU, sin dispositivo de audio el reloj es el del sistema y un medio roto deja su capa vacía; todo con mensajes en español.

## Herramientas y pruebas

| Comando | Qué hace |
|---|---|
| `Probar.bat` | Restaura, compila y corre todas las pruebas ([`build/probar.ps1`](../../build/probar.ps1)); `-Filtro texto` corre solo las que coinciden. Las de la categoría `TiempoReal` corren en una segunda pasada, solas, para que la carga de las demás no las falsee |
| `Probar.bat cobertura` | Lo mismo, más la cobertura: mínimo 99 % de líneas en `Rodaje.Core` y 90 % en `Rodaje.Export` |
| `Probar.bat aot` | Publica y ejecuta la prueba de humo del núcleo con Native AOT ([`publicar-aot-prueba.ps1`](../../build/publicar-aot-prueba.ps1)) |
| `Probar.bat aceptacion` | Pruebas de aceptación con video y audio reales, en serie ([`aceptacion.ps1`](../../build/aceptacion.ps1)) |
| `Rodaje.bat` | Publica en `dist\Rodaje\` (lanzador y `app\`) y abre el editor ([`app.ps1`](../../build/app.ps1)); `app.ps1 -Medir N` mide arranque y reproducción |
| `build\distribuir.ps1` | Arma instalador, zip portable, zip completo e índice de complementos en `dist\distribucion\` ([`distribuir.ps1`](../../build/distribuir.ps1)) |
| `build\dotnet-local.cmd <args>` | El SDK de `.entorno\` con cualquier argumento de `dotnet` |

- **Módulo común.** Los scripts comparten [`build/comun.psm1`](../../build/comun.psm1): localizan el entorno y sus marcas de instalación completa, fijan las variables del SDK local, añaden vswhere al PATH, llaman a dotnet con registro, crean la carpeta de cada corrida y leen los `.trx`.
- **Entorno.** El SDK y FFmpeg viven en `.entorno\`, con versiones y huellas fijadas en [`preparar-entorno.ps1`](../../build/preparar-entorno.ps1). Las variables `RODAJE_*` están en [`variables-entorno.md`](../variables-entorno.md).
- **Proyectos.** Las pruebas viven en `tests/`, un proyecto por capa y por módulo de IA (`Rodaje.Module.*.Tests`) más `Rodaje.Acceptance.Tests` y `Rodaje.AotSmoke`. La biblioteca de apoyo [`tests/Rodaje.TestMedia`](../../tests/Rodaje.TestMedia) genera medios con el número de cuadro dibujado y reúne en `Skip.cs` los atributos que omiten una prueba, con su motivo, cuando falta hardware o la variable de las pruebas largas.

## Problemas de compatibilidad conocidos

- **Windows PowerShell deshabilitado** (pasa en versiones recortadas como AtlasOS). `Probar.bat` lo detecta. Los scripts usan `-ExecutionPolicy Bypass` solo para su proceso.
- **Descargas cortadas.** Se escriben como `.part` y solo se aceptan completas y con la huella correcta: basta repetir.
- **Native AOT** necesita `link.exe` de Visual Studio Build Tools 2022 con la carga de C++.
- **Paquetes NuGet** fijados en `packages.lock.json`: un cambio sin actualizarlos hace fallar la restauración a propósito.
- **Filtros con `&` y `dotnet-local.cmd`.** cmd.exe corta el filtro en el `&`, y como el .cmd reenvía los argumentos, `^&` no alcanza. Desde PowerShell funciona `--filter 'Categoria=TiempoReal^^^&FullyQualifiedName~Timeline'` o el filtro con comillas dentro: `--filter '"Categoria=TiempoReal&FullyQualifiedName~Timeline"'`.
- **Espacios de nombres en las pruebas.** `Rodaje.X.Tests.Timing` tapa a `Rodaje.Core.Timing`; en las pruebas se usa el nombre completo o un `using`.
- **Carpetas sincronizadas** (OneDrive). Pueden bloquear el `File.Replace` del guardado atómico. Si pasa, guardar falla con un error y el proyecto anterior queda intacto.

## Otros documentos

- [Índice de la documentación](../README.md)
- [Formato de proyecto](../formato-proyecto.md) y [formato `.paquete`](../formato-paquete.md)
- [Protocolo de módulos](../protocolo-modulos.md) y [descarga de complementos](../descarga.md)
- [Arranque y distribución](../arranque-y-distribucion.md)
- [Variables de entorno](../variables-entorno.md)
- [Decisiones](../decisiones.md)
- [Fondo legal](../legal.md)
