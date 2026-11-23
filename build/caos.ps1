# Modo caos: corre el exe publicado con --caos <semilla> <minutos> (de fondo, la ventana no se ve) para cada semilla, una
# tras otra, y resume qué semillas cerraron la app, la colgaron o dejaron excepciones.
# Uso:
#   powershell -NoProfile -ExecutionPolicy Bypass -File build\caos.ps1 [-Semillas 1,2,3 | -Desde 1 -Hasta 30] [-Minutos 5] [-SinPublicar]
# Cada semilla trabaja en %TEMP%\rodaje-caos\<semilla> (datos, proxies, render, temporales, guardados y exportaciones) y la
# carpeta se borra al terminar. De una semilla que falla se guardan caos.log (cada paso con su número), los informes y el
# minivolcado de fallos\ y rodaje-error.txt en -Resultados (por defecto %TEMP%\rodaje-caos-resultados). Sin red (proxy
# cerrado) ni portapapeles, diálogos ni programas externos (los responde la app en el modo caos). Al final compara antes y
# después %LOCALAPPDATA%\Rodaje, Documentos, Vídeos, %TEMP% y las claves de Rodaje en HKCU, y falla si quedó algo.
# Sale con 0 si ninguna semilla falló y no quedó rastro.
[CmdletBinding(PositionalBinding = $false)]
param(
    # Con -File, «10,12» llega como un solo texto (y como número sería 1012): se separa aquí.
    [string[]]$Semillas = @(),
    [int]$Desde = 1,
    [int]$Hasta = 30,
    [double]$Minutos = 5,
    [string]$Resultados = (Join-Path ([IO.Path]::GetTempPath()) 'rodaje-caos-resultados'),
    [switch]$SinPublicar,
    [switch]$ConservarCorpus,
    # Guarda también el caos.log de las semillas que pasan (para ver qué hizo).
    [switch]$GuardarRegistros
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$Raiz = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'comun.psm1') -DisableNameChecking -Force
$E = Inicializar-Rodaje $Raiz
$Exe = Join-Path $Raiz 'dist\Rodaje\app\Rodaje.exe'
$ErrorTxt = Join-Path $Raiz 'dist\Rodaje\app\rodaje-error.txt'
$Base = Join-Path ([IO.Path]::GetTempPath()) 'rodaje-caos'
$Corpus = Join-Path $Base 'corpus'
# El archivo con el que se reparten el PC los programas de la carpeta de arriba (en la CI no existe).
$Coordinacion = @((Join-Path (Split-Path -Parent $Raiz) 'medicion-en-curso.txt'), 'D:\Programas de uso libre\medicion-en-curso.txt') | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
$Semillas = @($Semillas -split '[,; ]+' | Where-Object { $_ } | ForEach-Object { [int]$_ })
if ($Semillas.Count -eq 0) { $Semillas = @($Desde..$Hasta) }

