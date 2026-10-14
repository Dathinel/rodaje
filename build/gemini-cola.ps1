# Toma la siguiente tarea «gemini-local» de la cola y la hace con agy, sin gastar tokens de Claude (D-045).
# Gemini no escribe ni ejecuta nada: devuelve el archivo y este script lo escribe, lo prueba y hace el commit.
# Uso: build\gemini-cola.ps1 [-Cola <cola.md>] [-Publicar] [-Simular]
# -RespuestaGuardada <salida.json> reproduce una respuesta guardada de agy en vez de llamarlo (pruebas del script, sin cuota ni métricas).
param(
    [string] $Cola,
    [string] $Modelo = 'gemini-3.8-flash-medium',
    [string] $Esfuerzo = 'medium',
    [int] $MaxBytes = 65536,
    [int] $MaxReintentos = 2,
    [int] $TopeDiario = 10,
    [double] $MinutosAgy = 16,
    [double] $MinutosPruebas = 10,
    [string] $RespuestaGuardada,
    [switch] $Publicar,
    [switch] $Simular
)
$ErrorActionPreference = 'Stop'
$utf8 = New-Object Text.UTF8Encoding $false

# Raíz del repo principal aunque el script corra desde un worktree.
$gitComun = git -C $PSScriptRoot rev-parse --path-format=absolute --git-common-dir
$repo = Split-Path $gitComun
if (-not $Cola) { $Cola = Join-Path $repo '.proceso\nube\cola.md' }
$metricas = Join-Path $repo '.proceso\gemini\metricas.md'
$agy = Join-Path $env:LOCALAPPDATA 'agy\bin\agy.exe'

$permitido = '^(tests/|docs/|src/Rodaje\.(Core|Packages|Playback)/)'
$excluido = '(?i)(Serializ|Timing|Sync|Render|Engine|Install|Verif|LICENSE|UpstreamAttribution|CLAUDE\.md|^build/|^\.github/|publicar)'
# Lo generado se ejecuta en este equipo: nada de procesos, archivos, red, registro ni código nativo.
$peligroso = '(?i)\b(Process\b|ProcessStartInfo|File\.|Directory\.|FileStream|FileInfo|DirectoryInfo|HttpClient|WebClient|WebRequest|Socket|TcpClient|Registry|DllImport|LibraryImport|Marshal\.|Environment\.(Exit|SetEnvironmentVariable|FailFast)|Assembly\.Load|AppDomain|unsafe|stackalloc|Thread\.Sleep)'

