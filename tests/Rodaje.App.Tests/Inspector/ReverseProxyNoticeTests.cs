using Rodaje.App.Services.Inspector;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Proxies;

namespace Rodaje.App.Tests.Inspector;

public class ReverseProxyNoticeTests
{
    private const string Path4K = @"C:\medios\4k.mp4";

    private static readonly ProxyPolicy Policy = new();

    private static Clip Reversed(bool freeze = false) => new() { MediaId = "m", Speed = new ClipSpeed { Reverse = !freeze, Freeze = freeze } };

    private static MediaRef Media(int width, int height, bool video = true) =>
        new() { Id = "m", Path = Path4K, HasVideo = video, Width = width, Height = height, FrameRate = new Rational(30, 1) };

    private static ProxyStatus Status(ProxyState state, double progress = 0, string? message = null) =>
        new(Path4K, state, progress, Message: message);

    [Fact]
    public void SinReversa_O_HastaHd_NoHayAviso()
    {
        Assert.Null(ReverseProxyNotice.For(new Clip { MediaId = "m" }, Media(3840, 2160), null, Policy));
        Assert.Null(ReverseProxyNotice.For(Reversed(freeze: true), Media(3840, 2160), null, Policy));
        Assert.Null(ReverseProxyNotice.For(Reversed(), Media(1920, 1080), null, Policy));
        Assert.Null(ReverseProxyNotice.For(Reversed(), Media(1080, 1920), null, Policy));
        Assert.Null(ReverseProxyNotice.For(Reversed(), Media(3840, 2160, video: false), null, Policy));
        Assert.Null(ReverseProxyNotice.For(Reversed(), null, null, Policy));
    }

    [Fact]
    public void En4K_DiceElEstadoDelProxy()
    {
        MediaRef media = Media(3840, 2160);
        ReverseNotice generating = ReverseProxyNotice.For(Reversed(), media, Status(ProxyState.Generating, 0.426), Policy)!;
        Assert.True(generating.IsWarning);
        Assert.Contains("43 %", generating.Text, StringComparison.Ordinal);

        ReverseNotice ready = ReverseProxyNotice.For(Reversed(), media, Status(ProxyState.Ready, 1), Policy)!;
        Assert.False(ready.IsWarning);
        Assert.Contains("listo", ready.Text, StringComparison.Ordinal);

        Assert.Contains("en cola", ReverseProxyNotice.For(Reversed(), media, Status(ProxyState.Queued), Policy)!.Text, StringComparison.Ordinal);
        Assert.Contains("disco lleno", ReverseProxyNotice.For(Reversed(), media, Status(ProxyState.Failed, message: "disco lleno"), Policy)!.Text, StringComparison.Ordinal);
        Assert.StartsWith("Sin ffmpeg.", ReverseProxyNotice.For(Reversed(), media, Status(ProxyState.Unavailable, message: "Sin ffmpeg."), Policy)!.Text, StringComparison.Ordinal);
        Assert.Contains("se genera solo", ReverseProxyNotice.For(Reversed(), media, Status(ProxyState.Recommended), Policy)!.Text, StringComparison.Ordinal);
        Assert.Contains("no los usa", ReverseProxyNotice.For(Reversed(), media, null, Policy)!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void SinTamano_SeFiaDeLaUltimaEvaluacion()
    {
        MediaRef unknown = Media(0, 0);
        Assert.Null(ReverseProxyNotice.For(Reversed(), unknown, Status(ProxyState.Recommended), Policy));

        var required = new ProxyRecommendation(ProxyReasons.Reverse | ProxyReasons.Resolution, ["4K"]);
        ProxyStatus status = Status(ProxyState.Generating, 0.5) with { Recommendation = required };
        Assert.True(ReverseProxyNotice.Required(unknown, status, Policy));
        Assert.Contains("50 %", ReverseProxyNotice.For(Reversed(), unknown, status, Policy)!.Text, StringComparison.Ordinal);
    }
}
