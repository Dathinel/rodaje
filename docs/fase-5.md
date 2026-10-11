# Fase 5: efectos, LUT, mezcla, transiciones y audio

Plan del 10 de octubre de 2026, aprobado por Daniel: transiciones centradas en el corte sin solapar clips, texto y títulos en una fase 5b aparte, y paquetes solo con primitivas (sin shaders propios).

## Punto de partida

- **Modelo:** `EffectInstance` con parámetros escalares y keyframes; tipos en `BuiltInEffects` (`transform`, `audio`, `color`, `blur`, `sharpen`, `vignette`, `speed`, `stabilize`), ediciones completas.
- **Motor:** `TimelinePlan` solo lleva la primera transformación; un cuadro por pista.
- **Compositor:** un shader y un blend premultiplicado, directo a RGBA16F, sin intermedios. Shaders con `D3DCompile` en ejecución (D-015).
- **Audio:** `MixRenderer` suma sin ganancia.
- **Interfaz:** filtros con «Se aplica en la fase 5», biblioteca con «Próximamente».
- **Inconsistencia:** la envolvente de la línea de tiempo lee `rodaje.volume.gain` (lineal) y el inspector escribe `rodaje.audio.gainDb`. Se unifica en `gainDb` y la migración 2→3 convierte lo viejo.
- **Fuera de la fase 5:** velocidad (cambia la relación origen/secuencia, necesita su propio formato) y estabilizar (análisis, fase 7). Texto y títulos van en una **fase 5b** inmediata (clips sin medio, DirectWrite, fuentes con licencia, edición sobre el lienzo, base de los subtítulos).

## Diseño

### Registro de efectos (AOT, sin reflexión)

- `Rodaje.Core/Effects/EffectRegistry`: definiciones estáticas (`EffectDefinition` con código entero estable, destino, etapa, parámetros y opciones no numéricas) en un `FrozenDictionary`.
- `Rodaje.Render/Effects/GpuEffectRegistry`: tabla explícita código → implementación. Una prueba exige que registro y GPU coincidan.
- `Services/Inspector/EffectUi`: etiquetas, unidades y pasos por parámetro; `ParameterSpec` sale del registro.
- `LayerFrame` suma `Effects` (valores en `InlineArray`), `Blend` y `Transition`, con listas de un pool: **0 bytes reservados por `Tick`**.

### GPU

1. **Camino rápido intacto:** capa sin efectos y mezcla normal = igual que hoy.
2. **Efectos por píxel fusionados** (color, LUT 3D, viñeta) en el mismo shader que convierte YUV a RGB: +0,1–0,2 ms en 4K. Es la clave de la fluidez.
3. **Espaciales** (desenfoque con pirámide, nitidez) en intermedios del tamaño en pantalla, no del origen.
4. **Unidades en píxeles de secuencia:** la vista previa a media resolución coincide con la exportación.
5. **Mezcla:** normal, suma, multiplicar y trama por estado fijo; superponer, luz suave/fuerte, oscurecer, aclarar, diferencia, exclusión, sobreexponer y subexponer por shader, con fórmulas W3C y en gamma (como Photoshop).
6. **Transiciones:** dos intermedios y un shader de mezcla. Propias: fundido cruzado, fundido a color, barrido, empujar, zoom y desenfoque. Las de paquete son curvas de transformación más una mezcla.
7. **LUT `.cube`** como `Texture3D` con interpolación por hardware, cargada fuera del hilo; mientras carga, la capa sale sin LUT.
8. **Pool de texturas** con tope (≤300 MB en 4K) fuera del presupuesto de decodificadores.
9. **Calidad de vista previa:** Auto, Completa, Mitad y Cuarto. Auto baja si la GPU pasa de 10 ms (p95) al reproducir y vuelve a completa en pausa. La exportación siempre a completa.

**Shaders:** se mantiene `D3DCompile` en ejecución, con caché en disco (`cache\shaders`), calentamiento en segundo plano y una prueba que compila todas las variantes. Ningún cuadro espera a un shader.

