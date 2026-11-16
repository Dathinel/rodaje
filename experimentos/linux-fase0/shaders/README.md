# Fase 0 (d): EffectShaders por DXC a SPIR-V frente a D3D11 WARP

Prueba si el HLSL de los efectos (`src/Rodaje.Render/Effects/EffectShaders.cs`, las 77 variantes de `EffectShaders.All`) compila con DXC a SPIR-V y si, dibujado con Vulkan, da los mismos píxeles que D3D11 WARP (error máximo ≤ 1e-3 en RGBA16F de 64 × 64). Parte (d) de la fase 0 de D-066 (`docs/propuestas/mac-y-linux.md`).

## Piezas

| Pieza | Dónde | Qué hace |
|---|---|---|
| `compilar` | `Herramientas/` (Windows) | Compila cada variante y `VsQuad` con `dxc.exe` a `salida/spirv/<n>-<entrada>-<flags>.spv` y escribe `manifiesto.json` (entrada, defines, estado, error literal, recursos con registro HLSL, binding, tipo, dimensión y tamaño del cbuffer leídos del SPIR-V). Prueba además el parche mínimo (abajo) sobre una copia en memoria: con DXC a `salida/spirv-parche2021/` y con `D3DCompile`. |
| `referencia` | `Herramientas/` (Windows) | Dibuja cada caso con D3D11 WARP como hoy (`D3DCompile` `ps_5_0`, O3, avisos como errores; el muestreador, el rasterizador y las constantes de `QuadPipeline`) y vuelca los flotantes a `salida/ref/<caso>.bin` y los casos a `casos.json`. |
| `referencia-gpu` | `Herramientas/` (Windows) | Control: lo mismo con D3D11 en la GPU (`RODAJE_D3D_GPU` o la de más VRAM) en `salida/ref-gpu/`. |
| `ejecutar-vulkan` | `EjecutarVulkan/` (Windows y Linux, AOT) | Carga los `.spv`, reconstruye las texturas de `casos.json` (comprueba su hash FNV-1a), dibuja fuera de pantalla, compara y escribe la tabla y `resultado.json`. Sale con 0 solo si todos los casos dan ≤ 1e-3. |
| `ci.ps1` | Windows | `compilar` y `referencia` con `.entorno`; deja `salida/spirv` y `salida/ref`. Con `-Vulkan` corre también el ejecutor; con `-Control`, la referencia y la comparación en la GPU. |
| `ci.sh` | Ubuntu 24.04 + lavapipe | Publica `ejecutar-vulkan` con AOT para `linux-x64` y lo corre contra `FASE0_SPIRV` y `FASE0_REF` (el artefacto del trabajo de Windows). No instala nada: el workflow pone `mesa-vulkan-drivers` y `libvulkan1`. |

Variables del ejecutor: `RODAJE_VK_GPU` (subcadena del nombre de la GPU; en el corredor, `llvmpipe`), `FASE0_SPIRV`, `FASE0_REF`, `FASE0_RESULTADO` y `FASE0_VOLCADO` (carpeta donde dejar lo que dibuja Vulkan, para diagnosticar).

```powershell
$env:RODAJE_ENTORNO = '<repositorio>\.entorno'
powershell -File experimentos\linux-fase0\shaders\ci.ps1 -Vulkan -Control
```

Enlaces a `src/` sin tocarlo: `Effects/EffectShaders.cs`, `RenderShaders.cs`, `ShaderCompiler.cs` y `RenderException.cs`. `ShaderVariant` se declara en `Herramientas/ShaderVariant.cs` con la misma firma y el mismo `FullSource()`: el original vive en `Shaders/ShaderCache.cs`, que arrastra la caché de disco. Paquetes: `Microsoft.Direct3D.DXC` 1.9.2609.5 (LLVM/NCSA; solo se copian `dxc.exe` y `dxcompiler.dll`, `dxil.dll` no hace falta para SPIR-V), `TerraFX.Interop.Windows` 10.0.26100.6 y `TerraFX.Interop.Vulkan` 1.4.328.1 (MIT; su resolvedor ya busca `libvulkan.so.1` en Linux, así que no hizo falta Silk.NET).

## Casos

