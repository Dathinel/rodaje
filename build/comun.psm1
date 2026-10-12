# Lo que comparten probar.ps1, aceptacion.ps1, publicar-aot-prueba.ps1 y app.ps1: dónde está el entorno
# (.entorno o RODAJE_ENTORNO) y sus marcas de instalación completa, las variables del SDK local, la carpeta de
# vswhere.exe, la llamada a dotnet con registro, los diagnósticos de MSBuild, las carpetas de cada corrida y la
# lectura de los .trx. Se importa con:
#   Import-Module (Join-Path $PSScriptRoot 'comun.psm1') -DisableNameChecking -Force
# (-DisableNameChecking: los nombres van en español y PowerShell avisaría de verbos no aprobados).

$script:Raiz = ''
$script:Dotnet = ''
$script:TotalFases = 0

# Lee global.json y preparar-entorno.ps1 y devuelve las rutas del entorno. Listo = existen las marcas
# .sdk-<versión> y .ffmpeg-<versión> de preparar-entorno.ps1; dotnet.exe no basta tras un apagón.
function Inicializar-Rodaje([string]$Raiz, [int]$Fases = 0) {
    $script:Raiz = $Raiz
    $script:TotalFases = $Fases
    if ($env:RODAJE_ENTORNO) { $entorno = $env:RODAJE_ENTORNO } else { $entorno = Join-Path $Raiz '.entorno' }
    $dotnetDir = Join-Path $entorno 'dotnet'
    $script:Dotnet = Join-Path $dotnetDir 'dotnet.exe'
    try { $sdk = (Get-Content -LiteralPath (Join-Path $Raiz 'global.json') -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json).sdk.version } catch { $sdk = '' }
    $ffmpeg = ''
    $linea = Select-String -LiteralPath (Join-Path $PSScriptRoot 'preparar-entorno.ps1') -Pattern "^\`$FfmpegVersion = '([^']+)'" -ErrorAction Stop | Select-Object -First 1
    if ($linea) { $ffmpeg = $linea.Matches[0].Groups[1].Value }
    return [pscustomobject]@{
        Entorno = $entorno
        DotnetDir = $dotnetDir
        Dotnet = $script:Dotnet
        SdkVersion = $sdk
        MarcaSdk = Join-Path $dotnetDir ".sdk-$sdk"
        FfmpegVersion = $ffmpeg
        MarcaFfmpeg = Join-Path $entorno "ffmpeg\.ffmpeg-$ffmpeg"
        FfmpegBin = Join-Path $entorno 'ffmpeg\bin'
    }
}

# Las mismas variables que build\dotnet-local.cmd.
function Usar-DotnetLocal($E) {
    $env:DOTNET_ROOT = $E.DotnetDir
    $env:NUGET_PACKAGES = Join-Path $E.Entorno 'nuget'
    $env:DOTNET_CLI_HOME = $E.Entorno
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_CLI_FORCE_UTF8_ENCODING = '1'
    $env:PATH = "$($E.DotnetDir);$env:PATH"
}

# VsDevCmd.bat corre vswhere.exe desde la carpeta actual; con NoDefaultCurrentDirectoryInExePath
# puesto falla y daña la ruta de link.exe. Añadir la carpeta al PATH del proceso lo evita. Devuelve la carpeta.
function Agregar-Vswhere {
    $instaladorVs = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
    if (Test-Path -LiteralPath (Join-Path $instaladorVs 'vswhere.exe')) { $env:PATH = "$env:PATH;$instaladorVs" }
    return $instaladorVs
}

