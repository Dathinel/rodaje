# Parte (a) de la fase 0: qué parte de src\Rodaje.App compila sin cambios contra Uno (net10.0-desktop).
# Compila las capas de Windows, luego Vistas\Vistas.csproj (todo el código y el XAML de la app, enlazados), y cuenta los
# archivos con errores. Solo compila: no ejecuta nada. Deja salida\vistas.json y salida\vistas-errores.txt.
# Uso (desde la raíz del repositorio, con .entorno): powershell -NoProfile -ExecutionPolicy Bypass -File experimentos\linux-fase0\uno\medir-vistas.ps1
$ErrorActionPreference = 'Stop'
$raiz = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$dotnet = Join-Path $raiz 'build\dotnet-local.cmd'
$salida = Join-Path $PSScriptRoot 'salida'
New-Item -ItemType Directory -Force -Path $salida | Out-Null

foreach ($p in 'Export', 'Engine', 'Audio') {
    & $dotnet build (Join-Path $raiz "src\Rodaje.$p\Rodaje.$p.csproj") -c Release --nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "No compiló Rodaje.$p." }
}

$log = Join-Path $salida 'vistas.log'
& $dotnet build (Join-Path $PSScriptRoot 'Vistas\Vistas.csproj') -c Release --nologo -v m -p:TreatWarningsAsErrors=false > $log 2>&1

$app = Join-Path $raiz 'src\Rodaje.App'
$fuentes = @(Get-ChildItem -LiteralPath $app -Recurse -File -Include *.cs, *.xaml | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
$lineasDe = @{}
foreach ($f in $fuentes) { $lineasDe[$f.FullName.ToLowerInvariant()] = @(Get-Content -LiteralPath $f.FullName).Count }

# Una línea de error de MSBuild: «ruta(l,c): error CODIGO: texto [proyecto]». Las repetidas (dos pasadas) cuentan una vez.
$errores = @{}
foreach ($l in Get-Content -LiteralPath $log) {
    if ($l -match '^\s*(?<ruta>[^(]+?)\((?<pos>[\d,]+)\): error (?<cod>\w+): (?<txt>.*?)( \[[^\]]+\])?$') {
        $ruta = $Matches.ruta.Trim()
        if (-not [IO.Path]::IsPathRooted($ruta)) { $ruta = Join-Path (Join-Path $PSScriptRoot 'Vistas') $ruta }
        $ruta = [IO.Path]::GetFullPath($ruta).ToLowerInvariant()
        $clave = "$ruta|$($Matches.pos)|$($Matches.cod)"
        if (-not $errores.ContainsKey($clave)) { $errores[$clave] = [pscustomobject]@{ Ruta = $ruta; Codigo = $Matches.cod; Texto = $Matches.txt } }
    } elseif ($l -match ': error (?<cod>\w+): (?<txt>.*)$') {
        $clave = "sin-archivo|$($Matches.cod)|$($Matches.txt)"
        if (-not $errores.ContainsKey($clave)) { $errores[$clave] = [pscustomobject]@{ Ruta = ''; Codigo = $Matches.cod; Texto = $Matches.txt } }
    }
}

$todos = @($errores.Values)
$conError = @{}
foreach ($e in $todos) { if ($e.Ruta -and $lineasDe.ContainsKey($e.Ruta)) { $conError[$e.Ruta] = 1 + [int]$conError[$e.Ruta] } }
function Resumen([string]$filtro) {
    $grupo = @($fuentes | Where-Object { $_.Name -like $filtro })
    $malos = @($grupo | Where-Object { $conError.ContainsKey($_.FullName.ToLowerInvariant()) })
    $lineas = ($grupo | ForEach-Object { $lineasDe[$_.FullName.ToLowerInvariant()] } | Measure-Object -Sum).Sum
    $lineasMalas = ($malos | ForEach-Object { $lineasDe[$_.FullName.ToLowerInvariant()] } | Measure-Object -Sum).Sum
    [ordered]@{
        archivos = $grupo.Count; archivos_sin_errores = $grupo.Count - $malos.Count
        lineas = [int]$lineas; lineas_en_archivos_sin_errores = [int]($lineas - $lineasMalas)
        porcentaje_archivos = [Math]::Round(100.0 * ($grupo.Count - $malos.Count) / [Math]::Max(1, $grupo.Count), 1)
        porcentaje_lineas = [Math]::Round(100.0 * ($lineas - $lineasMalas) / [Math]::Max(1, $lineas), 1)
    }
}

# Lo que compila pero no correría: Uno0001 (miembro de WinUI sin implementar en Uno) y CA1416 (API solo de Windows).
$avisos = @{ Uno0001 = @{}; CA1416 = @{} }
$miembros = @{}
foreach ($l in Get-Content -LiteralPath $log) {
    if ($l -match '^\s*(?<ruta>[^(]+?)\((?<pos>[\d,]+)\): warning (?<cod>Uno0001|CA1416): (?<txt>.*?)( \[[^\]]+\])?$') {
        $avisos[$Matches.cod]["$($Matches.ruta.Trim().ToLowerInvariant())|$($Matches.pos)"] = $Matches.ruta.Trim().ToLowerInvariant()
        if ($Matches.cod -eq 'Uno0001' -and $Matches.txt -match '^(?<m>.+?) is not implemented') { $miembros[$Matches.m] = 1 + [int]$miembros[$Matches.m] }
    }
}
function Archivos-De($tabla) { @($tabla.Values | Sort-Object -Unique | Where-Object { $lineasDe.ContainsKey($_) }) }

$resultado = [ordered]@{
    sin_implementar_en_uno = [ordered]@{
        usos = $avisos.Uno0001.Count; miembros_distintos = $miembros.Count; archivos = (Archivos-De $avisos.Uno0001).Count
        miembros = @($miembros.Keys | Sort-Object)
    }
    solo_windows = [ordered]@{ usos = $avisos.CA1416.Count; archivos = (Archivos-De $avisos.CA1416).Count }
    errores_distintos = $todos.Count
    errores_fuera_de_la_app = @($todos | Where-Object { -not $_.Ruta -or -not $lineasDe.ContainsKey($_.Ruta) }).Count
    xaml = Resumen '*.xaml'
    codigo = Resumen '*.cs'
    por_codigo = @($todos | Group-Object Codigo | Sort-Object Count -Descending | Select-Object -First 15 | ForEach-Object { [ordered]@{ codigo = $_.Name; veces = $_.Count; ejemplo = $_.Group[0].Texto } })
    archivos_con_mas_errores = @($conError.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 25 | ForEach-Object { [ordered]@{ archivo = $_.Key.Substring($app.Length + 1); errores = $_.Value } })
}
$resultado | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $salida 'vistas.json') -Encoding UTF8
$todos | Sort-Object Ruta | ForEach-Object { "$($_.Ruta.Replace($app.ToLowerInvariant() + '\', '')): $($_.Codigo): $($_.Texto)" } | Set-Content -LiteralPath (Join-Path $salida 'vistas-errores.txt') -Encoding UTF8
$resultado | ConvertTo-Json -Depth 5
