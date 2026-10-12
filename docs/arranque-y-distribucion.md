# Arranque y distribución

Qué tarda Rodaje en abrir, qué se hizo para que se sienta instantáneo, cómo se entrega para que quien lo descarga vea solo `Rodaje.exe`, y por qué sigue con WinUI 3. Medido en el equipo de desarrollo (Ryzen 7 7435HS, RTX 3050, Windows 11 26100) con el exe Native AOT publicado.

## Cómo se mide

`build\medir-arranque.ps1` lanza el exe N veces y toma, desde fuera, cuándo existe su ventana, cuándo es visible y cuándo su cola de mensajes queda libre. Con `--arranque`, Rodaje anota marcas internas (desde que Windows creó el proceso), espera a que la interfaz responda y se cierra solo, sin autoguardado. `-Frio` copia antes la carpeta con E/S sin búfer (`robocopy /J`): sus archivos no están en la caché de Windows, como la primera vez tras descargar. Es el peor caso: tras un reinicio, el prefetcher de Windows ayuda a un programa ya usado. ETW (WPR) pide permisos de administrador; por eso las etapas salen de marcas propias.

- **Ventana visible:** el primer píxel de Rodaje en pantalla.
- **Interfaz completa:** primer cuadro de XAML (`primer_render`).
- **Interfaz usable:** la cola del hilo de la interfaz queda vacía tras ese cuadro: ya atiende clics y teclas.
- **Primer cuadro:** primer cuadro de video con un clip H.264 1080p pasado por línea de comandos.

## Cifras (mediana, ms desde el doble clic)

| | Antes, caliente | Después, caliente | Antes, frío | Después, frío |
|---|---:|---:|---:|---:|
| Ventana visible | 438 | **50** | 1356 | **215** |
| Interfaz completa | 526 | 423 | 1411 | 750 |
| Interfaz usable | 569 | 456 | 1456 | 790 |
| Visor listo (cadena de intercambio en el panel) | 777 | 500 | 1629 | 1165 |
| Primer cuadro de video (con un clip) | 1196 | 1194 | | |

«Después» incluye el lanzador (unos 17 ms en caliente y 75 ms en frío). Abriendo `app\Rodaje.exe` directamente, la ventana aparece a los 33 ms. Objetivo: ventana visible ≤300 ms en caliente y ≤1 s en frío. **Cumplido**, con margen en las dos cifras, y la interfaz completa también queda bajo 1 s en frío.

### Pisos de cada tecnología (mismo método, caliente / frío)

| App AOT mínima | Ventana visible | Primer cuadro |
|---|---:|---:|
| Win32 (una ventana GDI) | 89 / 61 | 126 / 37 |
| WinUI 3 vacía (`XamlControlsResources`, un botón) | 91 / 303 | 246 / 352 |
| WinUI 3 vacía con barra de título propia (`ExtendsContentIntoTitleBar`) | 302 | 324 |
| Rodaje después, sin lanzador | 33 (armazón) | 406 / 675 |

Extender el contenido a la barra de título cuesta en WinUI unos 100 ms fijos (la llamada adelanta y alarga la preparación de la ventana; probado antes y después de crear el XAML, con la API de `Window` y la de `AppWindow`). Rodaje queda a ~80 ms del piso de WinUI con barra propia: lo que suma su interfaz.

### Etapas (después, caliente; entre paréntesis, antes en frío)

| Etapa | ms |
|---|---:|
| Proceso, cargador y runtime AOT hasta `Main` | 24 (93) |
| Leer `ajustes.json` y pintar el armazón | 22 |
| Arranque del Windows App SDK y `App.xaml` (`XamlControlsResources`, tema) | 30 (538 antes, compitiendo con FFmpeg) |
| `Window` base | 21 |
| XAML de la ventana principal (barra, biblioteca, visor, panel derecho, línea de tiempo) | 91 |
| Barra de título propia | 115 |
| Atajos, sesión, catálogo de paquetes, módulos, fuentes | 5 (163 en frío el catálogo) |
| Unir las vistas a la sesión | 19 |
| `Activate` hasta el primer cuadro de XAML | 75 |
| Hasta que la interfaz atiende | 33 |
| Después, en segundo plano: FFmpeg | 13 |
| Después: dispositivo D3D11, presentador y títulos | 29 |