### Formato `.rodaje` 3

- `EffectInstance.Options` (modo de mezcla, LUT, origen del preajuste).
- `Track.Effects`: volumen de pista con keyframes.
- `Track.Transitions` (`from`, `to`, `duration`…), centradas en el corte con el material sobrante; si falta, se congela el cuadro del borde. Los clips nunca se solapan.
- `TransitionOps.Reconcile` al final de cada edición (dividir, rodar, recortar, mover, borrar) dentro de la misma instantánea del deshacer.
- Comandos nuevos: añadir, quitar, cambiar duración y tipo de transición.
- Migración 2→3 y esquema `rodaje-proyecto.v3.schema.json`.

### Contenido de `.paquete`

| Tipo | Archivo |
|---|---|
| `lut` | `.cube` 3D (2 a 65, ≤16 MB) |
| `filter` | `rodaje.filter` v1: lista de efectos primitivos |
| `effect` | `rodaje.effect` v1: animación de entrada, salida o completa sobre primitivas |
| `transition` | `rodaje.preset` v2: curvas de salida y entrada más una mezcla; el v1 de «Guardar como transición» se sigue leyendo |
| `text-style` | Fase 5b |

Filtros y efectos se **copian** al proyecto al aplicarlos; LUT y transiciones se **referencian**. Si falta el paquete: sin LUT, la transición cae a fundido cruzado y se avisa. Primeros paquetes propios, en repos aparte (`Dathinel/rodaje-transiciones` y `Dathinel/rodaje-filtros`, CC0): transiciones y looks con LUT generados por fórmulas propias.

### Audio

Ganancia del clip y de la pista con keyframes, fundidos de potencia constante y fundidos cruzados en las transiciones, aplicados por bloques de 64 muestras con rampa antes del limitador y de los medidores.

## Reparto

**Paso 0, en serie:** el contrato (modelo, registro, `CompositionTypes`, `IEffectContent`, DTO de contenido, ejemplo `proyecto-v3.rodaje`). Se revisa antes de repartir y luego queda congelado.

| Agente | Dueño de | Tareas |
|---|---|---|
| A. Núcleo y formato | `Rodaje.Core`, `schema/`, `formato-proyecto.md` | Migración, validador, `Reconcile`, comandos de transición, pruebas de propiedades |
| B. Render | `Rodaje.Render` | Pool, efectos, mezcla, transiciones, LUT, caché de shaders, rendimiento 4K |
| C. Motor | `Rodaje.Engine` | Plan de efectos sin reservas, carriles de transición, ganancia en el mezclador |
| D. Paquetes | `Rodaje.Paquetes`, `contenido/`, `formato-paquete.md` | Lectores, fuzz de `.cube`, `PackageContentStore`, `rodaje-filtros` y `rodaje-transiciones` (repos aparte) |
| E. Inspector y biblioteca | `Services/Inspector`, `Views/Inspector`, `Services/Packages`, `Views/Library` | Filtros con parámetros, mezcla, LUT, volumen con keyframes, aplicar paquetes |
| F. Línea de tiempo y reproductor | `Views/Timeline`, `Views/Player`, `Shell`, sesión y ajustes | Soltar transiciones, manijas de duración, fundido automático, envolvente, calidad de vista previa, medición |

Orden de unión: A → D → B → C → E → F, después integración y verificación por un agente distinto.

## Criterios de terminado

- **Pruebas:** 0 fallos, AOT sin advertencias, `Rodaje.Core` ≥99 % de cobertura; 10 000 proyectos aleatorios siempre válidos y deshacer idéntico byte a byte; v1 y v2 migran a v3.
- **Fidelidad:** cada efecto ≤2/255 frente a una referencia en la CPU; LUT identidad ≤1/255; los 13 modos de mezcla ≤2/255; mitad frente a completa ≤3/255 de media.
- **Rendimiento (RTX 3050 Laptop):**

  | Escenario | Objetivo |
  |---|---|
  | 8 capas 1080p sin efectos | ≤0,6 ms (sin retroceso) |
  | Una capa 4K con color y LUT | ≤1,0 ms |
  | 3 capas 4K con color, LUT, desenfoque y superponer | ≤6 ms de mediana, ≤8 ms p95 |
  | Transición 4K con efectos | ≤5 ms |
  | Media resolución | ≤40 % de la completa |
  | `Tick` con efectos | ≤0,3 ms y 0 B por cuadro |

