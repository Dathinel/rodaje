# Publica tests\Rodaje.AotSmoke con Native AOT en dist\prueba-aot\ y lo ejecuta.
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File herramientas\publicar-aot-prueba.ps1
# Requiere link.exe (Build Tools con C++). Sale con 0 solo si no hubo advertencias y la prueba pasó.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$Raiz = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'comun.psm1') -DisableNameChecking -Force
$E = Inicializar-Rodaje $Raiz -Fases 2
$Proyecto = Join-Path $Raiz 'tests\Rodaje.AotSmoke\Rodaje.AotSmoke.csproj'
$Salida = Join-Path $Raiz 'dist\prueba-aot'
$Exe = Join-Path $Salida 'Rodaje.AotSmoke.exe'
$Dotnet = $E.Dotnet
$MarcaSdk = $E.MarcaSdk
$Reloj = [System.Diagnostics.Stopwatch]::StartNew()

function Terminar([int]$codigo) {
    Write-Host ''
    Nota ("Tiempo total: " + (Duracion $Reloj.Elapsed))
    if ($script:Corrida) { Nota "Registro completo de la publicación: $script:Corrida" }
    Write-Host ''
    exit $codigo
}
function Es-De-Aot([string]$diagnostico) { return $diagnostico -match '(?i)\b(warning|advertencia|error)\s+IL\d+' }

Titulo 'Rodaje - prueba de humo del núcleo compilado con Native AOT'
Nota "Proyecto: $Raiz"

if (-not (Test-Path -LiteralPath $Dotnet) -or -not (Test-Path -LiteralPath $MarcaSdk)) {
    Write-Host ''
    if (Test-Path -LiteralPath $Dotnet) {
        Mal 'El SDK de .NET del proyecto está a medias (falta su marca de instalación completa), y así no es fiable.'
        Nota "Falta: $MarcaSdk"
    } else {
        Mal 'No encuentro el SDK de .NET del proyecto, y sin él no se puede compilar nada.'
        Nota "Lo busqué en: $Dotnet"
    }
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

Usar-DotnetLocal $E
$instaladorVs = Agregar-Vswhere
$script:Corrida = Nueva-Corrida 'aot-' 5

# Sin -r: el RID ya está en el .csproj; como propiedad global llegaría a Rodaje.Core y su
# packages.lock.json cambiaría en cada corrida.
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
        Nota 'tests\Rodaje.AotSmoke --force-evaluate y revisa el cambio.'
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
