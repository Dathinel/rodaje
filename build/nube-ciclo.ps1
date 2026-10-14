# Ciclo de sesiones en la nube con crédito (D-046), sin invocar ningún modelo: lanza sesiones con nube-sesion.ps1,
# vigila los PR de ramas claude/ por REST, los juzga con revisar-pr.ps1 y une con squash los de veredicto «unir».
# Uso: build\nube-ciclo.ps1 [-MaxSesiones 12] [-MaxUniones 10] [-Horas 6] [-MinutosConsulta 3] [-MinutosSesion 75]
param(
    [int] $MaxSesiones = 12,
    [int] $MaxUniones = 10,
    [int] $Simultaneas = 3,
    [double] $Horas = 6,
    [double] $MinutosConsulta = 3,
    [double] $MinutosSesion = 75,
    [string] $Repo = 'Dathinel/rodaje'
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$utf8 = New-Object Text.UTF8Encoding $false
$raiz = Split-Path (git -C $PSScriptRoot rev-parse --path-format=absolute --git-common-dir)
$nube = Join-Path $raiz '.proceso\nube'
$estadoRuta = Join-Path $nube 'estado-ciclo.json'
$unionesRuta = Join-Path $nube 'uniones.md'
$pendientesRuta = Join-Path $nube 'pendientes-revision.md'
$colaTemporal = Join-Path $raiz '.claude\nube-prompts\cola-main.md'
$powershell = (Get-Command powershell.exe).Source

function Guardar { $estado.latido = (Get-Date).ToString('o'); [IO.File]::WriteAllText($estadoRuta, ($estado | ConvertTo-Json -Depth 6), $utf8) }
function Anotar([string] $ruta, [string] $cabecera, [string] $linea) {
    if (-not (Test-Path -LiteralPath $ruta)) { [IO.File]::WriteAllText($ruta, "$cabecera`n", $utf8) }
    [IO.File]::AppendAllText($ruta, "$linea`n", $utf8)
}
function Api([string] $ruta) {
    $r = gh api $ruta 2>&1
    if ($LASTEXITCODE -ne 0) { throw "gh api $ruta falló: $(($r | Out-String).Trim())" }
    $r | ConvertFrom-Json
}
function Tareas-Abiertas {
    git -C (Join-Path $raiz '.proceso') fetch --quiet origin
    if ($LASTEXITCODE -ne 0) { throw 'git fetch de rodaje-proceso falló' }
    New-Item -ItemType Directory -Force -Path (Split-Path $colaTemporal) | Out-Null
    $t = (git -C (Join-Path $raiz '.proceso') show origin/main:nube/cola.md) -join "`n"
    [IO.File]::WriteAllText($colaTemporal, $t, $utf8)
    foreach ($b in ($t -split '(?m)^(?=## \d+\. )')) {
        if ($b -notmatch '^## (\d+)\. ' -or $b -notmatch '(?m)^- \*\*Agente:\*\* claude-nube' -or $b -notmatch '(?m)^- \*\*Estado:\*\* pendiente') { continue }
        $n = [int]([regex]::Match($b, '^## (\d+)\.').Groups[1].Value)
        $rama = [regex]::Match($b, '(?m)^- \*\*Rama:\*\* `([^`]+)`').Groups[1].Value
        $caminos = @([regex]::Matches(([regex]::Match($b, '(?m)^- \*\*Caminos:\*\* (.+)$').Groups[1].Value), '`([^`]+)`') |
            ForEach-Object { $_.Groups[1].Value } | Where-Object { $_ -notmatch '^tests/[^/]+/?$' })
        [pscustomobject]@{ Numero = $n; Rama = $rama; Caminos = $caminos }
    }
}

$estado = if (Test-Path -LiteralPath $estadoRuta) { Get-Content -LiteralPath $estadoRuta -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
if (-not $estado -or $estado.terminado) {
    $estado = [pscustomobject]@{ inicio = (Get-Date).ToString('o'); latido = ''; terminado = $false; parada = ''
        sesiones = @(); uniones = 0; revisados = @(); seguidasSinPr = 0 }
}
$fin = ([datetime]$estado.inicio).AddHours($Horas)
Add-Type -Namespace NubeCiclo -Name Energia -MemberDefinition '[System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint estado);'
[void][NubeCiclo.Energia]::SetThreadExecutionState([uint32]2147483649)
$parada = ''
try {
    gh api user --jq .login | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'gh no está autenticado' }
    while (-not $parada) {
        # 1. Sesiones en curso: con PR, útiles; sin PR pasado el plazo, cuentan como sesión sin PR útil.
        $abiertos = @(Api "repos/$Repo/pulls?state=open&per_page=100")
        foreach ($s in @($estado.sesiones | Where-Object { $_.estado -eq 'en curso' })) {
            $pr = $abiertos | Where-Object { $_.head.ref -eq $s.rama } | Select-Object -First 1
            if (-not $pr) {
                $cerrado = @(Api "repos/$Repo/pulls?state=closed&head=$($Repo.Split('/')[0]):$($s.rama)&per_page=1")
                if ($cerrado) { $pr = $cerrado[0] }
            }
            if ($pr) { $s.estado = 'con PR'; $s.pr = $pr.number; $estado.seguidasSinPr = 0 }
            elseif ((Get-Date) -ge ([datetime]$s.lanzada).AddMinutes($MinutosSesion)) { $s.estado = 'sin PR'; $estado.seguidasSinPr++ }
            if ($s.estado -ne 'en curso' -and $s.pid) { Stop-Process -Id $s.pid -Force -ErrorAction SilentlyContinue }
        }
        Guardar
        if ($estado.seguidasSinPr -ge 3) { $parada = '3 sesiones seguidas sin PR útil'; break }

        # 2. Revisar y unir los PR de ramas claude/.
        foreach ($pr in @($abiertos | Where-Object { $_.head.ref -like 'claude/*' })) {
            $clave = "$($pr.number)@$($pr.head.sha)"
            if ($estado.revisados -contains $clave) { continue }
            $checks = @((Api "repos/$Repo/commits/$($pr.head.sha)/check-runs").check_runs)
            if (@($checks | Where-Object { $_.status -ne 'completed' }).Count -or $checks.Count -eq 0) { continue }
            $v = (& $powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'revisar-pr.ps1') $pr.number | Out-String) | ConvertFrom-Json
            $estado.revisados += $clave
            if ($v.veredicto -eq 'unir' -and $estado.uniones -lt $MaxUniones) {
                $actual = Api "repos/$Repo/pulls/$($pr.number)"
                if ($actual.head.sha -ne $pr.head.sha -or $actual.mergeable -ne $true) {
                    Anotar $pendientesRuta '# PR pendientes de revisión (nube-ciclo)' "- #$($pr.number) ``$($pr.head.sha.Substring(0,8))`` revisar: cambió o tiene conflictos al ir a unir ($(Get-Date -Format 'yyyy-MM-dd HH:mm'))"
                    continue
                }
                if ($actual.draft) { gh pr ready $pr.number --repo $Repo | Out-Null; if ($LASTEXITCODE -ne 0) { Anotar $pendientesRuta '# PR pendientes de revisión (nube-ciclo)' "- #$($pr.number) revisar: no se pudo pasar de borrador a listo"; continue } }
                $u = gh api -X PUT "repos/$Repo/pulls/$($pr.number)/merge" -f merge_method=squash -f sha=$($pr.head.sha) 2>&1
                if ($LASTEXITCODE -ne 0) { Anotar $pendientesRuta '# PR pendientes de revisión (nube-ciclo)' "- #$($pr.number) revisar: la unión falló: $(($u | Out-String).Trim())"; continue }
                $commit = ($u | ConvertFrom-Json).sha
                $estado.uniones++
                Anotar $unionesRuta "# Uniones de nube-ciclo (para revertir: git revert <commit>)`n`n| PR | Commit | Hora |`n|---|---|---|" "| #$($pr.number) | ``$commit`` | $(Get-Date -Format 'yyyy-MM-dd HH:mm') |"
            } else {
                $motivos = if ($v.veredicto -eq 'unir') { "tope de $MaxUniones uniones alcanzado" } else { ($v.motivos -join '; ') }
                Anotar $pendientesRuta '# PR pendientes de revisión (nube-ciclo)' "- #$($pr.number) ``$($pr.head.sha.Substring(0,8))`` $(if ($v.veredicto -eq 'unir') { 'revisar' } else { $v.veredicto }): $motivos"
            }
            Guardar
        }
        if ($estado.uniones -ge $MaxUniones) { $parada = "tope de $MaxUniones uniones"; break }

        # 3. Lanzar sesiones nuevas si hay hueco, sin cruzar caminos con las que están en curso.
        $enCurso = @($estado.sesiones | Where-Object { $_.estado -eq 'en curso' })
        $lanzadas = @($estado.sesiones).Count
        if ($lanzadas -lt $MaxSesiones -and $enCurso.Count -lt $Simultaneas) {
            $ocupados = @($enCurso | ForEach-Object { $_.caminos })
            $hechas = @($estado.sesiones | ForEach-Object { [int]$_.tarea })
            foreach ($t in @(Tareas-Abiertas)) {
                if ($enCurso.Count -ge $Simultaneas -or $lanzadas -ge $MaxSesiones) { break }
                if ($hechas -contains $t.Numero -or -not $t.Rama) { continue }
                if (git -C $raiz ls-remote --heads origin $t.Rama) { continue }
                if (@($t.Caminos | Where-Object { $ocupados -contains $_ }).Count) { continue }
                & $powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'nube-sesion.ps1') -Tareas "$($t.Numero)" -Cola $colaTemporal | Out-Null
                $prompt = Join-Path $raiz ".claude\nube-prompts\tarea-$($t.Numero).txt"
                $cmd = "Set-Location '$raiz'; claude --cloud (Get-Content -LiteralPath '$prompt' -Raw -Encoding UTF8)"
                $p = Start-Process -FilePath $powershell -ArgumentList @('-NoProfile', '-Command', $cmd) -PassThru
                $nueva = [pscustomobject]@{ tarea = $t.Numero; rama = $t.Rama; caminos = $t.Caminos; lanzada = (Get-Date).ToString('o'); pid = $p.Id; estado = 'en curso'; pr = $null }
                $estado.sesiones += $nueva
                $enCurso += $nueva
                $ocupados += $t.Caminos
                $lanzadas++
                Write-Host "Lanzada la tarea $($t.Numero) ($($t.Rama))"
            }
            Guardar
        }
        $enCurso = @($estado.sesiones | Where-Object { $_.estado -eq 'en curso' })
        if (-not $enCurso.Count -and @($estado.sesiones).Count -ge $MaxSesiones) { $parada = "tope de $MaxSesiones sesiones"; break }
        if (-not $enCurso.Count -and -not @(Tareas-Abiertas | Where-Object { @($estado.sesiones | ForEach-Object { [int]$_.tarea }) -notcontains $_.Numero -and -not (git -C $raiz ls-remote --heads origin $_.Rama) })) {
            $parada = 'no quedan tareas claude-nube abiertas'; break
        }
        if ((Get-Date) -ge $fin) { $parada = "$Horas h cumplidas"; break }
        Start-Sleep -Seconds ([int]($MinutosConsulta * 60))
    }
} catch {
    $parada = "error: $($_.Exception.Message)"
} finally {
    [void][NubeCiclo.Energia]::SetThreadExecutionState([uint32]2147483648)
    $estado.terminado = $true
    $estado.parada = $parada
    Guardar
    $s = @($estado.sesiones)
    Write-Host "Ciclo terminado: $parada. Sesiones $($s.Count) (con PR $(@($s | Where-Object { $_.estado -eq 'con PR' }).Count)), uniones $($estado.uniones)."
}