function Preparar-Corpus {
    $ffmpeg = Join-Path $E.FfmpegBin 'ffmpeg.exe'
    if (-not (Test-Path -LiteralPath $ffmpeg)) { throw "Falta $ffmpeg (Probar.bat preparar-entorno)." }
    New-Item -ItemType Directory -Force -Path $Corpus | Out-Null
    $lienzo = 'testsrc2=size={0}:rate={1}:duration={2}'
    $tono = 'sine=frequency=440:sample_rate=48000:duration={0}'
    $piezas = @(
        @{ n = 'h264-1080p-23976.mp4'; a = @('-f', 'lavfi', '-i', ($lienzo -f '1920x1080', '24000/1001', 4), '-f', 'lavfi', '-i', ($tono -f 4), '-c:v', 'libopenh264', '-b:v', '4M', '-c:a', 'aac', '-shortest') },
        @{ n = 'vertical-1080x2400-60.mp4'; a = @('-f', 'lavfi', '-i', ($lienzo -f '1080x2400', 60, 3), '-f', 'lavfi', '-i', ($tono -f 3), '-c:v', 'libopenh264', '-b:v', '6M', '-c:a', 'aac', '-shortest') },
        @{ n = 'hevc-4k-30.mp4'; a = @('-f', 'lavfi', '-i', ($lienzo -f '3840x2160', 30, 2), '-c:v', 'libkvazaar', '-an') },
        @{ n = 'prores-720p-25.mov'; a = @('-f', 'lavfi', '-i', ($lienzo -f '1280x720', 25, 3), '-f', 'lavfi', '-i', ($tono -f 3), '-c:v', 'prores', '-c:a', 'pcm_s24le', '-shortest') },
        @{ n = 'mpeg4-360p-50.avi'; a = @('-f', 'lavfi', '-i', ($lienzo -f '640x360', 50, 3), '-c:v', 'mpeg4', '-an') },
        @{ n = 'diminuto-16x16.mp4'; a = @('-f', 'lavfi', '-i', ($lienzo -f '16x16', 30, 1), '-c:v', 'libopenh264', '-an') },
        @{ n = 'foto-4000x3000.png'; a = @('-f', 'lavfi', '-i', ($lienzo -f '4000x3000', 1, 1), '-frames:v', '1') },
        @{ n = 'foto-vertical.jpg'; a = @('-f', 'lavfi', '-i', ($lienzo -f '1080x1920', 1, 1), '-frames:v', '1') },
        @{ n = 'animado.gif'; a = @('-f', 'lavfi', '-i', ($lienzo -f '320x240', 10, 2), '-c:v', 'gif') },
        @{ n = 'tono-48k.wav'; a = @('-f', 'lavfi', '-i', ($tono -f 5), '-c:a', 'pcm_s16le') },
        @{ n = 'tono-44k.flac'; a = @('-f', 'lavfi', '-i', 'sine=frequency=220:sample_rate=44100:duration=6', '-c:a', 'flac') }
    )
    foreach ($p in $piezas) {
        $destino = Join-Path $Corpus $p.n
        if (Test-Path -LiteralPath $destino) { continue }
        # ffmpeg escribe en stderr aunque vaya bien (kvazaar): con Stop, PowerShell 5.1 lo tomaría por error.
        $ErrorActionPreference = 'Continue'
        & $ffmpeg -hide_banner -loglevel error -y @($p.a) $destino 2>&1 | Out-Null
        $ErrorActionPreference = 'Stop'
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $destino)) { Aviso "No se pudo generar $($p.n) (sigue sin él)." }
    }
    # Lo roto también lo importa una persona.
    $azar = New-Object byte[] 65536; (New-Object Random 7).NextBytes($azar)
    [IO.File]::WriteAllBytes((Join-Path $Corpus 'roto.mp4'), $azar)
    [IO.File]::WriteAllBytes((Join-Path $Corpus 'vacio.mov'), [byte[]]@())
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText((Join-Path $Corpus 'subtitulos.srt'), "1`r`n00:00:00,500 --> 00:00:02,000`r`nHola`r`n`r`n2`r`n00:00:01,000 --> 00:00:09,000`r`nSe pisa con el anterior y pasa del final`r`n", $utf8)
    [IO.File]::WriteAllText((Join-Path $Corpus 'roto.srt'), "esto no es`r`n99:99 --> nada`r`n", $utf8)
    [IO.File]::WriteAllText((Join-Path $Corpus 'lut.cube'), "LUT_3D_SIZE 2`n0 0 0`n1 0 0`n0 1 0`n1 1 0`n0 0 1`n1 0 1`n0 1 1`n1 1 1`n", $utf8)
    [IO.File]::WriteAllText((Join-Path $Corpus 'roto.cube'), "LUT_3D_SIZE 65`n0 0`n", $utf8)
    [IO.File]::WriteAllText((Join-Path $Corpus 'roto.rodaje'), '{"version": 999, "sequences": [', $utf8)
    return @(Get-ChildItem -LiteralPath $Corpus -File | Sort-Object Name | ForEach-Object FullName)
}

