using System.Diagnostics;
using System.Numerics;
using Rodaje.App.Services.Tracking;
using Rodaje.Core.Effects;
using Rodaje.Core.Timing;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.App.Tests.Analysis;

/// <summary>
/// El seguimiento de puntos con la decodificación de verdad: el fondo del video de prueba corre 4 px por cuadro en
/// horizontal. Cuadros por segundo con decodificar incluido (1080p y 4K), avance en vivo y cancelación.
/// </summary>
[Collection(StabilizeDecodeTests.Name)]
public sealed class PointTrackingDecodeTests(ITestOutputHelper output)
{
    private static readonly MediaSpec Uhd = new("h264-2160p-30-seguimiento", 3840, 2160, Rational.Fps30, TimeSpan.FromSeconds(3))
    {
        Codec = VideoCodec.H264,
        GopLength = 30,
        Container = ContainerFormat.Mp4,
    };

    [FfmpegLibraryFact(VideoCodec.H264)]
    [Trait("Categoria", "TiempoReal")]
    public async Task Points_on_the_moving_background_follow_it_at_1080p()
    {
        string path = TestMediaGenerator.CreateDefault()!.GetOrCreate(TestMediaPresets.H264Ntsc30BFrames).Path;
        (List<PointTrackFrame> frames, double fps) = await Track(path, Rational.Ntsc30, 90);
        output.WriteLine($"1080p H.264, 4 puntos: {fps:0} cuadros/s con decodificar.");
        Assert.Equal(91, frames.Count);
        AssertFollowsBackground(frames, 1920, 1080);
    }

    [FfmpegLibraryFact(VideoCodec.H264)]
    [Trait("Categoria", "TiempoReal")]
    public async Task Points_follow_at_4K_too()
    {
        string path = TestMediaGenerator.CreateDefault()!.GetOrCreate(Uhd).Path;
        (List<PointTrackFrame> frames, double fps) = await Track(path, Rational.Fps30, 80);
        output.WriteLine($"4K H.264, 4 puntos: {fps:0} cuadros/s con decodificar.");
        Assert.Equal(81, frames.Count);
        AssertFollowsBackground(frames, 3840, 2160);
    }

    [FfmpegLibraryFact(VideoCodec.H264)]
    public async Task Cancelling_stops_and_keeps_what_was_tracked()
    {
        string path = TestMediaGenerator.CreateDefault()!.GetOrCreate(TestMediaPresets.H264Ntsc30BFrames).Path;
        using var cancel = new CancellationTokenSource();
        var frames = new List<PointTrackFrame>();
        PointTrackRequest request = Request(path, Rational.Ntsc30, 500, forward: true);
        Task run = PointTracking.RunAsync(request, f =>
        {
            frames.Add(f);
            if (frames.Count == 12)
            {
                cancel.Cancel();
            }
        }, null, cancel.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.InRange(frames.Count, 12, 13);
    }

    [FfmpegLibraryFact(VideoCodec.H264)]
    public async Task Backwards_ends_where_forwards_started()
    {
        string path = TestMediaGenerator.CreateDefault()!.GetOrCreate(TestMediaPresets.H264Ntsc30BFrames).Path;
        var forward = new List<PointTrackFrame>();
        await PointTracking.RunAsync(Request(path, Rational.Ntsc30, 40, forward: true), forward.Add, null, CancellationToken.None);
        TrackSample[] end = forward[^1].Samples;
        var spec = new TrackPointSpec(end[0].Position, new Vector2(0.02f, 0.035f), new Vector2(0.04f, 0.07f));
        var backward = new List<PointTrackFrame>();
        var request = new PointTrackRequest(path, Time.FromFrames(40, Rational.Ntsc30), Time.Zero, false, PointTrackKind.Point, [spec], 0.7f,
            _ => CropRect.None);
        await PointTracking.RunAsync(request, backward.Add, null, CancellationToken.None);
        Assert.Equal(Time.Zero, backward[^1].Time);
        float error = Vector2.Distance(backward[^1].Samples[0].Position * new Vector2(1920, 1080), forward[0].Samples[0].Position * new Vector2(1920, 1080));
        Assert.InRange(error, 0, 0.5f);
    }

    private static PointTrackRequest Request(string path, Rational rate, int frames, bool forward)
    {
        Vector2[] at = [new(0.25f, 0.35f), new(0.75f, 0.35f), new(0.75f, 0.75f), new(0.25f, 0.75f)];
        return new PointTrackRequest(path, Time.Zero, Time.FromFrames(frames, rate), forward, PointTrackKind.Corners,
            [.. at.Select(p => new TrackPointSpec(p, new Vector2(0.02f, 0.035f), new Vector2(0.04f, 0.07f)))], 0.7f, _ => CropRect.None);
    }

    private static async Task<(List<PointTrackFrame> Frames, double Fps)> Track(string path, Rational rate, int frames)
    {
        var list = new List<PointTrackFrame>();
        var watch = Stopwatch.StartNew();
        await PointTracking.RunAsync(Request(path, rate, frames, true), list.Add, null, CancellationToken.None);
        return (list, frames / watch.Elapsed.TotalSeconds);
    }

    // El fondo corre 4 px por cuadro en horizontal (hacia un lado fijo); en vertical no se mueve.
    private static void AssertFollowsBackground(List<PointTrackFrame> frames, int width, int height)
    {
        var size = new Vector2(width, height);
        for (int k = 1; k < frames.Count; k++)
        {
            for (int p = 0; p < 4; p++)
            {
                Vector2 step = (frames[k].Samples[p].Position - frames[k - 1].Samples[p].Position) * size;
                Assert.True(frames[k].Samples[p].Confidence >= 0.7f, $"Cuadro {k}, punto {p}: confianza {frames[k].Samples[p].Confidence:0.00}");
                Assert.InRange(Math.Abs(step.X), 3.75, 4.25);
                Assert.InRange(step.Y, -0.25, 0.25);
            }
        }
    }
}
