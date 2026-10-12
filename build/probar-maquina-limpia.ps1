# Prueba la distribución ya armada en una máquina que no es la de desarrollo (la usa .github\workflows\distribucion.yml
# en windows-latest: sin GPU, sin audio, Windows en inglés). Necesita solo los artefactos de build\distribuir.ps1 y
# esta carpeta build\; nada del SDK. Deja en -Salida el registro, el entorno, las cifras de arranque y las capturas.
#   limpia  instalador en silencio + --autoprueba + lanzador + desinstalar (build\probar-instalador.ps1); portable
#           descomprimido y abierto por el lanzador; arranque en caliente y en frío.
#   hostil  lo mismo con %LOCALAPPDATA% y %TEMP% en una ruta con espacios y tildes (instala en la carpeta por defecto),
#           la escala al 150 %, sin conexión para los exe de Rodaje (Complementos debe abrir igual) y un proyecto
#           .rodaje con su material en otra unidad.
# Uso:
#   powershell -NoProfile -ExecutionPolicy Bypass -File build\probar-maquina-limpia.ps1 -Distribucion dist\distribucion
#       [-Salida carpeta] [-Variante limpia|hostil] [-Veces 5]
# La variante hostil cambia la configuración del usuario (carpetas, escala, cortafuegos): solo en una máquina desechable.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Distribucion,
    [string]$Salida = '',
    [ValidateSet('limpia', 'hostil')][string]$Variante = 'limpia',
    [int]$Veces = 5
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$Raiz = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'comun.psm1') -DisableNameChecking -Force
$E = Inicializar-Rodaje $Raiz
if (-not $Salida) { $Salida = Join-Path $Raiz "TestResults\maquina-$Variante" }
New-Item -ItemType Directory -Force -Path $Salida | Out-Null
$Salida = (Resolve-Path -LiteralPath $Salida).Path
$Distribucion = (Resolve-Path -LiteralPath $Distribucion).Path
$Utf8 = New-Object System.Text.UTF8Encoding($false)
$fallos = New-Object System.Collections.Generic.List[string]
$resumen = New-Object System.Collections.Generic.List[string]
function Comprobar([bool]$ok, [string]$texto) { if ($ok) { Bien $texto } else { Mal $texto; $fallos.Add($texto) }; $resumen.Add("- $(if ($ok) { 'bien' } else { '**FALLA**' }): $texto") }
function Anotar([string]$texto) { Nota $texto; $resumen.Add("- $texto") }

$instalador = Get-ChildItem -LiteralPath $Distribucion -Filter 'Rodaje-*-instalador.exe' | Select-Object -First 1
$portable = Get-ChildItem -LiteralPath $Distribucion -Filter 'Rodaje-*-portable.zip' | Select-Object -First 1
if (-not $instalador -or -not $portable) { throw "En $Distribucion faltan el instalador o el portable." }

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Pantalla
{
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(long pt, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr m, int tipo, out uint x, out uint y);
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint accion, int param, IntPtr p, uint ini);
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint accion, uint param, out int p, uint ini);
    public static void ConscienteDeDpi() { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
    public static uint Dpi() { uint x, y; return GetDpiForMonitor(MonitorFromPoint(0, 1), 0, out x, out y) == 0 ? x : 0; }
    // SPI_GETLOGICALDPIOVERRIDE / SPI_SETLOGICALDPIOVERRIDE: lo que mueve Configuración > Pantalla > Escala, en pasos
    // relativos a la escala recomendada (100, 125, 150, 175, 200...).
    public static int Paso() { int v; SystemParametersInfo(0x009E, 0, out v, 0); return v; }
    public static bool PonerPaso(int relativo) { return SystemParametersInfo(0x009F, relativo, IntPtr.Zero, 1); }
}
'@
[Pantalla]::ConscienteDeDpi()

