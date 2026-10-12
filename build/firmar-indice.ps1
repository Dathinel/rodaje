# Firma el índice de complementos (indice.json -> indice.json.sig) con la clave ECDSA P-256 de Dathinel.
# Uso:
#   powershell -NoProfile -ExecutionPolicy Bypass -File build\firmar-indice.ps1 -NuevaClave
#       Crea la clave privada en %USERPROFILE%\.rodaje-claves\catalogo-privada.pem (solo legible por tu usuario) y
#       escribe la pública en src\Rodaje.App\Services\Packages\clave-catalogo.txt, que va dentro del exe.
#       La privada NUNCA se versiona ni se sube. Respáldala (ver docs\descarga.md).
#   ... -Indice dist\distribucion\indice.json
#       Firma ese índice (por defecto el de dist\distribucion) y comprueba la firma con la clave pública del repo.
# La firma es ECDSA P-256 sobre SHA-256 de los bytes exactos del archivo, en DER y en base64. Funciona en
# Windows PowerShell 5.1 (CNG) sin instalar nada.
[CmdletBinding()]
param(
    [switch]$NuevaClave,
    [switch]$Reemplazar,
    [string]$Indice = '',
    [string]$Clave = (Join-Path $env:USERPROFILE '.rodaje-claves\catalogo-privada.pem'),
    [string]$Publica = ''
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
$Raiz = Split-Path -Parent $PSScriptRoot
if (-not $Publica) { $Publica = Join-Path $Raiz 'src\Rodaje.App\Services\Packages\clave-catalogo.txt' }
if (-not $Indice) { $Indice = Join-Path $Raiz 'dist\distribucion\indice.json' }

# SubjectPublicKeyInfo de P-256: prefijo DER fijo + 04 || X || Y.
$PrefijoSpki = [byte[]](0x30,0x59,0x30,0x13,0x06,0x07,0x2a,0x86,0x48,0xce,0x3d,0x02,0x01,0x06,0x08,0x2a,0x86,0x48,0xce,0x3d,0x03,0x01,0x07,0x03,0x42,0x00,0x04)

function Spki([System.Security.Cryptography.ECDsaCng]$ecdsa) {
    $p = $ecdsa.ExportParameters($false)
    return [Convert]::ToBase64String($PrefijoSpki + $p.Q.X + $p.Q.Y)
}

function Leer-Clave {
    if (-not (Test-Path -LiteralPath $Clave)) {
        throw "No encuentro la clave privada en $Clave. Créala con -NuevaClave o restaura tu respaldo ahí."
    }
    $pem = Get-Content -LiteralPath $Clave -Raw
    $b64 = ($pem -replace '-----[A-Z ]+-----', '' -replace '\s', '')
    $key = [System.Security.Cryptography.CngKey]::Import([Convert]::FromBase64String($b64), [System.Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob)
    return New-Object System.Security.Cryptography.ECDsaCng($key)
}

# r || s (64 bytes) a DER: SEQUENCE { INTEGER r, INTEGER s }.
function Entero-Der([byte[]]$v) {
    $i = 0
    while ($i -lt $v.Length - 1 -and $v[$i] -eq 0) { $i++ }
    [byte[]]$v = $v[$i..($v.Length - 1)]
    if ($v[0] -ge 0x80) { [byte[]]$v = @([byte]0) + $v }
    return [byte[]](@([byte]0x02, [byte]$v.Length) + $v)
}

function Firma-Der([byte[]]$p1363) {
    $cuerpo = (Entero-Der $p1363[0..31]) + (Entero-Der $p1363[32..63])
    return [byte[]](@([byte]0x30, [byte]$cuerpo.Length) + $cuerpo)
}

if ($NuevaClave) {
    if ((Test-Path -LiteralPath $Clave) -and -not $Reemplazar) {
        throw "Ya hay una clave en $Clave. Si de verdad quieres otra (las versiones publicadas de Rodaje dejarán de aceptar tus índices), usa -Reemplazar."
    }
    $carpeta = Split-Path -Parent $Clave
    New-Item -ItemType Directory -Force -Path $carpeta | Out-Null
    $parametros = New-Object System.Security.Cryptography.CngKeyCreationParameters
    $parametros.ExportPolicy = [System.Security.Cryptography.CngExportPolicies]::AllowPlaintextExport
    # NullString: con $null PowerShell pasaría "" y la clave quedaría guardada con ese nombre en el almacén de Windows.
    $cng = [System.Security.Cryptography.CngKey]::Create([System.Security.Cryptography.CngAlgorithm]::ECDsaP256, [System.Management.Automation.Language.NullString]::Value, $parametros)
    $pkcs8 = $cng.Export([System.Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob)
    $b64 = [Convert]::ToBase64String($pkcs8) -replace '(.{64})', "`$1`n"
    $pem = "-----BEGIN PRIVATE KEY-----`n$($b64.Trim())`n-----END PRIVATE KEY-----`n"
    [System.IO.File]::WriteAllText($Clave, $pem, (New-Object System.Text.UTF8Encoding($false)))
    # Solo tu usuario puede leerla.
    & icacls $Clave /inheritance:r /grant:r "$($env:USERNAME):(R,W)" | Out-Null
    $ecdsa = New-Object System.Security.Cryptography.ECDsaCng($cng)
    [System.IO.File]::WriteAllText($Publica, (Spki $ecdsa) + "`n", (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "Clave privada: $Clave"
    Write-Host "Clave pública: $Publica (haz commit de este archivo)"
    Write-Host 'Respáldala YA: copia el .pem a un pendrive y a tu gestor de contraseñas. Si se pierde, la próxima versión de Rodaje tendrá que traer otra clave pública.'
    exit 0
}

if (-not (Test-Path -LiteralPath $Indice)) { throw "No existe el índice $Indice (lo arma build\distribuir.ps1)." }
$ecdsa = Leer-Clave
$datos = [System.IO.File]::ReadAllBytes($Indice)
$firma = $ecdsa.SignData($datos, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
$der = Firma-Der $firma
$sig = "$Indice.sig"
[System.IO.File]::WriteAllText($sig, [Convert]::ToBase64String($der), [System.Text.Encoding]::ASCII)

# Comprobación: la firma vale y la clave pública del repo es la de esta clave privada.
if (-not $ecdsa.VerifyData($datos, $firma, [System.Security.Cryptography.HashAlgorithmName]::SHA256)) { throw 'La firma recién hecha no verifica.' }
$publicaRepo = if (Test-Path -LiteralPath $Publica) { (Get-Content -LiteralPath $Publica -Raw).Trim() } else { '' }
if ($publicaRepo -ne (Spki $ecdsa)) {
    Write-Warning "La clave pública de $Publica no corresponde a esta clave privada: esta versión de Rodaje rechazará el índice."
    exit 2
}
Write-Host "Firmado: $sig"
