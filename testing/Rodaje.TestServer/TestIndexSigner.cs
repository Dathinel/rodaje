using System.Security.Cryptography;
using System.Text;
using Rodaje.Paquetes.Download;

namespace Rodaje.TestServer;

/// <summary>
/// Firmante de prueba para el índice de la descarga. Firma y verifica con <see cref="IndexSignature"/>, el mismo código
/// que usa el programa (ECDSA P-256 sobre SHA-256 de los bytes de <c>indice.json</c>, DER RFC 3279 en base64: el
/// contenido de <c>indice.json.sig</c>). Cada instancia genera su propia clave; la prueba pasa
/// <see cref="PublicKeyBase64"/> al verificador en lugar de la clave real que va dentro del exe.
/// </summary>
/// <remarks>
/// Lo propio de esta clase son las firmas malas (<see cref="SignOtherData"/>, <see cref="SignWithForeignKey"/>) y las
/// formas de la clave pública. Si el formato cambia en <see cref="IndexSignature"/>, estas firmas lo siguen solas.
/// </remarks>
public sealed class TestIndexSigner : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>Clave pública en SubjectPublicKeyInfo DER (lo que lee <c>ECDsa.ImportSubjectPublicKeyInfo</c>).</summary>
    public byte[] PublicKey => _key.ExportSubjectPublicKeyInfo();

    /// <summary>Clave pública en SubjectPublicKeyInfo y base64, como la toma <c>GitHubReleaseOptions.PublicKey</c>.</summary>
    public string PublicKeyBase64 => Convert.ToBase64String(PublicKey);

    /// <summary>Clave pública en PEM, por si el verificador la toma así.</summary>
    public string PublicKeyPem => _key.ExportSubjectPublicKeyInfoPem();

    /// <summary>Firma buena de <paramref name="data"/>, en DER y base64 (el contenido de <c>indice.json.sig</c>).</summary>
    public string Sign(ReadOnlySpan<byte> data) =>
        Encoding.ASCII.GetString(SignFile(data));

    /// <summary>Firma buena de <paramref name="data"/> tal como se sirve en <c>indice.json.sig</c> (texto ASCII).</summary>
    public byte[] SignFile(ReadOnlySpan<byte> data) =>
        IndexSignature.Sign(data, _key);

    /// <summary>Firma buena de <paramref name="data"/> en DER, sin base64.</summary>
    public byte[] SignDer(ReadOnlySpan<byte> data) =>
        Convert.FromBase64String(Sign(data));

    /// <summary>
    /// Firma bien formada pero inválida para <paramref name="data"/>: la firma de otros bytes con la misma clave.
    /// </summary>
    public string SignOtherData(ReadOnlySpan<byte> data)
    {
        var other = data.ToArray();
        if (other.Length == 0)
        {
            other = [0];
        }
        else
        {
            other[0] ^= 0xFF;
        }

        return Sign(other);
    }

    /// <summary>Firma bien formada de <paramref name="data"/> con otra clave (un índice firmado por un tercero).</summary>
    public static string SignWithForeignKey(ReadOnlySpan<byte> data)
    {
        using var foreign = new TestIndexSigner();
        return foreign.Sign(data);
    }

    /// <summary>Verificación con <see cref="IndexSignature.Verify"/>, para comprobar en las pruebas que las firmas buenas lo son.</summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> data, string signatureBase64)
    {
        ArgumentNullException.ThrowIfNull(signatureBase64);
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(publicKey, out _);
        return IndexSignature.Verify(data, Encoding.ASCII.GetBytes(signatureBase64), key);
    }

    /// <inheritdoc />
    public void Dispose() => _key.Dispose();
}