function Captura([string]$nombre) {
    $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try { $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size); $bmp.Save((Join-Path $Salida "$nombre.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    catch { Aviso "No se pudo capturar la pantalla: $($_.Exception.Message)" }
    finally { $g.Dispose(); $bmp.Dispose() }
}

Titulo "Rodaje en una máquina limpia ($Variante)"
$sistema = Get-CimInstance Win32_OperatingSystem
$video = @(Get-CimInstance Win32_VideoController | ForEach-Object { "$($_.Name) ($($_.CurrentHorizontalResolution)x$($_.CurrentVerticalResolution))" }) -join '; '
$audio = @(Get-PnpDevice -Class AudioEndpoint -PresentOnly -ErrorAction SilentlyContinue | ForEach-Object { $_.FriendlyName })
$cultura = Get-Culture

# --- Variante hostil: carpetas del usuario con espacios y tildes, escala 150 %, sin red, proyecto en otra unidad ---
$trabajo = Join-Path ([System.IO.Path]::GetTempPath()) "rodaje-maquina-$Variante"
$localOriginal = $env:LOCALAPPDATA
if ($Variante -eq 'hostil') {
    $usuario = Join-Path $env:SystemDrive 'Usuarios de prueba\José Núñez Peña'
    $local = Join-Path $usuario 'AppData\Local'
    $temp = Join-Path $local 'Temp'
    New-Item -ItemType Directory -Force -Path $temp | Out-Null
    # La carpeta conocida LocalAppData sale de «User Shell Folders» (SHGetKnownFolderPath la lee en cada proceso nuevo);
    # %LOCALAPPDATA% y %TEMP% se cambian para este proceso y sus hijos.
    Set-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders' -Name 'Local AppData' -Value $local
    $env:LOCALAPPDATA = $local; $env:TEMP = $temp; $env:TMP = $temp
    $trabajo = Join-Path $temp 'Pruebas de Rodaje ñ'
    Anotar "LocalAppData y TEMP en $local"

    $antes = [Pantalla]::Dpi()
    $resolucion = 'sin cambiar'
    if (Get-Command Set-DisplayResolution -ErrorAction SilentlyContinue) {
        try { Set-DisplayResolution -Width 1920 -Height 1080 -Force -ErrorAction Stop *> $null; $resolucion = '1920x1080' } catch { $resolucion = "no se pudo ($($_.Exception.Message))" }
    }
    $ok = $false
    for ($paso = 2; $paso -le 4 -and -not $ok; $paso++) {
        [void][Pantalla]::PonerPaso($paso)
        Start-Sleep -Milliseconds 800
        $ok = [Pantalla]::Dpi() -ge 144
    }
    $dpi = [Pantalla]::Dpi()
    Anotar ("Escala: {0} % antes, {1} % ahora (resolución {2})" -f [math]::Round($antes / 0.96), [math]::Round($dpi / 0.96), $resolucion)
    if ($dpi -lt 144) { Aviso 'No se llegó al 150 %: la escala queda como estaba.' }

    # Otra unidad: la del área de trabajo de la CI (D:) si no es la del sistema; si no, una letra con subst.
    $otra = Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Root -ne "$env:SystemDrive\" -and $_.Free -gt 1GB } | Select-Object -First 1
    if ($otra) { $unidad = $otra.Root } else {
        $base = Join-Path $trabajo 'unidad'; New-Item -ItemType Directory -Force -Path $base | Out-Null
        & subst.exe R: $base; $unidad = 'R:\'
    }
    $carpetaProyecto = Join-Path $unidad 'Proyectos de prueba\Boda de Ñuño'
    Anotar "Proyecto en otra unidad: $carpetaProyecto"
}
New-Item -ItemType Directory -Force -Path $trabajo | Out-Null

$lineas = @(
    "Sistema: $($sistema.Caption) $($sistema.Version) ($($sistema.OSArchitecture))"
    "Cultura: $($cultura.Name) / interfaz $((Get-UICulture).Name); decimal «$($cultura.NumberFormat.NumberDecimalSeparator)», fecha $($cultura.DateTimeFormat.ShortDatePattern)"
    "Video: $video"
    "Pantalla: $([System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width)x$([System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height), escala $([math]::Round([Pantalla]::Dpi() / 0.96)) %"
    "Audio: $(if ($audio.Count) { $audio -join '; ' } else { 'ningún dispositivo' })"
    "Procesador: $((Get-CimInstance Win32_Processor | Select-Object -First 1).Name), $([math]::Round($sistema.TotalVisibleMemorySize / 1MB, 1)) GB"
    "LOCALAPPDATA: $env:LOCALAPPDATA"
    "PATH: $env:PATH"
)
[System.IO.File]::WriteAllLines((Join-Path $Salida 'entorno.txt'), [string[]]$lineas, $Utf8)
$lineas | Select-Object -SkipLast 1 | ForEach-Object { Nota $_ }
$resumen.Add("- Entorno: $($sistema.Caption) $($sistema.Version), $($cultura.Name), $video, $(if ($audio.Count) { "$($audio.Count) dispositivo(s) de audio" } else { 'sin audio' })")

# --- Portable: descomprimir en una ruta con espacios y tildes ---
$carpetaPortable = Join-Path $trabajo 'Portable de Rodaje á'
if (Test-Path -LiteralPath $carpetaPortable) { Remove-Item -LiteralPath $carpetaPortable -Recurse -Force }
Expand-Archive -LiteralPath $portable.FullName -DestinationPath $carpetaPortable
$lanzador = Join-Path $carpetaPortable 'Rodaje\Rodaje.exe'
$appPortable = Join-Path $carpetaPortable 'Rodaje\app\Rodaje.exe'
Comprobar ((Test-Path -LiteralPath $lanzador) -and (Test-Path -LiteralPath $appPortable)) "Portable descomprimido ($([math]::Round($portable.Length / 1MB, 1)) MB)"

if ($Variante -eq 'hostil') {
    # El instalador elige {userpf}: la carpeta Programs bajo la LocalAppData nueva o, si Windows no la sigue, la de siempre.
    $exes = @($lanzador, $appPortable)
    foreach ($instalado in (Join-Path $env:LOCALAPPDATA 'Programs\Rodaje'), (Join-Path $localOriginal 'Programs\Rodaje')) { $exes += (Join-Path $instalado 'Rodaje.exe'), (Join-Path $instalado 'app\Rodaje.exe') }
    foreach ($exe in $exes) {
        New-NetFirewallRule -DisplayName "Rodaje sin red $([guid]::NewGuid().ToString('N').Substring(0, 6))" -Direction Outbound -Action Block -Program $exe -Profile Any | Out-Null
    }
    Anotar 'Cortafuegos: salida bloqueada para los Rodaje.exe instalados y portables'
}

# --- Material de prueba hecho con el ffmpeg LGPL que trae Rodaje (OpenH264 + AAC) ---
$ffmpeg = Join-Path $carpetaPortable 'Rodaje\app\ffmpeg\ffmpeg.exe'
function Generar([string]$ruta, [int]$ancho, [int]$alto, [string]$fps, [int]$segundos) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $ruta) | Out-Null
    $anterior = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    & $ffmpeg -hide_banner -loglevel error -y -f lavfi -i "testsrc2=size=${ancho}x${alto}:rate=$fps" -f lavfi -i "sine=frequency=440:sample_rate=48000" `
        -t $segundos -c:v libopenh264 -b:v 8M -g 48 -pix_fmt yuv420p -c:a aac -ac 2 -b:a 192k -movflags +faststart $ruta 2>&1 | ForEach-Object { Nota "  ffmpeg: $_" }
    $ErrorActionPreference = $anterior
    if (-not (Test-Path -LiteralPath $ruta)) { throw "ffmpeg no pudo generar $ruta" }
}
if ($Variante -eq 'hostil') {
    # El ejemplo del formato 5 del repositorio (títulos y un clip de material/toma 01.mp4, 1080p a 30 fps, 14,1 s).
    $abrir = Join-Path $carpetaProyecto 'proyecto de prueba.rodaje'
    Generar (Join-Path $carpetaProyecto 'material\toma 01.mp4') 1920 1080 '30' 15
    # Sus títulos usan Inter, que Windows no trae: sin la fuente la exportación pide elegir la de respaldo, y la
    # autoprueba no elige; se cambia por Segoe UI.
    $json = [System.IO.File]::ReadAllText((Join-Path $Raiz 'tests\Rodaje.Core.Tests\Serialization\ejemplos\proyecto-v5.rodaje'))
    [System.IO.File]::WriteAllText($abrir, $json.Replace('"family": "Inter"', '"family": "Segoe UI"'), $Utf8)
} else {
    $abrir = Join-Path $trabajo 'Material de prueba\h264-1080p-23976-aac-ci.mp4'
    Generar $abrir 1920 1080 '24000/1001' 30
}
Anotar "La autoprueba abre $abrir"

# --- Instalador: silencio, archivos, accesos, asociaciones, --autoprueba, lanzador y desinstalación ---
$env:RODAJE_AUTOPRUEBA_CAPTURAS = Join-Path $Salida 'capturas-autoprueba'
$argumentos = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'probar-instalador.ps1'),
    '-Instalador', $instalador.FullName, '-Abrir', $abrir, '-Registro', (Join-Path $Salida 'instalador'))
if ($Variante -eq 'hostil') { $argumentos += '-CarpetaPorDefecto' }
$reloj = [System.Diagnostics.Stopwatch]::StartNew()
& powershell @argumentos
Comprobar ($LASTEXITCODE -eq 0) ("Instalador, autoprueba del exe instalado y desinstalación ({0:0} s; detalle en el registro del paso)" -f $reloj.Elapsed.TotalSeconds)
Remove-Item Env:RODAJE_AUTOPRUEBA_CAPTURAS
Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA 'Programs\Rodaje'), (Join-Path ([System.IO.Path]::GetTempPath()) 'Rodaje prueba*') -Recurse -Filter 'rodaje-error.txt' -ErrorAction SilentlyContinue |
    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $Salida 'rodaje-error-instalado.txt') -Force }

# --- Portable: el lanzador abre la app, oculta app\ y la app responde ---
$registroArranque = Join-Path $Salida 'portable-arranque.txt'
$l = Start-Process -FilePath $lanzador -ArgumentList '--arranque', '--registro', "`"$registroArranque`"" -PassThru -Wait
for ($i = 0; $i -lt 120 -and -not (Select-String -LiteralPath $registroArranque -Pattern 'interfaz_usable' -Quiet -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 250 }
Comprobar ($l.ExitCode -eq 0 -and (Select-String -LiteralPath $registroArranque -Pattern 'interfaz_usable' -Quiet -ErrorAction SilentlyContinue)) 'El lanzador del portable abrió la app y la interfaz respondió'
Comprobar ([bool]((Get-Item -LiteralPath (Join-Path $carpetaPortable 'Rodaje\app') -Force).Attributes -band [IO.FileAttributes]::Hidden)) 'El lanzador ocultó app\ en el primer arranque'
Get-Process -Name Rodaje -ErrorAction SilentlyContinue | ForEach-Object { if (-not $_.WaitForExit(30000)) { $_.Kill() } }

$errorPortable = Join-Path $carpetaPortable 'Rodaje\app\rodaje-error.txt'
Remove-Item -LiteralPath $errorPortable -ErrorAction SilentlyContinue
$env:RODAJE_AUTOPRUEBA_CAPTURAS = Join-Path $Salida 'capturas-portable'
$reloj.Restart()
$a = Start-Process -FilePath $appPortable -ArgumentList '--autoprueba', "`"$abrir`"" -PassThru
$termino = $a.WaitForExit(180000)
if (-not $termino) { Captura 'portable-autoprueba-colgada'; $a.Kill() }
Remove-Item Env:RODAJE_AUTOPRUEBA_CAPTURAS
Comprobar ($termino -and $a.ExitCode -eq 0 -and -not (Test-Path -LiteralPath $errorPortable)) ("Autoprueba del portable: código {0} en {1:0.0} s" -f $(if ($termino) { $a.ExitCode } else { 'sin terminar' }), $reloj.Elapsed.TotalSeconds)
if (Test-Path -LiteralPath $errorPortable) {
    Copy-Item -LiteralPath $errorPortable -Destination (Join-Path $Salida 'rodaje-error-portable.txt')
    Get-Content -LiteralPath $errorPortable -Encoding UTF8 | Select-Object -First 15 | ForEach-Object { Nota "  $_" }
    Remove-Item -LiteralPath $errorPortable
}

# --- Captura de la ventana abierta a mano (sin argumentos, como un doble clic) ---
$v = Start-Process -FilePath $lanzador -PassThru
Start-Sleep -Seconds 8
Captura 'portable-ventana'
$abierta = Get-Process -Name Rodaje -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $appPortable }
Comprobar ([bool]$abierta) 'La app abierta con doble clic sigue en pie a los 8 s'
$abierta | ForEach-Object { [void]$_.CloseMainWindow(); if (-not $_.WaitForExit(15000)) { $_.Kill() } }
if (Test-Path -LiteralPath $errorPortable) { Comprobar $false 'Abrir y cerrar a mano dejó rodaje-error.txt'; Copy-Item -LiteralPath $errorPortable -Destination (Join-Path $Salida 'rodaje-error-ventana.txt') -Force }

# --- Arranque en caliente y en frío (por el lanzador, como el doble clic) ---
foreach ($modo in 'caliente', 'frio') {
    $registroModo = Join-Path $Salida "arranque-$modo.txt"
    $argumentos = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$(Join-Path $PSScriptRoot 'medir-arranque.ps1')`"", '-Exe', "`"$lanzador`"",
        '-Veces', $Veces, '-Etiqueta', "`"portable $Variante`"")
    if ($modo -eq 'frio') { $argumentos += '-Frio' }
    $m = Start-Process -FilePath powershell -ArgumentList $argumentos -RedirectStandardOutput $registroModo -RedirectStandardError "$registroModo.err" -NoNewWindow -PassThru -Wait
    $codigo = $m.ExitCode
    $texto = @(Get-Content -LiteralPath $registroModo, "$registroModo.err" -Encoding UTF8 -ErrorAction SilentlyContinue)
    $texto | ForEach-Object { Nota $_ }
    $cifras = @($texto | Where-Object { $_ -match '^\s+(visible|interfaz_completa|interfaz_usable|primer_render)\s' } | ForEach-Object { $_.Trim() -replace '\s+', ' ' })
    Comprobar ($codigo -eq 0) "Arranque en $modo, $Veces vueltas (mediana mín máx, ms): $($cifras -join '; ')"
}

# --- Lo que dejó la app en %LOCALAPPDATA%\Rodaje (ajustes, avisos) ---
$datos = Join-Path $env:LOCALAPPDATA 'Rodaje'
if (Test-Path -LiteralPath $datos) {
    $copia = Join-Path $Salida 'datos-usuario'
    New-Item -ItemType Directory -Force -Path $copia | Out-Null
    Get-ChildItem -LiteralPath $datos -Recurse -File -Include '*.txt', '*.json', '*.log' -ErrorAction SilentlyContinue | Where-Object { $_.Length -lt 1MB } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $copia $_.Name) -Force }
}

$titulo = "Rodaje en una máquina limpia ($Variante): $(if ($fallos.Count) { "$($fallos.Count) falla(s)" } else { 'todo bien' })"
$md = @("### $titulo", '') + $resumen
[System.IO.File]::WriteAllLines((Join-Path $Salida 'resumen.md'), [string[]]$md, $Utf8)
if ($env:GITHUB_STEP_SUMMARY) { [System.IO.File]::AppendAllLines($env:GITHUB_STEP_SUMMARY, [string[]]($md + ''), $Utf8) }
Titulo $titulo
exit ([int]($fallos.Count -gt 0))
