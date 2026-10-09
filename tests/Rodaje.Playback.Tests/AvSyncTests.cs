using Rodaje.Core.Timing;
using Rodaje.Playback.Tests.Support;
using static Rodaje.Playback.Tests.Support.PlaybackHarness;

namespace Rodaje.Playback.Tests;

/// <summary>Criterio de la fase 2: la deriva entre audio y video queda por debajo de un cuadro en 10 minutos.</summary>
public class AvSyncTests
{
    public static TheoryData<long, long, double> Cases()
    {
        var data = new TheoryData<long, long, double>();
        foreach ((long num, long den) in new[] { (24000L, 1001L), (25L, 1L), (30000L, 1001L), (60000L, 1001L) })
        {
            foreach (double drift in new[] { -0.001, 0.0, 0.001 })
            {
                data.Add(num, den, drift);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void DiezMinutos_ElCuadroMostradoNuncaSeAlejaUnCuadroDelReloj(long num, long den, double drift)
    {
        var rate = new Rational(num, den);
        Time tenMinutes = Time.FromSeconds(600);
        // Entrada a mitad de cuadro y clip desplazado en la línea de tiempo
        Time sourceIn = Time.FromSeconds(1.5) + Time.FromTicks(12_345);
        var timing = new ClipTiming(Time.FromSeconds(2), sourceIn, sourceIn + tenMinutes + Time.FromSeconds(1), rate);
        using var h = new PlaybackHarness(timing, _ => Ms(4));
        Time frame = Time.FromFrames(1, rate);

        FramePresentation first = h.SettleAndTick();
        Assert.True(first.IsExact);
        h.Scheduler.Play();

        long previous = first.Frame!.Index;
        int refreshes = 0;
        while (h.Clock.Position < timing.Start + tenMinutes)
        {
            FramePresentation p = h.Refresh(Refresh60, 1 + drift);
            refreshes++;
            DecodedFrame shown = p.Frame!;
            if (!p.IsExact || !shown.Contains(p.SourceTime) || shown.Index < previous)
            {
                Assert.Fail($"Refresco {refreshes}: reloj en {p.SourceTime}, debía verse {p.TargetFrame} y se vio {shown.Index} ({shown.Pts}).");
            }

            previous = shown.Index;
        }

        PlaybackStats stats = h.Scheduler.Stats;
        Assert.True(refreshes > 35_000);
        Assert.Equal(0, stats.LateRefreshes);
        Assert.Equal(0, stats.FramesDropped);
        Assert.Equal(0, stats.DecodeErrors);
        Assert.True(stats.MaxAvOffset < frame, $"Desfase máximo {stats.MaxAvOffset}, un cuadro dura {frame}.");
        Assert.Equal(previous - first.Frame.Index + 1, stats.FramesShown);
        Assert.Equal(timing.FrameAt(h.Clock.Position), previous);
        Assert.Equal(0, h.Source.OverlappingCalls);
    }
}
