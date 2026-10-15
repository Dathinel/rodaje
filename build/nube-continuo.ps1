# Bucle de sesiones en la nube con crédito (D-048), sin ningún modelo propio: rellena la cola con
# generar-cola-nube-caza.ps1, encadena pasadas de nube-ciclo.ps1 y, entre pasadas, reintenta CI intermitente y une los PR
# de «revisar» que cumplen la regla de D-048. Se lanza con Start-Process, desacoplado.
# Uso: build\nube-continuo.ps1 [-MaxSesiones 50] [-Horas 10] [-PorPasada 8]
param(
    [int] $MaxSesiones = 50,
    [double] $Horas = 10,
    [int] $PorPasada = 8,
    [int] $Simultaneas = 3,
    [int] $MaxUnionesDia = 12,
    [int] $MaxSinPr = 4,
    [double] $HorasInforme = 2,
    [string] $Repo = 'Dathinel/rodaje'
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$utf8 = New-Object Text.UTF8Encoding $false
$raiz = Split-Path (git -C $PSScriptRoot rev-parse --path-format=absolute --git-common-dir)
$nube = Join-Path $raiz '.proceso\nube'
$estadoRuta = Join-Path $nube 'estado-continuo.json'
$informeRuta = Join-Path $raiz '.proceso\informe-continuo.md'
$unionesRuta = Join-Path $nube 'uniones.md'
$pendientesRuta = Join-Path $nube 'pendientes-revision.md'
$intermitentesRuta = Join-Path $nube 'intermitentes.md'
$powershell = (Get-Command powershell.exe).Source
$conocidas = '(ExtremeProjectSessionTests|CpuExportTests|TimelineStressTests|ShutdownReportTests|CubeReaderTests|StillExportFlowTests|PackageServiceTests.Simultaneous_installs_refreshes_and_uninstalls_run_one_at_a_time)'
$prohibidos = '(?i)(build/|\.github/|CLAUDE\.md|LICENSE|TRADEMARK\.md|UpstreamAttribution|dependencia|\.csproj)'

function Ahora { Get-Date -Format 'yyyy-MM-dd HH:mm' }
function Guardar { $estado.latido = (Get-Date).ToString('o'); [IO.File]::WriteAllText($estadoRuta, ($estado | ConvertTo-Json -Depth 6), $utf8) }
function Anotar([string] $ruta, [string] $linea) { [IO.File]::AppendAllText($ruta, "$linea`n", $utf8) }
function Api([string] $ruta) {
    $r = gh api $ruta 2>&1
    if ($LASTEXITCODE -ne 0) { throw "gh api $ruta falló: $(($r | Out-String).Trim())" }
    ($r -join "`n") | ConvertFrom-Json | ForEach-Object { $_ }
}
function Uniones-Hoy {
    if (-not (Test-Path -LiteralPath $unionesRuta)) { return 0 }
    $hoy = Get-Date -Format 'yyyy-MM-dd'
    @([IO.File]::ReadAllLines($unionesRuta, [Text.Encoding]::UTF8) | Where-Object { $_ -like '| #*' -and $_ -like "*$hoy*" }).Count
}
function Pruebas-Fallidas([string] $sha) {
    $runs = @((Api "repos/$Repo/actions/runs?head_sha=$sha&per_page=5").workflow_runs | Where-Object { $_.name -eq 'CI' })
    $fallos = @()
    foreach ($run in $runs | Select-Object -First 1) {
        $fallos += @(gh run view $run.id --repo $Repo --log-failed 2>$null | Select-String -Pattern '  Failed ([\w.]+)' -AllMatches |
            ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
        return [pscustomobject]@{ Run = $run.id; Pruebas = $fallos }
    }
    $null
}
# Tras una unión, main tiene que quedar en verde; un rojo que no sea intermitente conocido congela todo.
function Esperar-Main {
    Start-Sleep -Seconds 60
    $sha = (Api "repos/$Repo/commits/main").sha
    for ($vuelta = 0; $vuelta -lt 2; $vuelta++) {
        $limite = (Get-Date).AddMinutes(75)
        do {
            Start-Sleep -Seconds 180
            $runs = @((Api "repos/$Repo/actions/runs?head_sha=$sha&per_page=5").workflow_runs | Where-Object { $_.name -eq 'CI' })
        } while ((-not $runs -or @($runs | Where-Object { $_.status -ne 'completed' }).Count) -and (Get-Date) -lt $limite)
        if (-not $runs) { $script:parada = "no apareció CI para main ($($sha.Substring(0,8))) en 75 min; uniones congeladas"; return $false }
        if (@($runs | Where-Object { $_.status -ne 'completed' }).Count) { $script:parada = "la CI de main ($($sha.Substring(0,8))) no terminó en 75 min; uniones congeladas"; return $false }
        if ($runs[0].conclusion -eq 'success') { return $true }
        $f = Pruebas-Fallidas $sha
        if ($f -and $f.Pruebas.Count -and -not @($f.Pruebas | Where-Object { $_ -notmatch $conocidas }).Count -and $vuelta -eq 0) {
            foreach ($p in $f.Pruebas) { Anotar $intermitentesRuta "| $(Ahora) | ``$p`` | main | $($f.Run) | relanzado por nube-continuo |" }
            gh run rerun $f.Run --repo $Repo --failed | Out-Null
            continue
        }
        $script:parada = "main en rojo tras unir ($($sha.Substring(0,8)), fallan: $((@($f.Pruebas) -join ', '))); uniones congeladas"
        return $false
    }
    $true
}
function Reintentar-Ci($pr) {
    $clave = "$($pr.number)"
    $hechos = [int]($estado.reintentos.$clave)
    if ($hechos -ge 2) { return }
    $checks = @((Api "repos/$Repo/commits/$($pr.head.sha)/check-runs").check_runs)
    if (@($checks | Where-Object { $_.status -ne 'completed' }).Count -or -not @($checks | Where-Object { $_.conclusion -eq 'failure' }).Count) { return }
    $f = Pruebas-Fallidas $pr.head.sha
    if (-not $f -or -not $f.Pruebas.Count) { return }
    $archivos = @(Api "repos/$Repo/pulls/$($pr.number)/files?per_page=100" | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.filename) })
    foreach ($p in $f.Pruebas) {
        $clase = ($p -split '\.')[-2]
        if ($p -notmatch $conocidas -and ($archivos -contains $clase -or $archivos -contains ($clase -replace 'Tests$', ''))) {
            Anotar $pendientesRuta "- #$($pr.number) ``$($pr.head.sha.Substring(0,8))`` revisar: falla $p, relacionada con el PR; sin reintento ($(Ahora))"
            $estado.reintentos | Add-Member -NotePropertyName $clave -NotePropertyValue 2 -Force
            return
        }
    }
    foreach ($p in $f.Pruebas) { Anotar $intermitentesRuta "| $(Ahora) | ``$p`` | #$($pr.number) | $($f.Run) | reintento $($hechos + 1) |" }
    gh run rerun $f.Run --repo $Repo --failed | Out-Null
    $estado.reintentos | Add-Member -NotePropertyName $clave -NotePropertyValue ($hechos + 1) -Force
}
# Regla (b) de D-048: «revisar» solo por tamaño o área sensible, sin prohibidos, CI verde, prueba de regresión declarada y
# opinión «unir» de revisar-pr-agy.ps1 (una por commit; un error de agy deja el PR pendiente sin reintento).
function Unir-Revisar($pr) {
    $clave = "$($pr.number)@$($pr.head.sha)"
    if ($estado.agy -contains $clave) { return $false }
    $v = (& $powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'revisar-pr.ps1') $pr.number | Out-String) | ConvertFrom-Json
    if ($v.veredicto -ne 'revisar') { return $false }
    if (@($v.motivos | Where-Object { $_ -notmatch '(líneas añadidas, más de|área sensible)' }).Count) { return $false }
    if (@($v.motivos | Where-Object { $_ -match $prohibidos }).Count) { return $false }
    # Serialización, instalador, verificación y esquemas nunca se unen solos.
    if (@($v.motivos | Where-Object { $_ -match '(?i)(Serializ|Install|instalador|Verif|schema/)' }).Count) {
        $estado.agy += $clave
        Anotar $pendientesRuta "- #$($pr.number) ``$($pr.head.sha.Substring(0,8))`` revisar: toca serialización, instalador, verificación o esquemas; nunca se une solo ($(Ahora))"
        return $false
    }
    if ($pr.body -notmatch '(?i)(fallan? sin el arreglo|sin el arreglo[^.\n]{0,80}fall)') {
        $estado.agy += $clave
        Anotar $pendientesRuta "- #$($pr.number) ``$($pr.head.sha.Substring(0,8))`` revisar: la descripción no dice que la prueba de regresión falle sin el arreglo ($(Ahora))"
        return $false
    }
    $estado.agy += $clave
    Guardar
    $o = try { (& $powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'revisar-pr-agy.ps1') $pr.number 2>&1 | Out-String) | ConvertFrom-Json } catch { $null }
    if (-not $o -or $o.veredicto -ne 'unir') {
        $motivo = if ($o) { "agy dijo $($o.veredicto): $((@($o.razones) | Select-Object -First 2) -join '; ')" } else { 'agy no respondió (tope diario, 503 o error de cuenta)' }
        Anotar $pendientesRuta "- #$($pr.number) ``$($pr.head.sha.Substring(0,8))`` revisar: $motivo ($(Ahora))"
        return $false
    }
    if ((Uniones-Hoy) -ge $MaxUnionesDia) { Anotar $pendientesRuta "- #$($pr.number) revisar: tope de $MaxUnionesDia uniones del día ($(Ahora))"; return $false }
    $actual = Api "repos/$Repo/pulls/$($pr.number)"
    if ($actual.head.sha -ne $pr.head.sha -or $actual.mergeable -ne $true) { return $false }
    if ($actual.draft) { gh pr ready $pr.number --repo $Repo | Out-Null }
    $u = gh api -X PUT "repos/$Repo/pulls/$($pr.number)/merge" -f merge_method=squash -f sha=$($pr.head.sha) 2>&1
    if ($LASTEXITCODE -ne 0) { Anotar $pendientesRuta "- #$($pr.number) revisar: la unión falló ($(Ahora))"; return $false }
    Anotar $unionesRuta "| #$($pr.number) | ``$(($u | ConvertFrom-Json).sha)`` | $(Ahora) | regla (b) con agy |"
    $true
}
function Escribir-Informe {
    $hoy = Get-Date -Format 'yyyy-MM-dd'
    $uniones = if (Test-Path -LiteralPath $unionesRuta) { @([IO.File]::ReadAllLines($unionesRuta, [Text.Encoding]::UTF8) | Where-Object { $_ -like '| #*' -and $_ -like "*$hoy*" }) } else { @() }
    $pend = if (Test-Path -LiteralPath $pendientesRuta) { @([IO.File]::ReadAllLines($pendientesRuta, [Text.Encoding]::UTF8) | Where-Object { $_ -like "*$hoy*" -or $_ -like '- #*' } | Select-Object -Last 10) } else { @() }
    $inter = if (Test-Path -LiteralPath $intermitentesRuta) { @([IO.File]::ReadAllLines($intermitentesRuta, [Text.Encoding]::UTF8) | Where-Object { $_ -like "*$hoy*" }) } else { @() }
    $t = "# Informe del bucle de la nube ($(Ahora))`n`n- Sesiones lanzadas: $($estado.sesiones) de $MaxSesiones en $($estado.pasadas) pasadas.`n" +
        "- Uniones de hoy: $($uniones.Count) de $MaxUnionesDia.`n- Intermitencias de hoy: $($inter.Count).`n- Freno o parada: $(if ($script:parada) { $script:parada } else { 'ninguno, sigue en marcha' }).`n`n" +
        "## Uniones de hoy`n`n$($uniones -join "`n")`n`n## Últimos pendientes`n`n$($pend -join "`n")`n`n## Intermitencias de hoy`n`n$($inter -join "`n")`n"
    [IO.File]::WriteAllText($informeRuta, $t, $utf8)
    $estado.informe = (Get-Date).ToString('o')
}

