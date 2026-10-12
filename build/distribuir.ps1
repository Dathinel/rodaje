# Arma la distribución de Rodaje en dist\distribucion\: los zip «base» y «completa» y el índice del catálogo.
#   base      dist\Rodaje tal cual: Rodaje.exe (el lanzador) y app\ con el programa y FFmpeg LGPL, sin complementos.
#   completa  base + en app\: paquetes en paquetes\<id>, módulos en modulos\<id> y sus modelos en modelos\<sha256>.
#             Al descomprimir se ve solo Rodaje.exe y la carpeta app, que el lanzador oculta la primera vez.
#   indice.json  lo que el gestor de complementos puede descargar (paquetes, módulos y modelos) con URL, SHA-256 y
#             tamaño; se firma después con build\firmar-indice.ps1. El gestor y este script usan los mismos
#             .paquete, así que lo que trae la completa es exactamente lo que el gestor bajaría.
# No sube nada: las releases se crean a mano (o con gh) después de revisar y firmar.
# Uso:
#   powershell -NoProfile -ExecutionPolicy Bypass -File build\distribuir.ps1 [-SinPublicar] [-SinCompleta]
#       [-Complementos <carpeta con <repo>\dist\*.paquete>] [-Paquetes a.paquete,b.paquete] [-Modelos <caché de modelos>]
#       [-Excluir modelo-x,...] [-Version 1.0.0] [-Salida dist\distribucion]
[CmdletBinding()]
param(
    [string]$Version = '',
    [string]$Complementos = '',
    [string[]]$Paquetes = @(),
    [string]$Modelos = '',
    [string[]]$Excluir = @(),
    [string]$Salida = '',
    [switch]$SinPublicar,
    [switch]$SinCompleta
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$Raiz = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'comun.psm1') -DisableNameChecking -Force
$E = Inicializar-Rodaje $Raiz
$App = Join-Path $Raiz 'dist\Rodaje'
if (-not $Salida) { $Salida = Join-Path $Raiz 'dist\distribucion' }
if (-not $Modelos) { $Modelos = Join-Path $E.Entorno 'modelos' }
$LimiteAsset = 2GB
$Utf8 = New-Object System.Text.UTF8Encoding($false)

if (-not $Version) {
    $props = [xml](Get-Content -LiteralPath (Join-Path $Raiz 'Directory.Build.props') -Raw -Encoding UTF8)
    $Version = @($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
    if (-not $Version) { throw 'No encuentro <Version> en Directory.Build.props; pásala con -Version.' }
}

Titulo "Distribución de Rodaje $Version"

if (-not $SinPublicar) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'app.ps1') -SoloPublicar
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo publicar Rodaje.' }
}
foreach ($exe in 'Rodaje.exe', 'app\Rodaje.exe') {
    if (-not (Test-Path -LiteralPath (Join-Path $App $exe))) { throw "No está publicado $App\$exe (quita -SinPublicar)." }
}

if ($Paquetes.Count -eq 0) {
    if (-not $Complementos) { $Complementos = Join-Path (Split-Path -Parent $Raiz) 'Complementos' }
    if (Test-Path -LiteralPath $Complementos) {
        $Paquetes = @(Get-ChildItem -LiteralPath $Complementos -Directory | ForEach-Object {
            $dist = Join-Path $_.FullName 'dist'
            if (Test-Path -LiteralPath $dist) { Get-ChildItem -LiteralPath $dist -Filter '*.paquete' | Sort-Object LastWriteTime | Select-Object -Last 1 | ForEach-Object { $_.FullName } }
        })
    } else {
        Aviso "No encuentro ${Complementos}: la completa irá sin paquetes."
    }
}

function Sha256([string]$ruta) { (Get-FileHash -LiteralPath $ruta -Algorithm SHA256).Hash.ToLowerInvariant() }

function Leer-Manifiesto([string]$ruta) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ruta)
    try {
        $entrada = $zip.Entries | Where-Object { $_.FullName -eq 'paquete.json' -or $_.FullName -match '^[^/]+/paquete\.json$' } | Select-Object -First 1
        if (-not $entrada) { throw "$ruta no tiene paquete.json." }
        $lector = New-Object System.IO.StreamReader($entrada.Open(), $Utf8)
        try { $texto = $lector.ReadToEnd() } finally { $lector.Dispose() }
        $m = $texto | ConvertFrom-Json
        $m | Add-Member -NotePropertyName Envoltorio -NotePropertyValue ($entrada.FullName -replace 'paquete\.json$', '')
        return $m
    } finally { $zip.Dispose() }
}

