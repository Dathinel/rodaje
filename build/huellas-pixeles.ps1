# Huellas de píxeles: corre las pruebas de Render, Export y App con RODAJE_HUELLAS_PIXELES y deja, ordenadas, las SHA-256
# de todas las lecturas de la GPU. Dos corridas (antes y después de un cambio interno, como el paso a IGpu) se comparan con
# -Comparar: si no sobra ni falta ninguna línea, ningún cuadro cambió ni un bit.
#   build\huellas-pixeles.ps1 -Salida antes.txt
#   build\huellas-pixeles.ps1 -Salida despues.txt -Comparar antes.txt
param(
    [Parameter(Mandatory = $true)][string]$Salida,
    [string]$Comparar = '',
    [string[]]$Proyectos = @('tests\Rodaje.Render.Tests', 'tests\Rodaje.Export.Tests', 'tests\Rodaje.App.Tests')
)
$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSScriptRoot
$Salida = [IO.Path]::GetFullPath($Salida)
$crudo = "$Salida.crudo"
Remove-Item -LiteralPath $crudo -ErrorAction SilentlyContinue
$env:RODAJE_HUELLAS_PIXELES = $crudo
$fallos = 0
try {
    foreach ($p in $Proyectos) {
        Write-Host "  Pruebas de $p con huellas..."
        # Las de rendimiento y tiempo real leen tantos cuadros como dé el reloj: no son comparables.
        & (Join-Path $raiz 'build\dotnet-local.cmd') test (Join-Path $raiz $p) --nologo -v q --filter 'Categoria!=Rendimiento&Categoria!=TiempoReal' | Out-Host
        if ($LASTEXITCODE -ne 0) { $fallos++ }
    }
}
finally {
    Remove-Item Env:RODAJE_HUELLAS_PIXELES -ErrorAction SilentlyContinue
}
$lineas = if (Test-Path -LiteralPath $crudo) { @(Get-Content -LiteralPath $crudo -Encoding UTF8 | Sort-Object) } else { @() }
[IO.File]::WriteAllLines($Salida, [string[]]$lineas)
Remove-Item -LiteralPath $crudo -ErrorAction SilentlyContinue
Write-Host ("  {0} lecturas en {1}{2}" -f $lineas.Count, $Salida, $(if ($fallos) { " ($fallos proyecto(s) con pruebas fallidas)" } else { '' }))
if ($Comparar) {
    $antes = @(Get-Content -LiteralPath $Comparar -Encoding UTF8)
    $dif = @(Compare-Object -ReferenceObject $antes -DifferenceObject $lineas)
    if ($dif.Count -eq 0) {
        Write-Host "  Iguales a $Comparar ($($antes.Count) lecturas): ningún píxel cambió." -ForegroundColor Green
        exit 0
    }
    Write-Host "  Distintas de $Comparar`: $(@($dif | Where-Object SideIndicator -eq '<=').Count) solo antes, $(@($dif | Where-Object SideIndicator -eq '=>').Count) solo ahora." -ForegroundColor Red
    $dif | Select-Object -First 40 | ForEach-Object { Write-Host ("    {0} {1}" -f $_.SideIndicator, $_.InputObject) }
    exit 1
}
exit $(if ($fallos) { 1 } else { 0 })
