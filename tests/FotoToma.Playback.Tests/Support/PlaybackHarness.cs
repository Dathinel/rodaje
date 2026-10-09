using FotoToma.Core.Timing;

namespace FotoToma.Playback.Tests.Support;

/// <summary>Programador con reloj manual, decodificador simulado y ejecución en línea: todo en tiempo virtual.</summary>
internal sealed class PlaybackHarness : IDisposable
{
    /// <summary>Un refresco de una pantalla de 60 Hz, exacto en ticks.</summary>
    public static readonly Time Refresh60 = Time.FromTicks(Time.TicksPerSecond / 60);

    public PlaybackHarness(ClipTiming timing, Func<long, Time>? latency = null, int prefetch = 8, long budget = 64)
    {
        Timing = timing;
        Clock = new ManualClock(timing.Start);
        Source = new SimulatedSource(timing.FrameRate, Payloads) { Latency = latency ?? (_ => Time.Zero) };
        Cache = new FrameCache(budget);
        Scheduler = new PlaybackScheduler(timing, Clock, Source, Cache, Executor,
            new PlaybackOptions { PrefetchFrames = prefetch });
        Scheduler.ExactFrameReady += (_, e) => ExactReady.Add(e.FrameIndex);
        Scheduler.EndReached += (_, _) => EndCount++;
        Scheduler.DecodeFailed += (_, e) => Failures.Add(e);
    }

    public ClipTiming Timing { get; }

    public PayloadTracker Payloads { get; } = new();

    public InlineExecutor Executor { get; } = new();

    public ManualClock Clock { get; }

    public SimulatedSource Source { get; }

    public FrameCache Cache { get; }

    public PlaybackScheduler Scheduler { get; }

    public List<long> ExactReady { get; } = [];

    public List<DecodeFailedEventArgs> Failures { get; } = [];

    public int EndCount { get; private set; }

    public Time WallTime { get; private set; }

    public static Time Ms(double milliseconds) => Time.FromSeconds(milliseconds / 1000);

    /// <summary>Simula un refresco: pasa el tiempo de pared, el reloj avanza a su ritmo y luego se elige el cuadro.</summary>
    public FramePresentation Refresh(Time wallDelta, double clockRate = 1.0)
    {
        WallTime += wallDelta;
        Clock.Advance(Time.FromTicks((long)Math.Round(wallDelta.Ticks * clockRate)));
        Source.AdvanceTo(WallTime);
        return Scheduler.Tick();
    }

    public FramePresentation Refresh() => Refresh(Refresh60);

    public FramePresentation SettleAndTick()
    {
        Source.Settle();
        return Scheduler.Tick();
    }

    public void Dispose()
    {
        Scheduler.Dispose();
        Source.Settle();
        Cache.Dispose();
    }
}