- **App:** 1080p con 3 capas con efectos y 4 transiciones, 0 cuadros perdidos; mover un deslizador de color al reproducir se ve en ≤20 ms.
- **Audio:** −6 dB = 0,501 ±0,001; fundido cruzado mantiene la potencia ±0,5 dB; sin saltos de más de 0,1 dB.
- **Paquetes:** 1000 `.cube` aleatorios no tumban el lector; 65³ en ≤150 ms; `rodaje-filtros` y `rodaje-transiciones` instalan, se aplican y, si se desinstalan, el proyecto abre con aviso.

## Medido en la integración (10 de octubre, RTX 3050 Laptop)

| Criterio | Objetivo | Medido |
|---|---|---|
| 8 capas 1080p sin efectos | ≤0,6 ms | 0,54 ms (p95 0,76) |
| Una capa 4K con color y LUT | ≤1,0 ms | 0,33 ms |
| 3 capas 4K con color, LUT, desenfoque y superponer | ≤6 ms, p95 ≤8 | 2,7 ms, p95 5,1 |
| Transición 4K con color y LUT | ≤5 ms | 0,99 ms (fundido, barrido, zoom); 1,23 ms con desenfoque |
| Media resolución | ≤40 % | 22 % |
| `Tick` con efectos (8 capas × 4 efectos) | ≤0,3 ms y 0 B | 0,10 ms; 2831 B con y sin efectos (las clases `LayerFrame`/`CompositionFrame` del contrato; los efectos no suman) |
| App 1080p: 3 capas con efectos y 4 transiciones | 0 cuadros perdidos | 0 perdidos, 0 tarde de 1596; 5,1 efectos dibujados por cuadro; GPU 0,16 ms (p99 0,27) |
| Deslizador de color al reproducir | ≤20 ms | 2,4 ms de mediana, p99 4,8 |
| App 4K sin proxies, mismo escenario | ≤1 % de refrescos tarde | 0 tarde de 1860; GPU 0,70 ms (p99 2,3; la vista compone a 1920×1080 porque el panel es menor que la mitad de 4K); deslizador p99 34 ms |
| Carga: 4 pistas HEVC 4K con cortes cada 3 s | VRAM ≤ presupuesto | 1417 MB de 1485, pre-roll 93 %, 4 refrescos tarde de 2875 |
| Carga: 4 pistas HEVC 1080p con fundido en cada corte | pre-roll de transiciones ≥90 % | 28/28, 0 tarde, VRAM 869 MB |
| LUT y transición de paquete reales (`rodaje-filtros`, `rodaje-transiciones`) | se aplican; sin paquete abre con aviso | LUT a 0,24/255 de la referencia en la CPU; barrido de 180° del lado correcto; sin paquetes, aviso, sin LUT y fundido cruzado |

La primera edición tras armar el escenario sigue tardando 450–600 ms en llegar a pantalla (`edicion.a_present_ms`, p99): es el pendiente conocido de la fase 4.

**Para Daniel, a mano:** fluidez frente a CapCut con el mismo clip 4K en 3 capas con efectos; deslizadores de color reproduciendo; los 8 looks; soltar transiciones y su duración por defecto (0,5 s); modos de mezcla; fundidos y envolvente de audio; si nota la calidad Auto.

## Riesgos

- Los carriles de transición tocan el corazón del programador: con 4 pistas 4K y transiciones a la vez habrá cuadros tarde (mitigan proxies y calidad Auto).
- Retroceso del camino rápido o reservas por cuadro: pruebas de rendimiento sin efectos y de 0 B por `Tick`.
- Gráficas de 2 GB o integradas: el pool se limita según la VRAM.
