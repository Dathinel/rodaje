using System.Security.Cryptography;
using System.Text.Json;
using Rodaje.App.Services.Collab;
using Rodaje.Core.Model;

namespace Rodaje.App.Tests.Collab;

public sealed class MediaMatchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rodaje-match-" + Guid.NewGuid().ToString("N")[..8]);

    public MediaMatchTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    private string Write(string relative, byte[] data)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static byte[] Bytes(int seed, int length)
    {
        byte[] b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static MediaManifestEntry Entry(string id, byte[] data) =>
        new(id, id + ".mp4", data.Length, Convert.ToHexStringLower(SHA256.HashData(data)));

    [Fact]
    public async Task Encuentra_por_contenido_aunque_cambie_el_nombre_y_la_carpeta()
    {
        byte[] a = Bytes(1, 5000), b = Bytes(2, 7000);
        string pa = Write(@"otra\carpeta\renombrado.mov", a);
        string pb = Write(@"clips\toma-b.mp4", b);
        MediaMatchReport r = await new MediaMatch().SearchAsync([Entry("m1", a), Entry("m2", b)], [_root]);
        Assert.Equal(pa, r.Found["m1"]);
        Assert.Equal(pb, r.Found["m2"]);
        Assert.Empty(r.Missing);
    }

    [Fact]
    public async Task Mismo_tamano_pero_otro_contenido_no_cuenta_y_queda_sin_conexion()
    {
        byte[] original = Bytes(3, 4096), parecido = Bytes(4, 4096);
        Write("parecido.mp4", parecido);
        MediaMatchReport r = await new MediaMatch().SearchAsync([Entry("m1", original)], [_root]);
        Assert.Empty(r.Found);
        Assert.Equal("m1", Assert.Single(r.Missing).MediaId);
    }

    [Fact]
    public async Task Solo_lee_los_archivos_del_tamano_buscado()
    {
        byte[] a = Bytes(5, 1000);
        Write("a.mp4", a);
        Write("otro-tamano.mp4", Bytes(6, 1001));
        List<string> hashed = [];
        MediaMatch match = new(hasher: (p, ct) =>
        {
            hashed.Add(Path.GetFileName(p));
            return MediaMatch.Sha256Async(p, ct);
        });
        await match.SearchAsync([Entry("m1", a)], [_root]);
        Assert.Equal(["a.mp4"], hashed);
    }

    [Fact]
    public async Task Dos_medios_con_el_mismo_contenido_se_enlazan_los_dos_y_una_carpeta_inexistente_se_salta()
    {
        byte[] a = Bytes(7, 2000);
        string p = Write("a.mp4", a);
        MediaMatchReport r = await new MediaMatch().SearchAsync([Entry("m1", a), Entry("m2", a)], [Path.Combine(_root, "no-existe"), _root]);
        Assert.Equal(p, r.Found["m1"]);
        Assert.Equal(p, r.Found["m2"]);
    }

    [Fact]
    public async Task Un_archivo_solo_en_la_nube_no_se_abre_y_se_cuenta()
    {
        byte[] a = Bytes(8, 3000);
        Write("nube.mp4", a);
        MediaMatch match = new(new CloudFs(), (_, _) => throw new InvalidOperationException("no se debe abrir"));
        MediaMatchReport r = await match.SearchAsync([Entry("m1", a)], [_root]);
        Assert.Equal(1, r.CloudOnly);
        Assert.Single(r.Missing);
    }

    [Fact]
    public async Task Se_puede_cancelar()
    {
        Write("a.mp4", Bytes(9, 100));
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MediaMatch().SearchAsync([Entry("m1", Bytes(9, 100))], [_root], null, cts.Token));
    }

    [Fact]
    public void El_manifiesto_va_y_vuelve_y_descarta_lo_malo()
    {
        MediaManifestEntry ok = new("m1", "Toma 1.mp4", 1234, new string('a', 64));
        JsonElement json = MediaManifest.ToJson([ok]);
        Assert.Equal([ok], MediaManifest.Parse(json));

        string hostil = $$"""
            {"tipo":"medios","medios":[
              {"id":"a","nombre":"..\\..\\Windows\\win.ini","tam":5,"sha256":"{{new string('b', 64)}}"},
              {"id":"b","nombre":"x","tam":-1,"sha256":"{{new string('b', 64)}}"},
              {"id":"c","nombre":"x","tam":1,"sha256":"MAYUSCULAS-O-CORTA"},
              {"id":"a","nombre":"duplicado","tam":5,"sha256":"{{new string('b', 64)}}"},
              {"id":"","nombre":"x","tam":1,"sha256":"{{new string('b', 64)}}"},
              {"id":"d","nombre":"x\u0000y","tam":1,"sha256":"{{new string('c', 64)}}"},
              7, null, {"id":5}
            ]}
            """;
        IReadOnlyList<MediaManifestEntry> parsed = MediaManifest.Parse(JsonDocument.Parse(hostil).RootElement);
        Assert.Equal(["a", "d"], parsed.Select(p => p.MediaId));
        Assert.Equal("win.ini", parsed[0].Name);
        Assert.Equal("xy", parsed[1].Name);
        Assert.Empty(MediaManifest.Parse(JsonDocument.Parse("""{"tipo":"otra","medios":[]}""").RootElement));
        Assert.Empty(MediaManifest.Parse(JsonDocument.Parse("[]").RootElement));
    }

    [Fact]
    public async Task El_anfitrion_arma_el_manifiesto_sin_rutas_salta_lo_que_falta_y_no_relee_lo_que_no_cambio()
    {
        byte[] a = Bytes(10, 2500);
        string pa = Write("clips/a.mp4", a);
        MediaRef present = new() { Id = "m1", Path = pa, Name = "Toma A" };
        MediaRef missing = new() { Id = "m2", Path = Path.Combine(_root, "falta.mp4"), Name = "Falta" };
        int reads = 0;
        MediaManifestBuilder builder = new((p, ct) =>
        {
            reads++;
            return MediaMatch.Sha256Async(p, ct);
        });
        IReadOnlyList<MediaManifestEntry> first = await builder.BuildAsync([present, missing], m => m.Path);
        IReadOnlyList<MediaManifestEntry> second = await builder.BuildAsync([present, missing], m => m.Path);
        MediaManifestEntry entry = Assert.Single(first);
        Assert.Equal(Entry("m1", a) with { Name = "Toma A" }, entry);
        Assert.Equal(first, second);
        Assert.Equal(1, reads);
        Assert.DoesNotContain(_root, JsonSerializer.Serialize(MediaManifest.ToJson(first).GetRawText()), StringComparison.Ordinal);
    }

    private sealed class CloudFs : Rodaje.Core.Editing.IRelinkFileSystem
    {
        public IEnumerable<Rodaje.Core.Editing.RelinkFile> EnumerateFiles(string folder, CancellationToken cancellationToken) =>
            new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Select(f => new Rodaje.Core.Editing.RelinkFile(f.FullName, f.Length, CloudOnly: true));

        public Rodaje.Core.Editing.RelinkProbe? Probe(string path) => null;

        public string? Fingerprint(string path, int bytes) => null;
    }
}
