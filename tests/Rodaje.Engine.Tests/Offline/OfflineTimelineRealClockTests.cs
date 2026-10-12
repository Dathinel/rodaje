using Rodaje.Core.Timing;
using Rodaje.Engine.Offline;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Tests.Timeline;
using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;
using Xunit.Abstractions;

namespace Rodaje.Engine.Tests.Offline;

/// <summary>
/// <see cref="OfflineTimelineTests"/> compara con una vista previa de reloj quieto, así que ahí nunca se cierra un
/// decodificador ocioso ni vence el tope de la composición a medias. Esta prueba usa el reloj del sistema y comprueba
/// que, con esos dos caminos en marcha, la composición asentada sigue siendo la que da el render sin conexión.
/// </summary>
public sealed class OfflineTimelineRealClockTests(ITestOutputHelper output)
{
    // Tres cadencias bastan: la rejilla exacta ya la cubren las siete de OfflineTimelineTests.
    public static TheoryData<long, long> Rates => new() { { 24, 1 }, { 30000, 1001 }, { 60, 1 } };

    /// <summary>
    /// Con los dos topes en cero, en cada refresco se cierra todo decodificador sin demanda y, tras cada seek, se muestra
    /// lo que haya. Los decodificadores corren solo entre refrescos y los cuadros se visitan en desorden, así que tras casi
    /// cada seek falta algún cuadro. No depende de cuánto tarde el equipo: los dos caminos corren siempre, también en la CI.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rates))]
    public void Closing_idle_decoders_and_showing_partial_frames_keep_the_settled_composition(long num, long den)
    {
        var rate = new Rational(num, den);
        // El render sin conexión también decodifica con el ejecutor del motor: va con su propio motor falso, inmediato.
        var backend = new FakeTimelineBackend { ManualDecoding = true };
        var offlineBackend = new FakeTimelineBackend();
        var content = new FakeEffectContent();
        TimelineBuilder b = OfflineProjects.Full(rate, backend, content);
        TimelineBuilder same = OfflineProjects.Full(rate, offlineBackend, content);
        var options = new TimelineSessionOptions { Content = content, DecoderIdleTimeout = TimeSpan.Zero, PartialFrameTimeout = TimeSpan.Zero };
        using TimelineSession preview = TimelineSessionTests.Open(b, backend, options);
        using OfflineTimeline offline = OfflineProjects.Open(same, offlineBackend, new OfflineTimelineOptions { Content = content });

        Assert.Equal(preview.FrameCount, offline.FrameCount);
        long[] order = [.. Enumerable.Range(0, (int)offline.FrameCount).Select(i => (long)i)];
        new Random(46).Shuffle(order);
        foreach (long f in order)
        {
            preview.SeekToFrame(f);
            using CompositionFrame expected = Settle(preview, backend);
            using CompositionFrame actual = offline.Render(f, CancellationToken.None);
            Assert.Equal(OfflineProjects.Describe(expected), OfflineProjects.Describe(actual));
        }

        TimelineStats stats = preview.Stats;
        output.WriteLine($"{offline.FrameCount} cuadros a {rate}: {stats.DecodersClosed} decodificadores cerrados por ociosos, "
            + $"{stats.PartialPresentations} composiciones a medias, {stats.HeldRefreshes} refrescos retenidos.");
        Assert.True(stats.DecodersClosed > 0, "Ningún decodificador se cerró por ocioso: la prueba no cubre ese camino.");
        Assert.True(stats.PartialPresentations > 0, "No se mostró ninguna composición a medias: la prueba no cubre ese camino.");
        Assert.Equal(0, stats.HeldRefreshes);
        Assert.Equal(0, offline.MissingContent);
    }

    private static CompositionFrame Settle(TimelineSession session, FakeTimelineBackend backend)
    {
        for (int i = 0; ; i++)
        {
            CompositionFrame frame = session.Tick();
            if (!session.IsSeekPending)
            {
                return frame;
            }

            frame.Dispose();
            backend.RunDecoders();
            Assert.True(i < 20, "La vista previa no llegó a la composición completa en 20 refrescos.");
        }
    }
}
