# Fase 0 (d), parte de Windows: compila las variantes de EffectShaders con DXC a SPIR-V y dibuja las referencias con D3D11 WARP.
# Deja salida\spirv (los .spv y manifiesto.json) y salida\ref (casos.json y un .bin por caso) para el trabajo de Linux.
# Fuera de la CI, con GPU:
#   -Vulkan   corre además ejecutar-vulkan contra salida\ref (RODAJE_VK_GPU elige la GPU) y sale con su código.
#   -Control  dibuja también salida\ref-gpu con D3D11 en la GPU (RODAJE_D3D_GPU) y, con -Vulkan, compara Vulkan con ella.
param([switch]$Vulkan, [switch]$Control)
$ErrorActionPreference = 'Stop'
$aqui = $PSScriptRoot
$raiz = Resolve-Path (Join-Path $aqui '..\..\..')
$dotnet = Join-Path $raiz 'build\dotnet-local.cmd'
$salida = Join-Path $aqui 'salida'

& $dotnet build (Join-Path $aqui 'Herramientas\Herramientas.csproj') -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "No compila Herramientas ($LASTEXITCODE)." }
# Con el dotnet de .entorno: el runtime 10 no tiene por qué estar instalado en el sistema.
$dll = Join-Path $aqui 'Herramientas\bin\Release\net10.0-windows\fase0-shaders.dll'

& $dotnet $dll compilar $salida
$compilar = $LASTEXITCODE
& $dotnet $dll referencia $salida
$referencia = $LASTEXITCODE
if ($compilar -ne 0) { throw "compilar: alguna variante no compila con DXC (código $compilar)." }
if ($referencia -ne 0) { throw "referencia falló (código $referencia)." }
if ($Control) {
    & $dotnet $dll referencia-gpu $salida
    if ($LASTEXITCODE -ne 0) { throw "referencia-gpu falló ($LASTEXITCODE)." }
}

Write-Host "Listo: $salida"
if (-not $Vulkan) { exit 0 }

& $dotnet build (Join-Path $aqui 'EjecutarVulkan\EjecutarVulkan.csproj') -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "No compila EjecutarVulkan ($LASTEXITCODE)." }
$vk = Join-Path $aqui 'EjecutarVulkan\bin\Release\net10.0\ejecutar-vulkan.dll'
$env:FASE0_VOLCADO = Join-Path $salida 'vk'
& $dotnet $vk (Join-Path $salida 'spirv') (Join-Path $salida 'ref') | Tee-Object (Join-Path $salida 'vulkan.txt')
$codigo = $LASTEXITCODE
if ($Control) {
    $env:FASE0_VOLCADO = ''
    $env:FASE0_RESULTADO = Join-Path $salida 'resultado-control-gpu.json'
    & $dotnet $vk (Join-Path $salida 'spirv') (Join-Path $salida 'ref-gpu') | Tee-Object (Join-Path $salida 'vulkan-control-gpu.txt')
    Remove-Item Env:FASE0_RESULTADO
}

exit $codigo
