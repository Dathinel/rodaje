using System.Text;

namespace Rodaje.TestServer.Tests;

public sealed class PayloadAndSignerTests
{
    [Fact]
    public void Deterministic_payloads_repeat_by_seed()
    {
        Assert.Equal(TestPayloads.Deterministic(4096, 7), TestPayloads.Deterministic(4096, 7));
        Assert.NotEqual(TestPayloads.Deterministic(4096, 7), TestPayloads.Deterministic(4096, 8));
    }

    [Fact]
    public void Sha256_is_lowercase_hex()
    {
        // SHA-256 de «abc» (FIPS 180-2).
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            TestPayloads.Sha256Hex("abc"u8));
    }

    [Fact]
    public void Corrupt_keeps_the_size_and_changes_the_hash()
    {
        var good = TestPayloads.Deterministic(1000);
        var bad = TestPayloads.Corrupt(good, 999);

        Assert.Equal(good.Length, bad.Length);
        Assert.NotEqual(TestPayloads.Sha256Hex(good), TestPayloads.Sha256Hex(bad));
        Assert.Equal(good.AsSpan(0, 999).ToArray(), bad.AsSpan(0, 999).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => TestPayloads.Corrupt(good, 1000));
    }

    [Fact]
    public void Good_signature_verifies_and_bad_ones_do_not()
    {
        using var signer = new TestIndexSigner();
        var index = Encoding.UTF8.GetBytes("""{ "formatVersion": 1, "entries": [] }""");

        Assert.True(TestIndexSigner.Verify(signer.PublicKey, index, signer.Sign(index)));
        Assert.False(TestIndexSigner.Verify(signer.PublicKey, index, signer.SignOtherData(index)));
        Assert.False(TestIndexSigner.Verify(signer.PublicKey, index, TestIndexSigner.SignWithForeignKey(index)));
        Assert.False(TestIndexSigner.Verify(signer.PublicKey, index, "no es base64!"));
        Assert.False(TestIndexSigner.Verify(signer.PublicKey, TestPayloads.Corrupt(index, 0), signer.Sign(index)));
    }

    [Fact]
    public void Public_key_is_exported_in_der_and_pem()
    {
        using var signer = new TestIndexSigner();

        Assert.NotEmpty(signer.PublicKey);
        Assert.StartsWith("-----BEGIN PUBLIC KEY-----", signer.PublicKeyPem, StringComparison.Ordinal);
        Assert.Equal(signer.PublicKey, signer.PublicKey);
    }
}
