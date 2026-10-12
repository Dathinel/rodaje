# Prueba el instalador de punta a punta: lo instala en silencio en una carpeta temporal, comprueba archivos,
# accesos directos y asociaciones, abre el exe instalado con --autoprueba (clip de prueba de TestResults\media-cache
# si lo hay), prueba el lanzador y desinstala en silencio comprobando que no queda nada y que %LOCALAPPDATA%\Rodaje sigue.
# Ojo: mientras dura, la asociación de .rodaje y el acceso de Inicio son reales (del usuario actual).
# Uso:
#   powershell -NoProfile -ExecutionPolicy Bypass -File build\probar-instalador.ps1 [-Instalador dist\distribucion\Rodaje-<v>-instalador.exe]
#       [-Abrir video.mp4|proyecto.rodaje] [-CarpetaPorDefecto] [-Registro carpeta]
#   -Abrir              lo que abre la autoprueba en vez del clip de TestResults\media-cache.
#   -CarpetaPorDefecto  instala donde elige el instalador (%LOCALAPPDATA%\Programs\Rodaje), no en una carpeta temporal.
[CmdletBinding()]
param([string]$Instalador = '', [string]$Abrir = '', [switch]$CarpetaPorDefecto, [string]$Registro = '')

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$Raiz = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'comun.psm1') -DisableNameChecking -Force
$E = Inicializar-Rodaje $Raiz
if (-not $Instalador) { $Instalador = Join-Path $Raiz "dist\distribucion\Rodaje-$(Version-Rodaje)-instalador.exe" }
if (-not (Test-Path -LiteralPath $Instalador)) { throw "No existe $Instalador (corre build\instalador.ps1)." }

$destino = Join-Path ([System.IO.Path]::GetTempPath()) ("Rodaje prueba á " + (Get-Date -Format 'HHmmss'))
$registro = if ($Registro) { $Registro } else { Join-Path $Raiz 'TestResults\instalador' }
New-Item -ItemType Directory -Force -Path $registro | Out-Null
$fallos = New-Object System.Collections.Generic.List[string]
function Comprobar([bool]$ok, [string]$texto) { if ($ok) { Bien $texto } else { Mal $texto; $fallos.Add($texto) } }
$clave = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{6C0B2F4E-5D1A-4C7B-9E3F-52A8D9104A11}_is1'
$inicio = Join-Path ([Environment]::GetFolderPath('Programs')) 'Rodaje.lnk'
if (Test-Path -LiteralPath $clave) { throw 'Rodaje ya está instalado para este usuario: desinstálalo antes de la prueba.' }