# Extrae el .paquete en destino quitando la carpeta envoltorio de las releases, si la hay.
function Extraer([string]$ruta, [string]$destino, [string]$envoltorio) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ruta)
    try {
        foreach ($e in $zip.Entries) {
            $nombre = $e.FullName
            if ($envoltorio) { if (-not $nombre.StartsWith($envoltorio)) { continue }; $nombre = $nombre.Substring($envoltorio.Length) }
            if (-not $nombre -or $nombre.EndsWith('/')) { continue }
            if ($nombre -match '(^|/)\.\.(/|$)' -or $nombre -match '^[/\\]|:') { throw "Ruta no permitida en ${ruta}: $($e.FullName)" }
            $final = Join-Path $destino ($nombre -replace '/', '\')
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $final) | Out-Null
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($e, $final, $true)
        }
    } finally { $zip.Dispose() }
}

function Obtener-Modelo($modelo) {
    $cache = Join-Path $Modelos $modelo.sha256
    if ((Test-Path -LiteralPath $cache) -and (Sha256 $cache) -eq $modelo.sha256) { return $cache }
    if ($modelo.url -notmatch '^https://(huggingface\.co/.+/resolve/[0-9a-f]{40}/|github\.com/Dathinel/[^/]+/releases/download/)') {
        throw "El modelo $($modelo.id) no está fijado (Hugging Face por commit o release de Dathinel): $($modelo.url)"
    }
    Nota "Bajando el modelo $($modelo.id) ($([math]::Round($modelo.size / 1MB)) MB)..."
    New-Item -ItemType Directory -Force -Path $Modelos | Out-Null
    $parcial = "$cache.parcial"
    Invoke-WebRequest -Uri $modelo.url -OutFile $parcial -UseBasicParsing
    if ((Sha256 $parcial) -ne $modelo.sha256) { Remove-Item -LiteralPath $parcial -Force; throw "El modelo $($modelo.id) bajado no coincide con su SHA-256; se borró." }
    Move-Item -LiteralPath $parcial -Destination $cache -Force
    return $cache
}

New-Item -ItemType Directory -Force -Path $Salida | Out-Null
$entradas = New-Object System.Collections.Generic.List[object]
$sumas = New-Object System.Collections.Generic.List[string]
$modelosCompleta = @{}
$leidos = @()
foreach ($p in $Paquetes) {
    $m = Leer-Manifiesto $p
    $nombre = Split-Path -Leaf $p
    $tipo = if ($m.kind -eq 'module') { 'module' } else { 'content' }
    $sha = Sha256 $p
    $entrada = [ordered]@{ id = $m.id; version = $m.version; kind = $tipo
        url = "https://github.com/Dathinel/$($m.id)/releases/download/v$($m.version)/$nombre"; sha256 = $sha; size = (Get-Item -LiteralPath $p).Length }
    if ($m.minApp) { $entrada.minApp = $m.minApp }
    $entradas.Add($entrada)
    $sumas.Add("$sha  $nombre")
    if ($tipo -eq 'module' -and $m.module.models) {
        foreach ($mod in $m.module.models) {
            $entradas.Add([ordered]@{ id = $mod.id; version = $m.version; kind = 'model'; url = $mod.url; sha256 = $mod.sha256.ToLowerInvariant(); size = [long]$mod.size })
            if ($Excluir -notcontains $mod.id -and [long]$mod.size -lt $LimiteAsset) { $modelosCompleta[$mod.sha256.ToLowerInvariant()] = $mod }
            elseif ([long]$mod.size -ge $LimiteAsset) { Aviso "El modelo $($mod.id) pasa de 2 GiB: queda fuera de la completa y se baja desde el gestor." }
        }
    }
    $leidos += [pscustomobject]@{ Ruta = $p; Manifiesto = $m; Tipo = $tipo }
    Bien "$($m.id) $($m.version) ($tipo)"
}