Las sondas de codificadores no corren al arrancar (las hace Exportar al abrirse).

## Qué se cambió

1. **Armazón nativo** (`Shell/StartupSplash.cs`): antes de cargar WinUI, una ventana Win32 pintada con GDI con el fondo, la barra y los cuatro paneles en su sitio, con los anchos guardados. La ventana de WinUI se pone en el mismo lugar, oculta para DWM (*cloak*) hasta su primer cuadro; entonces se muestra y el armazón se cierra. Capturas de pantalla cada pocos ms: sin destello blanco ni salto de posición. Si WinUI no pintara oculta, a los 3 s se muestra igual.
2. **Motor después del primer cuadro.** Cargar FFmpeg y crear D3D11 en paralelo con WinUI se disputaba el cargador de DLL de Windows y el disco: en frío retrasaba la ventana ~500 ms. Ahora el motor arranca cuando la interfaz ya responde. Con archivos en la línea de comandos arranca enseguida, como antes, porque ahí manda el primer cuadro del video (diferirlo lo retrasaba 200 ms).
3. **Lo demás, también después:** `ModuleManager.Refresh`, el recorte de la caché de miniaturas y la limpieza de exportaciones a medias.
4. **Marcas de arranque** en `Program`, `App` y `MainWindow`, y `--arranque` para medir sin tocar el autoguardado.

Margen que queda: el XAML de las vistas (91 ms) se podría partir con `x:Load` en el panel derecho y la línea de tiempo. La barra de título propia es parte del diseño (D-018).

## Distribución: que se vea solo `Rodaje.exe`

