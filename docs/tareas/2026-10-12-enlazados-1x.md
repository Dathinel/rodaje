# Tarea 67: contrato de los enlazados al volver a 1×

Rama `claude/enlazados-1x`, PR #70. Parte de la entrega de la tarea 57 (`docs/tareas/2026-10-12-main-verde-2.md`) y de lo que pidió la revisión en bloque 7.

## Qué cumple cada miembro del grupo al volver a 1×

Queda escrito en `AlignEnd` y `LinkedEnd` (`src/Rodaje.Core/Editing/TimelineEdits.Speed.cs`, solo comentarios), y lo comprueban la propiedad y los ejemplos fijos.

Todo clip del grupo que vuelve a 1× desde otra velocidad:

1. **Sin velocidad.** `Speed` es `null`.
2. **Final en la rejilla.** El final cae en un cuadro de la secuencia o en el borde del archivo, es decir, con `SourceOut` igual a la duración del medio. El borde puede no caer en un cuadro si el inicio tampoco cae en uno: es el único final fuera de la rejilla de la secuencia. Hay una excepción: cuando ningún candidato sirve, `AlignEnd` deja el mapeo ajustado tal cual. Esto no apareció en ninguna de las cadenas al azar comprobadas (tres corridas de 300 000 casos).
3. **Sin material de más.** No pide material que el archivo no tiene.
4. **Proyecto válido.** La secuencia pasa `ModelValidator`.
5. **Material**, tras velocidades lentas y curvas (lo que recorre la propiedad):
   - **Exacto** si el final estaba en un cuadro, le quedaba al menos un cuadro de archivo detrás y el clip no era muy corto: 16 cuadros si lleva curva. Es el `Tolerance` de la propiedad, sin cambios.
   - **A un cuadro más lo que el final pasaba del cuadro**, en los demás casos.
   - **Un cuadro como mucho para el miembro que empieza en otra fase del cuadro que el clip editado**, aunque su final estuviera en la rejilla. El editado es el primero del grupo en `Retime` y lleva el material compartido a su rejilla, hasta medio cuadro. El otro lo redondea a la suya al volver, otro medio cuadro. No hay material que caiga en las dos rejillas.
   - **Igual que el editado** para los miembros que empiezan en su misma fase.

## Lo hecho

- **`TimelineEdits.Speed.cs`.** Documenta el contrato en `AlignEnd` y `LinkedEnd`. También reescribe el comentario «también es de la rejilla»: ahora dice qué es la rejilla de 1× (los finales en un cuadro y el borde, como en `GridMaterial`) y por qué `map.Start + target` deja `SourceOut` justo en el borde. No cambia nada de lo que hace el código.
- **`SpeedMaterialPropertiesTests`:**
  - Exige que el proyecto pase `ModelValidator`.
  - Exige que cada miembro que tenía velocidad termine en un cuadro o en el borde del archivo.
  - Estrecha la tolerancia. Antes era `group.Max(Tolerance)` para todo el grupo. Ahora cada miembro usa la suya, y solo el que empieza en otra fase que el editado suma hasta un cuadro. Con tasas cuyo cuadro no dura un número entero de ticks, un tick de diferencia de fase cuenta como la misma fase.
  - `RunChain` recibe la semilla opcional y la pone en todos los mensajes. Los mensajes de material ahora también dicen la tolerancia.
- **`SpeedMaterialRegressionTests`.** Pasa la semilla a `RunChain`. Su comentario explica qué caso cubre cada semilla: en las cuatro de fase se edita el clip que empieza fuera de un cuadro, y en `7LMaTUJGUpm6`, `ePnp7RIsF_41` y `7UwXLCK11vo3` el otro vuelve con un cuadro de diferencia.
- **`tests/Rodaje.Core.Tests/Editing/Speed/LinkedBackToNormalSpeedTests.cs` (nuevo).** Diez casos a 25 fps con video «c» y audio «ca» enlazados:
  - Los dos con el inicio en un cuadro vuelven exactos (0,5×, 0,7× y 0,1×).
  - Audio un tercio de cuadro después, en mitad del archivo: va al cuadro más cercano con un tercio de cuadro menos, y el video vuelve exacto.
  - Audio un tercio de cuadro después, al final del archivo: conserva el borde y todo su material, con el final fuera de un cuadro.
  - Editando el audio, que empieza medio cuadro después, a 0,3×: el video, que empezaba y terminaba en un cuadro, vuelve con 21 cuadros en vez de 20.
  - El mismo par editando el video: el video vuelve exacto.

