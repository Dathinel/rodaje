# Veredicto mecánico de un PR, sin ningún modelo: unir, revisar o bloqueado, en JSON con los motivos.
# Uso: build\revisar-pr.ps1 <número> [-Repo Dathinel/rodaje]
param(
    [Parameter(Mandatory)] [int] $Pr,
    [string] $Repo = 'Dathinel/rodaje',
    [int] $MaxLineas = 400
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$checks = 'Linux (Core, Packages, Playback)', 'Windows (solución completa)'
$prohibido = '(?i)^(LICENSE|TRADEMARK\.md|Directory\.Build\.props|Directory\.Packages\.props|global\.json|\.github/|build/)|(^|/)(CLAUDE\.md|UpstreamAttribution\.cs|packages\.lock\.json)$'
$sensible = '(?i)(Serializ|Timing|Sync|Render|Engine|Install|instalador|Verif)'
$plazo = '(?i)(timeout|plazo|tope|deadline|budget|delay|wait|From(Milli)?Seconds|FromMinutes|AntiHang)'

$bloqueos = New-Object Collections.Generic.List[string]
$revisar = New-Object Collections.Generic.List[string]

$d = gh pr view $Pr --repo $Repo --json state,headRefName,mergeable,files,additions,statusCheckRollup | ConvertFrom-Json
if ($d.mergeable -eq 'UNKNOWN') { Start-Sleep -Seconds 5; $d = gh pr view $Pr --repo $Repo --json state,headRefName,mergeable,files,additions,statusCheckRollup | ConvertFrom-Json }

if ($d.state -ne 'OPEN') { $bloqueos.Add("el PR está $($d.state)") }
if ($d.headRefName -notmatch '^(claude|gemini)/') { $bloqueos.Add("rama $($d.headRefName) fuera de claude/ y gemini/") }
foreach ($nombre in $checks) {
    $c = @($d.statusCheckRollup | Where-Object { $_.name -eq $nombre })
    $estado = if ($c) { if ($c[0].conclusion) { $c[0].conclusion } else { $c[0].status } } else { 'AUSENTE' }
    if ($estado -in 'FAILURE', 'CANCELLED', 'TIMED_OUT', 'ACTION_REQUIRED') { $bloqueos.Add("check $nombre en $estado") }
    elseif ($estado -ne 'SUCCESS') { $revisar.Add("check $nombre en $estado") }
}
if ($d.mergeable -eq 'CONFLICTING') { $bloqueos.Add('tiene conflictos con main') }
elseif ($d.mergeable -ne 'MERGEABLE') { $revisar.Add("GitHub no sabe si se puede unir ($($d.mergeable))") }

$archivos = @($d.files | ForEach-Object { $_.path })
foreach ($a in $archivos) {
    if ($a -match $prohibido) { $bloqueos.Add("toca $a") }
    elseif ($a -match '\.csproj$') { $revisar.Add("toca $a (posibles dependencias)") }
    elseif ($a -match $sensible) { $revisar.Add("toca un área sensible: $a") }
}
if ($d.additions -gt $MaxLineas) { $revisar.Add("$($d.additions) líneas añadidas, más de $MaxLineas") }

# Reglas de las pruebas, sobre las líneas del diff de tests/.
$archivo = ''
$quitadas = New-Object Collections.Generic.List[string]
$puestas = New-Object Collections.Generic.List[string]
foreach ($linea in (gh pr diff $Pr --repo $Repo)) {
    if ($linea -match '^\+\+\+ b/(.+)$') { $archivo = $Matches[1]; continue }
    if ($linea -match '^(---|\+\+\+) ') { continue }
    if ($archivo -match '\.csproj$' -and $linea -match '^\+.*<(PackageReference|ProjectReference)') { $bloqueos.Add("dependencia nueva en ${archivo}: $($linea.Substring(1).Trim())") }
    if ($archivo -notmatch '^tests/') { continue }
    if ($linea -match '^\+') {
        $t = $linea.Substring(1).Trim()
        $puestas.Add($t)
        if ($t -match '\bSkip\s*=' -and $t -notmatch 'Skip\s*=\s*"(Defecto candidato|Descartada):') { $bloqueos.Add("Skip nuevo en ${archivo}: $t") }
        if ($t -match 'TiempoReal') { $bloqueos.Add("prueba TiempoReal nueva en $archivo") }
    } elseif ($linea -match '^-') {
        $quitadas.Add($linea.Substring(1).Trim())
    }
}
foreach ($q in $quitadas) {
    if ($q -match '^\[(Fact|Theory)' -or $q -match '\bAssert\.') {
        if (-not $puestas.Contains($q)) { $bloqueos.Add("prueba o aserción borrada o cambiada: $q") }
    }
    if ($q -match $plazo -and $q -match '\d') {
        # La misma línea con números más grandes es un plazo subido.
        $molde = [regex]::Replace($q, '\d+(\.\d+)?', '#')
        foreach ($p in $puestas) {
            if ([regex]::Replace($p, '\d+(\.\d+)?', '#') -ne $molde) { continue }
            $a = @([regex]::Matches($q, '\d+(\.\d+)?') | ForEach-Object { [double]$_.Value })
            $b = @([regex]::Matches($p, '\d+(\.\d+)?') | ForEach-Object { [double]$_.Value })
            for ($i = 0; $i -lt $a.Count; $i++) { if ($b[$i] -gt $a[$i]) { $bloqueos.Add("plazo subido: '$q' -> '$p'"); break } }
        }
    }
}

$veredicto = if ($bloqueos.Count) { 'bloqueado' } elseif ($revisar.Count) { 'revisar' } else { 'unir' }
$soloPruebasODocs = -not ($archivos | Where-Object { $_ -notmatch '^(tests|docs)/' })
[ordered]@{ pr = $Pr; veredicto = $veredicto; motivos = @($bloqueos) + @($revisar); soloPruebasODocs = [bool]$soloPruebasODocs
    archivos = $archivos.Count; lineasAnadidas = $d.additions } | ConvertTo-Json -Depth 3