168 casos, las 77 variantes. Las 64 de `PsGraded` con YUV 4:2:0 de rango limitado y todo encendido por constantes (color con piel, LUT 17³, viñeta, mezcla por shader en modo 5, máscara, y con ADVANCED croma, ruedas, curvas con tono y calificador); además origen RGBA intermedio, plano RGBA, vistas de máscara y del calificador, capa girada (con `pixelOrigin` y sin él, interpolada) y secuencia HDR. `PsImage` con los diez modos de mezcla; `PsTransition` con cada tipo y sus variantes BLEND y MOTION; `PsDownsample`, dos sondas del bilineal, `PsBlur`, `PsSharpen`, `PsPixelate`, `PsMask` (las seis formas y la pasada gruesa), `PsMaskMix` y cada `FxOp` de `PsFx`. Para separar causas: ADVANCED por partes, cada parte otra vez desde un plano RGBA leído en los centros de texel (`-centros`) y el croma desde YUV 4:4:4. Las texturas salen de fórmulas sin aleatorios ni funciones trascendentes (`Comun/Patterns.cs`) y las constantes quedan escritas en `casos.json` como float32.

## Resultados (16-nov, RTX 3050 Laptop, Vulkan 1.4.341, controlador 0x988F8000)

**compilar.** `dxcompiler.dll 1.9.2609.5 (01b62ad47)`, `-spirv -fspv-target-env=vulkan1.1 -O3`, `ps_6_0` y `vs_6_0`:

- Tal cual, con el HLSL 2021 que DXC usa por defecto: **0 de 77**. Las 77 fallan por los mismos 7 errores (el vector como condición de `?:` o de `&&` ya no vale):
  ```
  rodaje-efectos.hlsl:474:23: error: condition for short-circuiting ternary operator must be scalar, for non-scalar types use 'select'
      return cb <= 0.25 ? ((16.0 * cb - 12.0) * cb + 4.0) * cb : sqrt(cb);
  rodaje-efectos.hlsl:479:22: (igual)  return cs <= 0.5 ? cb * 2.0 * cs : 1.0 - (1.0 - cb) * (1.0 - (2.0 * cs - 1.0));
  rodaje-efectos.hlsl:487:34: (igual)  case 5: return cs <= 0.5 ? cb - (1.0 - 2.0 * cs) * cb * (1.0 - cb) : cb + ...;
  rodaje-efectos.hlsl:493:54: (igual)  case 11: return cb <= 0.0 ? 0.0 : (cs >= 1.0 ? 1.0 : min(1.0, cb / (1.0 - cs)));
  rodaje-efectos.hlsl:494:54: (igual)  case 12: return cb >= 1.0 ? 1.0 : (cs <= 0.0 ? 0.0 : 1.0 - min(1.0, (1.0 - cb) / cs));
  rodaje-efectos.hlsl:649:61: error: operands for short-circuiting logical binary operator must be scalar, for non-scalar types use 'and'
      [branch] if (!vertices || all(l >= key1.xy - key0.w && l <= key1.zw + key0.w))
  rodaje-efectos.hlsl:1037:17: (como el primero)  m = m > 1.0 ? 2.0 - m : m;
  ```
- Con `-HV 2018`: **77 de 77**, sin avisos. Son los `.spv` de `salida/spirv` y los que se comparan abajo.
- Con el parche mínimo (`Herramientas/Hlsl2021Patch.cs`: esas 7 líneas con `select()` y `and()`, más dos macros para fxc, que no los conoce) y el HLSL 2021: **77 de 77** con DXC y **77 de 77** con `D3DCompile` `ps_5_0`. Sus `.spv` (`salida/spirv-parche2021`) dan los mismos estados en los 168 casos (9 transiciones bajan de 4,9e-4 a 2,4e-4).

**Vulkan frente a WARP** (`salida/vulkan.txt`): **116 de 168 casos ≤ 1e-3**; variantes con todos sus casos dentro: **41 de 77**; con alguno: 46. Peor caso: `transition-0-degradado2`, 0,834. **El objetivo de 1e-3 frente a WARP no se cumple.**

**Control, Vulkan frente a D3D11 en la misma RTX 3050** (`salida/vulkan-control-gpu.txt`): **164 de 168 ≤ 1e-3**; todas las variantes con algún caso dentro (77 de 77) y 73 con todos.

Los 52 casos que superan 1e-3 frente a WARP, por causa:

