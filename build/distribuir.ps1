# Arma la distribución de Rodaje en dist\distribucion\ (la versión sale de Directory.Build.props):
#   Rodaje-<v>-instalador.exe  build\instalador.ps1 (Inno Setup, por usuario, sin administrador).
#   Rodaje-<v>-portable.zip    dist\Rodaje tal cual: Rodaje.exe (el lanzador) y app\ con el programa y FFmpeg LGPL.
#   Rodaje-<v>-completo.zip    portable + en app\: paquetes en paquetes\<id>, módulos en modulos\<id> y sus modelos
#             en modelos\<sha256> (menos los de más de 2 GiB); los módulos de modules\ se publican aquí. Al descomprimir se ve solo Rodaje.exe y la carpeta
#             app, que el lanzador oculta la primera vez.
#   SHA256SUMS.txt y Rodaje-<v>-notas.md (de build\instalador\notas-version.md), lo que sube build\publicar-release.ps1.
#   indice.json  lo que el gestor de complementos puede descargar (paquetes, módulos y modelos) con URL, SHA-256 y
#             tamaño; se firma después con build\firmar-indice.ps1. El gestor y este script usan los mismos
#             .paquete, así que lo que trae la completa es exactamente lo que el gestor bajaría.
# Con RODAJE_CERTIFICADO se firman los dos Rodaje.exe y el instalador (Comando-Firma en comun.psm1).
# Al final prueba el instalador (build\probar-instalador.ps1). No sube nada.
# Uso:
#   powershell -NoProfile -ExecutionPolicy Bypass -File build\distribuir.ps1 [-SinPublicar] [-SinCompleta]
#       [-SinModulos] [-SinInstalador] [-SinComprobar]
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
    [switch]$SinCompleta,
    [switch]$SinModulos,
    [switch]$SinInstalador,
    [switch]$SinComprobar
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

if (-not $Version) { $Version = Version-Rodaje }

Titulo "Distribución de Rodaje $Version"

