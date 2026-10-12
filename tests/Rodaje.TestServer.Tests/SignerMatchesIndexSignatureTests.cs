using System.Security.Cryptography;
using System.Text;
using Rodaje.Packages.Download;

namespace Rodaje.TestServer.Tests;

/// <summary>El firmante de prueba produce lo mismo que verifica el programa (<see cref="IndexSignature"/>).</summary>
public sealed class SignerMatchesIndexSignatureTests
{
    private static readonly byte[] Index = Encoding.UTF8.GetBytes("""{ "formatVersion": 1, "generated": "2026-10-12T00:00:00Z", "entries": [] }""");

    [Fact]
    public void The_signature_file_verifies_with_the_program_key_reader()
    {
        using var signer = new TestIndexSigner();
        using ECDsa? key = IndexSignature.TryImportPublicKey(signer.PublicKeyBase64);

        Assert.NotNull(key);
        Assert.True(IndexSignature.Verify(Index, signer.SignFile(Index), key));
        Assert.True(IndexSignature.Verify(Index, Encoding.ASCII.GetBytes(signer.Sign(Index)), key));
        Assert.False(IndexSignature.Verify(Index, Encoding.ASCII.GetBytes(signer.SignOtherData(Index)), key));
        Assert.False(IndexSignature.Verify(Index, Encoding.ASCII.GetBytes(TestIndexSigner.SignWithForeignKey(Index)), key));
    }

    [Fact]
    public void Der_is_the_decoded_signature_file()
    {
        using var signer = new TestIndexSigner();
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(signer.PublicKey, out _);

        Assert.True(key.VerifyData(Index, signer.SignDer(Index), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    }
}
