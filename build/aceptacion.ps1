# Corre la aceptación de la fase 2 con el decodificador real y resume cada criterio con sus cifras.
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File build\aceptacion.ps1 [-Completa] [-Resumir <carpeta>]
#   -Completa agrega (con RODAJE_PRUEBAS_COMPLETAS=1) el clip de 10 minutos entero en tiempo real con el reloj
#             del dispositivo de audio a volumen 0; suma unos 11 minutos.
#   -Resumir rehace solo el resumen de una corrida anterior (TestResults\aceptacion-<fecha-hora>), sin volver a medir.
# Activa RODAJE_PRUEBAS_LARGAS=1 y corre, una suite tras otra y sin nada más en paralelo (las medidas en
# tiempo real se falsean con carga): Rodaje.Acceptance.Tests y las pruebas de audio con el dispositivo real.
# Los registros quedan en TestResults\aceptacion-<fecha-hora>\ (se conservan 5). Sale con 0 si todo pasó.

param(
    [switch]$Completa,
    [string]$Resumir = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$Raiz = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'comun.psm1') -DisableNameChecking -Force
$E = Inicializar-Rodaje $Raiz -Fases 4
$ProyectoAceptacion = Join-Path $Raiz 'tests\Rodaje.Acceptance.Tests\Rodaje.Acceptance.Tests.csproj'
$ProyectoAudio = Join-Path $Raiz 'tests\Rodaje.Audio.Tests\Rodaje.Audio.Tests.csproj'
$Dotnet = $E.Dotnet
$MarcaSdk = $E.MarcaSdk
$FfmpegVersion = $E.FfmpegVersion
$MarcaFfmpeg = $E.MarcaFfmpeg
$Reloj = [System.Diagnostics.Stopwatch]::StartNew()
$Invariante = [System.Globalization.CultureInfo]::InvariantCulture

function Terminar([int]$codigo) {
    Write-Host ''
    Nota ("Tiempo total: " + (Duracion $Reloj.Elapsed))
    if ($script:Corrida) { Nota "Registros completos y resultados en: $script:Corrida" }
    Write-Host ''
    exit $codigo
}

function Medidas($resultados) {
    $m = @{}
    foreach ($r in $resultados) {
        foreach ($l in ($r.Salida -split "`r?`n")) {
            if ($l -match '^\s*MEDIDA\s+(\S+)\s+=\s+(.*?)\s*$') { $m[$Matches[1]] = $Matches[2] }
        }
    }
    return $m
}

function Correr-Suite([int]$fase, [string]$nombre, [string]$proyecto, [string]$carpeta, [string[]]$extra) {
    New-Item -ItemType Directory -Force -Path $carpeta | Out-Null
    $argumentos = @('test', $proyecto, '--no-build', '-c', 'Debug', '--nologo',
        '--results-directory', $carpeta,
        '--logger', 'trx;LogFilePrefix=resultados',
        '--logger', 'console;verbosity=normal') + $extra
    $script:Hechas = 0
    $alVuelo = {
        param($l)
        if ($l -match '^\s{2}(Passed|Failed|Skipped|Correctas?|Superad[ao]s?|Con error|Omitid[ao]s?)\s+(.+?)(\s+\[[^\]]+\])?\s*$') {
            $script:Hechas++
            $corto = ($Matches[2] -replace '^Rodaje\.[A-Za-z]+\.Tests\.', '')
            Nota ("[{0}] {1} {2}" -f $script:Hechas, $Matches[1], $corto)
        }
    }
    $reloj = [System.Diagnostics.Stopwatch]::StartNew()
    $r = Invocar-Dotnet (Join-Path $carpeta "$fase-$nombre.log") $argumentos $alVuelo
    $resultados = Leer-Trx $carpeta
    Nota ("{0}: {1} pruebas en {2}" -f $nombre, $resultados.Count, (Duracion $reloj.Elapsed))
    return [pscustomobject]@{ Codigo = $r.Codigo; Lineas = $r.Lineas; Resultados = $resultados }
}

