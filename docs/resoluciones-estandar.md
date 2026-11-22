# Resoluciones estándar

Un proyecto nuevo nace en 1920×1080, pero la secuencia toma el formato del primer video que entra. Si ese video tiene un tamaño raro (la grabación de pantalla de un celular, 1080×2400), Rodaje lo avisa en la barra de avisos y ofrece el estándar más cercano; al exportar, cada preajuste dice el tamaño que saldrá de verdad y, si no es estándar, la línea «Formato» ofrece «Usar …» solo para esa exportación. Código en `ProjectFormats.StandardSizes` y `ProjectFormats.NearestStandard`; pruebas en `tests/Rodaje.App.Tests/Format/StandardSizeTests.cs`.

## Qué cuenta como estándar

| Familia | Tamaños |
|---|---|
| 16:9 | 1280×720, 1920×1080, 2560×1440, 3840×2160, 7680×4320 |
| 9:16 | 720×1280, 1080×1920, 1440×2560, 2160×3840 |
| Redes | 1080×1080 (1:1), 1080×1350 (4:5) |
| Cine DCI | 4096×2160, 2048×1080 |

Los de cine se aceptan, pero nunca se ofrecen: quien los usa los eligió a propósito.

## El estándar que se ofrece

1. Casi cuadrado (1 % de margen): **1080×1080**.
2. Vertical más cerca de 4:5 que de 9:16 (ancho/alto > 0,671, la media geométrica): **1080×1350**.
3. Lo demás va a **16:9 o 9:16 según la orientación**, con el menor lado corto (720, 1080, 1440, 2160, 4320) que no baje del de origen, con un 2 % de margen para los 1088 de algunos códecs. Así no se pierde resolución al cambiar.

| Origen | Se ofrece | Por qué |
|---|---|---|
| 1080×2400, 1080×2340 (celular) | 1080×1920 | Vertical, lado corto 1080 |
| 1440×3200 | 1440×2560 | Vertical, lado corto 1440 |
| 2340×1080 (celular horizontal) | 1920×1080 | Horizontal, lado corto 1080 |
| 1366×768 (portátil) | 1920×1080 | 768 no cabe en 720 sin bajar |
| 2560×1080, 3440×1440 (ultrapanorámico) | 1920×1080, 2560×1440 | Las plataformas no tienen 21:9: mejor 16:9 con recorte o barras |
| 640×480 | 1280×720 | El menor estándar |
| 864×1080 | 1080×1350 | Más cerca de 4:5 |
| 5120×2880 | 7680×4320 | Lado corto 2880 pide 4320 |

El encaje por defecto es **Rellenar** (escala para cubrir sin deformar y recorta un poco): en el proyecto con `ChangeFormat(…, ClipFit.Fill)`, un paso que se deshace; al exportar con `ExportFit.Crop`, y «Barras» se elige en Avanzado › Forma. Rellenar ajusta los clips que ya están en la línea de tiempo; los que se añaden después entran encajados como siempre.
