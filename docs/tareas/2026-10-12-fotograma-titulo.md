# Tarea 56: exportar fotograma con título falla al decodificar

Rama `claude/fotograma-titulo`, PR #71.

## Causa

`StillExporter` abre `OfflineTimeline` con `LookaheadFrames = 1` y `MaxDecodersPerMedia = 1`. Cuando el decodificador
usa D3D11VA, `VideoDecoder.TryCreateHardwareFrames` llama a `HardwarePoolSizer`, que en el motor fuera de línea es
`OfflineReaders.SizePool`. Ahí se hacía `Math.Clamp(fits, 2, _lookahead)`, es decir `Math.Clamp(fits, 2, 1)`, que
lanza `ArgumentException` («'2' cannot be greater than 1») porque el mínimo pasa al máximo. La llamada ocurre dentro
del `get_format` de FFmpeg (`VideoDecoder.SelectPixelFormat`, `UnmanagedCallersOnly`), que atrapa toda excepción y
devuelve `AV_PIX_FMT_NONE`: el códec no arranca, `GetFrame` devuelve `null` y `OfflineReaders.Pump` lo informa como
«El medio no tiene ese cuadro». El lector en reversa (`SizeReversePool`) tenía el mismo `Math.Clamp(…, 2, _lookahead)`.

No tiene que ver con el título: falla todo fotograma con medios cuando hay decodificación por hardware. Por eso solo se
veía en el PC de Daniel (RTX 3050): en la CI de Windows no hay D3D11VA y todas las `[StillGpuFact]` se omiten; con
decodificación por software `HardwarePoolSizer` no se llama. La exportación de video usa `LookaheadFrames` del perfil
(≥ 2) y no lo sufría.

## Lo hecho

- `src/Rodaje.Engine/Offline/OfflineReaders.cs`: el tope de la capacidad de un lector es
  `MaxCapacity = max(2, lectura por adelantado)`, en `SizePool` y en `SizeReversePool`. Para `LookaheadFrames >= 2` no
  cambia nada; con 1, el lector reserva 2 cuadros (el pedido y uno por delante), que es el mínimo que ya documentaba
  `SizePool`.

## Cómo se comprobó en Linux

Sin D3D11 ni FFmpeg 9 en Linux, la causa se reprodujo con el doble de `Rodaje.Engine.Tests` (`FakeTimelineBackend`
con `PoolSurfaceBytes`, que sí llama a `HardwarePoolSizer`) en una prueba temporal: `OfflineTimeline` con
`LookaheadFrames = 1` y `MaxDecodersPerMedia = 1`, `Render(15)`, hacia adelante y en reversa. Antes del arreglo las
dos fallaban con `ArgumentException : '2' cannot be greater than 1` en `SizePool`/`SizeReversePool`; después, las dos
pasan. La prueba no se dejó en el repo porque `tests/Rodaje.Engine.Tests/` no está entre los caminos de la tarea
(además, las pruebas de `Offline/` usan rutas `C:\…` y en Linux no abren medios; hubo que traducir las rutas por
reflexión).

## Pruebas

- `Rodaje.Export.Tests`, filtro `Still`, en Linux: 32 pasan, 12 omitidas, 0 fallan. Omitidas antes y después: las
  mismas 12 (necesitan `ffmpeg.exe` LGPL de Windows y, según la prueba, GPU con texturas o decodificación D3D11VA).
  Ninguna omisión nueva.
- CI: ver más abajo.

## Pendiente / sugerido

- Una prueba de regresión en `tests/Rodaje.Engine.Tests/Offline/` (fuera de los caminos de esta tarea): la de arriba,
  `LookaheadFrames = 1` con `PoolSurfaceBytes`, hacia adelante y en reversa.
- Opcional: que el `get_format` registre la excepción que atrapa; hoy un error del dimensionado del pool se ve como
  «El medio no tiene ese cuadro», que despista.

## Posibles choques

Ninguno conocido: el cambio son tres líneas de `OfflineReaders.cs`. La tarea 49 (PR #49) tocaba `src/Rodaje.Export/Still/`,
que aquí no se modifica.

## Qué debe comprobar Daniel en Windows

`ABRIR.bat` o `dotnet test tests\Rodaje.Export.Tests --filter FullyQualifiedName~StillExporterTests` en el PC con la
RTX 3050: las seis de `StillExporterTests` deben pasar, en especial
`El_png_es_el_cuadro_de_la_vista_previa_con_el_titulo`. La CI de Windows no puede confirmarlo porque no tiene
decodificación por hardware.

## CI
