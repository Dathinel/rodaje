# Rellena la cola de la nube con cazas contra documentos con contratos (D-048), sin ningún modelo: una tarea claude-nube
# por cada sección de nivel 2 con reglas verificables que no esté en .proceso\nube\cazado.md. Escribe en la rama main
# de rodaje-proceso (cola.md y cazado.md) con un commit propio.
# Uso: build\generar-cola-nube-caza.ps1 [-Minimo 6] [-Maximo 8]
param(
    [int] $Minimo = 6,
    [int] $Maximo = 8,
    [switch] $Simular
)
$ErrorActionPreference = 'Stop'
$utf8 = New-Object Text.UTF8Encoding $false
$repo = Split-Path (git -C $PSScriptRoot rev-parse --path-format=absolute --git-common-dir)
$raiz = Split-Path $PSScriptRoot
$proceso = Join-Path $repo '.proceso'
# Solo documentos cuyo código se compila y se prueba en Linux.
$documentos = [ordered]@{
    'docs/formato-paquete.md' = 'Packages'
    'docs/formato-proyecto.md' = 'Core'
    'docs/arquitectura/nucleo.md' = 'Core'
    'docs/arquitectura/reproduccion.md' = 'Playback'
}
$fuera = '(?i)(WinUI|instalador|Instalar|\bInstall|hardware|NVENC|WASAPI|Direct3D|D3D11|Módulos de IA|^Archivos$|^Pruebas$|^Pendiente$|^Un ejemplo completo$|^Paquetes de contenido de Dathinel$)'
$regla = '(?i)\b(debe|deben|se rechaza|rechaza|no puede|nunca|siempre|como máximo|como mínimo|obligatori\w*|por defecto|exactamente|se recorta|se acota|mayor que|menor que|hasta \d|de \d[\d.,]* a \d)'

