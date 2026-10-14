# Visor de solo lectura del lote de Gemini (D-045): lee archivos locales y git; no llama a ningún modelo.
# Uso: build\gemini-ver.ps1 [-Una] [-Segundos 5]
param(
    [switch] $Una,
    [int] $Segundos = 5
)
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$repo = Split-Path (git -C $PSScriptRoot rev-parse --path-format=absolute --git-common-dir)
$gemini = Join-Path $repo '.proceso\gemini'
$salidas = Join-Path $repo '.claude\gemini-salidas'

function Leer-Json([string] $ruta) {
    if (-not (Test-Path -LiteralPath $ruta)) { return $null }
    try { Get-Content -LiteralPath $ruta -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $null }
}
function Cola([string] $ruta, [int] $n) {
    if (Test-Path -LiteralPath $ruta) { Get-Content -LiteralPath $ruta -Encoding UTF8 -Tail $n | ForEach-Object { "    $_" } }
}
function Titulo([string] $t) { Write-Host ''; Write-Host "== $t" -ForegroundColor Cyan }

function Mostrar {
    Write-Host "Lote de Gemini  ·  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  ·  solo lectura" -ForegroundColor White

    $proceso = Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match 'gemini-cola\.ps1' -and $_.CommandLine -match '-Lote\b' } | Select-Object -First 1
    $topeTokens = 4000000
    $topeHoras = 7
    if ($proceso) {
        if ($proceso.CommandLine -match '-MaxTokens\s+(\d+)') { $topeTokens = [long]$Matches[1] }
        if ($proceso.CommandLine -match '-Horas\s+([\d.]+)') { $topeHoras = [double]$Matches[1] }
    }

    Titulo 'Proceso'
    if ($proceso) { Write-Host "  Vivo (PID $($proceso.ProcessId))" -ForegroundColor Green } else { Write-Host '  No hay ningún lote corriendo' -ForegroundColor Yellow }

    $ultima = Get-ChildItem -LiteralPath $salidas -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $estado = Leer-Json (Join-Path $gemini 'estado-lote.json')
    Titulo 'Estado'
    if (-not $estado) { Write-Host '  Todavía no hay estado-lote.json' }
    else {
        $tareas = @($estado.tareas | Where-Object { $_.rama -eq $estado.rama })
        $hechas = @($tareas | Where-Object { $_.commit }).Count
        $tokens = ($tareas | ForEach-Object { [long]$_.tokens } | Measure-Object -Sum).Sum
        if (-not $tokens) { $tokens = 0 }
        $actual = '(ninguna)'
        if ($ultima -and -not $estado.terminado -and $ultima.Name -match '^\d{8}-\d{6}-(.+)$') {
            $tema = $Matches[1]
            $actual = $tema
            $colaCaza = Join-Path $gemini 'cola-caza.md'
            if (Test-Path -LiteralPath $colaCaza) {
                $texto = [IO.File]::ReadAllText($colaCaza, [Text.Encoding]::UTF8)
                $m = [regex]::Match($texto, '(?m)^## (\d+)\. ([^\r\n]+)(?:\r?\n(?!## )[^\r\n]*)*?\r?\n- \*\*Tema:\*\* ' + [regex]::Escape($tema) + '\s*$')
                if ($m.Success) { $actual = "$($m.Groups[1].Value). $($m.Groups[2].Value)" }
            }
        }
        $horas = if ($estado.inicio) { ((Get-Date) - [datetime]$estado.inicio).TotalHours } else { 0 }
        Write-Host "  Rama: $($estado.rama)$(if ($estado.terminado) { '  (terminado)' })"
        Write-Host "  Tarea actual: $actual"
        Write-Host "  Hechas: $hechas   Fallidas: $($tareas.Count - $hechas)   Total: $($tareas.Count)"
        Write-Host ("  Tokens: {0:N0} de {1:N0} ({2:P0})" -f $tokens, $topeTokens, ($tokens / [double]$topeTokens))
        Write-Host ("  Horas: {0:N1} de {1}" -f $horas, $topeHoras)
        if ($estado.latido) {
            $minutos = ((Get-Date) - [datetime]$estado.latido).TotalMinutes
            Write-Host ("  Último latido: hace {0:N1} min" -f $minutos) -ForegroundColor $(if ($minutos -gt 12) { 'Red' } else { 'Gray' })
        }
    }

    Titulo 'Última salida'
    if (-not $ultima) { Write-Host '  Todavía no hay salidas' }
    else {
        $ronda = Get-ChildItem -LiteralPath $ultima.FullName -Filter 'ronda*-salida.json' -ErrorAction SilentlyContinue |
            Sort-Object { [int]($_.Name -replace '\D', '') } | Select-Object -Last 1
        $prefijo = if ($ronda) { $ronda.Name -replace '-salida\.json$', '' } else { 'ronda1' }
        Write-Host "  $($ultima.Name)   última ronda: $(if ($ronda) { $prefijo } else { '(agy aún no responde)' })"
        $s = if ($ronda) { Leer-Json $ronda.FullName } else { $null }
        if ($s) { Write-Host "  agy: estado $($s.status), tokens $($s.usage.total_tokens)" }
        $errores = Join-Path $ultima.FullName "$prefijo-errores.txt"
        if ((Test-Path -LiteralPath $errores) -and (Get-Item -LiteralPath $errores).Length) { Write-Host '  errores.txt:'; Cola $errores 15 }
        $pruebas = Get-ChildItem -LiteralPath $ultima.FullName -Filter '*-pruebas.txt' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
        if ($pruebas) { Write-Host "  $($pruebas.Name):"; Cola $pruebas.FullName 15 }
    }

    Titulo 'Commits del lote'
    $wt = Get-ChildItem -LiteralPath (Join-Path $repo '.claude\worktrees') -Directory -Filter 'gemini-lote-*' -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | Select-Object -First 1
    if (-not $wt) { Write-Host '  No hay worktree de lote' }
    else {
        $log = @(git -C $wt.FullName log --oneline origin/main..HEAD 2>$null)
        if (-not $log) { Write-Host "  $($wt.Name): sin commits todavía" }
        else {
            $log | ForEach-Object { "  $_" }
            $n = @(git -C $wt.FullName show --name-only --format= HEAD 2>$null | Where-Object { $_ }).Count
            Write-Host "  El último cambió $n archivo(s)"
        }
    }

    $informe = Join-Path $gemini 'informe-noche.md'
    if (Test-Path -LiteralPath $informe) {
        Titulo 'Informe listo'
        Get-Content -LiteralPath $informe -Encoding UTF8 -TotalCount 5 | ForEach-Object { "  $_" }
    }
}

if ($Una) { Mostrar; return }
while ($true) {
    Clear-Host
    Mostrar
    Write-Host ''
    Write-Host "Refresca cada $Segundos s. Ctrl+C para salir." -ForegroundColor DarkGray
    Start-Sleep -Seconds $Segundos
}