Titulo "Prueba del instalador: $(Split-Path -Leaf $Instalador)"
$reloj = [System.Diagnostics.Stopwatch]::StartNew()
$opciones = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$registro\instalar.log`"")
if (-not $CarpetaPorDefecto) { $opciones += "/DIR=`"$destino`"" }
$p = Start-Process -FilePath $Instalador -ArgumentList $opciones -PassThru -Wait
Comprobar ($p.ExitCode -eq 0) ("Instalado en silencio en {0:0.0} s (código {1})" -f $reloj.Elapsed.TotalSeconds, $p.ExitCode)
if ($CarpetaPorDefecto) {
    $destino = "$((Get-ItemProperty -LiteralPath $clave -ErrorAction SilentlyContinue).InstallLocation)".TrimEnd('\')
    Comprobar ($destino -and (Test-Path -LiteralPath $destino)) "Instalado en la carpeta por defecto: $destino"
}
$exe = Join-Path $destino 'app\Rodaje.exe'
Comprobar ((Test-Path -LiteralPath (Join-Path $destino 'Rodaje.exe')) -and (Test-Path -LiteralPath $exe)) 'Rodaje.exe (lanzador) y app\Rodaje.exe instalados'
Comprobar ((Get-Item -LiteralPath (Join-Path $destino 'app') -Force).Attributes -band [IO.FileAttributes]::Hidden) 'app\ oculta'
foreach ($f in 'LICENSE.txt', 'THIRD-PARTY-NOTICES.txt', 'TRADEMARK.md') { Comprobar (Test-Path -LiteralPath (Join-Path $destino "app\$f")) "app\$f" }
$tam = (Get-ChildItem -LiteralPath $destino -Recurse -File -Force | Measure-Object -Property Length -Sum).Sum
Nota ("Ocupa {0:0.0} MB instalado." -f ($tam / 1MB))
Comprobar (Test-Path -LiteralPath $clave) 'Entrada en Aplicaciones (HKCU, sin administrador)'
Comprobar (Test-Path -LiteralPath $inicio) 'Acceso directo en Inicio'
$comando = (Get-ItemProperty -LiteralPath 'HKCU:\Software\Classes\Rodaje.Proyecto\shell\open\command' -ErrorAction SilentlyContinue).'(default)'
Comprobar ($comando -like "*$destino\Rodaje.exe*") '.rodaje se abre con el Rodaje instalado'
Comprobar ($null -ne (Get-ItemProperty -LiteralPath 'HKCU:\Software\Classes\.mp4\OpenWithProgids' -Name 'Rodaje.Medio' -ErrorAction SilentlyContinue)) '«Abrir con» de .mp4'

$clip = Get-ChildItem -Path (Join-Path $Raiz 'TestResults\media-cache') -Filter 'h264-1080p-23976-aac-*.mp4' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $clip -and $env:RODAJE_REPO) { $clip = Get-ChildItem -Path (Join-Path $env:RODAJE_REPO 'TestResults\media-cache') -Filter 'h264-1080p-23976-aac-*.mp4' -ErrorAction SilentlyContinue | Select-Object -First 1 }
if ($Abrir) { $clip = Get-Item -LiteralPath $Abrir }
$argumentos = @('--autoprueba'); if ($clip) { $argumentos += "`"$($clip.FullName)`"" } else { Aviso 'Sin clip de prueba en TestResults\media-cache: la autoprueba abre la app vacía.' }
Remove-Item Env:DOTNET_ROOT -ErrorAction SilentlyContinue
$reloj.Restart()
$a = Start-Process -FilePath $exe -ArgumentList $argumentos -WorkingDirectory $destino -PassThru
if (-not $a.WaitForExit(120000)) { $a.Kill(); Comprobar $false 'La autoprueba no terminó en 120 s' }
else {
    $errorTxt = Join-Path $destino 'app\rodaje-error.txt'
    Comprobar ($a.ExitCode -eq 0 -and -not (Test-Path -LiteralPath $errorTxt)) ("Autoprueba del exe instalado: código {0} en {1:0.0} s" -f $a.ExitCode, $reloj.Elapsed.TotalSeconds)
    if (Test-Path -LiteralPath $errorTxt) { Get-Content -LiteralPath $errorTxt -Encoding UTF8 | Select-Object -First 10 | ForEach-Object { Nota "  $_" } }
}
# El lanzador abre app\Rodaje.exe y sale; con --arranque la app se cierra sola en cuanto responde.
$l = Start-Process -FilePath (Join-Path $destino 'Rodaje.exe') -ArgumentList '--arranque' -PassThru -Wait
Comprobar ($l.ExitCode -eq 0) "El lanzador abrió la app (código $($l.ExitCode))"
$hijo = $null
for ($i = 0; $i -lt 60 -and -not $hijo; $i++) { Start-Sleep -Milliseconds 250; $hijo = Get-Process -Name Rodaje -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } }
if ($hijo) { if (-not $hijo.WaitForExit(60000)) { $hijo.Kill() } }

$desinstalador = Join-Path $destino 'unins000.exe'
$reloj.Restart()
$u = Start-Process -FilePath $desinstalador -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$registro\desinstalar.log`"" -PassThru -Wait
# El desinstalador se copia a %TEMP% y termina allí; se espera a que la carpeta desaparezca.
for ($i = 0; $i -lt 60 -and (Test-Path -LiteralPath $destino); $i++) { Start-Sleep -Milliseconds 500 }
Comprobar ($u.ExitCode -eq 0) ("Desinstalado en silencio en {0:0.0} s" -f $reloj.Elapsed.TotalSeconds)
Comprobar (-not (Test-Path -LiteralPath $destino)) 'No queda la carpeta instalada'
Comprobar (-not (Test-Path -LiteralPath $clave)) 'No queda la entrada en Aplicaciones'
Comprobar (-not (Test-Path -LiteralPath $inicio)) 'No queda el acceso de Inicio'
Comprobar (-not (Test-Path -LiteralPath 'HKCU:\Software\Classes\Rodaje.Proyecto') -and -not (Test-Path -LiteralPath 'HKCU:\Software\Classes\Rodaje.Medio')) 'No quedan las asociaciones'
Comprobar ($null -eq (Get-ItemProperty -LiteralPath 'HKCU:\Software\Classes\.mp4\OpenWithProgids' -Name 'Rodaje.Medio' -ErrorAction SilentlyContinue)) '«Abrir con» de .mp4 quitado'
if (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'Rodaje')) { Comprobar $true '%LOCALAPPDATA%\Rodaje se conserva (sin /BORRARDATOS)' }
if (Test-Path -LiteralPath $destino) { Remove-Item -LiteralPath $destino -Recurse -Force -ErrorAction SilentlyContinue }

if ($fallos.Count -gt 0) { Titulo "Fallaron $($fallos.Count) comprobaciones."; exit 1 }
Titulo 'El instalador pasó todas las comprobaciones.'
exit 0
