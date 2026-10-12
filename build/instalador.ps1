# Compila el instalador de Rodaje con Inno Setup a partir de dist\Rodaje (ya publicado por app.ps1).
# Inno Setup se baja a .entorno\innosetup-<versión> en modo portable (sin registro ni desinstalador) y se
# comprueba por SHA-256; nunca se instala global. La licencia que muestra el asistente se arma aquí con LICENSE,
# el aviso de TRADEMARK.md y el de los binarios de Microsoft.
# Firma: con RODAJE_CERTIFICADO (ver Comando-Firma en comun.psm1) se firman el instalador y su desinstalador;
# sin él, sale sin firmar.
# Uso:
#   powershell -NoProfile -ExecutionPolicy Bypass -File build\instalador.ps1 [-Version 1.0.0] [-Origen dist\Rodaje] [-Salida dist\distribucion]
[CmdletBinding()]
param(
    [string]$Version = '',
    [string]$Origen = '',
    [string]$Salida = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$Raiz = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'comun.psm1') -DisableNameChecking -Force
$E = Inicializar-Rodaje $Raiz
if (-not $Origen) { $Origen = Join-Path $Raiz 'dist\Rodaje' }
if (-not $Salida) { $Salida = Join-Path $Raiz 'dist\distribucion' }
if (-not $Version) { $Version = Version-Rodaje }

$InnoVersion = '7.1.0'
$InnoUrl = "https://github.com/jrsoftware/issrc/releases/download/is-$($InnoVersion -replace '\.', '_')/innosetup-$InnoVersion-x64.exe"
$InnoSha256 = '0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f'
$Inno = Join-Path $E.Entorno "innosetup-$InnoVersion"
$Iscc = Join-Path $Inno 'ISCC.exe'
$Marca = Join-Path $Inno ".inno-$InnoVersion"

Titulo "Instalador de Rodaje $Version"
foreach ($exe in 'Rodaje.exe', 'app\Rodaje.exe') {
    if (-not (Test-Path -LiteralPath (Join-Path $Origen $exe))) { throw "No está publicado $Origen\$exe (corre build\app.ps1 -SoloPublicar)." }
}

if (-not (Test-Path -LiteralPath $Marca)) {
    Nota "Bajando Inno Setup $InnoVersion a $(Corto $Inno)..."
    $temporal = Join-Path ([System.IO.Path]::GetTempPath()) ("rodaje-inno-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $temporal | Out-Null
    try {
        $instalador = Join-Path $temporal "innosetup-$InnoVersion-x64.exe"
        Invoke-WebRequest -Uri $InnoUrl -OutFile $instalador -UseBasicParsing
        $sha = (Get-FileHash -LiteralPath $instalador -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($sha -ne $InnoSha256) { throw "Inno Setup bajado no coincide con su SHA-256 ($sha)." }
        if (Test-Path -LiteralPath $Inno) { Remove-Item -LiteralPath $Inno -Recurse -Force }
        $p = Start-Process -FilePath $instalador -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/PORTABLE=1', '/NOICONS', "/DIR=`"$Inno`"" -PassThru -Wait
        if ($p.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $Iscc)) { throw "No se pudo desempacar Inno Setup (código $($p.ExitCode))." }
        New-Item -ItemType File -Force -Path $Marca | Out-Null
    } finally {
        Remove-Item -LiteralPath $temporal -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$trabajo = Join-Path $Raiz 'TestResults\instalador'
New-Item -ItemType Directory -Force -Path $trabajo, $Salida | Out-Null
$licencia = Join-Path $trabajo 'licencia.txt'
$marca = (Get-Content -LiteralPath (Join-Path $Raiz 'TRADEMARK.md') -Raw -Encoding UTF8) -replace '\[LICENSE\]\(LICENSE\)', 'LICENSE'
$texto = @"
Rodaje $Version
Copyright (C) 2026 Dathinel - https://github.com/Dathinel/rodaje

Rodaje es software libre: GNU GPL versión 3 con los términos adicionales de la Sección 7 que están al final de la
licencia (atribución y permiso para complementos). El nombre y los logotipos de Rodaje no se licencian con el
código (ver el aviso de marca más abajo). Las licencias de los componentes de terceros (FFmpeg LGPL, .NET, Windows
App SDK y otros) están en THIRD-PARTY-NOTICES.txt, dentro de la carpeta instalada.

BINARIOS DE MICROSOFT. Los archivos del Windows App SDK y del runtime de .NET que acompañan a Rodaje conservan
los términos de licencia de Microsoft y no quedan sujetos a la GPLv3 de Rodaje.

======================================================================
$((Get-Content -LiteralPath (Join-Path $Raiz 'LICENSE') -Raw -Encoding UTF8).TrimEnd())

======================================================================
$($marca.TrimEnd())
"@
[System.IO.File]::WriteAllText($licencia, $texto.Replace("`r`n", "`n").Replace("`n", "`r`n"), (New-Object System.Text.UTF8Encoding($true)))

$argumentos = @('/Q', "/DVersion=$Version", "/DOrigen=$((Resolve-Path -LiteralPath $Origen).Path)", "/DLicencia=$licencia", "/O$Salida",
    (Join-Path $PSScriptRoot 'instalador\Rodaje.iss'))
$firma = Comando-Firma
if ($firma) {
    $comando = "`"$($firma.Exe)`" " + (($firma.Argumentos | ForEach-Object { if ($_ -match '\s') { "`$q$_`$q" } else { $_ } }) -join ' ') + ' $f'
    $argumentos = @("/Srodaje=$comando", '/DFirmar') + $argumentos
}
$anterior = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$salidaIscc = & $Iscc @argumentos 2>&1 | ForEach-Object { "$_" }
$codigo = $LASTEXITCODE
$ErrorActionPreference = $anterior
$resultado = Join-Path $Salida "Rodaje-$Version-instalador.exe"
if ($codigo -ne 0 -or -not (Test-Path -LiteralPath $resultado)) {
    Mal "ISCC terminó con código $codigo."
    $salidaIscc | Select-Object -Last 20 | ForEach-Object { Nota "  $_" }
    exit 1
}
if ($firma) { Bien 'Instalador firmado.' } else { Aviso 'Sin RODAJE_CERTIFICADO: el instalador va sin firmar (SmartScreen avisará).' }
Bien ("{0}: {1:0.0} MB" -f (Split-Path -Leaf $resultado), ((Get-Item -LiteralPath $resultado).Length / 1MB))
exit 0
