# Arma la cola de cazas para Gemini (D-045) desde los enlaces de docs\arquitectura, sin ningún modelo.
# Ordena por densidad de defectos: commits de arreglo de los últimos 30 días que tocaron cada fuente.
# Uso: build\generar-cola-caza.ps1 [-Salida <cola-caza.md>]
param(
    [string] $Salida,
    [int] $MaxFuenteKB = 40,
    [int] $MaxDocumentoKB = 60,
    [int] $Primera = 1001
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (git -C $PSScriptRoot rev-parse --path-format=absolute --git-common-dir)
$raiz = Split-Path $PSScriptRoot
if (-not $Salida) { $Salida = Join-Path $repo '.proceso\gemini\cola-caza.md' }
$excluido = '(?i)(Serializ|Timing|Sync|Render|Engine|Install|Verif|LICENSE|UpstreamAttribution)'
$arreglo = '(?i)(arregl|corrig|defecto|depuraci|\bfix)'

$vistas = @{}
$tareas = foreach ($doc in Get-ChildItem (Join-Path $raiz 'docs\arquitectura') -Filter *.md) {
    if ($doc.Length -gt $MaxDocumentoKB * 1KB) { continue }
    $texto = [IO.File]::ReadAllText($doc.FullName, [Text.Encoding]::UTF8)
    foreach ($m in [regex]::Matches($texto, '\]\((?:\.\./)+(src/Rodaje\.(Core|Packages|Playback)/[^)#\s]+\.cs)\)')) {
        $fuente = $m.Groups[1].Value
        $proyecto = $m.Groups[2].Value
        if ($vistas.ContainsKey($fuente) -or $fuente -match $excluido) { continue }
        $vistas[$fuente] = $true
        $ruta = Join-Path $raiz ($fuente -replace '/', '\')
        if (-not (Test-Path -LiteralPath $ruta) -or (Get-Item -LiteralPath $ruta).Length -gt $MaxFuenteKB * 1KB) { continue }
        # Una interfaz sola no tiene comportamiento que probar.
        if ([IO.File]::ReadAllText($ruta) -notmatch '\b(class|record|struct)\b') { continue }
        $clase = [IO.Path]::GetFileNameWithoutExtension($fuente) -replace '\..*$', ''
        $pruebas = Join-Path $raiz "tests\Rodaje.$proyecto.Tests"
        if (Get-ChildItem -LiteralPath $pruebas -Recurse -Filter "$clase*Tests.cs" -ErrorAction SilentlyContinue | Select-Object -First 1) { continue }
        $arreglos = @(git -C $raiz log --since=30.days --format=%s -- $fuente | Where-Object { $_ -match $arreglo }).Count
        [pscustomobject]@{ Fuente = $fuente; Documento = "docs/arquitectura/$($doc.Name)"; Proyecto = $proyecto; Clase = $clase; Arreglos = $arreglos }
    }
}

$n = $Primera
$sb = New-Object Text.StringBuilder
[void]$sb.AppendLine("# Cola de cazas para Gemini (generada $(Get-Date -Format 'yyyy-MM-dd HH:mm') con build\generar-cola-caza.ps1)")
foreach ($t in @($tareas) | Sort-Object Arreglos, Fuente -Descending) {
    $tema = 'caza-' + ($t.Clase -creplace '([a-z0-9])([A-Z])', '$1-$2').ToLowerInvariant()
    if ($tema.Length -gt 40) { $tema = $tema.Substring(0, 40).TrimEnd('-') }
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("## $n. Caza en $($t.Clase) ($($t.Arreglos) arreglos en 30 días)")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('- **Estado:** pendiente')
    [void]$sb.AppendLine('- **Agente:** gemini-local')
    [void]$sb.AppendLine('- **Tipo:** caza')
    [void]$sb.AppendLine("- **Tema:** $tema")
    [void]$sb.AppendLine("- **Fuente:** ``$($t.Fuente)``")
    [void]$sb.AppendLine("- **Documento:** ``$($t.Documento)``")
    [void]$sb.AppendLine("- **Destino:** ``tests/Rodaje.$($t.Proyecto).Tests/Caza/$($t.Clase)CazaTests.cs``")
    [void]$sb.AppendLine('- **Criterio:** compila sin advertencias; cada prueba cita una frase literal del documento')
    $n++
}
[IO.File]::WriteAllText($Salida, $sb.ToString(), (New-Object Text.UTF8Encoding $false))
Write-Host "$($n - $Primera) tareas de caza en $Salida"
