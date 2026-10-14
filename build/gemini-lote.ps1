# Lote de gemini-cola.ps1 -Lote (D-045). Se carga desde gemini-cola.ps1 y usa sus parámetros y funciones.
# Un worktree y una rama gemini/lote-<fecha> para todas las tareas; cada tarea corre en su propio proceso, con un commit
# propio si sale bien. Sin push ni PR. Presupuestos de tareas, tokens y horas, y paradas inmediatas.
$script:codigoLote = 0
Write-Host "Cola del lote: $Cola"
$estadoRuta = Join-Path $repo '.proceso\gemini\estado-lote.json'
$informeRuta = Join-Path $repo '.proceso\gemini\informe-noche.md'
$salidas = Join-Path $repo '.claude\gemini-salidas'
$paradaCuota = '(?i)(quota|rate.?limit|resource.?exhausted|\b429\b|usage limit|l[ií]mite de uso)'
$paradaAuth = '(?i)(unauthenticated|authenticat|not logged|log ?in again|sign ?in|\b401\b|invalid credentials|credencial)'
$paradaTerminos = '(?i)(suspen|terms of service|t[ée]rminos|violat|violaci[oó]n|disabled in this account)'

function Guardar-Estado { [IO.File]::WriteAllText($estadoRuta, ($estado | ConvertTo-Json -Depth 8), $utf8) }

function Escribir-Informe([string] $parada) {
    $tareasLote = @($estado.tareas | Where-Object { $_.rama -eq $estado.rama })
    $hechas = @($tareasLote | Where-Object { $_.commit })
    $candidatos = @($tareasLote | ForEach-Object { $t = $_; @($t.candidatos) | Where-Object { $_ } | ForEach-Object { $_ | Add-Member -NotePropertyName fuente -NotePropertyValue $t.fuente -Force -PassThru } })
    $descartadas = ($tareasLote | ForEach-Object { [int]$_.descartadas } | Measure-Object -Sum).Sum
    $tokensLote = ($tareasLote | ForEach-Object { [long]$_.tokens } | Measure-Object -Sum).Sum
    $sb = New-Object Text.StringBuilder
    [void]$sb.AppendLine("# Informe del lote $($estado.rama)")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("- Tareas: $($tareasLote.Count) intentadas, $($hechas.Count) con commit; rama ``$($estado.rama)``, sin push.")
    [void]$sb.AppendLine("- Pruebas en verde: las de las $($hechas.Count) tareas con commit (las candidatas y descartadas quedan con ``Skip``).")
    [void]$sb.AppendLine("- Candidatos: $($candidatos.Count); descartadas por no citar el documento: $descartadas.")
    [void]$sb.AppendLine("- Tokens de Gemini: $tokensLote de $MaxTokens; reloj: $([int]((Get-Date) - [datetime]$estado.inicio).TotalMinutes) min de $([int]($Horas * 60)).")
    [void]$sb.AppendLine("- Parada: $parada.")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## Defectos candidatos')
    foreach ($c in $candidatos) {
        [void]$sb.AppendLine()
        [void]$sb.AppendLine("### ``$($c.prueba)`` en ``$($c.fuente)``")
        [void]$sb.AppendLine()
        [void]$sb.AppendLine("- Documento: ``$($c.documento)``")
        [void]$sb.AppendLine("- Cita: «$($c.cita)»")
        [void]$sb.AppendLine('- Fallo:')
        [void]$sb.AppendLine()
        [void]$sb.AppendLine('```')
        [void]$sb.AppendLine((@($c.fallo -split "`n" | Select-Object -First 15) -join "`n").TrimEnd())
        [void]$sb.AppendLine('```')
    }
    if (-not $candidatos.Count) { [void]$sb.AppendLine(); [void]$sb.AppendLine('Ninguno.') }
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## Estado de cada tarea')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('| Tarea | Fuente | Desenlace | Rondas | Tokens | Segundos | Motivo |')
    [void]$sb.AppendLine('|---|---|---|---|---|---|---|')
    foreach ($t in $tareasLote) {
        [void]$sb.AppendLine("| $($t.numero) | ``$($t.fuente)`` | $($t.desenlace) | $($t.rondas) | $($t.tokens) | $($t.segundos) | $($t.motivo -replace '\|', '/' -replace '[\r\n]+', ' ') |")
    }
    [IO.File]::WriteAllText($informeRuta, $sb.ToString(), $utf8)
}

Add-Type -Namespace GeminiLote -Name Energia -MemberDefinition '[System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint estado);'
Get-ChildItem -LiteralPath $salidas -Directory -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-7) } | Remove-Item -Recurse -Force

