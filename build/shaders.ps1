# Script de compilación y comprobación de shaders SPIR-V de Rodaje.
# Uso:
#   powershell -NoProfile -ExecutionPolicy Bypass -File build\shaders.ps1 generar
#   powershell -NoProfile -ExecutionPolicy Bypass -File build\shaders.ps1 comprobar
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('generar', 'comprobar')]
    [string]$Accion = 'generar'
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$Raiz = Split-Path -Parent $PSScriptRoot
$DotnetLocal = Join-Path $PSScriptRoot 'dotnet-local.cmd'
$ToolProject = Join-Path $PSScriptRoot 'shaders\Rodaje.ShaderTool\Rodaje.ShaderTool.csproj'

if (-not (Test-Path -LiteralPath $DotnetLocal)) {
    Write-Error "No se encontró el ejecutable de .NET local en: $DotnetLocal"
    exit 1
}

# Compilar la herramienta de shaders con dotnet-local
& $DotnetLocal build "$ToolProject" -c Release --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Error "Error al compilar la herramienta de shaders Rodaje.ShaderTool."
    exit $LASTEXITCODE
}

$Dll = Join-Path $PSScriptRoot 'shaders\Rodaje.ShaderTool\bin\Release\net10.0-windows\Rodaje.ShaderTool.dll'
if (-not (Test-Path -LiteralPath $Dll)) {
    Write-Error "No se encontró el archivo compilado en: $Dll"
    exit 1
}

# Ejecutar la herramienta con dotnet-local
& $DotnetLocal "$Dll" $Accion
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