$estado = [pscustomobject]@{ inicio = (Get-Date).ToString('o'); latido = ''; sesiones = 0; pasadas = 0; parada = ''; informe = (Get-Date).ToString('o'); reintentos = [pscustomobject]@{}; agy = @() }
$fin = (Get-Date).AddHours($Horas)
$script:parada = ''
$vacias = 0
Add-Type -Namespace NubeContinuo -Name Energia -MemberDefinition '[System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint estado);'
[void][NubeContinuo.Energia]::SetThreadExecutionState([uint32]2147483649)
try {
    gh api user --jq .login | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'gh no está autenticado' }
    while (-not $script:parada) {
        if ($estado.sesiones -ge $MaxSesiones) { $script:parada = "tope de $MaxSesiones sesiones"; break }
        if ((Get-Date) -ge $fin) { $script:parada = "$Horas h cumplidas"; break }
        & $powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'generar-cola-nube-caza.ps1') | Out-Null

        $antes = Uniones-Hoy
        $esta = [Math]::Min($PorPasada, $MaxSesiones - $estado.sesiones)
        $horasPasada = [Math]::Max(0.5, [Math]::Min(3, ($fin - (Get-Date)).TotalHours))
        & $powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'nube-ciclo.ps1') -MaxSesiones $esta -Simultaneas $Simultaneas `
            -MaxUniones ([Math]::Max(0, $MaxUnionesDia - $antes)) -Horas $horasPasada -MaxSinPr $MaxSinPr | Out-Null
        $ciclo = Get-Content -LiteralPath (Join-Path $nube 'estado-ciclo.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $estado.sesiones += @($ciclo.sesiones).Count
        $estado.pasadas++
        # Una pasada sin sesiones espera 10 min; tres seguidas paran el bucle (sin bucle apretado sobre la API).
        if (@($ciclo.sesiones).Count -eq 0) { $vacias++ } else { $vacias = 0 }
        Guardar
        if ($vacias -ge 3) { $script:parada = 'sin tareas nuevas que atender'; break }
        if ($vacias -gt 0) { Start-Sleep -Seconds 600 }
        if ($ciclo.parada -match 'sin PR útil|^error') { $script:parada = "nube-ciclo: $($ciclo.parada)"; break }
        if ((Uniones-Hoy) -gt $antes -and -not (Esperar-Main)) { break }

        foreach ($pr in @(Api "repos/$Repo/pulls?state=open&per_page=100" | Where-Object { $_.head.ref -like 'claude/*' })) {
            Reintentar-Ci $pr
            if ((Unir-Revisar $pr) -and -not (Esperar-Main)) { break }
            Guardar
        }
        if ($script:parada) { break }
        if ($ciclo.parada -match 'no quedan tareas' -and -not @(@(Api "repos/$Repo/pulls?state=open&per_page=100") | Where-Object { $_.head.ref -like 'claude/*' }).Count) {
            # Sin tareas nuevas ni PR que atender: el generador ya no encontró secciones.
            $script:parada = 'no quedan tareas ni secciones nuevas que cazar'; break
        }
        if (((Get-Date) - [datetime]$estado.informe).TotalHours -ge $HorasInforme) { Escribir-Informe }
        Guardar
    }
} catch {
    $script:parada = "error: $($_.Exception.Message)"
} finally {
    [void][NubeContinuo.Energia]::SetThreadExecutionState([uint32]2147483648)
    $estado.parada = $script:parada
    Escribir-Informe
    Guardar
    Write-Host "Bucle terminado: $($script:parada)"
}
