# Toma la siguiente tarea «gemini-local» de la cola, arma su worktree y lanza agy (D-045). No gasta tokens de Claude.
# Uso: build\gemini-cola.ps1 [-Cola <ruta de cola.md>] [-Simular]
param(
    [string] $Cola = (Join-Path $PSScriptRoot '..\.proceso\nube\cola.md'),
    [string] $Modelo = 'gemini-3.8-flash-medium',
    [string] $Esfuerzo = 'medium',
    [int] $MaxBytes = 65536,
    [switch] $Simular
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$agy = Join-Path $env:LOCALAPPDATA 'agy\bin\agy.exe'

$permitido = '^(tests/|docs/|src/Rodaje\.(Core|Packages|Playback)/)'
$excluido = '(?i)(Serializ|Timing|Sync|Render|Engine|Install|Verif|LICENSE|UpstreamAttribution|CLAUDE\.md|^build/|^\.github/|publicar)'

function Leer-Tarea {
    $texto = [IO.File]::ReadAllText((Resolve-Path $Cola).Path, [Text.Encoding]::UTF8)
    foreach ($bloque in ($texto -split '(?m)^(?=## \d+\. )')) {
        if ($bloque -notmatch '^## (\d+)\. (.+)') { continue }
        $t = @{ Numero = $Matches[1]; Titulo = $Matches[2].Trim() }
        foreach ($campo in 'Estado', 'Agente', 'Tema', 'Fuente', 'Destino', 'Criterio', 'Qué') {
            if ($bloque -match "(?m)^- \*\*${campo}:\*\* (.+)$") { $t[$campo] = $Matches[1].Trim().Trim('`') }
        }
        if ($t.Agente -eq 'gemini-local' -and $t.Estado -like 'pendiente*') { return $t }
    }
    return $null
}

function Comprobar-Ruta([string] $ruta, [string] $nombre) {
    $r = $ruta -replace '\\', '/'
    if ($r -notmatch $permitido -or $r -match $excluido -or $r -match '\.\.' -or [IO.Path]::IsPathRooted($r)) {
        throw "La tarea pide $nombre fuera del alcance de Gemini (D-045): $ruta"
    }
    return $r
}

$tarea = Leer-Tarea
if (-not $tarea) { Write-Host 'No hay tareas gemini-local pendientes.'; exit 0 }
foreach ($c in 'Tema', 'Fuente', 'Destino', 'Criterio', 'Qué') { if (-not $tarea[$c]) { throw "La tarea $($tarea.Numero) no tiene el campo $c" } }
if ($tarea.Tema -notmatch '^[a-z0-9-]{3,40}$') { throw "Tema no válido: $($tarea.Tema)" }
$fuente = Comprobar-Ruta $tarea.Fuente 'un archivo fuente'
$destino = Comprobar-Ruta $tarea.Destino 'un destino'

$rama = "gemini/$($tarea.Tema)"
$worktree = Join-Path $repo ".claude\worktrees\gemini-$($tarea.Tema)"
if (Test-Path -LiteralPath $worktree) { throw "El worktree ya existe: $worktree" }
$instruccion = "$($tarea.Qué) Archivo fuente: $fuente. Archivo destino: $destino. Criterio de aceptación: $($tarea.Criterio). " +
    'Lee solo el archivo fuente y, si hace falta para el estilo, una prueba vecina. Devuelve el contenido completo del archivo destino ' +
    'en el campo content de la salida estructurada. No escribas ni modifiques archivos, no ejecutes comandos y no leas URLs.'
Write-Host "Tarea $($tarea.Numero): $($tarea.Titulo)`nRama $rama -> $destino"
if ($Simular) { Write-Host "Instrucción: $instruccion"; exit 0 }

git -C $repo fetch --quiet origin
git -C $repo worktree add --quiet $worktree -b $rama origin/main
if ($LASTEXITCODE -ne 0) { throw 'No se pudo crear el worktree' }
if (-not (Test-Path -LiteralPath (Join-Path $worktree $fuente))) { throw "No existe el archivo fuente: $fuente" }
$destinoCompleto = [IO.Path]::GetFullPath((Join-Path $worktree $destino))
if (Test-Path -LiteralPath $destinoCompleto) { throw "El destino ya existe; no se sobrescribe: $destino" }

$salida = Join-Path $repo ".claude\gemini-salidas\$(Get-Date -Format 'yyyyMMdd-HHmmss')-$($tarea.Tema)"
New-Item -ItemType Directory -Force -Path $salida | Out-Null
$esquema = Join-Path $salida 'esquema.json'
'{"type":"object","properties":{"content":{"type":"string"}},"required":["content"],"additionalProperties":false}' |
    Set-Content -LiteralPath $esquema -Encoding ascii

function Citar([string] $s) { '"' + ($s -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"' }
$argumentos = @('-p', $instruccion, '--add-dir', $worktree, '--sandbox', '--mode', 'plan', '--output-format', 'json',
    '--json-schema', $esquema, '--print-timeout', '15m', '--model', $Modelo, '--effort', $Esfuerzo) | ForEach-Object { Citar $_ }
$info = New-Object Diagnostics.ProcessStartInfo
$info.FileName = $agy
$info.Arguments = $argumentos -join ' '
$info.WorkingDirectory = $worktree
$info.UseShellExecute = $false
$info.RedirectStandardInput = $true
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
$info.StandardOutputEncoding = [Text.Encoding]::UTF8
$info.StandardErrorEncoding = [Text.Encoding]::UTF8

$inicio = Get-Date
$proceso = [Diagnostics.Process]::Start($info)
$proceso.StandardInput.Close()
$errores = $proceso.StandardError.ReadToEndAsync()
$out = $proceso.StandardOutput.ReadToEnd()
$proceso.WaitForExit()
$utf8 = New-Object Text.UTF8Encoding $false
[IO.File]::WriteAllText((Join-Path $salida 'salida.json'), $out, $utf8)
[IO.File]::WriteAllText((Join-Path $salida 'errores.txt'), $errores.Result, $utf8)

# El código 0 y SUCCESS no prueban nada: vale el contenido y que el archivo quede escrito.
$r = [ordered]@{ tarea = $tarea.Numero; rama = $rama; destino = $destino; codigo = $proceso.ExitCode
    segundos = [int]((Get-Date) - $inicio).TotalSeconds; escrito = $false; motivo = '' }
try {
    $json = $out | ConvertFrom-Json
    $r.estado = $json.status
    $r.tokens = $json.usage.total_tokens
    $contenido = $json.structured_output.content
    if ([string]::IsNullOrWhiteSpace($contenido)) { throw 'Respuesta vacía' }
    $bytes = $utf8.GetByteCount($contenido)
    if ($bytes -gt $MaxBytes) { throw "Respuesta de $bytes bytes; el tope es $MaxBytes" }
    New-Item -ItemType Directory -Force -Path (Split-Path $destinoCompleto) | Out-Null
    $flujo = [IO.File]::Open($destinoCompleto, [IO.FileMode]::CreateNew)
    try { $w = New-Object IO.StreamWriter($flujo, $utf8); $w.Write($contenido); $w.Flush() } finally { $flujo.Dispose() }
    if (-not (Test-Path -LiteralPath $destinoCompleto)) { throw 'El archivo no quedó escrito' }
    $r.escrito = $true
    $r.bytes = $bytes
} catch { $r.motivo = $_.Exception.Message }
$r | ConvertTo-Json | Tee-Object -FilePath (Join-Path $salida 'resultado.json')

$metricas = Join-Path (Split-Path (Split-Path (Resolve-Path $Cola).Path)) 'gemini\metricas.md'
if (Test-Path -LiteralPath $metricas) {
    $fila = "| $(Get-Date -Format 'yyyy-MM-dd HH:mm') | $($tarea.Numero) | $Modelo | $($r.segundos) | $($r.tokens) | $($r.escrito) | | | |"
    Add-Content -LiteralPath $metricas -Value $fila -Encoding UTF8
}
if (-not $r.escrito) { Write-Host "No se escribió nada: $($r.motivo)"; exit 1 }
Write-Host "Listo. Que Claude lea el diff antes de compilar: git -C `"$worktree`" status"
