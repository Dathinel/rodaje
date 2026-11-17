# Linux y macOS, fase 3: la capa `IGpu` en Windows

Resultado de la fase 3 de D-066 ([propuestas/mac-y-linux.md](propuestas/mac-y-linux.md)), 16-nov. Contrato en [igpu.md](igpu.md); manifiesto de shaders en [shaders-spirv.md](shaders-spirv.md).

## Qué quedó

- **`IGpu` y `D3D11Gpu`** (#586): el contrato, el motor de Windows sobre el `GpuDevice` de siempre (un cuadro = el candado del contexto inmediato) y el nivel *Raster* que pidió Unicomp. 17 pruebas en WARP.
- **Render y Export sobre `IGpu`** (#589): compositor, efectos, capas, máscaras, títulos ya rasterizados, transiciones, desenfoque de movimiento, presentación, monitores, LUT y curvas, medidor de luz HDR, revelado RAW, interpolación (mezcla y flujo óptico) y el conversor de exportación. El proyecto `Rodaje.Export` no llamaba a D3D11 directamente.
- **Manifiesto y DXC** (#588): las 100 variantes con id estable, `build\shaders.ps1` compila a SPIR-V con DXC 1.9.2609.5 e incrusta en `Rodaje.Render.Spirv` (sin referencias todavía). Windows sigue con `D3DCompile`.
- **Cambio de 7 líneas del HLSL de efectos** (#588): aplicado. Con `D3DCompile` las 77 variantes dan **el mismo DXBC byte a byte** que antes; las 100 compilan con DXC en HLSL 2021 sin `-HV 2018`.
- **Huellas de píxeles** (#585): `RODAJE_HUELLAS_PIXELES` y `build\huellas-pixeles.ps1`.

### Lo que sigue llamando a D3D11 fuera del motor (a propósito)

| Dónde | Por qué | Cuándo se va |
|---|---|---|
| `Text/TitleRasterizer*`, `GlyphOutlines`, `TitleFonts` | Direct2D y DirectWrite dibujan en la textura | Fase 5 (HarfBuzz/Skia) |
| `PlaneViewCache` | Vistas por plano y porción del arreglo de D3D11VA; se envuelven con `D3D11Texture.View` | Fase 4 (`IDecoderSurface`/`Acquire`) |
| `ExportFrameConverter.SourceViews` | Vistas de un tramo de mips (el contrato deja las vistas implícitas) | Fase 4, si Vulkan las necesita |
| `FrameInterpolator` | Textura NV12/P010 con vistas UAV por plano, que sale como `D3D11TexturePayload` | Fase 4, con las superficies |
| `Compositor.Texture`/`ShaderResourceView`, `Presenter.Draw(nint…)`, `ContentLightMeter.Measure(nint…)` | API pública que usa la app (cadena de intercambio de `SwapChainPanel`) | Fase 4/7, con `IGpuSwapchain` |

## «Hecho cuando», punto por punto

### 1. Mismo resultado píxel a píxel — cumplido para composición, presentación, títulos y monitores; exportación dentro de su ruido

`build\huellas-pixeles.ps1` (Render, Export y App sin `Rendimiento` ni `TiempoReal`), `main` con #585 frente a la rama:

| Lectura | `main` | `IGpu` | Diferencias |
|---|---|---|---|
| Composición (8 bits y coma flotante) | 2 488 | 2 488 | **0** |
| Presentación (búfer de la cadena) | 31 | 31 | **0** |
| Títulos | 29 | 29 | **0** |
| Monitores | 1 | 1 | **0** |
| Cuadros exportados | 8 949 | 8 969 | todos sus hashes están en las corridas de `main` |
| Miniaturas de exportación | 616 | 367 | varían igual entre dos corridas de `main` |

Las pruebas de exportación que cancelan, pausan o miden ritmo emiten más o menos cuadros según el reloj, y la miniatura sale del cuadro que toca cada 500 ms: dos corridas de `main` ya difieren en 109 cuadros y 443 miniaturas. Por eso el criterio para la exportación es que **ningún cuadro de la rama sea uno que `main` no haya producido nunca**, y se cumple. Las miniaturas no tienen referencia determinista (se dice, no se esconde).

Pruebas: Render 831 (712 de antes, 17 del motor y 102 del manifiesto de #588), Export 485 + 20 omitidas, App 2 689 + 16 omitidas, todas en verde con la máquina sin carga. Con la máquina cargada (cinco compilaciones a la vez) fallaron por tiempo 6 a 18 pruebas de App que no tocan la GPU (autoguardado, zoom, voz…); repetidas sin carga, pasan.

### 2. Autoprueba del exe AOT — cumplida

`build\app.ps1 -Autoprueba` (de fondo) con el exe publicado de la rama: **3 de 3** en verde (87,0, 68,9 y 63,4 s), exe de 40,4 MB (40,3 MB en `main`), publicado sin advertencias de recorte ni de AOT. Se repite con el exe publicado desde `main` tras unir (abajo, «Tras unir»).

En una tanda previa con un arnés propio (sin el módulo eco compilado con AOT, que solo prepara `app.ps1`) una de cinco corridas falló con «El monitor Waveform no llegó de la GPU al panel» (espera de 3 s, con la máquina cargada); no se repitió en las tres de `app.ps1`. En esa misma tanda el exe de `main` cerró dos de cinco veces con 0xC000027B. Queda anotado por si vuelve.

### 3. Arranque y ms por cuadro sin empeorar más del 2 %

Todo de fondo: pruebas de rendimiento en Release y `--arranque`/`--autoprueba` del exe publicado; nada de `-Medir`, `--medir` ni `--visible`. Las dos versiones se alternan (A, B, A, B…) en la misma RTX 3050 Laptop.

**ms por cuadro en la GPU** (marcas de tiempo, mediana de 5 rondas; tabla completa abajo): la suma de las 90 medidas pasa de 129,9 a 128,2 ms (**−1,3 %**). Las que no dependen del reloj del sistema (una capa, efectos 1080p, máscaras, 8 capas sin efectos) quedan iguales al 0,0 %; las transiciones 4K y el anillo de exportación varían ±20 % de una ronda a otra en las dos versiones.

**CPU de envío** (lo que tarda `Compositor.Render` en grabar, sin esperar a la GPU; 8 rondas):

| Capas | `main` | `IGpu` | |
|---|---|---|---|
| 1 | 0,038 ms | 0,038 ms | +0 % |
| 2 | 0,048 ms | 0,051 ms | +7 % |
| 4 | 0,073 ms | 0,079 ms | +8 % |
| 8 | 0,115 ms | 0,140 ms | +21 % |

Antes de afinar era +31 a +37 %: el motor ponía todo el estado en cada dibujo. Ahora `Draw` no repite canalización, destinos, viewport, constantes ni muestreadores si no cambian, y de las `t#` solo vuelve a enlazar el tramo que cambió. Queda un coste fijo pequeño por capa (≈ 3 µs: la estructura de enlaces y la búsqueda de la canalización). **En relativo, el envío de la CPU con 8 capas no cumple el 2 %; en absoluto son 0,025 ms en un cuadro de 16,7 ms (0,15 %).** Se deja así: no es lo que limita un cuadro.

**Arranque** (`--arranque`, de fondo, 5 rondas alternas; mediana):

| Marca | `main` | `IGpu` |
|---|---|---|
| Primer render | 1 372 ms | 1 326 ms |
| Interfaz usable | 1 447 ms | 1 432 ms |
| Sesión abierta | 1 855 ms | 1 763 ms (4 rondas) |

Una ronda de cada versión salió el doble de lenta por la carga de la máquina; con la mediana, el arranque no empeora. **Con esta dispersión el 2 % no se puede demostrar de fondo** (la diferencia entre rondas de la misma versión supera el 2 %); lo que sí se ve es que no hay una regresión mayor.

### Tras unir

Exe publicado desde `main` (19947402) con `app.ps1 -Publicar -Autoprueba`, de fondo: **2 de 3** en verde (61,2 y 60,5 s); la primera cerró con 0xC000027B (excepción guardada de WinUI) antes de terminar. El mismo cierre salió 2 de 5 veces con el exe de `main` **anterior** a la fase 3 en la tanda del arranque, así que no lo trae `IGpu`; queda como fallo intermitente para investigar aparte (no deja `rodaje-error.txt`).

## Revisión de bloque (Gemini, gemini-3.8-flash-high, solo lectura)

Diez hallazgos; se arreglaron los reales y se descartaron los que no lo eran, con el porqué:

| Hallazgo | Qué se hizo |
|---|---|
| Alta: fuga de la textura NV12 en `FrameInterpolator.CreateSlot` | **No es**: el `finally` suelta la referencia local tras `Wrap`. |
| Alta: `OMSetDepthStencilState(null)` deja la prueba de profundidad del estado por defecto | **Arreglado**: toda canalización lleva su estado, también la que apaga la profundidad. Rodaje no tenía destino de profundidad, Unicomp sí lo tendrá. |
| Media: una consulta de evento por `Submit` que solo suelta el finalizador (monitores y exportación, una por cuadro) | **Arreglado**: `IGpuFrame.Flush()` envía sin valla, como el `Flush` de antes. |
| Media: peligros entre etapas | **No es**: D3D11 quita la entrada, no la salida; el motor ya pone las salidas antes. |
| Media: `Map` que espera con el candado tomado | Igual que antes de la fase; queda anotado para Vulkan. |
| Media: máscara de escritura en MRT | **No es**: sin `IndependentBlendEnable`, D3D11 aplica el destino 0 a todos. |
| Bajas: `MultisampleEnable`, relleno de constantes, descripciones vacías de las vistas de planos | Sin efecto hoy; anotado. |
| Baja: `SetInputView` sin uso | **Quitado**. |

## Ayudantes

| Pieza | Quién | Cómo fue |
|---|---|---|
| Contrato, motor D3D11, *Raster*, núcleo del compositor, huellas, rendimiento, integración | Claude | — |
| Manifiesto, herramienta DXC, `Rodaje.Render.Spirv`, parche HLSL | Gemini (flash-high) | Bien a la primera (51 min); la comprobación del DXBC idéntico la hizo Claude. |
| Monitores, LUT/curvas/máscaras, `GpuTimer`, medidor de luz, RAW | Devin (swe-2-high) | Código correcto, pero la Cuadrilla cortó los tres por «colgados» mientras compilaban (máquina cargada, 20 min sin salida); Claude terminó la integración. |
| Interpolación | Gemini (flash-high) | Bien; quitado un `UnsafeAccessor` sobre un campo privado del motor. |
| Conversor de exportación | Gemini, cortado por Daniel a la hora; relanzado con Devin, sin cuota de pago | Claude terminó lo que Gemini dejó (faltaba el muestreador en dos pasadas). |
| Revisión de bloque | Gemini (flash-high, lectura) | 2 de 10 hallazgos reales. |

## Pendiente

- Fase 4: `IDecoderSurface`/`Acquire`, `IGpuSwapchain`, quitar el atributo de solo Windows de `Rodaje.Render` y el motor Vulkan.
- `D32FloatS8` (plantilla) y lectura de profundidad (`GpuCaps.DepthReadback`) cuando Unicomp las pida.
- Las huellas de las miniaturas no son deterministas: si hace falta, que la prueba fije el cuadro.