function Num([string]$texto) {
    $v = 0.0
    if ([double]::TryParse($texto, [System.Globalization.NumberStyles]::Float, $Invariante, [ref]$v)) { return $v }
    return [double]::NaN
}

function Formato([double]$valor, [string]$patron) {
    # Cifras con coma decimal, como en el resto de los mensajes en español.
    return $valor.ToString($patron, [System.Globalization.CultureInfo]::GetCultureInfo('es-CO'))
}

# Estado de un criterio: pasa si corrió al menos una prueba y ninguna falló.
function Estado($pruebas) {
    $corrieron = @($pruebas | Where-Object { $_.Resultado -ne 'NotExecuted' })
    $fallaron = @($pruebas | Where-Object { $_.Resultado -ne 'Passed' -and $_.Resultado -ne 'NotExecuted' })
    if ($fallaron.Count -gt 0) { return 'FALLA' }
    if ($corrieron.Count -eq 0) { return 'SIN DATOS' }
    return 'PASA'
}

function Mostrar-Estado([string]$criterio, [string]$estado) {
    switch ($estado) {
        'PASA' { Write-Host "  [PASA]  $criterio" -ForegroundColor Green }
        'FALLA' { Write-Host "  [FALLA] $criterio" -ForegroundColor Red }
        default { Write-Host "  [SIN DATOS] $criterio" -ForegroundColor Yellow }
    }
}

function Resultado-Guardado([string]$carpeta) {
    return [pscustomobject]@{ Codigo = 0; Lineas = (New-Object System.Collections.Generic.List[string]); Resultados = (Leer-Trx $carpeta) }
}

Titulo 'Rodaje - aceptación de la fase 2 (seek exacto y sincronía audio/video)'
Nota "Proyecto: $Raiz"
Nota 'Corre con RODAJE_PRUEBAS_LARGAS=1, una suite tras otra. Tarda varios minutos: no uses el equipo'
Nota 'para cosas pesadas mientras tanto, porque las medidas en tiempo real se falsean con la carga.'

if ($Resumir) {
    if (-not (Test-Path -LiteralPath $Resumir)) { Mal "No existe la carpeta $Resumir."; Terminar 1 }
    $script:Corrida = (Resolve-Path -LiteralPath $Resumir).Path
    Nota "Solo el resumen de la corrida guardada en $script:Corrida."
    $a = Resultado-Guardado (Join-Path $script:Corrida 'aceptacion')
    $b = Resultado-Guardado (Join-Path $script:Corrida 'audio')
} else {

if (-not (Test-Path -LiteralPath $Dotnet) -or -not (Test-Path -LiteralPath $MarcaSdk)) {
    Write-Host ''
    Mal 'No encuentro el SDK de .NET del proyecto completo, y sin él no se pueden compilar ni correr las pruebas.'
    Nota "Lo busqué en: $Dotnet"
    Nota 'Haz doble clic en Probar.bat (lo prepara solo) o corre build\preparar-entorno.ps1.'
    Terminar 1
}
if (-not $FfmpegVersion -or -not (Test-Path -LiteralPath $MarcaFfmpeg)) {
    Write-Host ''
    Mal 'Faltan las bibliotecas de FFmpeg del proyecto, o quedaron a medias; la aceptación decodifica video de verdad.'
    Nota "Falta: $MarcaFfmpeg"
    Nota 'Haz doble clic en Probar.bat (lo prepara solo) o corre build\preparar-entorno.ps1.'
    Terminar 1
}

Usar-DotnetLocal $E
$env:RODAJE_PRUEBAS_LARGAS = '1'
if ($Completa) {
    $env:RODAJE_PRUEBAS_COMPLETAS = '1'
    Nota 'Corrida completa: incluye 10 minutos de reproducción en tiempo real con el dispositivo de audio.'
} else {
    Remove-Item Env:\RODAJE_PRUEBAS_COMPLETAS -ErrorAction SilentlyContinue
}

$script:Corrida = Nueva-Corrida 'aceptacion-' 5

Fase 1 'restaurando paquetes y compilando las pruebas de aceptación y de audio...'
foreach ($proyecto in @($ProyectoAceptacion, $ProyectoAudio)) {
    $nombre = [System.IO.Path]::GetFileNameWithoutExtension($proyecto)
    $r = Invocar-Dotnet (Join-Path $script:Corrida "1-compilar-$nombre.log") @('build', $proyecto, '-c', 'Debug', '--nologo')
    if ($r.Codigo -ne 0) {
        $errores = Diagnosticos-De $r.Lineas
        Mal "No compila $nombre, así que no se puede medir nada."
        if ($errores.Count -gt 0) { Nota ('Primer error: ' + (Corto $errores[0])) }
        else { $r.Lineas | Select-Object -Last 15 | ForEach-Object { Nota ('  ' + (Corto $_)) } }
        Terminar 1
    }
}
Bien 'Compila sin errores ni advertencias.'

Fase 2 'seek exacto y deriva A/V con el decodificador real (la primera vez genera los videos de prueba)...'
$a = Correr-Suite 2 'aceptacion' $ProyectoAceptacion (Join-Path $script:Corrida 'aceptacion') @()

Fase 3 'reloj de audio con el dispositivo real (reproduce silencio)...'
$b = Correr-Suite 3 'audio' $ProyectoAudio (Join-Path $script:Corrida 'audio') @('--filter', 'FullyQualifiedName~Rodaje.Audio.Tests.Hardware')
}

