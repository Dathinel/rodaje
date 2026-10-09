using CsCheck;
using Rodaje.Core.Timing;
using Rodaje.Playback.Tests.Support;
using static Rodaje.Playback.Tests.Support.PlaybackHarness;

namespace Rodaje.Playback.Tests;

public class PlaybackPropertyTests
{
    private static readonly Gen<Rational> GenRate = Gen.OneOfConst(
        Rational.Ntsc24, Rational.Fps24, Rational.Fps25, Rational.Ntsc30,
        Rational.Fps30, Rational.Fps50, Rational.Ntsc60, Rational.Fps60);

    // Entrada en cualquier tick de los primeros 100 s, de 1 tick a 60 s de largo, en cualquier punto de la línea de tiempo
    private static readonly Gen<ClipTiming> GenTiming = Gen.Select(
        GenRate,
        Gen.Long[0, 100 * Time.TicksPerSecond],
        Gen.Long[1, 60 * Time.TicksPerSecond],
        Gen.Long[0, 1_000 * Time.TicksPerSecond],
        (rate, sourceIn, length, start) => new ClipTiming(
            Time.FromTicks(start), Time.FromTicks(sourceIn), Time.FromTicks(sourceIn + length), rate));

    private enum Op
    {
        Seek,
        Play,
        Pause,
        Run,
        Settle,
    }

    private static readonly Gen<(Op Op, double Where, int Count)> GenOp =
        Gen.Select(Gen.Enum<Op>(), Gen.Double[-0.1, 1.1], Gen.Int[1, 40]);

    [Fact]
    public void SeekToFrame_MuestraElCuadroExacto()
    {
        Gen.Select(GenTiming, Gen.Int[0, 3], Gen.Double[0, 1], Gen.Int[0, 50]).Sample((timing, edge, where, latencyMs) =>
        {
            long frames = timing.LastFrame - timing.FirstFrame;
            long target = edge switch
            {
                0 => timing.FirstFrame,
                1 => timing.LastFrame,
                _ => timing.FirstFrame + (long)Math.Round(where * frames),
            };

            using var h = new PlaybackHarness(timing, _ => Ms(latencyMs));
            h.Scheduler.SeekToFrame(target);
            FramePresentation p = h.SettleAndTick();

            Assert.True(p.IsExact);
            Assert.Equal(target, p.Frame!.Index);
            Assert.Equal(Time.FromFrames(target, timing.FrameRate), p.Frame.Pts);
            Assert.Equal(target, h.ExactReady[^1]);
            Assert.True(p.Frame.Contains(p.SourceTime) || target == timing.FirstFrame);
        }, iter: 400);
    }

    [Fact]
    public void Seek_AUnInstanteMuestraElCuadroQueLoContiene()
    {
        Gen.Select(GenTiming, Gen.Double[-0.2, 1.2]).Sample((timing, where) =>
        {
            Time at = timing.Start + Time.FromTicks((long)(where * timing.Duration.Ticks));
            Time clamped = timing.Clamp(at);
            long expected = timing.ToSource(clamped).ToFrames(timing.FrameRate);

            using var h = new PlaybackHarness(timing);
            h.Scheduler.Seek(at);
            FramePresentation p = h.SettleAndTick();

            Assert.Equal(clamped, h.Clock.Position);
            Assert.Equal(expected, p.Frame!.Index);
            Assert.True(p.Frame.Contains(timing.ToSource(clamped)));
        }, iter: 400);
    }

    [Fact]
    public void SecuenciasAleatorias_MantienenInvariantesYNoFiltranCuadros()
    {
        Gen.Select(GenTiming, Gen.Int[0, 80], Gen.Int[1, 10], Gen.Int[1, 20], GenOp.Array[1, 30], Gen.Double[0, 1])
            .Sample((timing, latencyMs, prefetch, budget, ops, finalWhere) =>
            {
                var h = new PlaybackHarness(timing, i => Ms(latencyMs * (1 + i % 3) / 2.0), prefetch, budget);
                foreach ((Op op, double where, int count) in ops)
                {
                    switch (op)
                    {
                        case Op.Seek:
                            h.Scheduler.Seek(timing.Start + Time.FromTicks((long)(where * timing.Duration.Ticks)));
                            break;
                        case Op.Play:
                            h.Scheduler.Play();
                            break;
                        case Op.Pause:
                            h.Scheduler.Pause();
                            break;
                        case Op.Run:
                            for (int i = 0; i < count; i++)
                            {
                                CheckInvariants(h, h.Refresh(Refresh60, 1 + (where - 0.5) * 0.002));
                            }

                            break;
                        case Op.Settle:
                            CheckInvariants(h, h.SettleAndTick());
                            break;
                    }
                }

                h.Scheduler.Pause();
                long target = timing.FirstFrame + (long)(finalWhere * (timing.LastFrame - timing.FirstFrame));
                h.Scheduler.SeekToFrame(target);
                FramePresentation last = h.SettleAndTick();
                Assert.True(last.IsExact);
                Assert.Equal(target, last.Frame!.Index);

                PlaybackStats stats = h.Scheduler.Stats;
                Assert.True(stats.FramesShown >= 1);
                Assert.True(stats.FramesDropped >= 0 && stats.LateRefreshes >= 0);
                Assert.Equal(0, h.Source.OverlappingCalls);

                h.Dispose();
                Assert.Equal(0, h.Payloads.Live);
            }, iter: 300);
    }

    private static void CheckInvariants(PlaybackHarness h, FramePresentation p)
    {
        Assert.InRange(p.TargetFrame, h.Timing.FirstFrame, h.Timing.LastFrame);
        Assert.Equal(h.Timing.FrameAt(h.Clock.Position), p.TargetFrame);
        if (p.Frame is { } frame)
        {
            Assert.False(frame.IsReleased);
            Assert.InRange(frame.Index, h.Timing.FirstFrame, h.Timing.LastFrame);
            Assert.Equal(p.IsExact, frame.Index == p.TargetFrame);
        }
        else
        {
            Assert.False(p.IsExact);
        }

        Assert.True(h.Cache.CostBytes <= h.Cache.BudgetBytes || h.Cache.Count == 1);
    }
}
