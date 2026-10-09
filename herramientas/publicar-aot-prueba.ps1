# Publica la prueba de humo de Native AOT (tests\FotoToma.AotSmoke) y la ejecuta, para comprobar que
# el núcleo FotoToma.Core funciona compilado de antemano a código nativo, que es como se va a
# entregar el editor. Explica el resultado en español sencillo: si hubo advertencias de recorte o de
# AOT (con archivo y línea), cuánto pesa el .exe y cuánto tardó en arrancar y terminar.
#
# Uso:
#   powershell -NoProfile -ExecutionPolicy Bypass -File herramientas\publicar-aot-prueba.ps1
#
# El ejecutable queda en dist\prueba-aot\ (git lo ignora). El entorno (SDK y caché de NuGet) está en
# <repo>\.entorno, salvo que la variable de entorno FOTOTOMA_ENTORNO apunte a otra carpeta (útil
# para worktrees de git que comparten el entorno de la copia principal).
#
# Native AOT necesita el enlazador de C++ de Microsoft (link.exe), que viene con Visual Studio Build
# Tools y la carga de trabajo "Desarrollo para el escritorio con C++". El SDK lo encuentra solo.
#
# Código de salida: 0 si publicó sin advertencias y la prueba pasó, 1 si algo falló.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$Raiz = Split-Path -Parent $PSScriptRoot
$Proyecto = Join-Path $Raiz 'tests\FotoToma.AotSmoke\FotoToma.AotSmoke.csproj'
$Salida = Join-Path $Raiz 'dist\prueba-aot'
$Exe = Join-Path $Salida 'FotoToma.AotSmoke.exe'
if ($env:FOTOTOMA_ENTORNO) { $Entorno = $env:FOTOTOMA_ENTORNO } else { $Entorno = Join-Path $Raiz '.entorno' }
$DotnetDir = Join-Path $Entorno 'dotnet'
$Dotnet = Join-Path $DotnetDir 'dotnet.exe'
$Reloj = [System.Diagnostics.Stopwatch]::StartNew()
$TotalFases = 2

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
    if ($script:Corrida) { Nota "Registro completo de la publicación: $script:Corrida" }
    Write-Host ''
    exit $codigo
}
function Corto([string]$texto) {
    return $texto.Replace($Raiz + '\', '').Replace($Raiz, '.')
}

# Corre dotnet con el entorno propio, guarda toda la salida en un .log y la devuelve.
function Invocar-Dotnet([string]$log, [string[]]$argumentos) {
    $lineas = New-Object System.Collections.Generic.List[string]
    $anterior = $ErrorActionPreference
    # En Windows PowerShell 5.1, lo que un programa escribe en stderr llega como ErrorRecord;
    # con 'Stop' eso cortaría el script. Aquí se trata como texto normal.
    $ErrorActionPreference = 'Continue'
    try {
        & $Dotnet @argumentos 2>&1 | ForEach-Object { $lineas.Add("$_") }
        $codigo = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $anterior
    }
    [System.IO.File]::WriteAllLines($log, $lineas, (New-Object System.Text.UTF8Encoding($false)))
    return [pscustomobject]@{ Codigo = $codigo; Lineas = $lineas }
}

# Diagnósticos de MSBuild/compilador/ILC ("archivo(12,5): warning IL2026: ...") sin repetir: MSBuild
# los imprime dos veces (en el momento y en el resumen final). La palabra clave puede venir en inglés
# (formato canónico de MSBuild) o traducida.
function Diagnosticos-De([System.Collections.Generic.List[string]]$lineas, [string]$tipo) {
    $vistos = @{}
    $lista = New-Object System.Collections.Generic.List[string]
    $patron = if ($tipo -eq 'error') { '(?i)\berror\s+[A-Z]{2,}\d+\s*:' } else { '(?i)\b(warning|advertencia)\s+[A-Z]{2,}\d+\s*:' }
    foreach ($l in $lineas) {
        if ($l -match $patron) {
            $limpia = ($l -replace '\s+\[[^\]]+\.(cs|fs|vb)proj[^\]]*\]\s*$', '').Trim()
            if (-not $vistos.ContainsKey($limpia)) { $vistos[$limpia] = $true; $lista.Add($limpia) }
        }
    }
    return ,$lista
}

# Los avisos de recorte (IL2xxx), de AOT (IL3xxx) y de archivo único (IL3000-IL3003) empiezan por IL.
function Es-De-Aot([string]$diagnostico) { return $diagnostico -match '(?i)\b(warning|advertencia|error)\s+IL\d+' }

Titulo 'FotoToma - prueba de humo del núcleo compilado con Native AOT'
Nota "Proyecto: $Raiz"

# --- Comprobaciones previas -------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $Dotnet)) {
    Write-Host ''
    Mal 'No encuentro el SDK de .NET del proyecto, y sin él no se puede compilar nada.'
    Nota "Lo busqué en: $Dotnet"
    Nota ''
    Nota 'Cómo arreglarlo (una sola vez, descarga unos 280 MB):'
    Nota '  - Haz doble clic en ABRIR.bat, que lo prepara solo, o'
    Nota '  - corre: powershell -NoProfile -ExecutionPolicy Bypass -File herramientas\preparar-entorno.ps1'
    Terminar 1
}
if (-not (Test-Path -LiteralPath $Proyecto)) {
    Mal "No encuentro el proyecto de la prueba: $Proyecto. ¿Se movió este script fuera de la carpeta herramientas?"
    Terminar 1
}