$tmp = Join-Path $repo '.claude\proceso-cola'
if (Test-Path -LiteralPath $tmp) { git -C $proceso worktree remove --force $tmp 2>$null }
git -C $proceso fetch --quiet origin
git -C $proceso worktree add --quiet --detach $tmp origin/main
if ($LASTEXITCODE -ne 0) { throw 'No se pudo preparar rodaje-proceso' }
try {
    $colaRuta = Join-Path $tmp 'nube\cola.md'
    $cazadoRuta = Join-Path $tmp 'nube\cazado.md'
    $cola = [IO.File]::ReadAllText($colaRuta, [Text.Encoding]::UTF8)
    $nl = if ($cola.Contains("`r`n")) { "`r`n" } else { "`n" }
    if (-not (Test-Path -LiteralPath $cazadoRuta)) {
        $semilla = "# Secciones ya cazadas o en cola (generar-cola-nube-caza.ps1)$nl$nl" +
            "- docs/formato-paquete.md#Contenido de los elementos (312, #298)$nl- docs/formato-paquete.md#``paquete.json`` (313, #297 y #300)$nl" +
            "- docs/guia/edicion.md#Cortar y borrar; Mover, recortar, slip, roll y slide; Grupos, colores y huecos; Pista principal magnética; Edición de tres y cuatro puntos (315, #303)$nl" +
            "- docs/guia/edicion.md#Marcadores y regiones; Clips compuestos (316, #304)$nl- docs/guia/titulos-y-subtitulos.md#subtítulos (316, #304)$nl"
        [IO.File]::WriteAllText($cazadoRuta, $semilla, $utf8)
    }
    $cazado = [IO.File]::ReadAllText($cazadoRuta, [Text.Encoding]::UTF8)

    # Tareas claude-nube aún pendientes.
    $abiertas = @(($cola -split '(?m)^(?=## \d+\. )') | Where-Object { $_ -match '(?m)^- \*\*Agente:\*\* claude-nube' -and $_ -match '(?m)^- \*\*Estado:\*\* pendiente' }).Count
    if ($abiertas -ge $Minimo) { Write-Host "Hay $abiertas tareas abiertas: no hace falta rellenar."; return }
    $numero = ([regex]::Matches($cola, '(?m)^## (\d+)\.') | ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Maximum).Maximum + 1

    $nuevas = New-Object Collections.Generic.List[string]
    $marcas = New-Object Collections.Generic.List[string]
    foreach ($doc in $documentos.Keys) {
        $proyecto = $documentos[$doc]
        $texto = [IO.File]::ReadAllText((Join-Path $raiz ($doc -replace '/', '\')), [Text.Encoding]::UTF8)
        foreach ($s in ($texto -split '(?m)^(?=## )')) {
            if ($nuevas.Count -ge $Maximo) { break }
            if ($s -notmatch '^## (.+)') { continue }
            $titulo = $Matches[1].Trim()
            if ($titulo -match $fuera -or $s -match '(?i)(WinUI|hardware|NVENC|WASAPI)') { continue }
            if ($cazado.Contains("$doc#$titulo")) { continue }
            if ([regex]::Matches($s, $regla).Count -lt 2) { continue }
            $carpeta = if ($titulo -match '\(`(\w+)/`\)') { "src/Rodaje.$proyecto/$($Matches[1])/" } elseif ($doc -eq 'docs/formato-proyecto.md') { 'src/Rodaje.Core/Serialization/' } else { "src/Rodaje.$proyecto/" }
            $slug = (($doc -replace '^docs/(arquitectura/)?|\.md$', '') + '-' + ($titulo -replace '[^A-Za-z0-9áéíóúñÁÉÍÓÚÑ ]', '')).ToLowerInvariant()
            $slug = ($slug.Normalize([Text.NormalizationForm]::FormD) -replace '\p{Mn}', '' -replace '[^a-z0-9]+', '-').Trim('-')
            if ($slug.Length -gt 40) { $slug = $slug.Substring(0, 40).TrimEnd('-') }
            $nuevas.Add(@"
## $numero. Caza con contrato: «$titulo» de ``$doc``

- **Estado:** pendiente
- **Agente:** claude-nube
- **Rama:** ``claude/caza-$slug``
- **Caminos:** ``$carpeta``, ``tests/Rodaje.$proyecto.Tests/``
- **Qué:** lee solo la sección «$titulo» de ``$doc`` y el código que cita. Escribe una prueba por cada promesa verificable, con su frase literal encima. Si alguna falla, arregla el defecto en ``src/`` con el cambio mínimo y comprueba que la prueba de regresión falla sin el arreglo: un defecto por PR, en borrador, y en la descripción di que la prueba falla sin el arreglo. Si el documento y el código discrepan y no está claro cuál vale, no toques el código: déjalo en la sección «Pendiente» del PR. Nada de lo que solo se comprueba en Windows (WinUI, instalador, hardware).
"@)
            $marcas.Add("- $doc#$titulo ($numero)")
            $numero++
        }
    }
    if (-not $nuevas.Count) { Write-Host 'No quedan secciones nuevas que cazar.'; return }
    if ($Simular) { $marcas | ForEach-Object { Write-Host $_ }; return }
    $cola = $cola.TrimEnd() + $nl + $nl + (($nuevas | ForEach-Object { $_.Trim() -replace "`r?`n", $nl }) -join ($nl + $nl)) + $nl
    [IO.File]::WriteAllText($colaRuta, $cola, $utf8)
    [IO.File]::WriteAllText($cazadoRuta, $cazado.TrimEnd() + $nl + ($marcas -join $nl) + $nl, $utf8)
    git -C $tmp add nube/cola.md nube/cazado.md
    git -C $tmp commit --quiet -m "Cola: $($nuevas.Count) cazas con contrato para la nube (generar-cola-nube-caza.ps1)"
    git -C $tmp push --quiet origin HEAD:main
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo subir la cola a rodaje-proceso' }
    Write-Host "Añadidas $($nuevas.Count) tareas de caza a la cola."
} finally {
    git -C $proceso worktree remove --force $tmp 2>$null
}
