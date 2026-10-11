using System.Security.Cryptography;

namespace Rodaje.TestServer;

/// <summary>
/// Firma de prueba para el índice de la descarga, con el formato que propone <c>docs/fase-7.md</c> (D-031):
/// <c>indice.json.sig</c> es una firma ECDSA P-256 sobre SHA-256 de los bytes de <c>indice.json</c>, en DER
/// (RFC 3279) y en base64. Cada instancia genera su propia clave; la prueba pasa <see cref="PublicKey"/> al
/// verificador en lugar de la clave real que va dentro del exe.
/// </summary>
/// <remarks>
/// El formato todavía no está fijado en código. Si la ronda B lo cambia, solo cambia esta clase: el servidor
/// sirve los bytes que se le den y no interpreta la firma.
/// </remarks>
public sealed class TestIndexSigner : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>Clave pública en SubjectPublicKeyInfo DER (lo que lee <c>ECDsa.ImportSubjectPublicKeyInfo</c>).</summary>
    public byte[] PublicKey => _key.ExportSubjectPublicKeyInfo();

    /// <summary>Clave pública en PEM, por si el verificador la toma así.</summary>
    public string PublicKeyPem => _key.ExportSubjectPublicKeyInfoPem();

    /// <summary>Firma buena de <paramref name="data"/>, en DER y base64 (el contenido de <c>indice.json.sig</c>).</summary>
    public string Sign(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(SignDer(data));

    /// <summary>Firma buena de <paramref name="data"/> en DER, sin base64.</summary>
    public byte[] SignDer(ReadOnlySpan<byte> data) =>
        _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

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

    /// <summary>Verificación de referencia, para comprobar en las pruebas que las firmas buenas lo son.</summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> data, string signatureBase64)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(publicKey, out _);
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    /// <inheritdoc />
    public void Dispose() => _key.Dispose();
}
