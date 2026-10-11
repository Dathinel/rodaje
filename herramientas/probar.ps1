# Corre las pruebas con el SDK de .entorno\ (o RODAJE_ENTORNO) y resume el resultado.
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File herramientas\probar.ps1 [-Cobertura] [-Filtro "texto"]
# Las de la categoría TiempoReal corren después, solas. Los registros quedan en TestResults\<fecha-hora>\ (se
# conservan 10). Sale con 0 si todo pasó. Con -Cobertura y sin -Filtro, Rodaje.Core debe quedar en el 99 % de líneas o más.
# Con RODAJE_PRUEBAS_LARGAS=1 y sin -Filtro hay una fase 5: las pruebas de tiempo de CPU que tienen criterio para código
# optimizado corren en Release, y la app se publica con Native AOT y se arranca de verdad (app.ps1 -Autoprueba).

param(
    [switch]$Cobertura,
    [string]$Filtro = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$Raiz = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'comun.psm1') -DisableNameChecking -Force
$Larga = $env:RODAJE_PRUEBAS_LARGAS -eq '1' -and -not $Filtro
$E = Inicializar-Rodaje $Raiz -Fases $(if ($Larga) { 5 } else { 4 })
$Solucion = Join-Path $Raiz 'Rodaje.slnx'
$Dotnet = $E.Dotnet
$MarcaSdk = $E.MarcaSdk
$FfmpegVersion = $E.FfmpegVersion
$MarcaFfmpeg = $E.MarcaFfmpeg
$Reloj = [System.Diagnostics.Stopwatch]::StartNew()

function Terminar([int]$codigo) {
    Write-Host ''
    Nota ("Tiempo total: " + (Duracion $Reloj.Elapsed))
    if ($script:Corrida) { Nota "Registros completos y resultados en: $script:Corrida" }
    Write-Host ''
    exit $codigo
}

Titulo 'Rodaje - pruebas automáticas del núcleo'
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

Usar-DotnetLocal $E
$script:Corrida = Nueva-Corrida '' 10

Fase 1 'restaurando paquetes NuGet (la primera vez los descarga; luego salen de la caché local)...'
$r = Invocar-Dotnet (Join-Path $script:Corrida '1-restaurar.log') @('restore', $Solucion, '--nologo')
if ($r.Codigo -ne 0) {
    $errores = Diagnosticos-De $r.Lineas
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
    $errores = Diagnosticos-De $r.Lineas
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

# Las pruebas de la categoría TiempoReal miden contra el reloj de pared: corren después, solas, para que la carga de
# las demás no las falsee. Si $Dotnet fuera un .cmd, cmd.exe partiría el filtro en el '&'; un '^&' no basta cuando el
# .cmd reenvía %* (como dotnet-local.cmd), así que ahí el filtro va entre comillas.
function Argumentos-Prueba([string]$prefijo, [string]$categoria) {
    $condicion = $categoria
    if ($script:Filtro) { $condicion += "&FullyQualifiedName~$($script:Filtro)" }
    if ($Dotnet -match '\.(cmd|bat)$') { $condicion = '"' + $condicion + '"' }
    return @('test', $Solucion, '--no-build', '-c', 'Debug', '--nologo',
        '--results-directory', $script:Corrida,
        '--logger', "trx;LogFilePrefix=$prefijo",
        '--logger', 'console;verbosity=normal',
        '--filter', $condicion)
}

Fase 3 $(if ($Cobertura) { 'corriendo las pruebas y midiendo la cobertura...' } else { 'corriendo las pruebas...' })
$argumentos = Argumentos-Prueba 'resultados' 'Categoria!=TiempoReal'
if ($Cobertura) { $argumentos += '--collect:XPlat Code Coverage' }

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
# Cuenta, para cada ensamblado con mínimo, el informe de su proyecto de pruebas en esta pasada: es el único que mide solo
# ese ensamblado (su coverlet.runsettings); los de otros proyectos miden otras rutas y la pasada de tiempo real escribe uno
# propio casi vacío. Rodaje.Export entra con la fase 6 (criterio de terminado: 90 %).
$MinimosCobertura = [ordered]@{ 'Rodaje.Core' = 99.0; 'Rodaje.Export' = 90.0 }
$script:CoberturaBaja = @()
$todosLosInformes = @(Get-ChildItem -LiteralPath $script:Corrida -Recurse -Filter 'coverage.cobertura.xml' -ErrorAction SilentlyContinue)
function Informes-De([string]$paquete) {
    return @($todosLosInformes | Where-Object {
        [xml]$x = [System.IO.File]::ReadAllText($_.FullName)
        $nombres = @($x.coverage.packages.package | ForEach-Object { $_.name })
        $nombres.Count -eq 1 -and $nombres[0] -eq $paquete
    })
}

Fase 4 'corriendo las pruebas de tiempo real, solas...'
$rTiempoReal = Invocar-Dotnet (Join-Path $script:Corrida '4-tiempo-real.log') (Argumentos-Prueba 'tiempo-real' 'Categoria=TiempoReal') $alVuelo

# Criterios que solo valen con código optimizado (como el de Native AOT): en Debug esas pruebas exigen un tope holgado.
$PruebasRelease = 'FullyQualifiedName~A_block_of_1024_stereo_frames'
$rRelease = $null
$codigoAutoprueba = 0
if ($Larga) {
    Fase 5 'pasada larga: tiempos en Release y arranque real del exe con Native AOT...'
    $proyectoMotor = Join-Path $Raiz 'tests\Rodaje.Engine.Tests\Rodaje.Engine.Tests.csproj'
    $rRelease = Invocar-Dotnet (Join-Path $script:Corrida '5-compilar-release.log') @('build', $proyectoMotor, '-c', 'Release', '--nologo')
    if ($rRelease.Codigo -eq 0) {
        $filtroRelease = $PruebasRelease
        if ($Dotnet -match '\.(cmd|bat)$') { $filtroRelease = '"' + $filtroRelease + '"' }
        $rRelease = Invocar-Dotnet (Join-Path $script:Corrida '5-release.log') @('test', $proyectoMotor, '--no-build', '-c', 'Release', '--nologo',
            '--results-directory', $script:Corrida, '--logger', 'trx;LogFilePrefix=release', '--filter', $filtroRelease)
    }
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'app.ps1') -Autoprueba
    $codigoAutoprueba = $LASTEXITCODE
}

