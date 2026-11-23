# Modo caos

Una persona errática con la app real, de fondo (la ventana no se ve), hasta que algo cierre o cuelgue Rodaje.

## Cómo se lanza

```powershell
# Varias semillas seguidas, con resumen (publica antes si src\ cambió)
powershell -NoProfile -ExecutionPolicy Bypass -File build\caos.ps1 -Desde 1 -Hasta 30 -Minutos 5
# Una semilla con el exe ya publicado
powershell -NoProfile -ExecutionPolicy Bypass -File build\app.ps1 -Caos -Semilla 7 -Minutos 5
# El exe a mano (exige RODAJE_DATOS; los archivos son el corpus)
dist\Rodaje\app\Rodaje.exe --caos 7 5 --sin-audio corpus\*.mp4
```

En la CI va en `.github/workflows/caos.yml`: de noche (6 semillas × 5 min con WARP), a mano con `workflow_dispatch`
(semillas y minutos) y corto en el PR que lo cambia. No bloquea los PR; lo de las semillas que fallan queda en el
artefacto `caos`.

## Qué hace

`ChaosRun` (`src/Rodaje.App/Shell/ChaosRun.Windows.cs`) elige con `Random(semilla)` y pesos de persona real:

| Peso | Acción | Por dónde |
|---|---|---|
| 26 | Un control a la vista o en el menú/diálogo abierto: botones, conmutadores, desplegables (abre y elige), deslizadores, campos con valores absurdos (`NaN`, `1e9`, `-1`, texto, 400 nueves) | `AutomationPeer` (Invoke, Toggle, ExpandCollapse, SelectionItem) |
| 16 | Una tecla: la de una acción registrada o una al azar con modificadores | `KeyboardShortcutHost.Press` (el mismo camino que `PreviewKeyDown`) |
| 14 | Una acción del catálogo de atajos (importar, exportar, guardar, abrir, nuevo, espacios de trabajo…) | `ShortcutRouter.Invoke` (lo que hace el menú) |
| 9 | Arrastrar un clip (mover, recortar bordes, slip, roll, slide, estirar) a otras pistas y fuera de rango; a veces se cancela | `ClipDragSession`, lo mismo que el ratón en la línea de tiempo |
| 6 / 5 / 5 | Reproducir o pausar; el cabezal en ráfagas (también negativo y pasado el final); doble clic en un medio | `EditorSession` |
| 5 / 4 / 4 / 3 | Seleccionar clips; deshacer/rehacer en ráfagas de hasta 50; cerrar a medias (diálogos, emergentes, Esc); copiar y pegar | |
| 3 / 3 / 2 / 2 | Importar; pestaña del inspector; tamaño de la ventana (800×600, 640×480, 2560×1440…); esperar | |

