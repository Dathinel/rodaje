# Compila igpu-pruebas.hlsl a SPIR-V con el DXC del entorno y las opciones del manifiesto (build\shaders\Rodaje.ShaderTool):
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests\Rodaje.Render.Vulkan.Tests\Gpu\compilar-spirv.ps1
$ErrorActionPreference = 'Stop'
$raiz = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
$entorno = if ($env:RODAJE_ENTORNO) { $env:RODAJE_ENTORNO } else { Join-Path $raiz '.entorno' }
$dxc = Join-Path $entorno 'nuget\microsoft.direct3d.dxc\1.9.2609.5\build\native\bin\x64\dxc.exe'
if (-not (Test-Path -LiteralPath $dxc)) { throw "Falta ${dxc}: compila build\shaders\Rodaje.ShaderTool (build\shaders.ps1) para restaurarlo." }
$salida = Join-Path $PSScriptRoot 'spirv'
New-Item -ItemType Directory -Force $salida | Out-Null
$entradas = @{ VsFull = 'vs_6_0'; VsSlope = 'vs_6_0'; VsMesh = 'vs_6_0'; PsColor = 'ps_6_0'; PsId = 'ps_6_0'; PsPlanes = 'ps_6_0'; CsFill = 'cs_6_0' }
foreach ($e in $entradas.Keys | Sort-Object) {
    & $dxc -spirv '-fspv-target-env=vulkan1.1' -O3 -fspv-use-unknown-image-format -fvk-b-shift 0 0 -fvk-t-shift 16 0 -fvk-s-shift 32 0 -fvk-u-shift 48 0 `
        -E $e -T $entradas[$e] -Fo (Join-Path $salida "$e.spv") (Join-Path $PSScriptRoot 'igpu-pruebas.hlsl')
    if ($LASTEXITCODE -ne 0) { throw "DXC falló en $e." }
}
"$($entradas.Count) módulos en $salida"
