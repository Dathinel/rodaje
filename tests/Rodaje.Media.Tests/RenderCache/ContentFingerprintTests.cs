using Rodaje.Media.RenderCache;
using Xunit;

namespace Rodaje.Media.Tests.RenderCache;

public sealed class ContentFingerprintTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("rodaje-huella-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void No_depende_de_la_ruta_ni_de_la_fecha()
    {
        byte[] data = Data(300_000);
        string a = Write("a.mp4", data);
        string b = Write(Path.Combine("otra carpeta", "ñandú.mov"), data);
        File.SetLastWriteTimeUtc(b, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(ContentFingerprint.Of(a), ContentFingerprint.Of(b));
        Assert.StartsWith("300000:", ContentFingerprint.Of(a));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(150_000)]
    [InlineData(299_999)]
    public void Cambia_con_el_contenido_de_principio_medio_y_final(int offset)
    {
        byte[] data = Data(300_000);
        string original = Write("a.mp4", data);
        data[offset] ^= 0xFF;
        string changed = Write("b.mp4", data);

        Assert.NotEqual(ContentFingerprint.Of(original), ContentFingerprint.Of(changed));
    }

    [Fact]
    public void Archivos_pequenos_y_que_faltan()
    {
        Assert.NotNull(ContentFingerprint.Of(Write("vacio.mp4", [])));
        Assert.NotEqual(ContentFingerprint.Of(Write("x.mp4", [1, 2])), ContentFingerprint.Of(Write("y.mp4", [1, 3])));
        Assert.Null(ContentFingerprint.Of(Path.Combine(_folder, "no-existe.mp4")));
    }

    private static byte[] Data(int length)
    {
        byte[] data = new byte[length];
        new Random(7).NextBytes(data);
        return data;
    }

    private string Write(string name, byte[] data)
    {
        string path = Path.Combine(_folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        return path;
    }
}