| Grupo | Casos | Error máx. frente a WARP | Frente a D3D11 en la GPU | Causa |
|---|---|---|---|---|
| ADVANCED con YUV 4:2:0 | las 32 genéricas de ADVANCED (4,6e-3 a 2,4e-2), `solo-croma` 5,1e-3, `solo-curvas-tono` 1,7e-3, `solo-calificador` 1,7e-3, `vista-calificador` 2,1e-2, `ADVANCED-girado` 2,5e-2, `-interpolado` 8,2e-2, `-hdr` 2,7e-2, `ADVANCED-centros` 2,0e-3, `solo-curvas-tono-centros` 1,2e-3 | hasta 8,2e-2 | ≤ 4,9e-4 salvo `-hdr` | El filtro bilineal del hardware, amplificado por funciones con mucha pendiente: la rampa del croma (1/tolerancia de 0,15 por la pendiente del smoothstep, unas 11 veces), los smoothstep del calificador (suavidad 0,1) y la curva de tono. Prueba: `solo-croma` 5,1e-3 pasa a 4,9e-4 desde centros de texel y a 7,6e-6 con croma 4:4:4; `solo-calificador` pasa de 1,7e-3 a 4,9e-4 desde centros; lo que queda en `-centros` es la tabla de curvas de 1024 y la LUT, que también se leen en fracciones arbitrarias. El filtro de NVIDIA no coincide con ninguna emulación simple que probé (fracción exacta, truncada o redondeada a 1/256, resultado redondeado a fp16); lo probado es que la diferencia está en el filtrado y que D3D11 en la misma GPU la comparte. |
| Bilineal en fracciones arbitrarias | `muestreo-escala0.9`, `transition-0-giro`, `-rebote`, `fx-aberracion-radial`, `fx-distorsion`, `fx-reencuadre360`, `fx-reencuadre360-estereo` (todos 1,465e-3); `graded-COLOR+LUT+VIGNETTE-girado` 3,9e-3, `-interpolado` 3,4e-3, `-hdr` 4,9e-3 | 1,465e-3 a 4,9e-3 | ≤ 9,8e-4 salvo `-hdr` y `reencuadre360-estereo` | WARP trunca la fracción del bilineal a 1/256 (lo dice el comentario de `SampleRgb`); la RTX 3050 no. Emulado en Python sobre `muestreo-escala0.9` (un solo `SampleA`): con la fracción truncada el resultado coincide con WARP a 4,9e-4 (1 ULP de fp16) y queda a 1,465e-3 de Vulkan; con la fracción exacta o redondeada, al revés. `muestreo-escala0.5` (fracciones 0,25/0,75, exactas en 8 bits) da 4,9e-4 con las tres. |
| Discontinuidad en un centro de píxel | `transition-0-degradado2`: 0,834 en 128 valores (los 32 píxeles de la diagonal × 4 canales) | 0,834 | 0,834 (igual) | El degradado angular es `frac(atan2(v.x, −v.y)/2π + 1 + key1.w)`; con `key1.w = 0,125` vale exactamente 1,0 en la diagonal x = y y una diferencia de 1 ULP en `atan2` (polinomio de fxc frente al `Atan2` del controlador) salta de 0 a 1. Con `key1.w = 0,13` (`degradado2-desfasado`) da 4,9e-4. Caso degenerado, no un error de traducción. |

Residuos del control (D3D11 frente a Vulkan en la misma GPU): `graded-COLOR+LUT+VIGNETTE-hdr` 1,465e-3 (2 valores de 16 384), `graded-COLOR+LUT+VIGNETTE+ADVANCED-hdr` 5,9e-3 (3 valores, en 1,44, donde 1 ULP de fp16 son 9,8e-4), `fx-reencuadre360-estereo` 1,2e-3 (1 valor) y `degradado2`. Todos pasan por funciones trascendentes: fxc expande `pow`, `atan2` y `asin` en secuencias de DXBC y DXC emite `Pow`, `Atan2` y `Asin` de GLSL.std.450, que implementa el controlador (`PsGraded` con LUT y ADVANCED lleva 27 `Pow`; en PQ el exponente 78,84 amplifica cualquier diferencia).

El ejecutable AOT (`win-x64`, 2,95 MB, 0 avisos de ILC) da las mismas cifras que el de JIT en los 168 casos.

## Mapa de registros HLSL → bindings de Vulkan

Set 0, desplazamientos fijos: `-fvk-b-shift 0 0 -fvk-t-shift 16 0 -fvk-s-shift 32 0 -fvk-u-shift 48 0`. El sampler va aparte (`VK_DESCRIPTOR_TYPE_SAMPLER` + `SAMPLED_IMAGE`, sin combinar).

