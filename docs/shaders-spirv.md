# Manifiesto de variantes de shader y compilación a SPIR-V

Este documento describe el manifiesto centralizado de variantes de shader de Rodaje y la infraestructura de compilación previa a SPIR-V (fase 3 de D-066).

## Manifiesto de variantes (`ShaderManifest`)

La clase estática `Rodaje.Render.Shaders.ShaderManifest` (en `src/Rodaje.Render/Shaders/ShaderManifest.cs`) expone la propiedad `All` con la lista completa y exhaustiva de todas las variantes de shader compiladas por Rodaje:

- **Render** (`RenderShaders`): 3 variantes (`VsQuad` en `vs_5_0`, `PsLayer` en `ps_5_0`, `PsPresent` en `ps_5_0`).
- **Efectos** (`EffectShaders.All`): 77 variantes en `ps_5_0` (corrección de color, desenfoque, máscaras, transformaciones, transiciones, etc.).
- **Exportación** (`ExportShaders`): 7 variantes en `cs_5_0` (`ScaleVariant`, `ConvertVariant`, `ConvertHdrVariant`, `ConvertToneMapVariant`, `ThumbnailVariant`, `ThumbnailToneMapVariant`, `LightVariant`).
- **Interpolación de flujo** (`FlowShaders`): 9 variantes en `cs_5_0` (`Luma`, `Chroma` y las 7 etapas de flujo óptico usadas por `FlowInterpolator`: `Downsample`, `Search`, `Densify`, `Histogram`, `Residual`, `Decide`, `Warp`).
- **Monitores / Scopes** (`ScopeShaders`): 3 variantes en `cs_5_0` (`Accumulate`, `Maximum`, `Draw`).
- **Revelado RAW** (`RawShaders`): 1 variante en `cs_5_0` (`DevelopVariant`).

Total: **100 variantes**.

Cada variante posee un identificador estable y unívoco generado por `ShaderManifest.Id(variant)`:
`"{SourceName}:{EntryPoint}"` acompañado de `"+DEF1+DEF2"` si la variante define macros de preprocesador.

## Compatibilidad HLSL 2021

El sombreador de efectos (`src/Rodaje.Render/Effects/EffectShaders.cs`) incluye las definiciones de compatibilidad para HLSL 2021 (`select` y `and` vectoriales mediante macros condicionales para fxc). Esto permite que el código fuente HLSL sea admitido por DXC en su modo predeterminado (HLSL 2021, sin requerir `-HV 2018`) sin alterar en modo alguno la compilación con fxc (`D3DCompile`) en tiempo de ejecución.

## Windows sigue con `D3DCompile`

En tiempo de ejecución en Windows **nada cambia**: Rodaje continúa compilando los shaders mediante `D3DCompile` (`ps_5_0`, `cs_5_0`, `vs_5_0`) con caché en memoria y en disco local, exactamente igual que antes.

Los binarios SPIR-V generados son consumidos exclusivamente por el nuevo ensamblado `Rodaje.Render.Spirv` (que expone la clase estática `SpirvShaders`), preparado para el futuro backend de Vulkan (fase 4). Ningún componente del editor de Windows referencia este ensamblado todavía.

## Regeneración y comprobación

La herramienta de consola `Rodaje.ShaderTool` (en `build/shaders/Rodaje.ShaderTool/`) compila las 100 variantes con DXC a SPIR-V (`vulkan1.1`, `-O3`, desplazamientos `b+0`, `t+16`, `s+32`, `u+48`) y genera un archivo `manifest.json` determinista con metadatos de compilación, huellas SHA-256 e información de reflexión de descriptores.

Se controla mediante el script de PowerShell `build/shaders.ps1`:

- **Generar los shaders SPIR-V y el manifiesto:**
  ```powershell
  powershell -ExecutionPolicy Bypass -File build\shaders.ps1 generar
  ```
  Escribe los 100 archivos `.spv` y `manifest.json` en `src/Rodaje.Render.Spirv/Shaders/`.

- **Comprobar que los shaders incrustados están al día:**
  ```powershell
  powershell -ExecutionPolicy Bypass -File build\shaders.ps1 comprobar
  ```
  Genera en un directorio temporal y verifica que el contenido coincide byte a byte con lo existente en `src/Rodaje.Render.Spirv/Shaders/`. Si existe alguna discrepancia, el comando falla con código de salida 1 enumerando las diferencias. Este paso se ejecuta automáticamente en el flujo de trabajo de integración continua (CI) de Windows.
