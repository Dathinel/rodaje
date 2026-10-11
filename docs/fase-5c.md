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

## Estado de la ronda 5c.2 (núcleo y motor, 11-oct)

- **Preajustes propios** («Montaje», «Héroe», «Bala», «Entrada rápida», «Salida rápida») como datos en `SpeedPresets`; `Edits.ApplySpeedPreset` es `SetCurve` con la forma (duración por la velocidad media). Formas en [nucleo.md](arquitectura/nucleo.md#velocidad-fase-5c).
- **Ediciones:** `RateStretch` estira una curva (escala `Span` y `Offset`, divide cada punto, acotado a 0,1×–10×) con las mismas reglas de magnetismo; dividir y recortar conservan el mapeo exacto en 10 000 proyectos aleatorios con curvas.
- **Video:** `MaxRate` es el pico real del tramo visible (política de proxies y demanda); ventanas y pre-roll siguen la curva; el keyframe cercano solo si la tasa instantánea no baja de 16× en toda la ventana. `AVDISCARD_NONREF` no hizo falta.
- **Audio:** el lector sigue entre los trozos de un clip dividido con curva (`SameCourse`, `Adopt`, mapeo unido en el plan): idéntico muestra a muestra.
- **Cifras:** «Bala» 2004/2004 seeks exactos con medio real; «Montaje» repetida 10 min: 0 cuadros de video distintos y 0,073 ms de error medio de audio (pedido ≤1 ms), 0,021 ms entre el primer y el último minuto.
- **Falta (interfaz):** editor de puntos de la curva con sus límites, mini curva en el clip, selector de preajustes en la pestaña «Velocidad», y que el arrastre de Alt + borde sobre una curva se calcule desde el inicio del gesto (con pasos de un cuadro el redondeo a centésimas apenas cambia los puntos, y el largo exacto sale de tomar o soltar material al final).

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
- **Motor:** `Tick` ≤0,3 ms; como en la fase 5, la velocidad (igual que los efectos) no añade reservas: el `Tick` reserva lo mismo con y sin velocidad, y los ~2,8 KB por refresco de las clases `LayerFrame`/`CompositionFrame` del contrato están aceptados (Daniel, 11-oct). Cambio de velocidad visible (`edicion.deslizador_a_present` de `app.ps1 -Medir 10 -Escenario velocidad`, desde antes de ejecutar el comando hasta el present) con **mediana ≤20 ms y p95 ≤40 ms** (aprobado por Daniel el 11-oct: dos refrescos de 60 Hz y margen; el máximo no cuenta, porque depende de la espera del vsync, ver abajo); audible en ≤60 ms incluido el búfer del dispositivo.
- **Audio:** tono 440 ± 0,5 Hz de 0,5× a 4×; THD+N ≤ −35 dB; sin chasquidos; transitorios ±5 ms; calidad ≥ `atempo`; ≤0,15 ms por bloque.

**Daniel, a mano:** fluidez a 2× y 4× frente a CapCut; voz y música con y sin mantener tono; si las rampas se sienten como en CapCut; Alt + borde; cámara lenta a 0,25×; reversa 4K; congelar; atajos.

## Cierre de la ronda 5c.1 (11-oct, RTX 3050 Laptop, clip H.264 1080p 23,976)

`app.ps1 -Medir 10 -Escenario X` con el exe de AOT; «antes» es a2c60fc (el padre de la unión del motor de video, 45b947a, con el arreglo de la cultura es-ES para que arranque), donde el motor todavía no aplicaba la velocidad.

| Escenario | Antes: perdidos / tarde | Ahora: perdidos / tarde |
|---|---|---|
| Un clip, sin editar | 0 / 0 | 0 / 0 |
| «capas»: 3 capas del mismo medio a 1×, moviendo una | 0 / 0 | 0 / 0 |
| «efectos»: 5 tramos, 4 transiciones, 3 capas con efectos | 0 / 0 | 0 / 0 en las ediciones (3 perdidos sueltos en una corrida, fuera de ellas) |
| «velocidad»: 1×, 2× y 0,5×, cambiando la de 2× | 0 / 0 (sin velocidad) | 345–348 / 48–72 → 1–7 / 75–226, todo en la capa que cambia salvo 1 cuadro de la de 1× en una corrida de tres |
| «rapido»: un clip a 2×, alternando 2× y 3× | 0 / 0 (sin velocidad) | 2 / 95–112 |

- **Defecto corregido.** Un clip que la edición traía bajo el cursor a mitad de camino (al cambiarle la velocidad a uno que ya había terminado) entraba como un corte natural: tomaba de la caché un cuadro del mismo medio decodificado por otra capa (un cuadro ajeno durante un refresco) y contaba como perdidos los cuadros entre ese y el debido. `TimelineScheduler.Reload` marca los carriles cuyo clip bajo el cursor cambió y su cuadro arranca en el debido (`TimelineSpeedTests.A_clip_brought_under_the_playhead…`).
- **Lo que queda en la capa que cambia** es el costo de un seek: `SetSpeed` deja quieto el inicio del clip, así que el origen bajo el cursor salta (de 2t a 3t) y el decodificador busca el keyframe y avanza hasta el cuadro; mientras tanto se ve el cuadro anterior y cuenta como «tarde». En reproducción sin editar, el motor nuevo no pierde cuadros (un clip y 3 capas a 1×, igual que antes).
- **Edición → pantalla** (30 ediciones por corrida): mediana 2,7–4,1 ms; p95 4,7–10 ms y, en algunas corridas, 30–38 ms. Por partes: interfaz (comando, copia y vistas) 0,2 ms, `TimelineSession.Reload` 0,1 ms, cuadro y present 0,2 ms de mediana; la cola sale de la espera del hilo de render en el vsync (`render.refrescos_lentos`: esperas de 15–60 ms del `FrameWaitHandle` de la cadena con `Tick` y dibujo en 0,1 ms), que aparece con los seeks del decodificador y con la ventana sin foco, no del motor ni de la interfaz. La **primera edición** tras importar (`edicion.a_present`, 460–860 ms) es la que pone el clip en la pista: espera a que se abra el decodificador (pool de D3D11VA) y llegue su primer cuadro; no es la interfaz.
- **Audible:** 50,0 ms de la mezcla + 20 ms del búfer = 70 ms (`AudioRetimeTests.A_speed_change_while_playing…`). Los 50 ms son el margen que la mezcla conserva tras cada edición para reabrir y reposicionar sin cortes; **pendiente de Daniel:** bajarlo a 30 ms daría 50 ms, con el riesgo de un hueco si reabrir el medio tarda más que eso.
- **Arranque AOT:** `probar.ps1` con `RODAJE_PRUEBAS_LARGAS=1` publica y lanza `Rodaje.exe --autoprueba` (todas las pestañas del inspector con un clip, un segundo de reproducción): 4,8 s.

## Riesgos

Refactor transversal del núcleo (mitigado con `ToSourceTime` idéntico a 1× y pruebas de corte); la reversa toca el corazón del programador (va al final); VRAM en reversa 4K (proxies); WSOLA con música (plan B: vocoder de fase o estudiar Signalsmith, MIT); choque con la fase 5b, que también cambia `Clip` y el formato.
