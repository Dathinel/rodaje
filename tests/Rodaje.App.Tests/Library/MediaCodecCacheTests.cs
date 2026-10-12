using Rodaje.App.Views.Library;

namespace Rodaje.App.Tests.Library;

public sealed class MediaCodecCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rodaje-codecs-" + Guid.NewGuid().ToString("N"));

    public MediaCodecCacheTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string MakeFile(string name, string content)
    {
        string path = Path.Combine(_dir, name);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ArchivoIgual_NoSeVuelveALeer()
    {
        var cache = new MediaCodecCache();
        string path = MakeFile("a.mp4", "uno");
        int reads = 0;

        MediaCodecs first = cache.Read(path, _ => { reads++; return ("h264", "aac"); });
        MediaCodecs second = cache.Read(path, _ => { reads++; return ("otro", null); });

        Assert.Equal(new MediaCodecs("h264", "aac", Failed: false), first);
        Assert.Equal(first, second);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void ErrorInesperado_SoloAfectaAEseMedio()
    {
        var cache = new MediaCodecCache();
        string raro = MakeFile("raro.mp4", "x");
        string bueno = MakeFile("bueno.mp4", "y");

        MediaCodecs failed = cache.Read(raro, _ => throw new NotSupportedException("formato raro"));
        MediaCodecs ok = cache.Read(bueno, _ => ("hevc", null));

        Assert.True(failed.Failed);
        Assert.Null(failed.Video);
        Assert.Equal(new MediaCodecs("hevc", null, Failed: false), ok);
    }

    [Fact]
    public void Fallo_SeGuardaConLaFecha_YSeReintentaSiElArchivoCambia()
    {
        var cache = new MediaCodecCache();
        string path = MakeFile("copiando.mp4", "a medias");
        int reads = 0;
        (string?, string?) Probe(string p)
        {
            reads++;
            return System.IO.File.ReadAllText(p) == "a medias" ? throw new IOException("incompleto") : ("h264", "aac");
        }

        Assert.True(cache.Read(path, Probe).Failed);
        Assert.True(cache.Read(path, Probe).Failed);
        Assert.Equal(1, reads);

        System.IO.File.WriteAllText(path, "ya terminado");
        System.IO.File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        Assert.Equal(new MediaCodecs("h264", "aac", Failed: false), cache.Read(path, Probe));
        Assert.Equal(2, reads);
    }

    [Fact]
    public void ArchivoQueNoEsta_NoSeGuarda()
    {
        var cache = new MediaCodecCache();
        string path = Path.Combine(_dir, "no-esta.mp4");
        int reads = 0;

        cache.Read(path, _ => { reads++; throw new FileNotFoundException(); });
        cache.Read(path, _ => { reads++; throw new FileNotFoundException(); });

        Assert.Equal(2, reads);
        Assert.Null(MediaCodecCache.StampOf(path));
    }

    [Fact]
    public void Marca_CambiaConElTamano()
    {
        string path = MakeFile("b.mp4", "uno");
        MediaFileStamp? before = MediaCodecCache.StampOf(path);
        DateTime time = System.IO.File.GetLastWriteTimeUtc(path);

        System.IO.File.WriteAllText(path, "uno más largo");
        System.IO.File.SetLastWriteTimeUtc(path, time);

        Assert.NotNull(before);
        Assert.NotEqual(before, MediaCodecCache.StampOf(path));
    }
}