| Opción | Probado | Arranque | Antivirus / SmartScreen | Actualizar / desinstalar | Veredicto |
|---|---|---|---|---|---|
| **(a) Lanzador + `app\`** | Sí: 0,7 MB, abre `app\Rodaje.exe` con los mismos argumentos, carpeta y modo de ventana | +17 ms caliente, +75 ms frío | Un exe más sin firmar; SmartScreen avisa una vez, por el que se abre con doble clic (el hijo lo abre `CreateProcess`, sin aviso) | Reemplazar la carpeta; borrar la carpeta | **Elegida** |
| (b) DLL en subcarpeta | Sí: falla. El inicializador del Windows App SDK carga `Microsoft.WindowsAppRuntime.dll` antes de `Main`, y AOT solo busca junto al exe (ni `PATH` sirve). Además el manifiesto incrustado registra unas 30 DLL de WinRT por nombre junto al exe | — | — | — | Haría falta reescribir el manifiesto tras cada compilación y adelantar la carga: frágil ante cada versión del SDK |
| (c) Atributo oculto | Sí, como parte de (a): el lanzador oculta `app\` la primera vez | 0 | Igual | Igual | Solo no basta: el zip no guarda el atributo y con «ver ocultos» se ve todo |
| (d) Instalador (Inno Setup) | Sí, desde la v1: `build\instalador.ps1`, Inno Setup 7.1.0 portable en `.entorno` (MSIX descartado: necesita firma de confianza o modo desarrollador) | 0 | Sin firma, SmartScreen avisa igual; firma lista con `RODAJE_CERTIFICADO` | Entrada en «Aplicaciones», acceso en Inicio, `.rodaje` y «Abrir con» de videos; desinstalación limpia | **Junto al portable** |

**Resultado:** al descomprimir `Rodaje-<versión>-portable.zip` se ve la carpeta `Rodaje` con `Rodaje.exe` y `app` (oculta tras el primer arranque). `app.ps1` publica así (`dist\Rodaje\Rodaje.exe` + `dist\Rodaje\app\`), `distribuir.ps1` arma los zip igual y la completa pone paquetes, módulos y modelos en `app\`. Para las pruebas y las medidas se usa `app\Rodaje.exe` directamente; el doble clic y `Rodaje.bat` pasan por el lanzador. Detalles que cambian: `rodaje-error.txt` queda en `app\` (el aviso de la app lo dice) y las licencias (`LICENSE.txt`, `THIRD-PARTY-NOTICES.txt`, `TRADEMARK.md`) viajan en `app\` junto al exe que cubren.

## Versión e instalador (v1)

- **Versión única** en `Directory.Build.props` (`<Version>`): la usan los dos exe (`FileVersion` 1.0.0.0 e `InformationalVersion` `1.0.0+<commit>`), «Acerca de», `CatalogSource.AppVersion` (el `minApp` del catálogo) y los nombres de los artefactos. Para una versión nueva se cambia solo esa línea.
- **Instalador** (`build\instalador.ps1` y `build\instalador\Rodaje.iss`): por usuario, sin administrador, en `%LOCALAPPDATA%\Programs\Rodaje`. Muestra la licencia (LICENSE con sus términos adicionales, aviso de `TRADEMARK.md` y de los binarios de Microsoft), crea el acceso en Inicio y, si se elige, en el escritorio, asocia `.rodaje` y añade Rodaje a «Abrir con» de videos y audios. Al desinstalar quita todo eso y la carpeta, pero no los proyectos ni `%LOCALAPPDATA%\Rodaje`, salvo que el usuario lo pida (en silencio, `/BORRARDATOS`).
- **Firma de código:** con `RODAJE_CERTIFICADO` (ruta a un `.pfx` y `RODAJE_CERTIFICADO_CLAVE`, o la huella de un certificado del almacén) se firman los dos `Rodaje.exe`, los módulos de la completa, el instalador y su desinstalador con `signtool`. Sin certificado todo sale sin firmar y SmartScreen avisa la primera vez.
- **Artefactos** (`build\distribuir.ps1`, en `dist\distribucion\`): `Rodaje-<v>-instalador.exe`, `Rodaje-<v>-portable.zip`, `Rodaje-<v>-completo.zip` (con complementos, módulos de `modules\` publicados y modelos de menos de 2 GiB), `SHA256SUMS.txt` y `Rodaje-<v>-notas.md` (de `build\instalador\notas-version.md`).
- **Comprobación** (`build\probar-instalador.ps1`, la corre `distribuir.ps1`): instala en silencio en una carpeta temporal con espacios y tildes, revisa archivos, accesos y asociaciones, abre el exe instalado con `--autoprueba` y el lanzador con `--arranque`, desinstala y comprueba que no queda nada.
- **Publicar** (`build\publicar-release.ps1`): verifica los hashes, que la etiqueta no exista y que el commit del exe esté en `origin/main`, y crea la release `v<v>` con `gh` (`-Borrador` para dejarla en borrador).

## Pruebas en máquinas limpias

`.github/workflows/distribucion.yml` (a mano con `gh workflow run distribucion.yml --ref <rama>` una vez que esté en `main`, y con cada etiqueta `v*`) arma instalador y portable con `distribuir.ps1 -SinModulos -SinCompleta -SinComprobar` y los prueba en **otra** VM `windows-latest` recién creada, solo con los artefactos y `build\`, y el PATH reducido al de Windows (ni dotnet, ni git, ni Visual Studio). Lo hace `build\probar-maquina-limpia.ps1`, que genera el material con el `ffmpeg.exe` LGPL que trae Rodaje (OpenH264 + AAC) y deja registros, entorno, cifras y capturas como artefacto `maquina-<variante>`:

- **limpia:** instalar en silencio, archivos, accesos y asociaciones, `--autoprueba` del exe instalado, lanzador, desinstalar sin restos (`probar-instalador.ps1`); portable descomprimido en una ruta con tildes, abierto por el lanzador (oculta `app\`), `--autoprueba`, ventana abierta a mano y capturada; arranque en caliente y en frío (5 vueltas, por el lanzador).
- **hostil:** lo mismo con `LocalAppData` y `%TEMP%` en `C:\Usuarios de prueba\José Núñez Peña\…` (el instalador usa su carpeta por defecto), 1920×1080 al **150 %**, la salida a internet de los cuatro `Rodaje.exe` bloqueada en el cortafuegos y un proyecto `.rodaje` (el ejemplo del formato 5) con su material en **otra unidad** (`D:\Proyectos de prueba\Boda de Ñuño`).

Las dos máquinas son Windows Server 2025 en inglés (en-US, decimal con punto), **sin GPU** (Microsoft Basic Render Driver, D3D11 nivel 11_1 por WARP, sin D3D11VA), **sin ningún dispositivo de audio** (ni salida ni micrófono) y sin Media Foundation para H.264. Resultado de la última corrida (12-oct): **las dos pasan todo**.

| Máquina limpia, ms (mediana de 5) | Caliente: visible / usable | Frío: visible / usable |
|---|---:|---:|
| limpia, 1024×768 al 100 % | 39 / 422 | 315 / 1440 (otra corrida: 4869 / 16 543) |
| hostil, 1920×1080 al 150 % | 37 / 336 | 265 / 1228 |

En caliente la ventana sale igual que en el equipo de desarrollo (~40 ms). En frío la ventana sigue bajo el objetivo de 1 s, pero la interfaz usable pasa de 0,79 s a ~1,2–1,4 s: WARP y el disco de una VM compartida. Una corrida dio 5 s / 16 s en frío con las mismas condiciones (la varianza de los runners y el antivirus al ver cada copia nueva); no se repitió y no cuenta como cifra. Instalar en silencio tarda 13–23 s y ocupa 242 MB; desinstalar, 2–14 s.

Lo que encontraron y se arregló:

- **Sin H.264 (Windows N, Windows Server).** Sin GPU ni Media Foundation no hay codificador de H.264 ni HEVC (D-028), y la cola decía «No está el ffmpeg.exe LGPL de Rodaje. Reinstala Rodaje», falso y sin salida. Ahora el diálogo explica por qué y pide AV1 o VP9, y el error de la cola ofrece «Cambiar ajustes». La autoprueba exporta en AV1 en esos equipos.
- **Pantallas de 1024×768.** El visor compone a la mitad cuando el panel no llega a la mitad de la secuencia (a propósito); la autoprueba lo tomaba por fallo.
- **Complementos sin red:** la autoprueba ahora abre la página y espera la consulta del catálogo. Sin conexión abre igual y lo dice en la nota (con el texto del error de Windows, en el idioma del sistema).

Sin probar: **Windows Sandbox** no está instalado en el equipo de desarrollo (`WindowsSandbox.exe` no existe y consultar la característica pide administrador). Un escritorio Windows 11 de consumo (no Server) con Media Foundation tampoco: lo cubre el equipo de desarrollo.

## Por qué sigue con WinUI 3

**Se sigue con WinUI 3.** Con el armazón, la ventana aparece a los 50 ms (215 en frío), al nivel de una app Win32 mínima. Lo que queda por ganar es entre que aparece y que se puede usar: ~400 ms en caliente y ~550 en frío, de los que unos 325 / 350 son el piso de WinUI con barra propia.

| Opción | Interfaz usable (estimado) | Gana | Pierde | Costo de migrar |
|---|---|---|---|---|
| **WinUI 3 + armazón (hoy)** | 456 / 790 ms | Controles completos, accesibilidad (UIA), IME, DPI, tema, lo ya construido | ~57 MB y 230 archivos del SDK; 325 ms de piso | 0 |
| WinUI 3 en una ventana Win32 propia (`DesktopWindowXamlSource`) | igual | La ventana es la misma desde el ms 30, sin cambio de ventana | Barra de título y arrastre a mano | Días: solo `MainWindow` |
| Win32 + Direct2D/DirectWrite con controles propios | ~100 / ~250 ms | ~350 ms al usarla, ~50 MB menos | Hacer a mano texto editable, IME, menús, desplazamiento, foco, teclado, accesibilidad, DPI | Reescribir ~26 000 líneas de interfaz (27 XAML y 23 000 de C# en `Views` y `Shell`) más el kit de controles: meses |
| Avalonia (AOT, Skia) | parecido a WinUI | Multiplataforma | Otro dialecto de XAML, su propio piso de arranque, otro runtime gráfico junto al D3D11 del visor | Reescribir las vistas: semanas a meses |
| WPF / WinForms | peor | — | Sin Native AOT completo; WPF arranca más lento | Reescribir |

Los servicios (`Services/`, 13 600 líneas) no dependen de WinUI y sobrevivirían a cualquier cambio. Aun así, una reescritura gana unos 350 ms una vez por sesión, a cambio de meses sin avanzar en el editor y de perder accesibilidad e IME hechos. Con la ventana visible a los 50 ms, la sensación de rapidez ya está; si se nota el cambio del armazón a la ventana real, el siguiente paso es la ventana Win32 propia con XAML dentro, no cambiar de tecnología.
