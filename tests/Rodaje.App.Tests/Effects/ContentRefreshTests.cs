using Rodaje.App.Services.Effects;
using Rodaje.Playback.Composition;

namespace Rodaje.App.Tests.Effects;

public class ContentRefreshTests
{
    private const string Look = "p@1.0.0/look";

    [Fact]
    public async Task LutCargada_DespiertaYPideComponer_UnaSolaVez()
    {
        var loader = new FakeContentLoader();
        loader.Luts[Look] = FakeContentLoader.Identity();
        var content = new EffectContent(loader);
        int wakes = 0;
        using var refresh = new ContentRefresh(() => Interlocked.Increment(ref wakes));
        refresh.Attach(content);
        Assert.Equal(ContentUpdate.None, refresh.Take());

        content.TryGetLut(new ResourceKey(Look), out _);
        await content.WhenLoadedAsync(Look);

        Assert.Equal(1, Volatile.Read(ref wakes));
        Assert.Equal(ContentUpdate.Redraw, refresh.Take());
        Assert.Equal(ContentUpdate.None, refresh.Take());
    }

    [Fact]
    public void PaquetesCambiados_PidenRearmarElPlan()
    {
        var content = new EffectContent(new FakeContentLoader());
        using var refresh = new ContentRefresh(() => { });
        refresh.Attach(content);

        content.Invalidate();
        Assert.Equal(ContentUpdate.Rebuild, refresh.Take());

        content.Invalidate();
        content.Invalidate();
        Assert.Equal(ContentUpdate.Rebuild, refresh.Take());
        Assert.Equal(ContentUpdate.None, refresh.Take());
    }

    [Fact]
    public void ContenidoAnterior_DejaDeAvisar()
    {
        var old = new EffectContent(new FakeContentLoader());
        int wakes = 0;
        using var refresh = new ContentRefresh(() => wakes++);
        refresh.Attach(old);
        refresh.Attach(new EffectContent(new FakeContentLoader()));

        old.Invalidate();

        Assert.Equal(0, wakes);
        Assert.Equal(ContentUpdate.None, refresh.Take());
        refresh.Attach(null);
        Assert.Equal(ContentUpdate.None, refresh.Take());
    }
}
