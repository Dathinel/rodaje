using Rodaje.Core.Timing;

namespace Rodaje.Playback.Tests.Support;

internal sealed class InlineExecutor : IDecodeExecutor
{
    public int Posted { get; private set; }

    public void Post(Action work)
    {
        Posted++;
        work();
    }
}

internal sealed class PayloadTracker
{
    private int _created;
    private int _disposed;

    public int Created => Volatile.Read(ref _created);

    public int Live => Volatile.Read(ref _created) - Volatile.Read(ref _disposed);

    public IDisposable Create()
    {
        Interlocked.Increment(ref _created);
        return new Payload(this);
    }

    private sealed class Payload(PayloadTracker owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                throw new InvalidOperationException("Contenido liberado dos veces.");
            }

            Interlocked.Increment(ref owner._disposed);
        }
    }
}

internal static class Frames
{
    public static DecodedFrame Make(long index, Rational rate, PayloadTracker? tracker = null, long size = 1)
    {
        Time pts = Time.FromFrames(index, rate);
        return new DecodedFrame(index, pts, Time.FromFrames(index + 1, rate) - pts, tracker?.Create(), size);
    }
}

/// <summary>Decodificador falso con latencia en tiempo virtual: cada pedido termina cuando el arnés llega a su vencimiento.</summary>
internal sealed class SimulatedSource(Rational rate, PayloadTracker tracker) : IVideoFrameSource
{
    private Pending? _inFlight;

    public Func<long, Time> Latency { get; set; } = _ => Time.Zero;

    /// <summary>Permite inyectar fallos: devuelve el cuadro a entregar (o lanza) en lugar del normal.</summary>
    public Func<long, DecodedFrame?>? Override { get; set; }

    public bool HonorCancellation { get; set; } = true;

    public Time Now { get; set; }

    public List<long> Requests { get; } = [];

    public int OverlappingCalls { get; private set; }

    public bool IsBusy => _inFlight is not null;

    public ValueTask<DecodedFrame?> GetFrameAsync(long frameIndex, CancellationToken cancellationToken)
    {
        if (_inFlight is not null)
        {
            OverlappingCalls++;
        }

        Requests.Add(frameIndex);
        Time latency = Latency(frameIndex);
        if (latency == Time.Zero)
        {
            return new ValueTask<DecodedFrame?>(Produce(frameIndex));
        }

        var completion = new TaskCompletionSource<DecodedFrame?>();
        var pending = new Pending(frameIndex, Now + latency, completion);
        _inFlight = pending;
        if (HonorCancellation)
        {
            pending.Registration = cancellationToken.Register(() =>
            {
                if (_inFlight == pending)
                {
                    _inFlight = null;
                }

                completion.TrySetCanceled(cancellationToken);
            });
        }

        return new ValueTask<DecodedFrame?>(completion.Task);
    }

    /// <summary>Avanza el tiempo virtual completando en orden lo que vence, como un decodificador secuencial.</summary>
    public void AdvanceTo(Time now)
    {
        while (_inFlight is { } pending && pending.Due <= now)
        {
            Now = pending.Due;
            Complete(pending);
        }

        Now = now;
    }

    /// <summary>Termina todo lo pendiente sin esperar su vencimiento.</summary>
    public void Settle()
    {
        for (int guard = 0; _inFlight is { } pending; guard++)
        {
            if (guard > 100_000)
            {
                throw new InvalidOperationException("La decodificación no se detiene.");
            }

            Complete(pending);
        }
    }

    private void Complete(Pending pending)
    {
        _inFlight = null;
        pending.Registration.Dispose();
        try
        {
            pending.Completion.SetResult(Produce(pending.Index));
        }
        catch (Exception ex)
        {
            pending.Completion.SetException(ex);
        }
    }

    private DecodedFrame? Produce(long index) =>
        Override is { } custom ? custom(index) : Frames.Make(index, rate, tracker);

    private sealed class Pending(long index, Time due, TaskCompletionSource<DecodedFrame?> completion)
    {
        public long Index { get; } = index;

        public Time Due { get; } = due;

        public TaskCompletionSource<DecodedFrame?> Completion { get; } = completion;

        public CancellationTokenRegistration Registration { get; set; }
    }
}

internal sealed class FakeTimeProvider : TimeProvider
{
    public long Timestamp { get; set; } = 1_000;

    public override long TimestampFrequency => 10_000_000;

    public override long GetTimestamp() => Timestamp;

    public void Advance(TimeSpan delta) => Timestamp += delta.Ticks;
}
