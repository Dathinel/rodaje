# Genera los iconos de Rodaje desde los SVG de assets\marca con el ffmpeg de .entorno (librsvg): rodaje.ico y
# rodaje-proyecto.ico (16, 20, 24, 32, 40, 48, 64, 128 y 256 px, en PNG dentro del .ico) y los PNG sueltos.
# De 16 a 32 px se usa rodaje-pequeno.svg, con menos detalle. Los resultados se versionan: compilar no necesita este script.
#   powershell -NoProfile -ExecutionPolicy Bypass -File build\generar-iconos.ps1
$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSScriptRoot
$marca = Join-Path $raiz 'assets\marca'
$entorno = if ($env:RODAJE_ENTORNO) { $env:RODAJE_ENTORNO } else { Join-Path $raiz '.entorno' }
$ffmpeg = Join-Path $entorno 'ffmpeg\bin\ffmpeg.exe'
if (-not (Test-Path -LiteralPath $ffmpeg)) { throw "Falta $ffmpeg (corre build\preparar-entorno.ps1)." }
$tamanos = 16, 20, 24, 32, 40, 48, 64, 128, 256
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('rodaje-iconos-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null

function Rasterizar([string]$svg, [int]$lado, [string]$png) {
    & $ffmpeg -hide_banner -v error -y -width $lado -height $lado -i $svg -frames:v 1 -pix_fmt rgba $png
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $png)) { throw "ffmpeg no pudo rasterizar $svg a $lado px." }
}

function Escribir-Ico([string]$destino, [string[]]$pngs, [int[]]$lados) {
    $datos = foreach ($p in $pngs) { , [IO.File]::ReadAllBytes($p) }
    $ms = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter($ms)
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$pngs.Count)
    $offset = 6 + 16 * $pngs.Count
    for ($i = 0; $i -lt $pngs.Count; $i++) {
        $l = if ($lados[$i] -ge 256) { 0 } else { $lados[$i] }
        $w.Write([byte]$l); $w.Write([byte]$l); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$datos[$i].Length); $w.Write([uint32]$offset)
        $offset += $datos[$i].Length
    }
    foreach ($d in $datos) { $w.Write($d) }
    $w.Flush()
    [IO.File]::WriteAllBytes($destino, $ms.ToArray())
}

try {
    foreach ($icono in @(@{ Nombre = 'rodaje'; Pequeno = 'rodaje-pequeno.svg'; Grande = 'rodaje.svg' },
                         @{ Nombre = 'rodaje-proyecto'; Pequeno = 'rodaje-proyecto.svg'; Grande = 'rodaje-proyecto.svg' })) {
        $pngs = foreach ($t in $tamanos) {
            $svg = Join-Path $marca $(if ($t -le 32) { $icono.Pequeno } else { $icono.Grande })
            $png = Join-Path $tmp "$($icono.Nombre)-$t.png"
            Rasterizar $svg $t $png
            $png
        }
        Escribir-Ico (Join-Path $marca "$($icono.Nombre).ico") $pngs $tamanos
        Write-Host "  $($icono.Nombre).ico"
    }
    foreach ($t in 32, 256) { Copy-Item -LiteralPath (Join-Path $tmp "rodaje-$t.png") -Destination (Join-Path $marca "rodaje-$t.png") -Force }
    Rasterizar (Join-Path $marca 'rodaje.svg') 512 (Join-Path $marca 'rodaje-512.png')
    Write-Host '  rodaje-32.png, rodaje-256.png, rodaje-512.png'
}
finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