Fase 4 'resumiendo...'
$todas = New-Object System.Collections.Generic.List[object]
foreach ($suite in @($a, $b)) { foreach ($r in $suite.Resultados) { if ($r) { $todas.Add($r) } } }
$medidas = Medidas $todas
if ($todas.Count -eq 0) {
    Mal 'Las pruebas no llegaron a correr: el ejecutor terminó antes de dar resultados.'
    (@($a.Lineas) + @($b.Lineas)) | Select-Object -Last 15 | ForEach-Object { Nota ('  ' + (Corto $_)) }
    Terminar 1
}

Titulo 'Resumen por criterio'

# Criterio 1: seek exacto al cuadro.
$seek = @($a.Resultados | Where-Object { $_.Nombre -like '*FrameExactSeekTests*' })
$exactos = 0; $pedidos = 0
$filas = New-Object System.Collections.Generic.List[string]
foreach ($clave in ($medidas.Keys | Where-Object { $_ -like 'seek.*' } | Sort-Object)) {
    if ($medidas[$clave] -match '^(\d+)/(\d+)$') {
        $ok = [int]$Matches[1]; $total = [int]$Matches[2]
        $exactos += $ok; $pedidos += $total
        $partes = $clave.Split('.')
        $via = if ($partes[3] -eq 'getframe') { 'GetFrame' } else { 'PlaybackScheduler.SeekToFrame' }
        $filas.Add(('{0,-5} {1,-9} {2,-30} {3} de {4} exactos' -f $partes[1].ToUpperInvariant().Replace('H264', 'H.264'), $partes[2], $via, $ok, $total))
    }
}
Write-Host ''
Mostrar-Estado 'Seek exacto al cuadro en H.264 y HEVC (hardware D3D11VA y software)' (Estado $seek)
if ($pedidos -gt 0) {
    Nota ('Exactitud: {0} % ({1} de {2} pedidos al azar, más primero, último y keyframes con vecinos)' -f (Formato (100.0 * $exactos / $pedidos) '0.00'), $exactos, $pedidos)
    foreach ($f in $filas) { Nota "  $f" }
}

