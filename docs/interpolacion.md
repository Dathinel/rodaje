# Interpolación de fotogramas

Cuadros intermedios para la cámara lenta, las rampas de velocidad y los clips de otra cadencia (D-063). Diseño completo con la comparación frente a un modelo de IA en [`propuestas/flujo-optico.md`](propuestas/flujo-optico.md).

## Modos

`ClipSpeed.FrameSampling` (formato 4, sin subir versión):

| Modo | Qué sale |
|---|---|
| `nearest` | El cuadro que contiene el instante del origen (de fábrica). |
| `blend` | Mezcla de los dos cuadros vecinos pesada por la fracción t. |
| `opticalFlow` | Cuadro intermedio por flujo óptico en la GPU; por encima de 4× cae a `blend` y en un corte de escena al cuadro más cercano. |

Un Rodaje 1.0.3 o anterior ve `blend` y `opticalFlow` como `nearest`.

## Cuándo interpola

- Clip de medio (no imagen fija, título ni compuesto), con modo distinto de `nearest`, sin congelar, y con t entre 0,002 y 0,998.
- También a 1× si la cadencia del clip no es la de la secuencia (24 en 30): un clip a 1× conserva el modo (`Normalize` y copiar atributos ya no lo borran; `ClipSpeed.IsIdentity` no cambia).
- En reversa, el par va del cuadro i al i − 1 con t medido hacia atrás.
- Transiciones y capas compañeras igual; con proxies se interpola el proxy en la vista previa y el original al exportar.

## Cómo

- **Motor.** `VideoClipPlan.SampleAt` da el par (A, B), t y el modo efectivo. La vista previa (`TimelineScheduler`) y la exportación (`OfflineTimeline`) piden los dos cuadros y los llevan en `LayerFrame.Next`/`Fraction`/`Sampling`; reproduciendo, si B no llegó, sale A.
- **Render.** `Compositor.DrawLeaf` sustituye el cuadro por el interpolado antes de `LayerRenderer`. El interpolador trabaja **en los planos del origen** (NV12, P010 o P016) y devuelve planos del mismo formato y tamaño: la forma del píxel, el giro, la conversión de color y el HDR no cambian.
- **Flujo.** DIS (Kroeger et al., 2016) de ida y vuelta en pirámide sobre la luma, consistencia de ida y vuelta para las oclusiones (lo tapado se toma del otro lado; donde fallan los dos, mezcla), flujos al instante t con la aproximación de Super SloMo y la croma deformada con el flujo a media escala. Un par es corte de escena si la diferencia de histogramas de luma y el residuo tras deformar superan sus umbrales; se decide en la GPU sin leer de vuelta.
- **Referencia.** `FlowReference` (Analysis) hace lo mismo en la CPU y es la vara del shader.

## Cifras objetivo

| Medida | Objetivo |
|---|---|
| Flujo 1080p / 4K, RTX 3050 | ≤ 4 ms / ≤ 12 ms por cuadro |
| Reproducir 1080p a 0,25× en 60 cuadros/s | 0 cuadros tarde en 10 s |
| VRAM extra | ≤ 64 MB en 1080p, ≤ 200 MB en 4K |
| WARP 1080p al exportar | ≥ 3 cuadros/s |
| Con `nearest` | los bytes de salida no cambian |

## Estado

Fases en la tabla de [`propuestas/flujo-optico.md`](propuestas/flujo-optico.md#4-fases). La calidad «IA» con RIFE (F7) queda para cuando Daniel la pida.
