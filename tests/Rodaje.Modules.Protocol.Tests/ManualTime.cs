using System.Threading.Channels;

namespace Rodaje.Modules.Protocol.Tests;

/// <summary>
/// Un reloj que solo avanza con <see cref="Advance"/>: los plazos de <see cref="ModuleClientOptions"/> se comprueban sin
/// esperarlos. Cada temporizador nuevo sale por <see cref="Created"/>, para avanzar solo cuando ya existe.
/// </summary>
internal sealed class ManualTime : TimeProvider
{
    private readonly Lock _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private long _now;

    public Channel<ManualTimer> Created { get; } = Channel.CreateUnbounded<ManualTimer>();

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_lock) return _now;
    }

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ManualTimer timer = new(this, callback, state);
        timer.Change(dueTime, period);
        Created.Writer.TryWrite(timer);
        return timer;
    }

    /// <summary>El siguiente temporizador creado; la cota de reloj real solo evita colgarse si nunca llega.</summary>
    public async Task<ManualTimer> NextTimerAsync() => await Created.Reader.ReadAsync().AsTask().WaitAsync(Support.CiBound);

    /// <summary>Avanza el reloj y dispara, en este hilo, los temporizadores que vencen.</summary>
    public void Advance(TimeSpan delta)
    {
        long target;
        lock (_lock) target = _now + delta.Ticks;
        while (true)
        {
            ManualTimer? due;
            lock (_lock)
            {
                due = _timers.Where(t => t.DueAt <= target).MinBy(t => t.DueAt);
                if (due is null)
                {
                    _now = target;
                    return;
                }
                _now = Math.Max(_now, due.DueAt);
                due.Fired++;
                if (due.Period > 0) due.DueAt += due.Period;
                else _timers.Remove(due);
            }
            due.Invoke();
        }
    }

    internal sealed class ManualTimer(ManualTime time, TimerCallback callback, object? state) : ITimer
    {
        public long DueAt { get; set; }

        public long Period { get; private set; }

        /// <summary>Lo que se pidió esperar la última vez (<see cref="Change"/>).</summary>
        public TimeSpan Due { get; private set; }

        public int Fired { get; set; }

        public bool Pending
        {
            get
            {
                lock (time._lock) return time._timers.Contains(this);
            }
        }

        public void Invoke() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (time._lock)
            {
                time._timers.Remove(this);
                Due = dueTime;
                Period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
                if (dueTime == Timeout.InfiniteTimeSpan) return true;
                DueAt = time._now + dueTime.Ticks;
                time._timers.Add(this);
                return true;
            }
        }

        public void Dispose()
        {
            lock (time._lock) time._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
