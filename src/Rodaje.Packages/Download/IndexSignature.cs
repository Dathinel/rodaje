using System.Security.Cryptography;
using System.Text;

namespace Rodaje.Packages.Download;

/// <summary>Una descarga que no se pudo hacer; el mensaje se le puede mostrar a la persona.</summary>
public class PackageDownloadException : Exception
{
    public PackageDownloadException()
    {
    }

    public PackageDownloadException(string message)
        : base(message)
    {
    }

    public PackageDownloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Lo descargado no es lo que firma el índice (hash, tamaño o firma): ya se borró.</summary>
public sealed class DownloadVerificationException : PackageDownloadException
{
    public DownloadVerificationException()
    {
    }

    public DownloadVerificationException(string message)
        : base(message)
    {
    }

    public DownloadVerificationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Firma del índice: ECDSA P-256 sobre SHA-256 de los bytes exactos de <c>indice.json</c>, en DER y en base64
/// (<c>indice.json.sig</c>). La clave pública viaja dentro del programa como SubjectPublicKeyInfo en base64; la privada
/// solo existe en el equipo de quien publica (<c>build\firmar-indice.ps1</c>).
/// </summary>
public static class IndexSignature
{
    /// <summary>Lo más que puede ocupar el archivo de firma (una firma DER de P-256 en base64 ocupa unos 96 caracteres).</summary>
    public const int MaxSignatureBytes = 1024;

    /// <summary>Lee una clave pública P-256 en base64 (SubjectPublicKeyInfo); <c>null</c> si falta o no es una clave P-256.</summary>
    public static ECDsa? TryImportPublicKey(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        ECDsa key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(base64.Trim()), out _);
            if (key.KeySize == 256 && key.ExportParameters(false).Curve.Oid.Value == ECCurve.NamedCurves.nistP256.Oid.Value)
            {
                return key;
            }
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
        }

        key.Dispose();
        return null;
    }

    /// <summary>Si <paramref name="signatureFile"/> (el contenido de <c>.sig</c>) firma exactamente <paramref name="data"/>.</summary>
    public static bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signatureFile, ECDsa publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        if (signatureFile.Length is 0 or > MaxSignatureBytes)
        {
            return false;
        }

        try
        {
            string text = Encoding.ASCII.GetString(signatureFile).Trim();
            byte[] der = Convert.FromBase64String(text);
            return publicKey.VerifyData(data, der, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Firma (para las pruebas y herramientas en .NET; la publicación usa <c>firmar-indice.ps1</c>).</summary>
    public static byte[] Sign(ReadOnlySpan<byte> data, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        byte[] der = privateKey.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return Encoding.ASCII.GetBytes(Convert.ToBase64String(der));
    }
}