# Lo que el modo caos no puede tocar: lista de archivos con su fecha, para comparar al final.
function Foto-Rastro {
    $foto = @{}
    $carpetas = @(
        (Join-Path $env:LOCALAPPDATA 'Rodaje'),
        [Environment]::GetFolderPath('MyDocuments'),
        [Environment]::GetFolderPath('MyVideos'),
        [Environment]::GetFolderPath('Desktop')
    )
    foreach ($c in $carpetas) {
        if (-not $c -or -not (Test-Path -LiteralPath $c)) { continue }
        Get-ChildItem -LiteralPath $c -Recurse -File -Force -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\(node_modules|\.git)\\' } |
            ForEach-Object { $foto[$_.FullName] = $_.LastWriteTimeUtc.Ticks }
    }
    # De %TEMP% solo lo de arriba (otras apps lo cambian sin parar): lo que no es de la corrida se mira por nombre.
    @([IO.Path]::GetTempPath(), (Join-Path $env:LOCALAPPDATA 'Temp')) | Select-Object -Unique | ForEach-Object { Get-ChildItem -LiteralPath $_ -Force -ErrorAction SilentlyContinue } |
        # rodaje-sala-pruebas y similares son de las pruebas de otros (dotnet test), no de la app.
        Where-Object { $_.Name -notmatch '^rodaje-caos|^rodaje-sala-pruebas|^rodaje pruebas' -and $_.Name -match '(?i)rodaje|ffmpeg|dathinel|caos' } |
        ForEach-Object { $foto[$_.FullName] = $_.LastWriteTimeUtc.Ticks }
    foreach ($clave in 'HKCU:\Software\Rodaje', 'HKCU:\Software\Classes\.rodaje', 'HKCU:\Software\Classes\rodaje', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs\.rodaje', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\OpenSavePidlMRU\rodaje') {
        if (Test-Path -LiteralPath $clave) {
            $valores = (Get-ItemProperty -LiteralPath $clave -ErrorAction SilentlyContinue | Out-String)
            $foto[$clave] = $valores.GetHashCode()
        }
    }
    return $foto
}

function Comparar-Rastro($antes, $despues) {
    $cambios = @()
    foreach ($k in $despues.Keys) {
        if (-not $antes.ContainsKey($k)) { $cambios += "nuevo: $k" }
        elseif ($antes[$k] -ne $despues[$k]) { $cambios += "cambiado: $k" }
    }
    return $cambios
}

function Coordinar([string]$linea) {
    if (-not $Coordinacion) { return }
    for ($i = 0; $i -lt 120; $i++) {
        $texto = if (Test-Path -LiteralPath $Coordinacion) { Get-Content -LiteralPath $Coordinacion -Raw -Encoding UTF8 } else { '' }
        # Unicomp mide (no solo autoprueba): las cifras se estropean con otra carga pesada.
        if ($texto -notmatch '(?im)^.*unicomp.*medici[oó]n(?!.*autoprueba).*$') { break }
        Nota 'Unicomp está midiendo; espero un minuto.'
        Start-Sleep -Seconds 60
    }
    Add-Content -LiteralPath $Coordinacion -Value $linea -Encoding UTF8
}

function Descoordinar([string]$linea) {
    if (-not $Coordinacion) { return }
    $resto = @(Get-Content -LiteralPath $Coordinacion -Encoding UTF8 | Where-Object { $_ -ne $linea })
    Set-Content -LiteralPath $Coordinacion -Value $resto -Encoding UTF8
}

function Causa([string]$carpeta, [int]$codigo, [string]$registro) {
    $log = if (Test-Path -LiteralPath $registro) { @(Get-Content -LiteralPath $registro -Encoding UTF8) } else { @() }
    $cuelgue = $log | Where-Object { $_ -match "`t(CUELGUE|ATASCADO)`t" } | Select-Object -First 1
    if ($cuelgue) { return "cuelgue: $cuelgue" }
    $informe = Get-ChildItem -LiteralPath $carpeta -Filter '*.txt' -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.Name -ne 'caos.log' -and $_.Name -ne 'rodaje-error.txt' } | Sort-Object Name | Select-Object -First 1
    if ($informe) {
        $t = Get-Content -LiteralPath $informe.FullName -Raw -Encoding UTF8
        $excepcion = ([regex]::Match($t, '(?m)^\s*([A-Za-z0-9_.]+(Exception|Error)\b[^\r\n]*)')).Groups[1].Value
        $marco = ([regex]::Match($t, '(?m)^\s+at (Rodaje\.[^\r\n(]+)')).Groups[1].Value
        if ($excepcion -or $marco) { return "$excepcion @ $marco".Trim() }
    }
    if ($codigo -ne 0) { return ('código 0x{0:X8}' -f $codigo) }
    return 'informe en fallos\'
}

