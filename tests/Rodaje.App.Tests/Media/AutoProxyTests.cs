using Rodaje.App.Services.Media;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Timing;
using Rodaje.Media.Proxies;

namespace Rodaje.App.Tests.Media;

public class AutoProxyTests
{
    private const long BigGpu = 8L << 30;

    private static ProxyCandidate Candidate(int width, int height, string codec = "h264", int bits = 8) =>
        new(width, height, codec, bits, Rational.Fps30);

    [Fact]
    public void FourKSiempre_1080pH264Nunca()
    {
        Assert.True(AutoProxyRule.Decide(Candidate(3840, 2160), BigGpu).Generate);
        Assert.False(AutoProxyRule.Decide(Candidate(1920, 1080), BigGpu).Generate);
        Assert.False(AutoProxyRule.Decide(Candidate(1920, 1080), 0).Generate);
        Assert.Contains("1080p", AutoProxyRule.Decide(Candidate(3840, 2160), BigGpu).Reason);
    }

    [Fact]
    public void GpuModesta_TambienProResYHevcDe10Bits()
    {
        Assert.True(AutoProxyRule.Decide(Candidate(1920, 1080, "prores"), 2L << 30).Generate);
        Assert.True(AutoProxyRule.Decide(Candidate(1080, 1920, "hevc", 10), 2L << 30).Generate);
        Assert.False(AutoProxyRule.Decide(Candidate(1920, 1080, "prores"), BigGpu).Generate);
        Assert.False(AutoProxyRule.Decide(Candidate(1280, 720, "prores"), 2L << 30).Generate);
        Assert.False(AutoProxyRule.Decide(Candidate(1920, 1080, "hevc", 8), 2L << 30).Generate);
    }

    [Fact]
    public void EsperaAlResolvedorYRespetaElAjuste()
    {
        MediaSourceResolver? resolver = null;
        bool enabled = true;
        var service = new AutoProxyService(() => resolver, () => BigGpu, () => enabled, null);
        var media = new[]
        {
            MediaSamples.Video("cuatro-k.mp4", 2, 3840, 2160, Rational.Fps30, audio: false),
            MediaSamples.Video("hd.mp4", 2, 1920, 1080, Rational.Fps30, audio: false),
            MediaSamples.Audio("musica.wav", 2),
        };

        Assert.Equal(1, service.OnImported(media));
        Assert.Equal(1, service.PendingCount);

        // Sin ffmpeg.exe el resolvedor no genera: lo pendiente se descarta sin error.
        using var store = new TempDir();
        resolver = new MediaSourceResolver(new ProxyStore(store.Path), generator: null);
        service.Tick();
        Assert.Equal(0, service.PendingCount);
        resolver.Dispose();

        enabled = false;
        Assert.Equal(0, service.OnImported(media));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("rodaje-autoproxy-").FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
