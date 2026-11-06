function Confirmar-Autoprueba {
    param(
        [Parameter(Mandatory = $true)][AllowNull()][Nullable[int]]$Codigo,
        [Parameter(Mandatory = $true)][string]$ErrorTxt,
        [Parameter(Mandatory = $true)][string]$Fallos,
        [string]$Registro = ''
    )
    if ($null -eq $Codigo) { throw 'No se pudo leer el código de salida de Rodaje.' }
    if ($Codigo -ne 0) { throw "Rodaje terminó con código $Codigo." }
    if (Test-Path -LiteralPath $ErrorTxt) { throw "Rodaje dejó $ErrorTxt`: $(Get-Content -LiteralPath $ErrorTxt -Raw -Encoding UTF8)" }
    if ((Test-Path -LiteralPath $Fallos) -and @(Get-ChildItem -LiteralPath $Fallos -Recurse -File -Force).Count -gt 0) {
        throw "Rodaje dejó un informe en $Fallos."
    }
    if ($Registro) {
        if (-not (Test-Path -LiteralPath $Registro)) { throw "Rodaje no dejó el registro de arranque $Registro." }
        $texto = Get-Content -LiteralPath $Registro -Raw -Encoding UTF8
        foreach ($marca in 'ventana_lista', 'primer_render', 'interfaz_usable') {
            if ($texto -notmatch "(?m)^arranque\.$($marca)_ms: [0-9]+\r?$") { throw "La ventana no llegó al estado lista: falta $marca." }
        }
        if ($texto -match '(?m)^error:') { throw "El arranque informó un error: $texto" }
    }
}

Export-ModuleMember -Function Confirmar-Autoprueba