$indice = [ordered]@{ formatVersion = 1; generated = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'); entries = $entradas.ToArray() }
$rutaIndice = Join-Path $Salida 'indice.json'
[System.IO.File]::WriteAllText($rutaIndice, ($indice | ConvertTo-Json -Depth 6), $Utf8)
Remove-Item -LiteralPath "$rutaIndice.sig" -Force -ErrorAction SilentlyContinue
Bien "Índice con $($entradas.Count) entradas: $rutaIndice (sin firmar)"

function Comprimir([string]$carpeta, [string]$zip) {
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    # A mano y no con CreateFromDirectory: en Windows PowerShell 5.1 este escribe las rutas con «\», fuera del estándar zip.
    $padre = (Split-Path -Parent (Resolve-Path -LiteralPath $carpeta).Path).TrimEnd('\') + '\'
    $archivo = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($f in Get-ChildItem -LiteralPath $carpeta -Recurse -File -Force) {
            $nombre = $f.FullName.Substring($padre.Length).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archivo, $f.FullName, $nombre, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $archivo.Dispose() }
    $tam = (Get-Item -LiteralPath $zip).Length
    if ($tam -ge $LimiteAsset) { Aviso "$(Split-Path -Leaf $zip) ocupa $([math]::Round($tam / 1GB, 2)) GB: un asset de GitHub no puede pasar de 2 GiB." }
    $sumas.Add("$(Sha256 $zip)  $(Split-Path -Leaf $zip)")
    Bien "$(Split-Path -Leaf $zip): $([math]::Round($tam / 1MB, 1)) MB"
}

$temporal = Join-Path $Salida '.armando'
if (Test-Path -LiteralPath $temporal) { Remove-Item -LiteralPath $temporal -Recurse -Force }
try {
    $base = Join-Path $temporal 'base\Rodaje'
    New-Item -ItemType Directory -Force -Path $base | Out-Null
    Copy-Item -Path (Join-Path $App '*') -Destination $base -Recurse -Force -Exclude '*.pdb'
    Get-ChildItem -LiteralPath $base -Recurse -Force -File | Where-Object { $_.Name -in 'rodaje-error.txt', 'arranque.txt', 'medidas.txt' } | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
    # El atributo oculto no viaja en el zip; lo pone el lanzador al primer arranque.
    (Get-Item -LiteralPath (Join-Path $base 'app') -Force).Attributes = 'Directory'
    Comprimir $base (Join-Path $Salida "Rodaje-$Version-base.zip")

    if (-not $SinCompleta) {
        $completa = Join-Path $temporal 'completa\Rodaje'
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $completa) | Out-Null
        Move-Item -LiteralPath $base -Destination $completa
        foreach ($l in $leidos) {
            $raizTipo = if ($l.Tipo -eq 'module') { 'modulos' } else { 'paquetes' }
            Extraer $l.Ruta (Join-Path $completa "app\$raizTipo\$($l.Manifiesto.id)") $l.Manifiesto.Envoltorio
        }
        foreach ($sha in $modelosCompleta.Keys) {
            $origen = Obtener-Modelo $modelosCompleta[$sha]
            New-Item -ItemType Directory -Force -Path (Join-Path $completa 'app\modelos') | Out-Null
            Copy-Item -LiteralPath $origen -Destination (Join-Path $completa "app\modelos\$sha")
        }
        Comprimir $completa (Join-Path $Salida "Rodaje-$Version-completa.zip")
    }
} finally {
    if (Test-Path -LiteralPath $temporal) { Remove-Item -LiteralPath $temporal -Recurse -Force }
}

[System.IO.File]::WriteAllLines((Join-Path $Salida 'SHA256SUMS.txt'), $sumas, $Utf8)
Titulo 'Listo. Siguiente paso:'
Nota "1. Firmar el índice: build\firmar-indice.ps1 -Indice `"$rutaIndice`""
Nota '2. Subir cada .paquete a la release v<versión> de su repo, y indice.json + indice.json.sig a la release nueva de Dathinel/rodaje-catalogo.'
Nota '3. Subir los zip a la release de Dathinel/rodaje con SHA256SUMS.txt.'
