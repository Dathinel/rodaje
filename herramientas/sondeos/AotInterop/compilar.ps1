# Compila (build, sin AOT) una variante y muestra solo errores y advertencias.
# Uso: compilar.ps1 [-Variante Cargado] [-Graficos Vortice|TerraFX]
param([string]$Variante = 'Enlazado', [string]$Graficos = 'TerraFX')
$ErrorActionPreference = 'Continue'
$raiz = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$dotnet = Join-Path $raiz 'herramientas\dotnet-local.cmd'
if (-not $env:FOTOTOMA_ENTORNO -and -not (Test-Path (Join-Path $raiz '.entorno\dotnet'))) {
    $principal = Join-Path $raiz '..\..\..\.entorno'
    if (Test-Path $principal) { $env:FOTOTOMA_ENTORNO = (Resolve-Path $principal).Path }
}
$out = & $dotnet build (Join-Path $PSScriptRoot 'AotInterop.csproj') -c Release "-p:Variante=$Variante" "-p:Graficos=$Graficos" 2>&1 | ForEach-Object { "$_" }
$codigo = $LASTEXITCODE
$out | Where-Object { $_ -match '(error|warning|advertencia) [A-Z]+\d+' } | ForEach-Object { $_ -replace '\s+\[[^\]]+\.csproj[^\]]*\]\s*$', '' } | Sort-Object -Unique
"código de salida: $codigo"