Los diálogos de archivo no se abren: `FileDialogs.FakeOpen` responde con el corpus (a veces cancela o da un archivo del
tipo equivocado) y `FakeSave` con `salidas\` en la carpeta de la corrida. No se tocan: salas y colaboración, «Salir»,
otra pantalla, actualizaciones, carpetas vigiladas, grabar voz (micrófono) ni enlaces que abren otro programa.

## Registro y fallos

- `caos.log` en `RODAJE_DATOS`: cada paso con su número antes de ejecutarse (`n<TAB>acción`) y su detalle
  (`n<TAB>  …`), escrito hasta el disco: un cierre nativo no se lo lleva.
- Al terminar, una ráfaga de 30 deshacer/rehacer y el cierre; `OnClosed` y `Program.Main` anotan sus etapas en
  `caos.log` (`ChaosRun.Mark`).
- Excepción de una acción: informe en `fallos\`, se sigue y la app sale con 2. Cierre nativo (0xC0000005): el filtro
  de `NativeCrashHandler` deja informe y minivolcado; el 0xC000027B de WinUI sale por `RaiseFailFastException` y solo
  deja el código de salida. Cuelgue: el vigilante, si el hilo de la interfaz no atiende en 10 s, anota `CUELGUE`, deja
  `cuelgue-paso-N.dmp` y termina el proceso; si un paso no termina en 60 s aunque la interfaz atienda (un diálogo modal),
  anota `ATASCADO` y hace lo mismo.
- `caos.ps1` guarda de cada semilla que falla `caos.log`, `fallos\*` y `rodaje-error.txt` en `-Resultados` y sigue con
  la siguiente; al final resume semilla, código, pasos y causa (excepción y primer marco de Rodaje).

Repetir una semilla da las mismas elecciones, pero el estado depende del tiempo (cuadros, tareas de fondo): para
reducir un fallo se lee el registro hasta el paso que cayó y se escribe la prueba con esa secuencia.

## Sin rastro

Cada semilla trabaja en `%TEMP%\rodaje-caos\<semilla>`: `RODAJE_DATOS` (ajustes, informes, cachés, diario de
exportación), `RODAJE_PROXIES`, `RODAJE_RENDER`, `TEMP`/`TMP` y las salidas; se borra al terminar. En el modo caos
(`AppDataPaths.Sandbox`) las carpetas del usuario (Vídeos, Documentos) apuntan ahí, el portapapeles y los programas
externos no se usan (`SystemAccess`) y la pantalla completa no cambia el presentador. Sin red: `HTTP(S)_PROXY` a un
puerto cerrado. Al final, `caos.ps1` compara antes y después `%LOCALAPPDATA%\Rodaje`, Documentos, Vídeos, Escritorio,
lo de Rodaje en `%TEMP%` y las claves de Rodaje en HKCU (`Software\Rodaje`, `.rodaje`, recientes y diálogos), y falla si
quedó algo.

## Lo que encontró

| Ronda | Semillas | Síntoma | Causa | Arreglo |
|---|---|---|---|---|
| 1 | 12 × 3 min | `COMException 0x8000FFFF` al cerrar (semilla 12) | Tareas de la cola de la interfaz (`LibraryView.ScanVisibleCovers`) que corrían con el árbol de XAML ya desmontado | `DispatcherQueueLater` descarta lo pendiente desde `MainWindow.OnClosed` (#704) |
| 1 | | Cierre nativo 0xC000027B al cerrar, sin informe (semilla 10) | No se reprodujo; probable misma familia que la anterior (el error «stowed» no pasa por el filtro de fallos) | El mismo; queda como caso a vigilar |
| 2 | 18 × 5 min | Fotogramas guardados en la carpeta desde la que se abrió Rodaje («291\», «%\», «1,5\») | «Carpeta de capturas» aceptaba cualquier texto y se resolvía contra el directorio actual | Solo vale una ruta completa (`StillCaptureNaming`, `Personalization.NormalizeFolder` en Preferencias y en el render en caché) (#704) |
| 3 | 8 + 10 × 5 min | La corrida se quedó parada en «Carpetas vigiladas… › Añadir carpeta…» (semilla 33) | El selector de carpetas de Windows (`FolderDialog`, modal) no tenía respuesta del modo caos | `FolderDialog.FakePick`; el vigilante corta un paso que no termina en 60 s (ATASCADO) (#716) |
| 3 | | `COMException 0x8000FFFF` en `PluginsView` (x:Bind) al cerrar (semilla 33) | Una instalación de complemento sin red terminaba después de cerrar la ventana | Tras cerrar la ventana, ese error va solo al registro (`Program.IgnoreAfterWindowClosed`) (#716) |
| 4 | 8 × 4 min | `ArgumentException` «This element does not have a XamlRoot» al abrir el Historial nada más arrancar (semilla 48) | `ShowGlobalPanel` llamaba `Flyout.ShowAt` con el botón aún sin `XamlRoot` (Ctrl+H al arrancar) | Solo se muestra con el botón ya en el árbol |
| 5-8 | 4 × 8 × 4 min | Cierre nativo 0xC000027B al cerrar, sin informe ni minivolcado (semillas 49 y 75; antes la 10) | Las marcas de cierre (`ChaosRun.Mark`) lo sitúan después de `OnClosed`, al desmontar XAML, con la lista de un desplegable abierta (cierres forzados: «Ámbito» de la línea de tiempo tras 30 deshacer, semilla 3010). El error «stowed» de WinUI sale por `RaiseFailFastException` y no pasa por el filtro de fallos | `OnClosed` cierra los emergentes abiertos (listas, menús; no los diálogos) antes de desmontar. 60 cierres forzados con una lista abierta, sin repetirlo; sigue a vigilar |
| 8 | 40 cierres forzados | `ArgumentException` «does not have a XamlRoot» con Ctrl+H al arrancar (semilla 3031) | El atajo del Historial llamaba `Flyout.ShowAt` directamente, sin pasar por la guarda de #719 | El atajo pasa por `ShowGlobalPanel` |

Repetir una semilla no repite el estado: el reparto de acciones cambia al cambiar la tabla de pesos y el resultado
depende del tiempo. Por eso los casos fijos son las pruebas (`StillCaptureNamingTests`) y, para lo que solo sale en la
app real, el cierre de cada corrida con trabajo pendiente en la cola (todas las semillas lo hacen al terminar).
