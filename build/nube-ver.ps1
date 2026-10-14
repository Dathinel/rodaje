# Visor de solo lectura del bucle de la nube (D-048): lee archivos locales; no llama a ningún modelo ni a GitHub.
# Uso: build\nube-ver.ps1 [-Una] [-Segundos 5]
param(
    [switch] $Una,
    [int] $Segundos = 5
)
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$repo = Split-Path (git -C $PSScriptRoot rev-parse --path-format=absolute --git-common-dir)
$nube = Join-Path $repo '.proceso\nube'

function Leer-Json([string] $ruta) {
    if (-not (Test-Path -LiteralPath $ruta)) { return $null }
    try { Get-Content -LiteralPath $ruta -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $null }
}
function Lineas([string] $ruta, [scriptblock] $filtro) {
    if (-not (Test-Path -LiteralPath $ruta)) { return @() }
    @([IO.File]::ReadAllLines($ruta, [Text.Encoding]::UTF8) | Where-Object $filtro)
}
function Titulo([string] $t) { Write-Host ''; Write-Host "== $t" -ForegroundColor Cyan }

function Mostrar {
    $hoy = Get-Date -Format 'yyyy-MM-dd'
    Write-Host "Bucle de la nube  ·  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  ·  solo lectura" -ForegroundColor White
    $proceso = Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match 'nube-continuo\.ps1' } | Select-Object -First 1
    Titulo 'Bucle'
    if ($proceso) { Write-Host "  Vivo (PID $($proceso.ProcessId))" -ForegroundColor Green } else { Write-Host '  No está corriendo' -ForegroundColor Yellow }
    $e = Leer-Json (Join-Path $nube 'estado-continuo.json')
    if ($e) {
        Write-Host "  Sesiones lanzadas: $($e.sesiones) en $($e.pasadas) pasadas"
        if ($e.parada) { Write-Host "  Parada: $($e.parada)" -ForegroundColor Yellow }
        if ($e.latido) {
            $m = ((Get-Date) - [datetime]$e.latido).TotalMinutes
            Write-Host ("  Último latido: hace {0:N1} min" -f $m) -ForegroundColor $(if ($m -gt 90) { 'Red' } else { 'Gray' })
        }
    }
    $c = Leer-Json (Join-Path $nube 'estado-ciclo.json')
    Titulo 'Pasada actual (nube-ciclo)'
    if ($c) {
        foreach ($s in @($c.sesiones)) { Write-Host "  Tarea $($s.tarea): $($s.estado)$(if ($s.pr) { " -> #$($s.pr)" })" }
        if ($c.terminado) { Write-Host "  Terminada: $($c.parada)" }
    }
    Titulo 'Uniones de hoy'
    Lineas (Join-Path $nube 'uniones.md') { $_ -like '| #*' -and $_ -like "*$hoy*" } | ForEach-Object { "  $_" }
    Titulo 'Pendientes (últimos 6)'
    Lineas (Join-Path $nube 'pendientes-revision.md') { $_ -like '- #*' } | Select-Object -Last 6 | ForEach-Object { "  $_" }
    Titulo 'Intermitencias de hoy'
    Lineas (Join-Path $nube 'intermitentes.md') { $_ -like "*$hoy*" } | ForEach-Object { "  $_" }
}

if ($Una) { Mostrar; return }
while ($true) {
    Clear-Host
    Mostrar
    Write-Host ''
    Write-Host "Refresca cada $Segundos s. Ctrl+C para salir." -ForegroundColor DarkGray
    Start-Sleep -Seconds $Segundos
}