Titulo 'Modo caos de Rodaje'
if (-not $SinPublicar) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'app.ps1') -SoloPublicar
    if ($LASTEXITCODE -ne 0) { Mal 'No se pudo publicar.'; exit 1 }
}
if (-not (Test-Path -LiteralPath $Exe)) { Mal "Falta $Exe."; exit 1 }
Exigir-Fondo $Exe
Remove-Item Env:DOTNET_ROOT -ErrorAction SilentlyContinue

$linea = "Claude-Rodaje caos oculto $(Get-Date -Format 'HH:mm')"
$antes = Foto-Rastro
Coordinar $linea
$fallos = @()
$reloj = [Diagnostics.Stopwatch]::StartNew()
$variables = 'RODAJE_DATOS', 'RODAJE_PROXIES', 'RODAJE_RENDER', 'TEMP', 'TMP', 'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'NO_PROXY'
$guardadas = @{}
foreach ($v in $variables) { $guardadas[$v] = [Environment]::GetEnvironmentVariable($v) }
try {
    $archivos = Preparar-Corpus
    Nota "Corpus: $($archivos.Count) archivos en $Corpus"
    foreach ($semilla in $Semillas) {
        $carpeta = Join-Path $Base $semilla
        if (Test-Path -LiteralPath $carpeta) { Remove-Item -LiteralPath $carpeta -Recurse -Force }
        $datos = Join-Path $carpeta 'datos'
        $tmp = Join-Path $carpeta 'tmp'
        # La carpeta de trabajo del proceso: lo que la app escriba con una ruta relativa cae aquí y se cuenta como fallo.
        $actual = Join-Path $carpeta 'actual'
        New-Item -ItemType Directory -Force -Path $datos, $tmp, $actual | Out-Null
        Remove-Item -LiteralPath $ErrorTxt -ErrorAction SilentlyContinue
        $env:RODAJE_DATOS = $datos
        $env:RODAJE_PROXIES = Join-Path $carpeta 'proxies'
        $env:RODAJE_RENDER = Join-Path $carpeta 'render'
        $env:TEMP = $tmp; $env:TMP = $tmp
        # Sin red: todo lo que use el proxy del sistema va a un puerto cerrado.
        $env:HTTP_PROXY = 'http://127.0.0.1:9'; $env:HTTPS_PROXY = 'http://127.0.0.1:9'; $env:ALL_PROXY = 'http://127.0.0.1:9'; $env:NO_PROXY = ''
        $argumentos = @('--caos', $semilla, $Minutos.ToString([Globalization.CultureInfo]::InvariantCulture), '--sin-audio') + @($archivos | ForEach-Object { '"' + $_ + '"' })
        $inicio = Get-Date
        $p = Start-Process -FilePath $Exe -ArgumentList $argumentos -WorkingDirectory $actual -PassThru
        foreach ($v in $variables) { [Environment]::SetEnvironmentVariable($v, $guardadas[$v]) }
        $null = $p.Handle
        $limite = [int](($Minutos * 60 + 90) * 1000)
        $termino = $p.WaitForExit($limite)
        if (-not $termino) { try { $p.Kill() } catch { }; $p.WaitForExit(10000) | Out-Null }
        $p.Refresh()
        $codigo = if ($termino) { $p.ExitCode } else { -2 }
        $p.Dispose()
        $registro = Join-Path $datos 'caos.log'
        $pasos = if (Test-Path -LiteralPath $registro) { @(Select-String -LiteralPath $registro -Pattern "^\d+`t[^ ]" -Encoding UTF8).Count } else { 0 }
        $informes = @(Get-ChildItem -LiteralPath (Join-Path $datos 'fallos') -File -ErrorAction SilentlyContinue)
        $relativos = @(Get-ChildItem -LiteralPath $actual -Recurse -File -Force -ErrorAction SilentlyContinue)
        $mal = -not $termino -or $codigo -ne 0 -or $informes.Count -gt 0 -or (Test-Path -LiteralPath $ErrorTxt) -or $relativos.Count -gt 0
        $segundos = [int]((Get-Date) - $inicio).TotalSeconds
        if ($mal) {
            $guardar = Join-Path $Resultados "semilla-$semilla"
            if (Test-Path -LiteralPath $guardar) { Remove-Item -LiteralPath $guardar -Recurse -Force }
            New-Item -ItemType Directory -Force -Path $guardar | Out-Null
            if (Test-Path -LiteralPath $registro) { Copy-Item -LiteralPath $registro -Destination $guardar }
            foreach ($i in $informes) { Copy-Item -LiteralPath $i.FullName -Destination $guardar }
            if (Test-Path -LiteralPath $ErrorTxt) { Move-Item -LiteralPath $ErrorTxt -Destination $guardar -Force }
            if ($relativos.Count -gt 0) { $relativos | ForEach-Object { $_.FullName.Substring($actual.Length) } | Set-Content -LiteralPath (Join-Path $guardar 'escritos-con-ruta-relativa.txt') -Encoding UTF8 }
            $causa = if ($relativos.Count -gt 0 -and $codigo -eq 0 -and $informes.Count -eq 0) { "escribió con ruta relativa: $($relativos[0].FullName.Substring($actual.Length))" } elseif (-not $termino) { 'no terminó a tiempo (cuelgue sin vigilante o cierre trabado)' } else { Causa $guardar $codigo (Join-Path $guardar 'caos.log') }
            $fallos += [pscustomobject]@{ Semilla = $semilla; Codigo = ('0x{0:X8}' -f $codigo); Pasos = $pasos; Causa = $causa; Carpeta = $guardar }
            Mal ("Semilla {0}: falló tras {1} pasos y {2} s: {3}" -f $semilla, $pasos, $segundos, $causa)
        } else {
            Bien ("Semilla {0}: {1} pasos en {2} s sin fallos." -f $semilla, $pasos, $segundos)
            if ($GuardarRegistros -and (Test-Path -LiteralPath $registro)) {
                New-Item -ItemType Directory -Force -Path $Resultados | Out-Null
                Copy-Item -LiteralPath $registro -Destination (Join-Path $Resultados "caos-$semilla.log") -Force
            }
        }
        for ($i = 0; $i -lt 5 -and (Test-Path -LiteralPath $carpeta); $i++) {
            try { Remove-Item -LiteralPath $carpeta -Recurse -Force -ErrorAction Stop } catch { Start-Sleep -Seconds 2 }
        }
    }
}
finally {
    foreach ($v in $variables) { [Environment]::SetEnvironmentVariable($v, $guardadas[$v]) }
    Descoordinar $linea
    if (-not $ConservarCorpus -and (Test-Path -LiteralPath $Corpus)) { Remove-Item -LiteralPath $Corpus -Recurse -Force -ErrorAction SilentlyContinue }
    if ((Test-Path -LiteralPath $Base) -and @(Get-ChildItem -LiteralPath $Base -Force).Count -eq 0) { Remove-Item -LiteralPath $Base -Force }
}

$rastro = Comparar-Rastro $antes (Foto-Rastro)
Titulo ("Resumen: {0} semillas × {1} min en {2:0} min" -f $Semillas.Count, $Minutos, $reloj.Elapsed.TotalMinutes)
if ($fallos.Count -gt 0) {
    $fallos | Format-Table -AutoSize -Wrap | Out-String -Width 220 | ForEach-Object { Nota $_ }
    Nota "Registros e informes en $Resultados"
} else {
    Bien 'Ninguna semilla falló.'
}
if ($rastro.Count -gt 0) {
    Mal 'Quedó rastro fuera de la carpeta del caos (puede ser de otra app; revísalo):'
    $rastro | Select-Object -First 30 | ForEach-Object { Nota "  $_" }
} else {
    Bien 'Sin rastro en LOCALAPPDATA\Rodaje, Documentos, Vídeos, Escritorio, %TEMP% ni HKCU.'
}
if ($fallos.Count -gt 0 -or $rastro.Count -gt 0) { exit 1 }
exit 0
