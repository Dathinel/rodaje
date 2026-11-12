using Rodaje.App.Services;
using Rodaje.App.Tests.Support;

namespace Rodaje.App.Tests.Session;

public sealed class PrefetchingProberTests
{
    [Fact]
    public async Task RepeatedPath_IsProbedOnceAcrossConcurrentPrefetches()
    {
        var inner = new FakeProber().Video("prefetched.mp4");
        var prober = new PrefetchingProber(inner);
        string path = Path.GetFullPath("prefetched.mp4");

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
            prober.Prefetch(["prefetched.mp4", path, "./prefetched.mp4", path.ToUpperInvariant()]))));
        MediaProbeResult result = await Task.Run(() => prober.Probe(path));

        Assert.True(result.IsUsable);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public void ConsumedOrUnknownPath_UsesInnerProber()
    {
        var inner = new FakeProber().Video("prefetched.mp4").Video("other.mp4");
        var prober = new PrefetchingProber(inner);
        string path = Path.GetFullPath("prefetched.mp4");

        prober.Prefetch([path]);
        Assert.True(prober.Probe(path).IsUsable);
        Assert.True(prober.Probe(path).IsUsable);
        Assert.True(prober.Probe(Path.GetFullPath("other.mp4")).IsUsable);
        Assert.Equal(3, inner.Calls);
    }
}