# Las mismas variables que fija herramientas\dotnet-local.cmd: nada global, nada de telemetría.
$env:DOTNET_ROOT = $DotnetDir
$env:NUGET_PACKAGES = Join-Path $Entorno 'nuget'
$env:DOTNET_CLI_HOME = $Entorno
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_FORCE_UTF8_ENCODING = '1'
$env:PATH = "$DotnetDir;$env:PATH"

# Para hallar link.exe, el compilador AOT llama a vcvarsall.bat de Visual Studio, y este (VsDevCmd.bat)
# hace "pushd" a la carpeta del instalador y corre "vswhere.exe" contando con que Windows lo busque en
# la carpeta actual. Si la variable NoDefaultCurrentDirectoryInExePath está puesta (algunas terminales
# y herramientas la ponen por seguridad), no lo encuentra, el error se cuela en la ruta del enlazador y
# la publicación falla con "vswhere.exe no se reconoce". Poner esa carpeta en el PATH de este proceso lo
# evita sin tocar nada del sistema.
$instaladorVs = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (Test-Path -LiteralPath (Join-Path $instaladorVs 'vswhere.exe')) { $env:PATH = "$env:PATH;$instaladorVs" }

# Registro de esta corrida, junto a los de las pruebas (se guardan los 5 últimos de AOT).
$Resultados = Join-Path $Raiz 'TestResults'
New-Item -ItemType Directory -Force -Path $Resultados | Out-Null
$script:Corrida = Join-Path $Resultados ('aot-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force -Path $script:Corrida | Out-Null
Get-ChildItem -LiteralPath $Resultados -Directory | Where-Object { $_.Name -match '^aot-\d{8}-\d{6}$' } |
    Sort-Object Name -Descending | Select-Object -Skip 5 |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }

# --- Fase 1: publicar con Native AOT ------------------------------------------------------------
# El RID (win-x64) ya está fijado en el .csproj. No se pasa -r en la línea de comandos porque así se
# volvería una propiedad global que llega también a FotoToma.Core, y su packages.lock.json ganaría una
# sección win-x64 que la compilación normal vuelve a quitar: el archivo cambiaría en cada corrida.
Fase 1 'compilando el núcleo y la prueba a código nativo (Native AOT, puede tardar un minuto)...'
if (Test-Path -LiteralPath $Salida) { Remove-Item -LiteralPath $Salida -Recurse -Force }
$r = Invocar-Dotnet (Join-Path $script:Corrida 'publicar.log') @(
    'publish', $Proyecto, '-c', 'Release', '-o', $Salida, '--nologo')

$errores = Diagnosticos-De $r.Lineas 'error'
$avisos = Diagnosticos-De $r.Lineas 'warning'
# Con TreatWarningsAsErrors, una advertencia de AOT llega como "error IL...": cuenta igual.
$deAot = @(@($errores) + @($avisos) | Where-Object { Es-De-Aot $_ })

if ($deAot.Count -gt 0) {
    Mal "El código (el núcleo o la prueba) no es del todo compatible con Native AOT: hubo $($deAot.Count) aviso(s) de recorte o de AOT."
    Nota 'Cada aviso dice en qué archivo y línea hay código que el compilador nativo no puede garantizar'
    Nota '(casi siempre reflexión o JSON sin el generador de código). Son estos:'
    Nota ''
    $i = 0
    foreach ($d in $deAot) {
        $i++
        if ($i -gt 25) { Nota "  ... y $($deAot.Count - 25) más (todos están en publicar.log)."; break }
        Nota ("  {0}. {1}" -f $i, (Corto $d))
    }
    Nota ''
    Nota 'IL2026/IL2104: reflexión que el recorte puede romper. IL3050/IL3051: código que se genera en tiempo'
    Nota 'de ejecución y no existe en Native AOT. La solución suele ser usar el contexto de JSON generado.'
    Terminar 1
}