function Leer-Tarea {
    $texto = [IO.File]::ReadAllText((Resolve-Path $Cola).Path, [Text.Encoding]::UTF8)
    foreach ($bloque in ($texto -split '(?m)^(?=## \d+\. )')) {
        if ($bloque -notmatch '^## (\d+)\. (.+)') { continue }
        $t = @{ Numero = $Matches[1]; Titulo = $Matches[2].Trim(); Tipo = 'pruebas' }
        foreach ($campo in 'Estado', 'Agente', 'Tipo', 'Tema', 'Fuente', 'Documento', 'Destino', 'Criterio', 'Qué') {
            if ($bloque -match "(?m)^- \*\*${campo}:\*\* (.+?)\s*$") { $t[$campo] = $Matches[1].Trim().Trim('`') }
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

function Contar-Hoy {
    if (-not (Test-Path -LiteralPath $metricas)) { return 0 }
    $hoy = Get-Date -Format 'yyyy-MM-dd'
    @([IO.File]::ReadAllLines($metricas, [Text.Encoding]::UTF8) | Where-Object { $_ -like "| $hoy *" }).Count
}

function Citar([string] $s) { '"' + ($s -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"' }

function Ejecutar([string] $exe, [string[]] $argumentos, [string] $dir, [double] $minutos, [hashtable] $entorno = @{}) {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $exe
    $info.Arguments = ($argumentos | ForEach-Object { Citar $_ }) -join ' '
    $info.WorkingDirectory = $dir
    $info.UseShellExecute = $false
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.Encoding]::UTF8
    $info.StandardErrorEncoding = [Text.Encoding]::UTF8
    foreach ($k in $entorno.Keys) { $info.EnvironmentVariables[$k] = $entorno[$k] }
    $p = [Diagnostics.Process]::Start($info)
    $p.StandardInput.Close()
    $err = $p.StandardError.ReadToEndAsync()
    $out = $p.StandardOutput.ReadToEndAsync()
    $agotado = -not $p.WaitForExit([int]($minutos * 60000))
    if ($agotado) { & taskkill.exe /T /F /PID $p.Id 2>&1 | Out-Null; [void]$p.WaitForExit(15000) }
    # Un nieto que sobreviva (servidor de compilación) puede dejar abiertas las tuberías: no se le espera más de 15 s.
    [void][Threading.Tasks.Task]::WaitAll(@($out, $err), 15000)
    [pscustomobject]@{ Codigo = $(if ($agotado) { -1 } else { $p.ExitCode }); Agotado = $agotado
        Salida = $(if ($out.IsCompleted) { $out.Result } else { '' }); Errores = $(if ($err.IsCompleted) { $err.Result } else { '' }) }
}

function Pedir-Archivo([string] $instruccion, [string] $esquema, [string] $prefijo) {
    if ($RespuestaGuardada) {
        $r = [pscustomobject]@{ Codigo = 0; Agotado = $false; Salida = [IO.File]::ReadAllText($RespuestaGuardada, [Text.Encoding]::UTF8); Errores = '' }
    } elseif ($env:GEMINI_COLA_SIN_AGY) {
        throw 'GEMINI_COLA_SIN_AGY está puesta: no se llama a agy'
    } else {
        $r = Ejecutar $agy @('-p', $instruccion, '--add-dir', $worktree, '--sandbox', '--mode', 'plan', '--output-format', 'json',
            '--json-schema', $esquema, '--print-timeout', '15m', '--model', $Modelo, '--effort', $Esfuerzo) $worktree $MinutosAgy
    }
    if ($r.Agotado) { throw "agy no terminó en $MinutosAgy min; se cortó" }
    [IO.File]::WriteAllText((Join-Path $salida "$prefijo-salida.json"), $r.Salida, $utf8)
    [IO.File]::WriteAllText((Join-Path $salida "$prefijo-errores.txt"), $r.Errores, $utf8)
    # El código 0 y SUCCESS no prueban nada: vale el contenido.
    $json = $r.Salida | ConvertFrom-Json
    $script:tokens += [int]$json.usage.total_tokens
    $out = $json.structured_output
    if (-not $out -or [string]::IsNullOrWhiteSpace($out.content)) { throw "agy no devolvió contenido (estado $($json.status)): $(($r.Errores -split "`n")[0])" }
    $bytes = $utf8.GetByteCount($out.content)
    if ($bytes -gt $MaxBytes) { throw "Respuesta de $bytes bytes; el tope es $MaxBytes" }
    if ($out.content -match $peligroso) { throw "El archivo generado usa una API no permitida ($($Matches[0])); no se ejecuta" }
    $out
}

function Probar([string] $ronda) {
    $proyecto = ($destino -split '/')[0..1] -join '\'
    $clase = [IO.Path]::GetFileNameWithoutExtension($destino)
    $trx = "$ronda.trx"
    $r = Ejecutar "$env:ComSpec" @('/c', 'build\dotnet-local.cmd', 'test', $proyecto, '--filter', "FullyQualifiedName~$clase",
        '--blame-hang-timeout', '60s', '--logger', "trx;LogFileName=$trx", '--results-directory', $salida) $worktree $MinutosPruebas `
        @{ RODAJE_ENTORNO = (Join-Path $repo '.entorno'); DOTNET_CLI_UI_LANGUAGE = 'en'; VSLANG = '1033' }
    $texto = $r.Salida + $r.Errores
    if ($r.Agotado) { $texto += "`nerror: las pruebas no terminaron en $MinutosPruebas min y se cortaron" }
    [IO.File]::WriteAllText((Join-Path $salida "$ronda-pruebas.txt"), $texto, $utf8)
    $fallos = @()
    $ejecutadas = 0
    $pasadas = 0
    $ruta = Join-Path $salida $trx
    if (Test-Path -LiteralPath $ruta) {
        [xml] $x = Get-Content -LiteralPath $ruta -Raw -Encoding UTF8
        $contadores = $x.TestRun.ResultSummary.Counters
        $ejecutadas = [int]$contadores.executed
        $pasadas = [int]$contadores.passed
        foreach ($u in $x.TestRun.Results.UnitTestResult) {
            if ($u.outcome -eq 'Failed') {
                $fallos += [pscustomobject]@{ Prueba = $u.testName; Mensaje = (($u.Output.ErrorInfo.Message + "`n" + $u.Output.ErrorInfo.StackTrace) -split "`n" | Select-Object -First 40) -join "`n" }
            }
        }
    }
    $advertencias = @(($texto -split "`n") | Where-Object { $_ -match 'warning [A-Z]+\d+' -and $_ -match [regex]::Escape(($destino -replace '/', '\')) })
    # Una clase con otro nombre, un archivo sin pruebas o todo con Skip dan cero ejecutadas: no es verde.
    if ($ejecutadas -eq 0) { $texto += "`nerror: no se ejecutó ninguna prueba de la clase $clase (¿otro nombre de clase, sin pruebas o todas con Skip?)" }
    $ok = -not $r.Agotado -and $r.Codigo -eq 0 -and $advertencias.Count -eq 0 -and $ejecutadas -gt 0 -and $pasadas -eq $ejecutadas
    [pscustomobject]@{ Ok = $ok; Compila = (Test-Path -LiteralPath $ruta) -and -not $r.Agotado; Ejecutadas = $ejecutadas; Pasadas = $pasadas
        Fallos = $fallos; Advertencias = $advertencias
        Recorte = ((($texto -split "`n") | Where-Object { $_ -match 'error|warning|Failed|Assert|Expected|Actual|   at |hang|Blame' } | Select-Object -First 200) -join "`n") }
}

function Metodo([string] $prueba) { (($prueba -split '\(')[0] -split '\.')[-1] }

function Omitir([string] $codigo, [string] $metodo, [string] $motivo) {
    $m = $motivo -replace '"', "'" -replace '[\r\n]+', ' '
    if ($m.Length -gt 180) { $m = $m.Substring(0, 180) + '…' }
    $patron = '\[(Fact|Theory)(\(\))?\]((?:\s*\[[^\]]*\])*\s*)(public\s+(?:async\s+)?[\w<>\[\]]+\s+' + [regex]::Escape($metodo) + '\s*\()'
    ([regex] $patron).Replace($codigo, { param($g) "[$($g.Groups[1].Value)(Skip = ""$m"")]$($g.Groups[3].Value)$($g.Groups[4].Value)" }, 1)
}

function Marcar-Hecha([string] $archivo, [string] $numero, [string] $pr) {
    $texto = [IO.File]::ReadAllText($archivo, [Text.Encoding]::UTF8)
    $patron = '(?m)(^## ' + $numero + '\. [^\r\n]*\r?\n(?:(?!^## )[^\r\n]*\r?\n)*?- \*\*Estado:\*\* )pendiente[^\r\n]*'
    $nuevo = ([regex] $patron).Replace($texto, { param($g) $g.Groups[1].Value + "hecha ($pr, Gemini; pendiente de la revisión de Daniel)" }, 1)
    if ($nuevo -eq $texto) { throw "No se pudo marcar como hecha la tarea $numero en la cola" }
    [IO.File]::WriteAllText($archivo, $nuevo, $utf8)
}

function Normalizar([string] $s) { ($s -replace '[\s`*_>#|-]+', ' ').Trim().ToLowerInvariant() }

# --- Tarea ---
$hechasHoy = Contar-Hoy
if ($hechasHoy -ge $TopeDiario) { Write-Host "Tope diario alcanzado ($hechasHoy de $TopeDiario en metricas.md)."; exit 2 }
$tarea = Leer-Tarea
if (-not $tarea) { Write-Host 'No hay tareas gemini-local pendientes.'; exit 0 }
$caza = $tarea.Tipo -eq 'caza'
$requeridos = @('Tema', 'Fuente', 'Destino', 'Criterio') + $(if ($caza) { 'Documento' } else { 'Qué' })
foreach ($c in $requeridos) { if (-not $tarea[$c]) { throw "La tarea $($tarea.Numero) no tiene el campo $c" } }
if ($tarea.Tema -notmatch '^[a-z0-9-]{3,40}$') { throw "Tema no válido: $($tarea.Tema)" }
$fuente = Comprobar-Ruta $tarea.Fuente 'un archivo fuente'
$destino = Comprobar-Ruta $tarea.Destino 'un destino'
if ($fuente -notmatch '^src/') { throw 'La fuente debe estar en src/' }
if ($destino -notmatch '^tests/[^/]+/.+\.cs$') { throw 'El destino debe ser un .cs dentro de un proyecto de tests/' }
$documento = if ($caza) { Comprobar-Ruta $tarea.Documento 'un documento' } else { $null }

$rama = "gemini/$($tarea.Tema)"
$worktree = Join-Path $repo ".claude\worktrees\gemini-$($tarea.Tema)"
Write-Host "Tarea $($tarea.Numero) ($($tarea.Tipo)): $($tarea.Titulo)`nRama $rama -> $destino"
if ($Simular) { exit 0 }
if (Test-Path -LiteralPath $worktree) { throw "El worktree ya existe: $worktree" }

$salida = Join-Path $repo ".claude\gemini-salidas\$(Get-Date -Format 'yyyyMMdd-HHmmss')-$($tarea.Tema)"
New-Item -ItemType Directory -Force -Path $salida | Out-Null
$script:tokens = 0
$inicio = Get-Date
$res = [ordered]@{ tarea = $tarea.Numero; tipo = $tarea.Tipo; rama = $rama; destino = $destino; modelo = $Modelo
    rondas = 0; tokens = 0; segundos = 0; desenlace = ''; motivo = ''; descartadas = @(); candidatos = @(); commit = ''; pr = '' }
$creado = $false
$terminado = $false
try {
    git -C $repo fetch --quiet origin
    git -C $repo worktree add --quiet $worktree -b $rama origin/main
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo crear el worktree' }
    $creado = $true
    foreach ($r in @($fuente) + @($documento | Where-Object { $_ })) {
        if (-not (Test-Path -LiteralPath (Join-Path $worktree $r))) { throw "No existe: $r" }
    }
    $destinoCompleto = Join-Path $worktree ($destino -replace '/', '\')
    if (Test-Path -LiteralPath $destinoCompleto) { throw "El destino ya existe; no se sobrescribe: $destino" }
    New-Item -ItemType Directory -Force -Path (Split-Path $destinoCompleto) | Out-Null

    $esquema = Join-Path $salida 'esquema.json'
    $comun = "Archivo fuente: $fuente. Archivo destino: $destino. Criterio de aceptación: $($tarea.Criterio). " +
        'Lee solo los archivos nombrados y, si hace falta para el estilo, una prueba vecina. Pruebas xUnit, nombres en inglés, sin comentarios ni dependencias nuevas, ' +
        'sin acceso a archivos, procesos ni red. Devuelve el archivo destino completo en el campo content. No escribas ni modifiques archivos, no ejecutes comandos y no leas URLs.'
    if ($caza) {
        '{"type":"object","properties":{"content":{"type":"string"},"claims":{"type":"array","items":{"type":"object","properties":{"test":{"type":"string"},"cita":{"type":"string"}},"required":["test","cita"],"additionalProperties":false}}},"required":["content","claims"],"additionalProperties":false}' |
            Set-Content -LiteralPath $esquema -Encoding ascii
        $instruccion = "Caza de defectos. Documento: $documento. Escribe pruebas de lo que el documento promete sobre el comportamiento del archivo fuente, no de lo que hace el código. " +
            'Cada prueba debe apoyarse en una frase del documento: en claims pon, por cada método de prueba, su nombre en test y la frase copiada literalmente del documento en cita. ' +
            "No inventes promesas que el documento no haga. $comun"
    } else {
        '{"type":"object","properties":{"content":{"type":"string"}},"required":["content"],"additionalProperties":false}' |
            Set-Content -LiteralPath $esquema -Encoding ascii
        $instruccion = "$($tarea.Qué) $comun"
    }

    $respuesta = $null
    $prueba = $null
    for ($ronda = 1; $ronda -le 1 + $MaxReintentos; $ronda++) {
        $res.rondas = $ronda
        if ($ronda -gt 1) {
            $instruccion = "El archivo $destino no pasa. Corrígelo y devuelve el archivo completo. Salida de dotnet test:`n$($prueba.Recorte)`n" +
                $(if ($caza) { 'Solo corrige errores de compilación o advertencias; no cambies lo que las pruebas esperan. Mantén claims. ' } else { '' }) + $comun
        }
        $respuesta = Pedir-Archivo $instruccion $esquema "ronda$ronda"
        [IO.File]::WriteAllText($destinoCompleto, $respuesta.content, $utf8)
        if (-not (Test-Path -LiteralPath $destinoCompleto)) { throw 'El archivo no quedó escrito' }
        $prueba = Probar "ronda$ronda"
        if ($caza) { if ($prueba.Compila -and $prueba.Advertencias.Count -eq 0 -and $prueba.Ejecutadas -gt 0) { break } }
        elseif ($prueba.Ok) { break }
    }

    if ($caza) {
        if (-not $prueba.Compila) { throw "No compila tras $($res.rondas) rondas" }
        $texto = Normalizar ([IO.File]::ReadAllText((Join-Path $worktree $documento), [Text.Encoding]::UTF8))
        $citas = @{}
        foreach ($c in @($respuesta.claims)) {
            $n = Normalizar $c.cita
            if ($n.Length -ge 15 -and $texto.Contains($n)) { $citas[$c.test] = $c.cita }
        }
        $codigo = $respuesta.content
        $metodos = [regex]::Matches($codigo, '\[(?:Fact|Theory)[^\]]*\](?:\s*\[[^\]]*\])*\s*public\s+(?:async\s+)?[\w<>\[\]]+\s+(\w+)\s*\(') | ForEach-Object { $_.Groups[1].Value }
        foreach ($m in $metodos) {
            if (-not $citas.ContainsKey($m)) { $res.descartadas += $m; $codigo = Omitir $codigo $m 'Descartada: sin cita literal al documento' }
        }
        $vistos = @{}
        foreach ($f in $prueba.Fallos) {
            $m = Metodo $f.Prueba
            if (-not $citas.ContainsKey($m) -or $vistos.ContainsKey($m)) { continue }
            $vistos[$m] = $true
            $res.candidatos += [ordered]@{ prueba = $m; documento = $documento; cita = $citas[$m]; fallo = $f.Mensaje }
            $codigo = Omitir $codigo $m "Defecto candidato: $documento dice «$($citas[$m])»"
        }
        [IO.File]::WriteAllText($destinoCompleto, $codigo, $utf8)
        $final = Probar 'final'
        if (-not $final.Ok) { throw "Ejecución final de la caza sin verde: $($final.Pasadas) de $($final.Ejecutadas) pruebas ejecutadas pasaron tras omitir candidatos y descartadas" }
        $res.desenlace = if ($res.candidatos.Count) { "$($res.candidatos.Count) defectos candidatos" } else { 'sin defectos' }
    } else {
        if (-not $prueba.Ok) { throw "Las pruebas no pasan tras $($res.rondas) rondas" }
        $res.desenlace = 'pruebas en verde'
    }

    git -C $worktree add -- $destino
    $mensaje = "Pruebas: $($tarea.Titulo) (tarea $($tarea.Numero))`n`n$($res.desenlace); $($res.rondas) rondas.`n`nAssisted-by: Gemini ($Modelo) vía Antigravity CLI"
    $archivoMensaje = Join-Path $salida 'commit.txt'
    [IO.File]::WriteAllText($archivoMensaje, $mensaje, $utf8)
    git -C $worktree commit --quiet -F $archivoMensaje
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo hacer el commit' }
    $res.commit = (git -C $worktree rev-parse --short HEAD)
    $terminado = $true

    if ($Publicar) {
        git -C $worktree push --quiet -u origin $rama
        if ($LASTEXITCODE -ne 0) { throw 'No se pudo hacer push' }
        $cuerpo = "Escrito por Gemini ($Modelo) con ``build/gemini-cola.ps1`` (D-045); pendiente de la revisión de Daniel.`n`n" +
            "- Tarea $($tarea.Numero) ($($tarea.Tipo)): $($res.desenlace), $($res.rondas) rondas, $($script:tokens) tokens.`n" +
            $(if ($res.candidatos.Count) { "- Defectos candidatos (omitidos con ``Skip``): " + (($res.candidatos | ForEach-Object { "``$($_.prueba)``" }) -join ', ') + "`n" } else { '' }) +
            $(if ($res.descartadas.Count) { "- Descartadas por no citar el documento: $($res.descartadas.Count)`n" } else { '' }) +
            "`nEn borrador: no se une sin la aprobación de Daniel."
        $archivoCuerpo = Join-Path $salida 'pr.md'
        [IO.File]::WriteAllText($archivoCuerpo, $cuerpo, $utf8)
        $res.pr = (gh pr create --repo Dathinel/rodaje --base main --head $rama --draft --title "Pruebas: $($tarea.Titulo) (Gemini)" --body-file $archivoCuerpo)
        if ($LASTEXITCODE -ne 0) { throw 'No se pudo abrir el PR' }
        Marcar-Hecha $Cola $tarea.Numero $res.pr
    }
} catch {
    $res.motivo = $_.Exception.Message
    if (-not $res.desenlace -or $res.desenlace -eq 'pruebas en verde') { $res.desenlace = 'fallida' }
} finally {
    $res.tokens = $script:tokens
    $res.segundos = [int]((Get-Date) - $inicio).TotalSeconds
    if ($creado -and -not $terminado) {
        git -C $repo worktree remove --force $worktree 2>$null
        git -C $repo branch -D $rama --quiet 2>$null
    }
    [IO.File]::WriteAllText((Join-Path $salida 'resultado.json'), ($res | ConvertTo-Json -Depth 5), $utf8)
    if (-not $RespuestaGuardada -and (Test-Path -LiteralPath $metricas)) {
        $fila = "| $(Get-Date -Format 'yyyy-MM-dd HH:mm') | $($tarea.Numero) $($tarea.Tipo) | $Modelo | $($res.segundos) | $($res.tokens) | $($res.rondas) rondas: $($res.desenlace) | | | |"
        Add-Content -LiteralPath $metricas -Value $fila -Encoding UTF8
    }
}
$res | ConvertTo-Json -Depth 3 | Write-Host
if (-not $terminado) { exit 1 }