# Criterio 2: deriva A/V en 10 minutos.
$deriva = @($a.Resultados | Where-Object { $_.Nombre -like '*Diez_minutos_con_reloj_manual*' })
Write-Host ''
Mostrar-Estado 'Deriva entre audio y video menor que 1 cuadro en 10 minutos' (Estado $deriva)
$peorMs = 0.0; $peorCuadros = 0.0
foreach ($ritmo in @(@('nominal', 'reloj nominal'), @('mas', 'reloj +0,1 %'), @('menos', 'reloj -0,1 %'))) {
    $ms = $medidas["deriva.manual.$($ritmo[0]).desfase_max_ms"]
    if (-not $ms) { continue }
    $cuadros = $medidas["deriva.manual.$($ritmo[0]).desfase_max_cuadros"]
    $marcas = $medidas["deriva.manual.$($ritmo[0]).marcas"]
    $alineacion = $medidas["deriva.manual.$($ritmo[0]).alineacion_audio_ms"]
    $coinciden = $medidas["deriva.manual.$($ritmo[0]).refrescos_con_el_cuadro_del_audio"]
    $errorCuadros = $medidas["deriva.manual.$($ritmo[0]).error_max_cuadros"]
    $peorMs = [Math]::Max($peorMs, (Num $ms)); $peorCuadros = [Math]::Max($peorCuadros, (Num $cuadros))
    Nota ('{0,-14} cuadro correcto en {1} refrescos (error máximo {2} cuadros); desfase audio-cuadro máximo {3} ms ({4} cuadros);' -f $ritmo[1], $coinciden, $errorCuadros, (Formato (Num $ms) '0.000'), (Formato (Num $cuadros) '0.000'))
    Nota ('{0,-14} {1} marcas de audio comprobadas, cada una a {2} ms como mucho de su segundo' -f '', $marcas, (Formato (Num $alineacion) '0.000'))
}
if ($medidas.ContainsKey('deriva.manual.nominal.desfase_max_ms')) {
    Nota ('Peor caso: {0} ms = {1} cuadros (el límite es 1 cuadro = 33,367 ms a 29,97 fps). El desfase va de 0 a casi' -f (Formato $peorMs '0.000'), (Formato $peorCuadros '0.000'))
    Nota 'un cuadro por definición (un cuadro se ve durante toda su duración); la deriva real es el error en cuadros.'
}

$real = @($a.Resultados | Where-Object { $_.Nombre -like '*Treinta_segundos_en_tiempo_real*' })
Write-Host ''
Mostrar-Estado 'Sincronía en tiempo real (30 s con el reloj del dispositivo de audio)' (Estado $real)
if ($medidas.ContainsKey('tiempo_real.desfase_max_ms')) {
    Nota ('Dispositivo: {0}' -f $medidas['tiempo_real.dispositivo'])
    Nota ('Desfase máximo reloj-cuadro {0} ms ({1} cuadros); {2} % de los refrescos con el cuadro debido; refrescos tarde {3}' -f (Formato (Num $medidas['tiempo_real.desfase_max_ms']) '0.000'), (Formato (Num $medidas['tiempo_real.desfase_max_cuadros']) '0.000'), (Formato (Num $medidas['tiempo_real.refrescos_en_sincronia']) '0.00'), $medidas['tiempo_real.refrescos_tarde'])
}

$diez = @($a.Resultados | Where-Object { $_.Nombre -like '*Diez_minutos_en_tiempo_real*' })
$diezCorrio = @($diez | Where-Object { $_.Resultado -ne 'NotExecuted' }).Count -gt 0
if ($Completa -or $diezCorrio) {
    Write-Host ''
    Mostrar-Estado 'Deriva en 10 minutos de punta a punta: el clip entero en tiempo real con el reloj del dispositivo' (Estado $diez)
    if ($medidas.ContainsKey('tiempo_real_10min.desfase_max_ms')) {
        Nota ('Dispositivo: {0}; {1} s reproducidos' -f $medidas['tiempo_real_10min.dispositivo'], (Formato (Num $medidas['tiempo_real_10min.duracion_s']) '0.0'))
        Nota ('Error máximo {0} cuadros; desfase máximo reloj-cuadro {1} ms ({2} cuadros); {3} % de los refrescos con el cuadro debido' -f $medidas['tiempo_real_10min.error_max_cuadros'], (Formato (Num $medidas['tiempo_real_10min.desfase_max_ms']) '0.000'), (Formato (Num $medidas['tiempo_real_10min.desfase_max_cuadros']) '0.000'), (Formato (Num $medidas['tiempo_real_10min.refrescos_en_sincronia']) '0.00'))
        Nota ('Refrescos tarde {0}; cuadros saltados {1}; reloj del dispositivo frente al de pared al final: {2} ms' -f $medidas['tiempo_real_10min.refrescos_tarde'], $medidas['tiempo_real_10min.cuadros_saltados'], (Formato (Num $medidas['tiempo_real_10min.reloj_menos_pared_ms']) '+0.0;-0.0'))
    }
}