## Comprobaciones

- **El arreglo de la 57 sigue cubierto.** Comenté a mano el `ends.Insert(0, map.Start + target)` de `AlignEnd` y corrí las pruebas. Fallaron la semilla `0pZ2Mq9N8IE2`, ahora con el mensaje «material 446917035, antes 482160000, tolerancia 23482965 (semilla 0pZ2Mq9N8IE2)», y los dos casos de `Fuera_de_la_rejilla_al_final_del_archivo_conserva_el_borde`. Después restauré el código.
- **La tolerancia estrecha basta.** La propiedad estrecha pasó tres corridas de 300 000 casos al azar en Linux, con un banco de pruebas temporal que no se sube. En cada una se comprobaron unas 86 000 cadenas. Las cinco semillas de regresión también pasan.
- **Antes de estrechar.** Con la tolerancia propia de cada miembro y sin la de fase, fallan `7LMaTUJGUpm6`, `ePnp7RIsF_41` y `7UwXLCK11vo3`, siempre en el enlazado de la rejilla y por un cuadro justo. En 40 000 casos al azar no apareció ningún final fuera de un cuadro y del borde, salvo un clip que ya estaba a 1× y al que la cadena aplicó 1×, que no cambia nada. Ningún proyecto quedó inválido.

## Decisiones

- **La tolerancia de fase va al enlazado, no al editado.** La tarea pedía estrechar la tolerancia «al miembro que empieza fuera de fase». La fase se mide frente al clip editado, porque el que pierde el cuadro es el enlazado: el editado lleva el material a su rejilla. Si la fase se midiera frente a la rejilla de la secuencia, fallarían las tres semillas de arriba, porque el que pierde el cuadro empieza en un cuadro.
- **No se cambió lo que hace el código.** El contrato describe lo que hace main desde la 57, y las pruebas lo fijan. Que el enlazado de la rejilla vuelva exacto pediría otra regla en `LinkedEnd` a 1×. La 57 probó copiar la duración del primero y lo descartó (`61Idl4YbLW02`).
- **El final en el borde sin caer en un cuadro se acepta.** `ModelValidator` lo acepta, y es lo que conserva el material del clip que empieza fuera de un cuadro.

## Pendiente y observaciones

- **Velocidad rápida constante.** Puede perder un cuadro aunque el clip estuviera en la rejilla. Por ejemplo, 100 cuadros a 3× y de vuelta a 1× dan 99 cuadros: a 3× el final más cercano deja 99 cuadros de material y la rejilla de 1× los conserva. La propiedad solo recorre velocidades lentas y curvas, así que esto no lo cubre. Queda anotado en el comentario de `AlignEnd`. Si se quiere que vuelva exacto, haría falta una tarea aparte.
- **Ni la propiedad ni los ejemplos cubren** reversa, tono ni `RateStretch` al volver a 1×.

## Posibles choques

Ninguno conocido. Al abrir el PR, ninguna otra rama `claude/*` del remoto toca `TimelineEdits.Speed.cs` ni las pruebas `SpeedMaterial*`, y en `origin/main` el último cambio de esos archivos es el de la tarea 57 (e61b00a).

## Pruebas omitidas

No hay omisiones nuevas: ninguna prueba nueva lleva categoría ni `Skip`. Los conteos de omitidas no cambian.

## Verificación en Linux

- **Pruebas de velocidad.** `SpeedMaterial*` y `LinkedBackToNormalSpeedTests` pasan, 16 de 16.
- **Core completo** con el filtro de la CI (sin `SoloWindows`, `TiempoReal` ni `Rendimiento`): pasan 1404 de 1404.

## CI

Pendiente al escribir esta entrega; se completa abajo.

## Qué comprobar en Windows

No hay cambios de comportamiento, solo comentarios y pruebas. Basta con que `ABRIR.bat` quede en verde.
