# Arma la cola de cobertura para Gemini (D-045) sin ningún modelo: clases de Core, Packages y Playback que no aparecen en
# ninguna prueba, ordenadas por riesgo (commits de los últimos 30 días que tocaron su archivo).
# Uso: build\generar-cola-cobertura.ps1 [-Salida <cola-cobertura.md>]
param(
    [string] $Salida,
    [int] $MaxFuenteKB = 40,
    [int] $Primera = 2001
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (git -C $PSScriptRoot rev-parse --path-format=absolute --git-common-dir)
$raiz = Split-Path $PSScriptRoot
if (-not $Salida) { $Salida = Join-Path $repo '.proceso\gemini\cola-cobertura.md' }
$excluido = '(?i)(Serializ|Timing|Sync|Render|Engine|Install|Verif|LICENSE|UpstreamAttribution)'

$vistas = @{}
$tareas = foreach ($ruta in git -C $raiz ls-files 'src/Rodaje.Core/*.cs' 'src/Rodaje.Packages/*.cs' 'src/Rodaje.Playback/*.cs') {
    if ($ruta -match $excluido -or $ruta -notmatch '^src/Rodaje\.(Core|Packages|Playback)/') { continue }
    $proyecto = $Matches[1]
    $completa = Join-Path $raiz ($ruta -replace '/', '\')
    if ((Get-Item -LiteralPath $completa).Length -gt $MaxFuenteKB * 1KB) { continue }
    # Una interfaz sola no tiene comportamiento que probar.
    if ([IO.File]::ReadAllText($completa) -notmatch '\b(class|record|struct)\b') { continue }
    $clase = [IO.Path]::GetFileNameWithoutExtension($ruta) -replace '\..*$', ''
    if ($vistas.ContainsKey($clase)) { continue }
    $vistas[$clase] = $true
    if (git -C $raiz ls-files "tests/*/$clase*Tests.cs" "tests/*/*/$clase*Tests.cs") { continue }
    git -C $raiz grep -q -w -e $clase -- 'tests/*.cs'
    if ($LASTEXITCODE -eq 0) { continue }
    $cambios = @(git -C $raiz log --since=30.days --format=%h -- $ruta).Count
    [pscustomobject]@{ Fuente = $ruta; Proyecto = $proyecto; Clase = $clase; Cambios = $cambios }
}

$n = $Primera
$sb = New-Object Text.StringBuilder
[void]$sb.AppendLine("# Cola de cobertura para Gemini (generada $(Get-Date -Format 'yyyy-MM-dd HH:mm') con build\generar-cola-cobertura.ps1)")
foreach ($t in @($tareas) | Sort-Object Cambios, Fuente -Descending) {
    $tema = 'cob-' + ($t.Clase -creplace '([a-z0-9])([A-Z])', '$1-$2').ToLowerInvariant()
    if ($tema.Length -gt 40) { $tema = $tema.Substring(0, 40).TrimEnd('-') }
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("## $n. Cobertura de $($t.Clase) ($($t.Cambios) cambios en 30 días)")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('- **Estado:** pendiente')
    [void]$sb.AppendLine('- **Agente:** gemini-local')
    [void]$sb.AppendLine("- **Tema:** $tema")
    [void]$sb.AppendLine("- **Fuente:** ``$($t.Fuente)``")
    [void]$sb.AppendLine("- **Destino:** ``tests/Rodaje.$($t.Proyecto).Tests/Cobertura/$($t.Clase)CoberturaTests.cs``")
    [void]$sb.AppendLine('- **Criterio:** compila sin advertencias y las pruebas pasan sobre el código actual')
    [void]$sb.AppendLine("- **Qué:** Escribe pruebas de caracterización de los métodos públicos de $($t.Clase): fija el comportamiento actual con valores concretos, incluidos los casos límite y los errores. Sin tiempo real, hilos, archivos ni red.")
    $n++
}
[IO.File]::WriteAllText($Salida, $sb.ToString(), (New-Object Text.UTF8Encoding $false))
Write-Host "$($n - $Primera) tareas de cobertura en $Salida"