| Registro | Nombre | Binding | Tipo | Lo usan |
|---|---|---|---|---|
| b0 | `Quad` (272 bytes, 17 float4) | 0 | uniform buffer | `VsQuad` y todos |
| b1 | `Effects` (416 bytes, 26 float4) | 1 | uniform buffer | todos |
| t0 | `lumaPlane` | 16 | 2D array | `PsGraded` |
| t1 | `chromaPlane` | 17 | 2D array | `PsGraded` |
| t3 | `background` | 19 | 2D | `PsGraded`, `PsImage`, `PsTransition` (BLEND) |
| t4 | `lut` | 20 | 3D | `PsGraded` (LUT) |
| t5 | `imageA` | 21 | 2D | casi todos |
| t6 | `imageB` | 22 | 2D | `PsFx`, `PsMaskMix`, `PsSharpen`, `PsTransition` |
| t7 | `maskImage` | 23 | 2D | `PsFx`, `PsGraded` (MASK), `PsMaskMix` |
| t8 | `pathPoints` | 24 | 2D | `PsMask` |
| t9 | `matteImage` | 25 | 2D | `PsMask`, `PsTransition` |
| t10 | `curveTable` | 26 | 2D | `PsGraded` (ADVANCED) |
| t11 | `coarseDistance` | 27 | 2D | `PsMask` |
| s0 | `linearClamp` | 32 | sampler | todos |

`VsQuad` se compila con `-fvk-invert-y` y el viewport va con alto positivo: así el NDC de D3D sirve tal cual, y `SV_Position` en el pixel shader tiene el origen arriba a la izquierda como en D3D, con lo que `Exact()` (`pixelOrigin`) y `background.Load(position)` funcionan sin cambios (los casos BLEND y de capa girada lo comprueban).

## Qué implica para el manifiesto de variantes de la fase 3

- **Versión de HLSL.** O se compila con `-HV 2018` o se aplica en `src/` el parche de 7 líneas con `select`/`and` y dos macros para fxc; compila con los dos compiladores y da los mismos resultados. Lo segundo evita depender de un modo heredado de DXC.
- **Recursos por variante.** DXC quita los recursos que la variante no usa (la 39 no declara t3 ni t7), así que el conjunto cambia por variante. O el manifiesto guarda los recursos de cada una (como aquí, leídos del SPIR-V), o se usa un único layout con los 14 bindings de la tabla, válido para todas porque un layout puede declarar bindings que el shader no usa. Lo segundo es más simple.
- **cbuffers.** Hoy todo son `float4` y la disposición de HLSL coincide con la de Vulkan (272 y 416 bytes). Si se añade un miembro que no sea `float4`, hará falta `-fvk-use-dx-layout` o revisar los desplazamientos; el manifiesto debería guardar el tamaño para comprobarlo.
- **Dimensión.** Los planos son `Texture2DArray`: la vista de Vulkan tiene que ser `2D_ARRAY`. El manifiesto necesita la dimensión de cada imagen.
- **DXC en Linux.** El paquete NuGet solo trae binarios de Windows. Lo práctico es compilar los SPIR-V al construir en Windows (o en la CI) y distribuirlos con su manifiesto; compilar en Linux exigiría la versión de DXC para Linux que publica Microsoft en GitHub (misma licencia). `compilar` y `referencia` son solo de Windows (`net10.0-windows`, usan D3D).
- **Referencias de las pruebas.** Con 1e-3 frente a WARP no se puede comprobar Vulkan en una GPU real: la precisión del bilineal (WARP trunca a 1/256) y las funciones con mucha pendiente lo impiden. La traducción sí se puede comprobar contra D3D11 en la misma GPU (164 de 168) o contra referencias tomadas con lavapipe. Queda por medir cómo filtra lavapipe frente a WARP (lo dirá `ci.sh`).

## Sin comprobar

- `ci.sh` en Ubuntu 24.04 con lavapipe: solo se comprobó la sintaxis (`bash -n`); la publicación AOT para `linux-x64` no se puede enlazar desde Windows. El ejecutor sí corre aquí con JIT y con AOT `win-x64`.
- El workflow (`.github/workflows/linux-fase0.yml`) todavía no tiene el trabajo de esta parte: Windows corre `ci.ps1` y sube `salida/spirv` y `salida/ref`; Linux instala `mesa-vulkan-drivers libvulkan1`, los descarga y corre `ci.sh` con `RODAJE_VK_GPU=llvmpipe`.