$trx = @(Get-ChildItem -LiteralPath $script:Corrida -Recurse -Filter '*.trx' -ErrorAction SilentlyContinue)
$total = 0; $pasaron = 0; $fallaron = 0; $omitidas = 0
$fallidas = New-Object System.Collections.Generic.List[object]
foreach ($res in (Leer-Trx $script:Corrida)) {
    $total++
    switch ($res.Resultado) {
        'Passed'      { $pasaron++ }
        'NotExecuted' { $omitidas++ }
        default       { $fallaron++; $fallidas.Add($res) }
    }
}

if ($trx.Count -eq 0 -and $r.Codigo -ne 0) {
    Mal 'Las pruebas no llegaron a correr: el ejecutor de pruebas terminó con error antes de dar resultados.'
    $errores = Diagnosticos-De $r.Lineas
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
        $donde = Donde-Fallo $f.Pila
        if ($donde) { Nota ('   Dónde: ' + (Corto $donde.Trim())) }
    }
}

if ($Cobertura) {
    foreach ($paquete in $MinimosCobertura.Keys) {
        $minimo = $MinimosCobertura[$paquete]
        $informesCobertura = Informes-De $paquete
        Titulo "Cobertura de $paquete"
        if ($informesCobertura.Count -eq 0) {
            Nota 'No se generó el informe de cobertura (sin pruebas no hay nada que medir).'
            continue
        }
        # vstest deja dos copias del informe. Una línea puede aparecer en dos clases (parciales, lambdas): se cuenta una
        # vez, con su mejor dato.
        $lineas = @{}
        foreach ($xmlCob in $informesCobertura) {
            [xml]$c = [System.IO.File]::ReadAllText($xmlCob.FullName)
            foreach ($p in @($c.coverage.packages.package)) {
                if (-not $p -or $p.name -ne $paquete) { continue }
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
        }
        if ($lineas.Count -eq 0) {
            Nota "El informe no trae datos de $paquete."
            if (-not $Filtro) { $script:CoberturaBaja += [pscustomobject]@{ Paquete = $paquete; Porcentaje = 0.0; Minimo = $minimo } }
            continue
        }
        $lt = $lineas.Count
        $lc = @($lineas.Values | Where-Object { $_.Hits -gt 0 }).Count
        $bt = 0; $bc = 0
        foreach ($v in $lineas.Values) { $bt += $v.Tot; $bc += $v.Cub }
        $porcentaje = 100.0 * $lc / $lt
        Nota ("Líneas: {0:0.0} %  ({1} de {2} líneas se ejecutan en alguna prueba; mínimo {3:0} %)" -f $porcentaje, $lc, $lt, $minimo)
        if (-not $Filtro -and $porcentaje -lt $minimo) {
            $script:CoberturaBaja += [pscustomobject]@{ Paquete = $paquete; Porcentaje = $porcentaje; Minimo = $minimo }
        }
        if ($bt -gt 0) {
            Nota ("Ramas:  {0:0.0} %  ({1} de {2} caminos de if/switch/?: se recorren)" -f (100.0 * $bc / $bt), $bc, $bt)
        } else {
            Nota 'Ramas:  no hay decisiones (if/switch) que medir.'
        }
        Nota ("Informes: {0} en {1}" -f $informesCobertura.Count, (Corto $script:Corrida))
    }
}

Write-Host ''
if ($fallaron -gt 0) {
    Mal 'RESULTADO: hay pruebas que fallan. El mensaje de cada una dice qué esperaba y qué obtuvo.'
    Terminar 1
}
if ($codigoAutoprueba -ne 0) {
    Mal 'RESULTADO: las pruebas pasan, pero el exe de Native AOT no arranca bien (mira la autoprueba más arriba).'
    Terminar 1
}
foreach ($pasada in @($r, $rTiempoReal, $rRelease)) {
    if ($pasada -and $pasada.Codigo -ne 0) {
        Mal 'RESULTADO: el ejecutor de pruebas terminó con error aunque ninguna prueba falló.'
        $pasada.Lineas | Select-Object -Last 10 | ForEach-Object { Nota ('  ' + (Corto $_)) }
        Terminar 1
    }
}
if ($script:CoberturaBaja.Count -gt 0) {
    foreach ($baja in $script:CoberturaBaja) {
        Mal ("RESULTADO: las pruebas pasan, pero {0} queda en {1:0.0} % de líneas (mínimo {2:0} %)." -f $baja.Paquete, $baja.Porcentaje, $baja.Minimo)
    }
    Terminar 1
}
if ($total -eq 0) {
    Write-Host '    RESULTADO: compila bien; aún no hay pruebas.' -ForegroundColor Yellow
} else {
    Bien 'RESULTADO: todas las pruebas pasaron.'
}
Terminar 0
