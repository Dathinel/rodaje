# Fase 5c: velocidad de clips

Plan del 10 de octubre de 2026, aprobado por Daniel: va antes que la fase 5b (textos); en una pista libre, si el clip alargado choca, se corre esa pista y las de su enlace, cada una con su Δ; por encima de 8× el audio sigue sonando (granuloso) con un aviso de los límites en el inspector, sin silenciar. Empieza cuando la integración de la fase 5 esté unida a main.

## Rondas

| Ronda | Qué |
|---|---|
| **Paso 0** | Contrato y **formato 4 completo** (constante, reversa, curva y congelado), para no volver a cambiar el formato por la velocidad. |
| **5c.1** | Velocidad constante 0,1×–100× con duración que cambia y ripple coherente; congelar cuadro; audio con «mantener tono»; Alt + arrastrar un borde para estirar. |
| **5c.2** | Curvas: preajustes propios «Montaje», «Héroe», «Bala», «Entrada rápida», «Salida rápida» y curva editable. |
| **5c.3** | Reversa en video y audio (decodificación por GOP hacia atrás). |
| Después | Mezcla de cuadros en cámara lenta; el flujo óptico es un módulo de IA de la fase 7. |

## Diseño

- **Propiedad del clip, no efecto.** `Clip.Speed` (`ClipSpeed`: `Rate` racional, `Curve`, `Offset`, `Reverse`, `Freeze`, `PreservePitch`, `FrameSampling`) y `Clip.Duration` guardada. Cambia el final del clip y los enlazados necesitan el mismo valor; así lo hacen los editores profesionales. `rodaje.speed` queda reservado y oculto; la migración 3 → 4 quita sus instancias (nunca se aplicaron).
- **Tiempo exacto sin deriva.** `RetimeMath` integra la velocidad en `Int128` (constante `num/den`; curva lineal a tramos en centésimas, 10–1000) y `ClipTimeMap` es la única puerta: origen de cada instante calculado desde el ancla del clip, cortes exactos tick a tick, inversa para transiciones, keyframes y miniaturas. A 1× todo es idéntico al formato 3.
- **Keyframes de efectos** en tiempo del origen (van con el material: a 2× un fundido de 1 s dura 0,5 s); al invertir se reflejan en el mismo paso del deshacer; en un congelado usan un tiempo virtual (Ken Burns sobre el cuadro).
- **Transiciones** con la misma regla, material sobrante a la tasa del borde. **Enlazados** cambian juntos; cada pista con su propio Δ de ripple.
- **Magnetismo al cambiar la velocidad** (Daniel, 11-oct): en cualquier pista, si el clip se acorta el siguiente se une y la transición del corte se queda (acotada al clip más corto); si crece, respeta los huecos y solo empuja lo que pisa. `FreezeFrame` inserta y corre lo que sigue. Alt + borde deja quieto el inicio. Siguen el modo de ripple de la sesión: solo las pistas del grupo (cada una con su Δ), todas, o todas y marcadores. «Mantener tono» queda desactivado a 1×.
- **Audio:** WSOLA propio en C# con la posición atada al mapeo (deriva 0, curvas gratis, apto para AOT, <1 % de un núcleo); sin «mantener tono», remuestreador sinc propio; reversa por bloques de 0,5 s. `rubberband` descartado (GPL); `atempo` solo como referencia de calidad en las pruebas.
- **Decodificación rápida:** se decodifica todo pero la caché guarda solo los cuadros que se ven; proxies en Auto cuando fps×velocidad supera lo que mide el decodificador; `AVDISCARD_NONREF` si hace falta; de 16× a 100× en reproducción se muestra el keyframe cercano (en pausa y al exportar, siempre exacto).
- **Reversa:** índice de keyframes, ventana descendente por GOP, proxy obligatorio en 4K o dos pasadas.
- **Interfaz:** pestaña «Velocidad» en el inspector (deslizador logarítmico, duración resultante editable, mantener tono, invertir, curvas con preajustes y editor, «Congelar»); indicador en el clip («2,0×», «◀», mini curva); miniaturas y onda con el mapeo; Alt + borde; atajos Ctrl+R (velocidad), Mayús+R (congelar), Ctrl+Alt+. y Ctrl+Alt+, (más rápido y más lento).

## Reparto

Paso 0 en serie (modelo, `RetimeMath`, `ClipTimeMap`, formato 4, validador, firmas del motor, D-026). Luego 7 agentes: A ediciones, B medios, C motor de video, D motor de audio, E inspector, F línea de tiempo, G medición. Unión 0 → A → … → G, integración y verificación por un agente nuevo de **otro modelo (Sonnet)**.

## Criterios de terminado

- **Núcleo:** 0 fallos, ≥99 % de cobertura; 10 000 proyectos aleatorios con velocidad válidos y con deshacer idéntico; dividir o recortar no cambia ningún cuadro de origen; `RetimeMath` igual a una referencia `BigInteger` en 10⁶ casos.
- **Seek exacto:** 100 % del cuadro esperado en 2000 seeks por caso (0,25×, 0,5×, 1,37×, 2×, 4×, 1001/1000×, 100×, reversa, «Bala», congelado).
- **Deriva A/V en 10 min** a 1,5×, 0,5× y «Montaje»: 0 cuadros de video; audio ≤5 ms de error medio.
- **Cuadros perdidos (RTX 3050):** 1080p a 2× y 4× sin proxy: 0; 4K a 2×: ≤0,5 % sin proxy y 0 con proxy; 4K a 4× y reversa 4K: 0 con proxy; reversa 1080p: 0.
- **Motor:** `Tick` ≤0,3 ms y 0 B; cambio de velocidad visible en ≤20 ms y audible en ≤60 ms.
- **Audio:** tono 440 ± 0,5 Hz de 0,5× a 4×; THD+N ≤ −35 dB; sin chasquidos; transitorios ±5 ms; calidad ≥ `atempo`; ≤0,15 ms por bloque.

**Daniel, a mano:** fluidez a 2× y 4× frente a CapCut; voz y música con y sin mantener tono; si las rampas se sienten como en CapCut; Alt + borde; cámara lenta a 0,25×; reversa 4K; congelar; atajos.

## Riesgos

Refactor transversal del núcleo (mitigado con `ToSourceTime` idéntico a 1× y pruebas de corte); la reversa toca el corazón del programador (va al final); VRAM en reversa 4K (proxies); WSOLA con música (plan B: vocoder de fase o estudiar Signalsmith, MIT); choque con la fase 5b, que también cambia `Clip` y el formato.
