# Tarea 16: material perdido al encadenar curvas de velocidad

## Qué cambió

- `src/Rodaje.Core/Editing/TimelineEdits.Speed.cs`, `AlignEnd` (y dos ayudantes nuevos, `GridMaterial` y `CurveForMaterial`):
  - **Causa:** con curva, el final se llevaba al cuadro más cercano recortando el mapeo, y eso movía el material hasta `v_final · ½ cuadro`. El cambio siguiente partía del material ya redondeado, así que los errores se sumaban. En `origin/main`, «Bala» → «Héroe» → «Ninguna» sobre 10 s da 10,054 s a 25 fps (el informe hablaba de 9,942 s a otra cadencia).
  - **Material buscado:** es el del clip llevado a su **rejilla de 1×**. Esa rejilla son los finales en un cuadro de la secuencia más el borde del archivo, para que un clip que llega al final del archivo lo conserve. No depende del redondeo anterior.
  - **Con curva:** el material se conserva exacto con el final en un cuadro. Para eso se busca el `SpeedCurve.Span` (secante más un barrido de ticks vecinos) con el que la duración alineada recorre justo ese material. El `Span` se mueve como mucho un octavo de la duración, entre 2 y 8 cuadros, así la forma sigue cubriendo el clip. Nunca pide más material del que tiene el archivo.
  - **Con velocidad constante**, entre los cuadros vecinos se elige el que deja el material más cercano al buscado.
  - **Al volver a 1×** (si el clip tenía velocidad), el final va al cuadro más cercano de la rejilla.
- Pruebas nuevas:
  - `tests/Rodaje.Core.Tests/Editing/Speed/CurveMaterialTests.cs`:
    - el caso del defecto a 24, 25, 29,97, 30 y 60 fps;
    - cada preajuste tras cada otro;
    - velocidades lentas intercaladas con «Héroe»;
    - un clip que termina entre cuadros se mueve una sola vez.
  - `tests/Rodaje.Core.Tests/Editing/Speed/CurveMaterialPropertyTests.cs`:
    - 3000 cadenas al azar (preajustes, curvas al azar, velocidades ≤ 1×) a seis cadencias vuelven exactas a 1×;
    - una velocidad rápida mueve a lo sumo medio cuadro de la línea de tiempo y lo que sigue no añade nada;
    - repetir un preajuste nunca cambia el material.
  - `tests/Rodaje.Core.Tests/Properties/SpeedMaterialPropertiesTests.cs`: la propiedad sobre los **10 000 proyectos al azar** de `RandomTimeline`. Cuesta unos 7 s y se comprueban unas 3000 cadenas.
- Con el código de `origin/main` fallan las 11 pruebas nuevas; con el arreglo pasan.

## Límites (inherentes, documentados en las pruebas)

- **Velocidad constante > 1×:** con el final en un cuadro, el material solo puede ser múltiplo de `r` cuadros de origen. Ese cambio puede moverlo hasta `r/2` cuadros de origen (medio cuadro de la línea de tiempo), y otro cambio rápido posterior puede volver a moverlo. Para conservarlo exacto habría que guardar el material original en el modelo (formato) o dejar el final fuera de la rejilla. Lo lento, las curvas y la vuelta a 1× no añaden error.
- **Menos de un cuadro de archivo detrás del clip:** una velocidad lenta no puede redondear hacia arriba y puede volver con un cuadro menos. Pasa una vez y no se acumula.
- **Clips muy cortos** (menos de unos 16 cuadros) con curvas rápidas, y curvas cuya velocidad media coincide con la del final: el `Span` no alcanza a compensar y puede quedar un cuadro de diferencia.
- **Clip a 1× cuyo final no cae en un cuadro** (por ejemplo, 10 s a 29,97 fps): el primer cambio de velocidad lo lleva al cuadro más cercano (menos de medio cuadro) y luego queda fijo.
- **`Span` y duración:** el `Span` de la curva puede diferir de la duración del clip en unos cuadros (antes también diferían en menos de un cuadro). `SpeedCurveMath` de la App acota la posición del cursor a la curva, así que no cambia nada visible.

## Decisiones

- **Por qué no se recalcula «desde el material original»:** el modelo no lo guarda, y los caminos permitidos no incluyen `Model/` ni el formato. En su lugar la rejilla de 1× hace de ancla.
- Ni `RateStretch` (usa `place`) ni `FreezeFrame` cambian.

## Pruebas

- Linux, `Rodaje.Core.Tests` con el filtro de la CI: 1081 de 1081. Sin el filtro solo falla `Save_TempLockedByAnotherProgram…`, de la categoría `SoloWindows` y fuera de la CI de Linux.
- **Pruebas omitidas:** 0 antes y 0 después; no se añadió ningún `Skip`.
- **CI:** la de 49cf1c8 falló porque, sobre `main` con el formato 5, la propiedad elegía clips de título, que no tienen medio. 1b23390 los salta. Se comprobó localmente sobre `origin/main` unido a la rama: 1226 pruebas, todas en verde.

## Choques

- En `origin/main` entraron los clips generadores (formato 5). Tocan 3 líneas de `TimelineEdits.Speed.cs` (`EnsureNoGenerator`); git las une sin conflicto. Un título no tiene velocidad, así que no pasa por `AlignEnd`.
- `docs/arquitectura/nucleo.md` (sección Velocidad) podría mencionar la rejilla de 1×; no está entre los caminos permitidos.

## Qué comprobar en Windows

- Inspector → Velocidad: aplicar «Bala», «Héroe» y «Ninguna» a un clip de 10 s; debe volver a 10 s (00:00:10:00).
- Arrastrar puntos de la curva varias veces y volver a «Ninguna»: la duración original no cambia.
- La curva dibujada en el inspector sigue cubriendo el clip entero.