function Titulo([string]$texto) { Write-Host ''; Write-Host "  $texto" -ForegroundColor Cyan }
function Fase([int]$n, [string]$texto) { Write-Host ''; Write-Host "  Fase $n/$($script:TotalFases): $texto" -ForegroundColor Cyan }
function Bien([string]$texto) { Write-Host "    $texto" -ForegroundColor Green }
function Mal([string]$texto) { Write-Host "    $texto" -ForegroundColor Red }
function Aviso([string]$texto) { Write-Host "    $texto" -ForegroundColor Yellow }
function Nota([string]$texto) { Write-Host "    $texto" }
function Duracion([TimeSpan]$t) {
    if ($t.TotalMinutes -ge 1) { return ('{0} min {1} s' -f [int][Math]::Floor($t.TotalMinutes), $t.Seconds) }
    return ('{0:0.0} s' -f $t.TotalSeconds)
}
function Corto([string]$texto) {
    return $texto.Replace($script:Raiz + '\', '').Replace($script:Raiz, '.')
}

# Corre dotnet y guarda toda su salida en $log. $AlVuelo recibe cada línea mientras llega, para mostrar progreso.
function Invocar-Dotnet([string]$log, [string[]]$argumentos, [scriptblock]$AlVuelo = $null) {
    $lineas = New-Object System.Collections.Generic.List[string]
    $anterior = $ErrorActionPreference
    # En PowerShell 5.1 el stderr de un programa llega como ErrorRecord y 'Stop' cortaría el script.
    $ErrorActionPreference = 'Continue'
    try {
        & $script:Dotnet @argumentos 2>&1 | ForEach-Object {
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

# Errores ('error') o advertencias ('warning') de MSBuild, sin repetir (MSBuild los imprime dos veces: al momento
# y en el resumen); la palabra clave puede venir en inglés o traducida.
function Diagnosticos-De([System.Collections.Generic.List[string]]$lineas, [string]$tipo = 'error') {
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

# Crea TestResults\<prefijo><fecha-hora>\ y borra las corridas viejas con el mismo prefijo más allá de $conservar.
function Nueva-Corrida([string]$prefijo, [int]$conservar) {
    $resultados = Join-Path $script:Raiz 'TestResults'
    New-Item -ItemType Directory -Force -Path $resultados | Out-Null
    $corrida = Join-Path $resultados ($prefijo + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Force -Path $corrida | Out-Null
    $patron = '^' + [regex]::Escape($prefijo) + '\d{8}-\d{6}$'
    Get-ChildItem -LiteralPath $resultados -Directory | Where-Object { $_.Name -match $patron } |
        Sort-Object Name -Descending | Select-Object -Skip $conservar |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
    return $corrida
}

# Lee los .trx de una carpeta: cada prueba con su resultado (Passed, Failed, NotExecuted...), su mensaje, su pila
# y su salida estándar.
function Leer-Trx([string]$carpeta) {
    $lista = New-Object System.Collections.Generic.List[object]
    foreach ($archivo in @(Get-ChildItem -LiteralPath $carpeta -Recurse -Filter '*.trx' -ErrorAction SilentlyContinue)) {
        [xml]$x = [System.IO.File]::ReadAllText($archivo.FullName)
        foreach ($res in @($x.TestRun.Results.UnitTestResult)) {
            if (-not $res) { continue }
            $mensaje = ''; $pila = ''; $salida = ''
            if ($res.Output) {
                if ($res.Output.ErrorInfo) { $mensaje = "$($res.Output.ErrorInfo.Message)".Trim(); $pila = "$($res.Output.ErrorInfo.StackTrace)".Trim() }
                if ($res.Output.StdOut) { $salida = "$($res.Output.StdOut)" }
            }
            $lista.Add([pscustomobject]@{ Nombre = "$($res.testName)"; Resultado = "$($res.outcome)"; Mensaje = $mensaje; Pila = $pila; Salida = $salida })
        }
    }
    return ,$lista
}

# La primera línea de la pila que dice archivo y línea, para el "Dónde:" de una prueba fallida.
function Donde-Fallo([string]$pila) {
    return ($pila -split "`r?`n") | Where-Object { $_ -match ' in .+:line \d+' -or $_ -match ' en .+:l.nea \d+' } | Select-Object -First 1
}

Export-ModuleMember -Function *