$estado = if (Test-Path -LiteralPath $estadoRuta) { Get-Content -LiteralPath $estadoRuta -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
if (-not $estado) { $estado = [pscustomobject]@{ fuentesHechas = @(); tareas = @(); rama = ''; worktreeLote = ''; inicio = ''; latido = ''; terminado = $true } }
if (-not $estado.terminado -and $estado.worktreeLote -and (Test-Path -LiteralPath $estado.worktreeLote)) {
    $wt = $estado.worktreeLote
    git -C $wt clean -fdq -- tests
    Write-Host "Se retoma el lote $($estado.rama) tras un corte."
} elseif (-not (Leer-Tareas | Where-Object { $estado.fuentesHechas -notcontains ($_.Fuente -replace '\\', '/') })) {
    Write-Host 'Lote terminado: no quedan tareas sin hacer. Informe: sin cambios'
    return
} else {
    $sello = Get-Date -Format 'yyyyMMdd-HHmmss'
    $estado.rama = "gemini/lote-$sello"
    $wt = Join-Path $repo ".claude\worktrees\gemini-lote-$sello"
    git -C $repo fetch --quiet origin
    git -C $repo worktree add --quiet $wt -b $estado.rama origin/main
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo crear el worktree del lote' }
    $estado.worktreeLote = $wt
    $estado.inicio = (Get-Date).ToString('o')
    $estado.terminado = $false
}
Guardar-Estado

$finLote = ([datetime]$estado.inicio).AddHours($Horas)
$powershell = (Get-Command powershell.exe).Source
$seguidas = 0
$parada = ''
[void][GeminiLote.Energia]::SetThreadExecutionState([uint32]2147483649)
try {
    while (-not $parada) {
        $delLote = @($estado.tareas | Where-Object { $_.rama -eq $estado.rama })
        $tokensLote = ($delLote | ForEach-Object { [long]$_.tokens } | Measure-Object -Sum).Sum
        if ($delLote.Count -ge $MaxTareas) { $parada = "presupuesto de tareas ($MaxTareas)"; break }
        if ($tokensLote -ge $MaxTokens) { $parada = "presupuesto de tokens ($tokensLote de $MaxTokens)"; break }
        if ((Get-Date) -ge $finLote) { $parada = "presupuesto de reloj ($Horas h)"; break }
        $tarea = Leer-Tareas | Where-Object { $estado.fuentesHechas -notcontains ($_.Fuente -replace '\\', '/') } | Select-Object -First 1
        if (-not $tarea) { $parada = 'no quedan tareas sin hacer'; break }

        $estado.latido = (Get-Date).ToString('o')
        Guardar-Estado
        $argumentos = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'gemini-cola.ps1'), '-Cola', $Cola,
            '-WorktreeLote', $wt, '-NumeroTarea', $tarea.Numero, '-MaxReintentos', '1', '-MinutosAgy', "$MinutosTarea", '-MinutosPruebas', "$MinutosTarea",
            '-Modelo', $Modelo, '-Esfuerzo', $Esfuerzo)
        if ($RespuestaGuardada) { $argumentos += @('-RespuestaGuardada', $RespuestaGuardada) }
        $inicioTarea = Get-Date
        $hijo = Ejecutar $powershell $argumentos $wt $MinutosTarea
        $r = $null
        if ($hijo.Salida -match 'RESULTADO=(.+)') { $r = Get-Content -LiteralPath $Matches[1].Trim() -Raw -Encoding UTF8 | ConvertFrom-Json }
        if ($hijo.Agotado -or -not $r) {
            $destinoSuelto = Join-Path $wt ($tarea.Destino -replace '/', '\')
            if ((git -C $wt status --porcelain -- $tarea.Destino) -like '`?`?*' -and (Test-Path -LiteralPath $destinoSuelto)) { Remove-Item -LiteralPath $destinoSuelto -Force }
            $r = [pscustomobject]@{ desenlace = 'fallida'; motivo = $(if ($hijo.Agotado) { "superó $MinutosTarea min y se cortó" } else { "sin resultado: $(($hijo.Errores -split "`n")[0])" })
                rondas = 0; tokens = 0; commit = ''; candidatos = @(); descartadas = @(); agy = '' }
        }
        $estado.tareas += [pscustomobject]@{ numero = $tarea.Numero; fuente = ($tarea.Fuente -replace '\\', '/'); rama = $estado.rama
            desenlace = $r.desenlace; motivo = $r.motivo; rondas = $r.rondas; tokens = [long]$r.tokens; segundos = [int]((Get-Date) - $inicioTarea).TotalSeconds
            commit = $r.commit; candidatos = @($r.candidatos); descartadas = @($r.descartadas).Count }
        $estado.fuentesHechas += ($tarea.Fuente -replace '\\', '/')
        $estado.latido = (Get-Date).ToString('o')
        Guardar-Estado
        Write-Host "Tarea $($tarea.Numero): $($r.desenlace) $($r.motivo)"

        $aviso = "$($r.agy) $($r.motivo)"
        $seguidas = if ($r.commit) { 0 } else { $seguidas + 1 }
        $hechasLote = @($estado.tareas | Where-Object { $_.rama -eq $estado.rama })
        if ($aviso -match $paradaTerminos) { $parada = "agy menciona suspensión, términos o violación: $($Matches[0])" }
        elseif ($aviso -match $paradaCuota) { $parada = "error de cuota: $($Matches[0])" }
        elseif ($aviso -match $paradaAuth) { $parada = "error de autenticación: $($Matches[0])" }
        elseif ($seguidas -ge 3) { $parada = 'tres fallos seguidos' }
        elseif ($hechasLote.Count -eq 3 -and (($hechasLote | ForEach-Object { [long]$_.tokens } | Measure-Object -Average).Average -gt 200000)) {
            $parada = 'el promedio de las tres primeras tareas pasa de 200 000 tokens'
        }
        if (-not $parada) { Start-Sleep -Seconds $PausaSegundos }
    }
} catch {
    $parada = "error del lote: $($_.Exception.Message)"
    $script:codigoLote = 1
} finally {
    [void][GeminiLote.Energia]::SetThreadExecutionState([uint32]2147483648)
    $estado.terminado = $true
    $estado.latido = (Get-Date).ToString('o')
    Guardar-Estado
    Escribir-Informe $parada
    Write-Host "Lote terminado: $parada. Informe: $informeRuta"
}