if ($r.Codigo -ne 0 -or -not (Test-Path -LiteralPath $Exe)) {
    Mal 'No se pudo publicar la prueba con Native AOT.'
    $texto = ((@($errores) + @($r.Lineas)) -join "`n")
    if ($texto -match '(?i)vswhere\.exe.{0,10}(no se reconoce|is not recognized)') {
        Nota 'Visual Studio Build Tools está, pero su script vcvarsall.bat no pudo correr vswhere.exe, así que la'
        Nota 'ruta del enlazador (link.exe) llegó dañada. Comprueba que exista:'
        Nota "  $instaladorVs\vswhere.exe"
        Nota 'y, si existe, abre una consola nueva: alguna variable de entorno (NoDefaultCurrentDirectoryInExePath)'
        Nota 'impide correr programas desde la carpeta actual.'
    } elseif ($texto -match '(?i)link\.exe|vswhere|Platform linker|MSVC|desktop development with C\+\+|Desarrollo para el escritorio') {
        Nota 'Native AOT necesita el enlazador de C++ de Microsoft y no lo encontró. Instala (o repara) Visual'
        Nota 'Studio Build Tools 2022 con la carga de trabajo "Desarrollo para el escritorio con C++":'
        Nota '  winget install Microsoft.VisualStudio.2022.BuildTools --override "--add Microsoft.VisualStudio.Workload.VCTools --includeRecommended --passive"'
    } elseif ($texto -match 'NU1301|NU1101|NU1102|service index|No such host|unable to load') {
        Nota 'Parece un problema de conexión: la primera vez hay que descargar el compilador AOT (unos 50 MB).'
    } elseif ($texto -match 'NU1004|NU1403|lock file|packages.lock.json') {
        Nota 'Los paquetes no coinciden con packages.lock.json. Corre herramientas\dotnet-local.cmd restore'
        Nota 'tests\FotoToma.AotSmoke --force-evaluate y revisa el cambio.'
    }
    if ($errores.Count -gt 0) {
        Nota ''; Nota 'Primer error:'; Nota ('  ' + (Corto $errores[0]))
        if ($errores.Count -gt 1) { Nota "  (y $($errores.Count - 1) error(es) más; están todos en publicar.log)" }
    } else {
        $r.Lineas | Select-Object -Last 15 | ForEach-Object { Nota ('  ' + (Corto $_)) }
    }
    Terminar 1
}

if ($avisos.Count -gt 0) {
    Mal "La publicación dejó $($avisos.Count) advertencia(s), y el proyecto exige cero:"
    $avisos | Select-Object -First 15 | ForEach-Object { Nota ('  ' + (Corto $_)) }
    Terminar 1
}

$tamExe = (Get-Item -LiteralPath $Exe).Length
$tamCarpeta = (Get-ChildItem -LiteralPath $Salida -File -Recurse | Where-Object { $_.Extension -ne '.pdb' } |
    Measure-Object -Property Length -Sum).Sum
Bien 'Publicado sin advertencias de recorte ni de AOT.'
Nota ("Ejecutable: {0} ({1:0.00} MB)" -f (Corto $Exe), ($tamExe / 1MB))
if ($tamCarpeta -gt $tamExe) {
    Nota ("Carpeta completa sin símbolos de depuración (.pdb): {0:0.00} MB" -f ($tamCarpeta / 1MB))
}

# --- Fase 2: ejecutar el .exe nativo ------------------------------------------------------------
Fase 2 'ejecutando el programa nativo (no usa el SDK ni el runtime de .NET instalados)...'
Write-Host ''
$salidaExe = New-Object System.Collections.Generic.List[string]
$cronometro = [System.Diagnostics.Stopwatch]::StartNew()
$anterior = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    # Sin DOTNET_ROOT: un .exe de Native AOT no debe necesitar nada de .NET para arrancar.
    $raizDotnet = $env:DOTNET_ROOT
    Remove-Item Env:DOTNET_ROOT -ErrorAction SilentlyContinue
    & $Exe 2>&1 | ForEach-Object { $l = "$_"; $salidaExe.Add($l); Write-Host "    $l" }
    $codigoExe = $LASTEXITCODE
} finally {
    $ErrorActionPreference = $anterior
    $env:DOTNET_ROOT = $raizDotnet
}
$cronometro.Stop()
[System.IO.File]::WriteAllLines((Join-Path $script:Corrida 'ejecutar.log'), $salidaExe, (New-Object System.Text.UTF8Encoding($false)))

Titulo 'Resumen'
Nota ("Tamaño del ejecutable: {0:0.00} MB, un solo archivo que funciona sin instalar .NET." -f ($tamExe / 1MB))
Nota ("Arranque + prueba completa: {0:0} ms (medido desde fuera del proceso)." -f $cronometro.Elapsed.TotalMilliseconds)
$interno = $salidaExe | Where-Object { $_ -match '\((\d+) ms\)' } | Select-Object -Last 1
if ($interno -and $interno -match '\((\d+) ms\)') { Nota "  De eso, la prueba en sí tardó $($Matches[1]) ms; el resto es arrancar y cerrar el proceso." }

Write-Host ''
if ($codigoExe -ne 0) {
    Mal "RESULTADO: el programa nativo terminó con error (código $codigoExe). Arriba está el paso que falló y por qué."
    Nota 'Si las pruebas normales (ABRIR.bat) pasan pero esto falla, el núcleo usa algo que Native AOT recorta.'
    Terminar 1
}
if ($salidaExe -match 'Modo: JIT') {
    Mal 'RESULTADO: el ejecutable corrió con JIT, así que no se probó Native AOT. Revisa PublishAot en el .csproj.'
    Terminar 1
}
Bien 'RESULTADO: el núcleo funciona compilado con Native AOT.'
Terminar 0