# Reloj de audio: deriva frente a QPC y jitter, de la salida de WasapiHardwareTests.
$audio = @($b.Resultados | Where-Object { $_ })
Write-Host ''
Mostrar-Estado 'Reloj de audio con el dispositivo real (deriva, jitter, pausa, seek y pérdida del dispositivo)' (Estado $audio)
foreach ($r in ($audio | Where-Object { $_.Nombre -like '*ElRelojSigueAlDispositivo*' })) {
    $modo = if ($r.Nombre -match 'lowLatency: True') { 'baja latencia' } elseif ($r.Nombre -match 'lowLatency: False') { 'modo normal' } else { $r.Nombre }
    if ($r.Salida -match 'deriva ([+-]?[\d.]+) ppm.*?residuo máx ([\d.]+) ms, RMS ([\d.]+) ms') {
        Nota ('{0,-14} deriva {1} ppm frente al reloj del sistema; jitter máx {2} ms, RMS {3} ms' -f $modo, (Formato (Num $Matches[1]) '+0.0;-0.0'), (Formato (Num $Matches[2]) '0.000'), (Formato (Num $Matches[3]) '0.000'))
    } elseif ($r.Resultado -eq 'NotExecuted') {
        Aviso ("{0}: omitida. {1}" -f $modo, $r.Mensaje)
    }
}

$fallidas = @($todas | Where-Object { $_.Resultado -ne 'Passed' -and $_.Resultado -ne 'NotExecuted' })
$omitidas = @($todas | Where-Object { $_.Resultado -eq 'NotExecuted' -and $_.Nombre -notlike '*Tono*' -and ($Completa -or $_.Nombre -notlike '*Diez_minutos_en_tiempo_real*') })
if ($omitidas.Count -gt 0) {
    Titulo 'Pruebas omitidas'
    foreach ($o in $omitidas) { Aviso ("{0}: {1}" -f ($o.Nombre -replace '^Rodaje\.[A-Za-z]+\.Tests\.', ''), $o.Mensaje) }
}
if ($fallidas.Count -gt 0) {
    Titulo 'Pruebas que fallaron'
    $i = 0
    foreach ($f in $fallidas) {
        $i++
        Write-Host ''
        Mal ("{0}. {1}" -f $i, ($f.Nombre -replace '^Rodaje\.[A-Za-z]+\.Tests\.', ''))
        ($f.Mensaje -split "`r?`n") | Select-Object -First 8 | ForEach-Object { Nota ('   ' + $_) }
        $donde = Donde-Fallo $f.Pila
        if ($donde) { Nota ('   Dónde: ' + (Corto $donde.Trim())) }
    }
}

$estados = @((Estado $seek), (Estado $deriva), (Estado $real), (Estado $audio))
if ($Completa) { $estados += (Estado $diez) }
Write-Host ''
if ($fallidas.Count -gt 0 -or $estados -contains 'FALLA') {
    Mal 'RESULTADO: la fase 2 NO cumple todos los criterios. Arriba está qué falló y con qué cifras.'
    Terminar 1
}
if ($estados -contains 'SIN DATOS' -or $a.Codigo -ne 0 -or $b.Codigo -ne 0) {
    Aviso 'RESULTADO: no se pudo medir todo (ver las pruebas omitidas o el registro). No se da por aceptada.'
    Terminar 1
}
Bien 'RESULTADO: la fase 2 cumple los criterios de aceptación.'
Terminar 0
