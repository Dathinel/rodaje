# Regresión del contrato que impide dar por bueno un proceso que sale sin abrir la ventana.
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'autoprueba.psm1') -DisableNameChecking -Force
$raiz = Join-Path (Split-Path -Parent $PSScriptRoot) ('TestResults\contrato-autoprueba-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $raiz -Force | Out-Null
$registro = Join-Path $raiz 'arranque.txt'
$errorTxt = Join-Path $raiz 'rodaje-error.txt'
$fallos = Join-Path $raiz 'fallos'
$marcas = @('arranque.ventana_lista_ms: 10', 'arranque.primer_render_ms: 20', 'arranque.interfaz_usable_ms: 30')
$casos = @(
    @{ Nombre = 'ventana lista'; Codigo = 0; Marcas = $marcas; Falla = $false },
    @{ Nombre = 'cierre prematuro con cero'; Codigo = 0; Marcas = $null; Falla = $true; Motivo = '*no dejó el registro*' },
    @{ Nombre = 'sin ventana'; Codigo = 0; Marcas = $marcas[1..2]; Falla = $true; Motivo = '*falta ventana_lista*' },
    @{ Nombre = 'sin render'; Codigo = 0; Marcas = $marcas[0,2]; Falla = $true; Motivo = '*falta primer_render*' },
    @{ Nombre = 'UI bloqueada'; Codigo = 0; Marcas = $marcas[0..1]; Falla = $true; Motivo = '*falta interfaz_usable*' },
    @{ Nombre = 'salida fallida'; Codigo = 3; Marcas = $marcas; Falla = $true; Motivo = '*código 3*' },
    @{ Nombre = 'salida desconocida'; Codigo = $null; Marcas = $marcas; Falla = $true; Motivo = '*código de salida*' },
    @{ Nombre = 'error junto al exe'; Codigo = 0; Marcas = $marcas; Error = $true; Falla = $true; Motivo = '*rodaje-error.txt*' },
    @{ Nombre = 'informe recuperado'; Codigo = 0; Marcas = $marcas; Informe = $true; Falla = $true; Motivo = '*informe en*' },
    @{ Nombre = 'error en sonda'; Codigo = 0; Marcas = $marcas + 'error: motor'; Falla = $true; Motivo = '*informó un error*' }
)
foreach ($caso in $casos) {
    Remove-Item -LiteralPath $registro, $errorTxt -Force -ErrorAction SilentlyContinue
    # Solo se borra el informe de esta prueba, nunca una carpeta calculada de datos personales.
    Remove-Item -LiteralPath (Join-Path $fallos 'recuperado.txt') -Force -ErrorAction SilentlyContinue
    if ($null -ne $caso.Marcas) { Set-Content -LiteralPath $registro -Value $caso.Marcas -Encoding UTF8 }
    if ($caso.Error) { Set-Content -LiteralPath $errorTxt -Value 'fallo simulado' }
    if ($caso.Informe) {
        New-Item -ItemType Directory -Path $fallos -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $fallos 'recuperado.txt') -Value 'fallo recuperado'
    }
    $mensaje = $null
    try { Confirmar-Autoprueba -Codigo $caso.Codigo -Registro $registro -ErrorTxt $errorTxt -Fallos $fallos }
    catch { $mensaje = $_.Exception.Message }
    if ($caso.Falla -ne [bool]$mensaje -or ($mensaje -and $mensaje -notlike $caso.Motivo)) {
        throw "Falló el caso '$($caso.Nombre)': $mensaje"
    }
    Write-Host "PASA: $($caso.Nombre)"
}
Write-Host "$($casos.Count)/$($casos.Count) comprobaciones correctas."
