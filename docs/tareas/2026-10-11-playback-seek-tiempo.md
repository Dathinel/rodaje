# Seek lejano de `PlaybackScheduler` sin depender de la carga

## Hecho
- `SeekLejosReproduciendo_CuestaLoMismoQueUnSaltoCorto` (1e6 y 1e10 cuadros) ya no mide 2 s de reloj de pared: solo tiene una guarda de 60 s contra cuelgues. Un seek O(salto) con 1e10 cuadros tarda minutos, así que la guarda sigue detectando la regresión.
- El límite estricto de 2 s pasa a `SeekLejosReproduciendo_TardaMenosDeDosSegundos`, categoría `TiempoReal` (la CI la excluye en Linux y Windows).
- Lógica común extraída a `SeekLejos`.

## Pruebas omitidas
Ninguna omitida ni excluida nueva: antes 0, después 0. La nueva prueba `TiempoReal` la excluye el filtro que ya existía en la CI.

## Pendiente / choques
Nada. Sin choques con `origin/main`.

## Para Daniel
En el PC, `RODAJE_PRUEBAS_LARGAS` no hace falta: corre `dotnet test --filter Categoria=TiempoReal` en Rodaje.Playback.Tests y comprueba que pasa (<1 ms esperado).