if (-not $SinPublicar) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'app.ps1') -SoloPublicar
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo publicar Rodaje.' }
}
foreach ($exe in 'Rodaje.exe', 'app\Rodaje.exe') {
    if (-not (Test-Path -LiteralPath (Join-Path $App $exe))) { throw "No está publicado $App\$exe (quita -SinPublicar)." }
}
if (Firmar-Codigo @((Join-Path $App 'Rodaje.exe'), (Join-Path $App 'app\Rodaje.exe'))) { Bien 'Rodaje.exe y app\Rodaje.exe firmados.' }
else { Aviso 'Sin RODAJE_CERTIFICADO: los exe van sin firmar.' }

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
    # preparar-modelos.ps1 los deja por nombre: modelos/<ruta> del paquete.json, o solo el nombre.
    if ($modelo.file) {
        foreach ($local in (Join-Path $Modelos (($modelo.file -replace '^modelos/', '') -replace '/', '\')), (Join-Path $Modelos (Split-Path -Leaf $modelo.file))) {
            if ((Test-Path -LiteralPath $local -PathType Leaf) -and (Sha256 $local) -eq $modelo.sha256) { return $local }
        }
    }
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
    if ($tipo -eq 'module' -and $m.module.models) {
        foreach ($mod in $m.module.models) {
            $entradas.Add([ordered]@{ id = $mod.id; version = $m.version; kind = 'model'; url = $mod.url; sha256 = $mod.sha256.ToLowerInvariant(); size = [long]$mod.size })
            if ($Excluir -notcontains $mod.id -and $mod.size -and [long]$mod.size -lt $LimiteAsset) { $modelosCompleta[$mod.sha256.ToLowerInvariant()] = $mod }
            elseif (-not $mod.size -or [long]$mod.size -ge $LimiteAsset) { Aviso "El modelo $($mod.id) pasa de 2 GiB (o no declara tamaño): queda fuera de la completa y se baja desde el gestor." }
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
    Bien "$(Split-Path -Leaf $zip): $([math]::Round($tam / 1MB, 1)) MB"
}

# Los módulos de IA de modules\ (menos el eco de pruebas) aún no tienen repo ni .paquete propio: se publican aquí
# para la completa, en modulos\<id> con su paquete.json, y sus modelos se suman a los de la completa.
function Publicar-Modulos([string]$destino) {
    Usar-DotnetLocal $E
    Agregar-Vswhere | Out-Null
    try {
        foreach ($dir in Get-ChildItem -LiteralPath (Join-Path $Raiz 'modules') -Directory) {
            $json = Join-Path $dir.FullName 'paquete.json'
            $csproj = Get-ChildItem -LiteralPath $dir.FullName -Filter '*.csproj' | Select-Object -First 1
            if (-not (Test-Path -LiteralPath $json) -or -not $csproj) { continue }
            $m = Get-Content -LiteralPath $json -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($m.id -eq 'rodaje-eco' -or $Excluir -contains $m.id) { continue }
            $carpeta = Join-Path $destino $m.id
            $entrada = Join-Path $carpeta ($m.module.entry -replace '/', '\')
            Nota "Publicando el módulo $($m.id)..."
            # Los que no son AOT van autocontenidos: la completa no puede pedir .NET instalado.
            $r = Invocar-Dotnet (Join-Path $Raiz "TestResults\distribuir-$($m.id).log") @('publish', $csproj.FullName, '-c', 'Release',
                '-o', (Split-Path -Parent $entrada), '--nologo', '-v', 'q', '-p:SelfContained=true')
            if ($r.Codigo -ne 0 -or -not (Test-Path -LiteralPath $entrada)) { throw "No se pudo publicar el módulo $($m.id) (TestResults\distribuir-$($m.id).log)." }
            Get-ChildItem -LiteralPath $carpeta -Recurse -Filter '*.pdb' -File | Remove-Item -Force
            foreach ($f in 'paquete.json', 'LICENCIAS.md', 'README.md') {
                $origen = Join-Path $dir.FullName $f
                if (Test-Path -LiteralPath $origen) { Copy-Item -LiteralPath $origen -Destination $carpeta -Force }
            }
            Firmar-Codigo @($entrada) | Out-Null
            foreach ($mod in @($m.module.models)) {
                if (-not $mod) { continue }
                if ($Excluir -notcontains $mod.id -and $mod.size -and [long]$mod.size -lt $LimiteAsset) { $modelosCompleta[$mod.sha256.ToLowerInvariant()] = $mod }
                elseif (-not $mod.size -or [long]$mod.size -ge $LimiteAsset) { Aviso "El modelo $($mod.id) pasa de 2 GiB (o no declara tamaño): queda fuera de la completa y se baja desde el gestor." }
            }
            Bien ("{0} {1}: {2:0.0} MB" -f $m.id, $m.version, ((Get-ChildItem -LiteralPath $carpeta -Recurse -File | Measure-Object Length -Sum).Sum / 1MB))
        }
    } finally {
        Remove-Item Env:DOTNET_ROOT -ErrorAction SilentlyContinue
    }
}

$temporal = Join-Path $Salida '.armando'
if (Test-Path -LiteralPath $temporal) { Remove-Item -LiteralPath $temporal -Recurse -Force }
try {
    $base = Join-Path $temporal 'portable\Rodaje'
    New-Item -ItemType Directory -Force -Path $base | Out-Null
    Copy-Item -Path (Join-Path $App '*') -Destination $base -Recurse -Force -Exclude '*.pdb'
    Get-ChildItem -LiteralPath $base -Recurse -Force -File | Where-Object { $_.Name -in 'rodaje-error.txt', 'arranque.txt', 'medidas.txt' } | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
    # El atributo oculto no viaja en el zip; lo pone el lanzador al primer arranque.
    (Get-Item -LiteralPath (Join-Path $base 'app') -Force).Attributes = 'Directory'
    Comprimir $base (Join-Path $Salida "Rodaje-$Version-portable.zip")

    if (-not $SinCompleta) {
        $completa = Join-Path $temporal 'completo\Rodaje'
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $completa) | Out-Null
        Move-Item -LiteralPath $base -Destination $completa
        foreach ($l in $leidos) {
            $raizTipo = if ($l.Tipo -eq 'module') { 'modulos' } else { 'paquetes' }
            Extraer $l.Ruta (Join-Path $completa "app\$raizTipo\$($l.Manifiesto.id)") $l.Manifiesto.Envoltorio
        }
        if (-not $SinModulos) { Publicar-Modulos (Join-Path $completa 'app\modulos') }
        foreach ($sha in $modelosCompleta.Keys) {
            $origen = Obtener-Modelo $modelosCompleta[$sha]
            New-Item -ItemType Directory -Force -Path (Join-Path $completa 'app\modelos') | Out-Null
            Copy-Item -LiteralPath $origen -Destination (Join-Path $completa "app\modelos\$sha")
        }
        Comprimir $completa (Join-Path $Salida "Rodaje-$Version-completo.zip")
    }
} finally {
    if (Test-Path -LiteralPath $temporal) { Remove-Item -LiteralPath $temporal -Recurse -Force }
}

$instalador = Join-Path $Salida "Rodaje-$Version-instalador.exe"
if (-not $SinInstalador) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'instalador.ps1') -Version $Version -Origen $App -Salida $Salida
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo armar el instalador.' }
}

# SHA256SUMS y las notas cubren lo que sube publicar-release.ps1: instalador, portable y completo.
$artefactos = @(foreach ($n in "Rodaje-$Version-instalador.exe", "Rodaje-$Version-portable.zip", "Rodaje-$Version-completo.zip") {
    $ruta = Join-Path $Salida $n
    if (Test-Path -LiteralPath $ruta) { Get-Item -LiteralPath $ruta }
})
$sumas = @($artefactos | ForEach-Object { "$(Sha256 $_.FullName)  $($_.Name)" })
[System.IO.File]::WriteAllLines((Join-Path $Salida 'SHA256SUMS.txt'), [string[]]$sumas, $Utf8)

$para = @{ instalador = 'Recomendado: instala y asocia los proyectos'; portable = 'Sin instalar: descomprime y abre'; completo = 'Portable con complementos y modelos de IA' }
$filas = @($artefactos | ForEach-Object {
    $tipo = ($_.BaseName -replace "^Rodaje-$([regex]::Escape($Version))-", '')
    $tam = if ($_.Length -ge 1GB) { '{0:0.00} GB' -f ($_.Length / 1GB) } else { '{0:0} MB' -f ($_.Length / 1MB) }
    "| ``$($_.Name)`` | $($para[$tipo]) | $tam |"
})
$aviso = if (Comando-Firma) { '' } else { "`n`nEsta versión va sin firma de código: Windows SmartScreen puede avisar la primera vez («Más información» → «Ejecutar de todas formas»)." }
$notas = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'instalador\notas-version.md') -Raw -Encoding UTF8).
    Replace('{{VERSION}}', $Version).Replace('{{FECHA}}', (Get-Date -Format 'yyyy-MM-dd')).
    Replace('{{ARTEFACTOS}}', ($filas -join "`n")).Replace('{{AVISO_FIRMA}}', $aviso)
$rutaNotas = Join-Path $Salida "Rodaje-$Version-notas.md"
[System.IO.File]::WriteAllText($rutaNotas, $notas.Replace("`r`n", "`n"), $Utf8)

Titulo 'Artefactos'
$artefactos | ForEach-Object { Nota ('{0,-34} {1,10:N1} MB' -f $_.Name, ($_.Length / 1MB)) }
Nota "Notas: $(Corto $rutaNotas)"

if (-not $SinInstalador -and -not $SinComprobar) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'probar-instalador.ps1') -Instalador $instalador
    if ($LASTEXITCODE -ne 0) { throw 'El instalador no pasó su prueba.' }
}

Titulo 'Listo. Siguiente paso:'
Nota "1. Firmar el índice: build\firmar-indice.ps1 -Indice `"$rutaIndice`""
Nota '2. Subir cada .paquete a la release v<versión> de su repo, y indice.json + indice.json.sig a la release nueva de Dathinel/rodaje-catalogo.'
Nota "3. Publicar la release de Rodaje: build\publicar-release.ps1 (v$Version, artefactos, SHA256SUMS.txt y notas)."