using System.Security.Cryptography;
using System.Text;
using Rodaje.Paquetes.Download;

namespace Rodaje.Paquetes.Tests.Download;

public sealed class SignedIndexTests : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _public;

    public SignedIndexTests()
    {
        _public = IndexSignature.TryImportPublicKey(Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()))!;
    }

    public void Dispose()
    {
        _key.Dispose();
        _public.Dispose();
    }

    internal static string Entry(string id = "rodaje-filtros", string version = "1.0.0", string kind = "content",
        string url = "https://github.com/Dathinel/rodaje-filtros/releases/download/v1.0.0/rodaje-filtros.paquete",
        string sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", long size = 100, string? minApp = null) =>
        $$"""{ "id": "{{id}}", "version": "{{version}}", "kind": "{{kind}}", "url": "{{url}}", "sha256": "{{sha}}", "size": {{size}}{{(minApp is null ? "" : $", \"minApp\": \"{minApp}\"")}} }""";

    internal static byte[] Index(params string[] entries) =>
        Encoding.UTF8.GetBytes($$"""{ "formatVersion": 1, "generated": "2026-10-11T12:00:00Z", "entries": [ {{string.Join(",", entries)}} ] }""");

    private SignedIndex Load(byte[] json, List<string>? problems = null) =>
        SignedIndex.Load(json, IndexSignature.Sign(json, _key), _public, DownloadUrlPolicy.Default, problems);

    [Fact]
    public void Reads_a_signed_index()
    {
        SignedIndex index = Load(Index(Entry(), Entry(version: "1.1.0", minApp: "9.0.0"), Entry("modelo-x", kind: "model",
            url: "https://huggingface.co/org/modelo/resolve/0123456789abcdef0123456789abcdef01234567/m.onnx")));

        Assert.Equal(3, index.Entries.Count);
        Assert.Equal(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero), index.Generated);
        Assert.Equal("1.0.0", index.Latest("rodaje-filtros", PackageVersion.Parse("0.7.0"))!.Version.ToString());
        Assert.Equal("1.1.0", index.Latest("rodaje-filtros", PackageVersion.Parse("9.0.0"))!.Version.ToString());
        Assert.Equal(IndexEntryKind.Model, index.Latest("modelo-x", PackageVersion.Parse("0.7.0"))!.Kind);
    }

    [Fact]
    public void Every_altered_byte_or_signature_is_rejected()
    {
        byte[] json = Index(Entry(), Entry("rodaje-transiciones"));
        byte[] signature = IndexSignature.Sign(json, _key);
        var random = new Random(11);
        for (int i = 0; i < 500; i++)
        {
            byte[] bad = (byte[])json.Clone();
            bad[random.Next(bad.Length)] ^= (byte)(1 + random.Next(255));
            Assert.Throws<DownloadVerificationException>(() => SignedIndex.Load(bad, signature, _public, DownloadUrlPolicy.Default));
        }

        byte[] der = Convert.FromBase64String(Encoding.ASCII.GetString(signature));
        for (int i = 0; i < 500; i++)
        {
            byte[] badDer = (byte[])der.Clone();
            badDer[random.Next(badDer.Length)] ^= (byte)(1 + random.Next(255));
            byte[] badSignature = Encoding.ASCII.GetBytes(Convert.ToBase64String(badDer));
            Assert.Throws<DownloadVerificationException>(() => SignedIndex.Load(json, badSignature, _public, DownloadUrlPolicy.Default));
        }

        using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        foreach (byte[] wrong in new[] { IndexSignature.Sign(json, other), [], "no es base64"u8.ToArray(), new byte[5000] })
        {
            Assert.Throws<DownloadVerificationException>(() => SignedIndex.Load(json, wrong, _public, DownloadUrlPolicy.Default));
        }
    }

    [Theory]
    [InlineData("http://github.com/Dathinel/r/releases/download/v1/a.paquete")]
    [InlineData("https://github.com/Otro/r/releases/download/v1/a.paquete")]
    [InlineData("https://github.com/Dathinel/r/archive/v1.zip")]
    [InlineData("https://huggingface.co/org/modelo/resolve/main/m.onnx")]
    [InlineData("https://huggingface.co/org/modelo/resolve/0123456789ABCDEF0123456789abcdef01234567/m.onnx")]
    [InlineData("https://ejemplo.com/a.paquete")]
    [InlineData("https://github.com/Dathinel/r/releases/download/v1/a.paquete?x=1")]
    [InlineData("https://usuario@github.com/Dathinel/r/releases/download/v1/a.paquete")]
    public void Entries_from_other_origins_are_skipped(string url)
    {
        var problems = new List<string>();
        SignedIndex index = Load(Index(Entry(url: url)), problems);
        Assert.Empty(index.Entries);
        Assert.Single(problems);
    }

    [Theory]
    [InlineData("sha", "0123")]
    [InlineData("sha", "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEZ")]
    [InlineData("id", "Mal Id")]
    [InlineData("version", "uno")]
    [InlineData("kind", "binario")]
    public void Malformed_entries_are_skipped(string field, string value)
    {
        string entry = field switch
        {
            "sha" => Entry(sha: value),
            "id" => Entry(id: value),
            "version" => Entry(version: value),
            _ => Entry(kind: value),
        };
        var problems = new List<string>();
        Assert.Empty(Load(Index(entry, Entry(size: 0)), problems).Entries);
        Assert.Equal(2, problems.Count);
    }

    [Fact]
    public void A_newer_format_is_refused()
    {
        byte[] json = """{ "formatVersion": 2, "entries": [] }"""u8.ToArray();
        Assert.Throws<FormatException>(() => Load(json));
    }

    [Fact]
    public void Invalid_public_keys_are_not_imported()
    {
        Assert.Null(IndexSignature.TryImportPublicKey(null));
        Assert.Null(IndexSignature.TryImportPublicKey("  "));
        Assert.Null(IndexSignature.TryImportPublicKey("no-base64"));
        using ECDsa p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Null(IndexSignature.TryImportPublicKey(Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo())));
    }
}
