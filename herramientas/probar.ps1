# Corre las pruebas con el SDK de .entorno\ (o FOTOTOMA_ENTORNO) y resume el resultado.
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File herramientas\probar.ps1 [-Cobertura] [-Filtro "texto"]
# Los registros quedan en TestResults\<fecha-hora>\ (se conservan 10). Sale con 0 si todo pasó.

param(
    [switch]$Cobertura,
    [string]$Filtro = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$Raiz = Split-Path -Parent $PSScriptRoot
$Solucion = Join-Path $Raiz 'FotoToma.slnx'
if ($env:FOTOTOMA_ENTORNO) { $Entorno = $env:FOTOTOMA_ENTORNO } else { $Entorno = Join-Path $Raiz '.entorno' }
$DotnetDir = Join-Path $Entorno 'dotnet'
$Dotnet = Join-Path $DotnetDir 'dotnet.exe'
# Listo = existe la marca .sdk-<versión> de preparar-entorno.ps1; dotnet.exe no basta tras un apagón.
try { $SdkVersion = (Get-Content -LiteralPath (Join-Path $Raiz 'global.json') -Raw -Encoding UTF8 | ConvertFrom-Json).sdk.version } catch { $SdkVersion = '' }
$MarcaSdk = Join-Path $DotnetDir ".sdk-$SdkVersion"
$FfmpegVersion = ''
$lineaFf = Select-String -LiteralPath (Join-Path $PSScriptRoot 'preparar-entorno.ps1') -Pattern "^\`$FfmpegVersion = '([^']+)'" | Select-Object -First 1
if ($lineaFf) { $FfmpegVersion = $lineaFf.Matches[0].Groups[1].Value }
$MarcaFfmpeg = Join-Path $Entorno "ffmpeg\.ffmpeg-$FfmpegVersion"
$Reloj = [System.Diagnostics.Stopwatch]::StartNew()
$TotalFases = 3

function Titulo([string]$texto) { Write-Host ''; Write-Host "  $texto" -ForegroundColor Cyan }
function Fase([int]$n, [string]$texto) { Write-Host ''; Write-Host "  Fase $n/${TotalFases}: $texto" -ForegroundColor Cyan }
function Bien([string]$texto) { Write-Host "    $texto" -ForegroundColor Green }
function Mal([string]$texto) { Write-Host "    $texto" -ForegroundColor Red }
function Nota([string]$texto) { Write-Host "    $texto" }
function Duracion([TimeSpan]$t) {
    if ($t.TotalMinutes -ge 1) { return ('{0} min {1} s' -f [int][Math]::Floor($t.TotalMinutes), $t.Seconds) }
    return ('{0:0.0} s' -f $t.TotalSeconds)
}
function Terminar([int]$codigo) {
    Write-Host ''
    Nota ("Tiempo total: " + (Duracion $Reloj.Elapsed))
    if ($script:Corrida) { Nota "Registros completos y resultados en: $script:Corrida" }
    Write-Host ''
    exit $codigo
}

# $AlVuelo recibe cada línea mientras llega, para mostrar progreso.
function Invocar-Dotnet([string]$log, [string[]]$argumentos, [scriptblock]$AlVuelo = $null) {
    $lineas = New-Object System.Collections.Generic.List[string]
    $anterior = $ErrorActionPreference
    # En PowerShell 5.1 el stderr de un programa llega como ErrorRecord y 'Stop' cortaría el script.
    $ErrorActionPreference = 'Continue'
    try {
        & $Dotnet @argumentos 2>&1 | ForEach-Object {
            $l = "$_"
            $lineas.Add($l)
            if ($AlVuelo) { & $AlVuelo $l }
        }
        $codigo = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $anterior
    }
    [System.IO.File]::WriteAllLines($log, $lineas, (New-Object System.Text.UTF8Encoding($false)))
    return [pscustomobject]@{ Codigo = $codigo; Lineas = $lineas }
}

# Sin repetir: MSBuild imprime cada error dos veces (al momento y en el resumen).
function Errores-De([System.Collections.Generic.List[string]]$lineas) {
    $vistos = @{}
    $lista = New-Object System.Collections.Generic.List[string]
    foreach ($l in $lineas) {
        if ($l -match '(?i)\berror\s+[A-Z]{2,}\d+\s*:') {
            $limpia = ($l -replace '\s+\[[^\]]+\.(cs|fs|vb)proj[^\]]*\]\s*$', '').Trim()
            if (-not $vistos.ContainsKey($limpia)) { $vistos[$limpia] = $true; $lista.Add($limpia) }
        }
    }
    return ,$lista
}

function Corto([string]$texto) {
    return $texto.Replace($Raiz + '\', '').Replace($Raiz, '.')
}

Titulo 'FotoToma - pruebas automáticas del núcleo'
Nota "Proyecto: $Raiz"

if (-not (Test-Path -LiteralPath $Dotnet) -or -not (Test-Path -LiteralPath $MarcaSdk)) {
    Write-Host ''
    if (Test-Path -LiteralPath $Dotnet) {
        Mal 'El SDK de .NET del proyecto está a medias (falta su marca de instalación completa), y así no es fiable.'
        Nota "Falta: $MarcaSdk"
    } else {
        Mal 'No encuentro el SDK de .NET del proyecto, y sin él no se pueden compilar ni correr las pruebas.'
        Nota "Lo busqué en: $Dotnet"
    }
    Nota ''
    Nota 'Cómo arreglarlo (una sola vez, descarga unos 280 MB):'
    Nota '  - Haz doble clic en ABRIR.bat, que lo prepara solo, o'
    Nota '  - corre: powershell -NoProfile -ExecutionPolicy Bypass -File herramientas\preparar-entorno.ps1'
    Terminar 1
}
if (-not $FfmpegVersion -or -not (Test-Path -LiteralPath $MarcaFfmpeg)) {
    Write-Host ''
    Mal 'Faltan las bibliotecas de FFmpeg del proyecto, o quedaron a medias (falta su marca de instalación completa).'
    Nota "Falta: $MarcaFfmpeg"
    Nota ''
    Nota 'Cómo arreglarlo (una sola vez, descarga unos 75 MB):'
    Nota '  - Haz doble clic en ABRIR.bat, que lo prepara solo, o'
    Nota '  - corre: powershell -NoProfile -ExecutionPolicy Bypass -File herramientas\preparar-entorno.ps1'
    Terminar 1
}
if (-not (Test-Path -LiteralPath $Solucion)) {
    Mal "No encuentro la solución $Solucion. ¿Se movió este script fuera de la carpeta herramientas?"
    Terminar 1
}

# Las mismas variables que herramientas\dotnet-local.cmd.
$env:DOTNET_ROOT = $DotnetDir
$env:NUGET_PACKAGES = Join-Path $Entorno 'nuget'
$env:DOTNET_CLI_HOME = $Entorno
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_FORCE_UTF8_ENCODING = '1'
$env:PATH = "$DotnetDir;$env:PATH"

$Resultados = Join-Path $Raiz 'TestResults'
New-Item -ItemType Directory -Force -Path $Resultados | Out-Null
$script:Corrida = Join-Path $Resultados (Get-Date -Format 'yyyyMMdd-HHmmss')
New-Item -ItemType Directory -Force -Path $script:Corrida | Out-Null
Get-ChildItem -LiteralPath $Resultados -Directory | Where-Object { $_.Name -match '^\d{8}-\d{6}$' } |
    Sort-Object Name -Descending | Select-Object -Skip 10 |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }

Fase 1 'restaurando paquetes NuGet (la primera vez los descarga; luego salen de la caché local)...'
$r = Invocar-Dotnet (Join-Path $script:Corrida '1-restaurar.log') @('restore', $Solucion, '--nologo')
if ($r.Codigo -ne 0) {
    $errores = Errores-De $r.Lineas
    Mal 'No se pudieron preparar los paquetes que usan las pruebas.'
    $texto = ($errores -join "`n")
    if ($texto -match 'NU1301|NU1101|NU1102|service index|No such host|unable to load') {
        Nota 'Parece un problema de conexión: la primera vez hay que tener internet para bajar los paquetes.'
    } elseif ($texto -match 'NU1004|NU1403|lock file|packages.lock.json') {
        Nota 'Los paquetes no coinciden con los fijados en packages.lock.json (alguien cambió un paquete sin'
        Nota 'actualizar el archivo de bloqueo). Corre herramientas\dotnet-local.cmd restore --force-evaluate y revisa el cambio.'
    }
    if ($errores.Count -gt 0) { Nota ''; Nota 'Primer error:'; Nota ('  ' + (Corto $errores[0])) }
    Terminar 1
}
Bien 'Paquetes listos.'

Fase 2 'compilando el núcleo y las pruebas...'
$r = Invocar-Dotnet (Join-Path $script:Corrida '2-compilar.log') @('build', $Solucion, '--no-restore', '-c', 'Debug', '--nologo')
if ($r.Codigo -ne 0) {
    $errores = Errores-De $r.Lineas
    Mal 'El código no compila, así que todavía no se pueden correr las pruebas.'
    if ($errores.Count -gt 0) {
        Nota ''
        Nota 'Este es el primer error:'
        Nota ('  ' + (Corto $errores[0]))
        if ($errores.Count -gt 1) { Nota "  (y $($errores.Count - 1) error(es) más; están todos en 2-compilar.log)" }
        Nota ''
        Nota 'Se lee así: archivo(línea,columna): error CÓDIGO: explicación. Corrige esa línea y vuelve a probar.'
    } else {
        Nota 'No reconocí el mensaje de error; mira el final del registro 2-compilar.log.'
        $r.Lineas | Select-Object -Last 15 | ForEach-Object { Nota ('  ' + (Corto $_)) }
    }
    Terminar 1
}
Bien 'Compila sin errores ni advertencias.'

Fase 3 $(if ($Cobertura) { 'corriendo las pruebas y midiendo la cobertura...' } else { 'corriendo las pruebas...' })
$argumentos = @('test', $Solucion, '--no-build', '-c', 'Debug', '--nologo',
    '--results-directory', $script:Corrida,
    '--logger', 'trx;LogFilePrefix=resultados',
    '--logger', 'console;verbosity=normal')
if ($Cobertura) { $argumentos += '--collect:XPlat Code Coverage' }
if ($Filtro) { $argumentos += @('--filter', "FullyQualifiedName~$Filtro") }

$script:Hechas = 0
$alVuelo = {
    param($l)
    if ($l -match '^\s{2}(Passed|Failed|Skipped|Correctas?|Superad[ao]s?|Con error|Omitid[ao]s?)\s+\S') {
        $script:Hechas++
        if ($script:Hechas % 25 -eq 0) { Write-Host ("`r    pruebas ejecutadas: {0}" -f $script:Hechas) -NoNewline }
    }
}
$r = Invocar-Dotnet (Join-Path $script:Corrida '3-probar.log') $argumentos $alVuelo
if ($script:Hechas -ge 25) { Write-Host ("`r    pruebas ejecutadas: {0}" -f $script:Hechas) }

$trx = @(Get-ChildItem -LiteralPath $script:Corrida -Recurse -Filter '*.trx' -ErrorAction SilentlyContinue)
$total = 0; $pasaron = 0; $fallaron = 0; $omitidas = 0
$fallidas = New-Object System.Collections.Generic.List[object]
foreach ($archivo in $trx) {
    [xml]$x = [System.IO.File]::ReadAllText($archivo.FullName)
    foreach ($res in @($x.TestRun.Results.UnitTestResult)) {
        if (-not $res) { continue }
        $total++
        switch ($res.outcome) {
            'Passed'      { $pasaron++ }
            'NotExecuted' { $omitidas++ }
            default {
                $fallaron++
                $info = $res.Output.ErrorInfo
                $mensaje = ''; $pila = ''
                if ($info) { $mensaje = "$($info.Message)".Trim(); $pila = "$($info.StackTrace)".Trim() }
                $fallidas.Add([pscustomobject]@{ Nombre = $res.testName; Mensaje = $mensaje; Pila = $pila })
            }
        }
    }
}

if ($trx.Count -eq 0 -and $r.Codigo -ne 0) {
    Mal 'Las pruebas no llegaron a correr: el ejecutor de pruebas terminó con error antes de dar resultados.'
    $errores = Errores-De $r.Lineas
    if ($errores.Count -gt 0) { Nota ('Primer error: ' + (Corto $errores[0])) }
    else { $r.Lineas | Select-Object -Last 15 | ForEach-Object { Nota ('  ' + (Corto $_)) } }
    Terminar 1
}

Titulo 'Resumen'
if ($total -eq 0) {
    if ($Filtro) { Nota "Ninguna prueba coincide con el filtro ""$Filtro"" (o todavía no hay pruebas)." }
    else { Nota 'Todavía no hay pruebas que correr: el proyecto de pruebas está vacío.' }
    Nota 'Eso no es un error: el núcleo compila. Cuando se escriban pruebas aparecerán aquí.'
} else {
    Nota ("Pruebas: {0} en total" -f $total)
    if ($pasaron -gt 0) { Bien ("Pasaron:  {0}" -f $pasaron) }
    if ($fallaron -gt 0) { Mal ("Fallaron: {0}" -f $fallaron) } else { Nota 'Fallaron: 0' }
    if ($omitidas -gt 0) { Write-Host ("    Omitidas: {0} (marcadas para saltarse)" -f $omitidas) -ForegroundColor Yellow }
}

if ($fallidas.Count -gt 0) {
    Titulo 'Pruebas que fallaron'
    $i = 0
    foreach ($f in $fallidas) {
        $i++
        if ($i -gt 20) { Nota "... y $($fallidas.Count - 20) más (todas están en el .trx)."; break }
        Write-Host ''
        Mal ("{0}. {1}" -f $i, $f.Nombre)
        if ($f.Mensaje) {
            ($f.Mensaje -split "`r?`n") | Select-Object -First 8 | ForEach-Object { Nota ('   ' + $_) }
        }
        $donde = ($f.Pila -split "`r?`n") | Where-Object { $_ -match ' in .+:line \d+' -or $_ -match ' en .+:l.nea \d+' } | Select-Object -First 1
        if ($donde) { Nota ('   Dónde: ' + (Corto $donde.Trim())) }
    }
}

if ($Cobertura) {
    Titulo 'Cobertura de FotoToma.Core'
    $xmlCob = Get-ChildItem -LiteralPath $script:Corrida -Recurse -Filter 'coverage.cobertura.xml' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $xmlCob) {
        Nota 'No se generó el informe de cobertura (sin pruebas no hay nada que medir).'
    } else {
        [xml]$c = [System.IO.File]::ReadAllText($xmlCob.FullName)
        # Una línea puede aparecer en dos clases (parciales, lambdas): se cuenta una vez, con su mejor dato.
        $lineas = @{}
        foreach ($p in @($c.coverage.packages.package)) {
            if (-not $p -or $p.name -ne 'FotoToma.Core') { continue }
            foreach ($cl in @($p.classes.class)) {
                if (-not $cl) { continue }
                foreach ($ln in @($cl.lines.line)) {
                    if (-not $ln) { continue }
                    $clave = "$($cl.filename)|$($ln.number)"
                    $hits = [long]$ln.hits
                    $cub = 0; $tot = 0
                    if ($ln.branch -eq 'True' -and "$($ln.'condition-coverage')" -match '\((\d+)/(\d+)\)') {
                        $cub = [int]$Matches[1]; $tot = [int]$Matches[2]
                    }
                    if ($lineas.ContainsKey($clave)) {
                        $v = $lineas[$clave]
                        if ($hits -gt $v.Hits) { $v.Hits = $hits }
                        if ($tot -gt $v.Tot) { $v.Tot = $tot }
                        if ($cub -gt $v.Cub) { $v.Cub = $cub }
                    } else {
                        $lineas[$clave] = [pscustomobject]@{ Hits = $hits; Cub = $cub; Tot = $tot }
                    }
                }
            }
        }
        if ($lineas.Count -eq 0) {
            Nota 'El informe no trae datos de FotoToma.Core.'
        } else {
            $lt = $lineas.Count
            $lc = @($lineas.Values | Where-Object { $_.Hits -gt 0 }).Count
            $bt = 0; $bc = 0
            foreach ($v in $lineas.Values) { $bt += $v.Tot; $bc += $v.Cub }
            Nota ("Líneas: {0:0.0} %  ({1} de {2} líneas se ejecutan en alguna prueba)" -f (100.0 * $lc / $lt), $lc, $lt)
            if ($bt -gt 0) {
                Nota ("Ramas:  {0:0.0} %  ({1} de {2} caminos de if/switch/?: se recorren)" -f (100.0 * $bc / $bt), $bc, $bt)
            } else {
                Nota 'Ramas:  no hay decisiones (if/switch) que medir.'
            }
            Nota "Informe completo: $(Corto $xmlCob.FullName)"
        }
    }
}

Write-Host ''
if ($fallaron -gt 0) {
    Mal 'RESULTADO: hay pruebas que fallan. El mensaje de cada una dice qué esperaba y qué obtuvo.'
    Terminar 1
}
if ($r.Codigo -ne 0) {
    Mal 'RESULTADO: el ejecutor de pruebas terminó con error aunque ninguna prueba falló.'
    $r.Lineas | Select-Object -Last 10 | ForEach-Object { Nota ('  ' + (Corto $_)) }
    Terminar 1
}
if ($total -eq 0) {
    Write-Host '    RESULTADO: compila bien; aún no hay pruebas.' -ForegroundColor Yellow
} else {
    Bien 'RESULTADO: todas las pruebas pasaron.'
}
Terminar 0
