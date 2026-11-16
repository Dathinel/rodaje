using System.Diagnostics;

namespace Rodaje.Export.Running;

/// <summary>Detiene la entrega de cuadros sin cerrar la tubería; una pausa larga cancela el fragmento.</summary>
public sealed class ExportPauseToken
{
    private readonly object _gate = new();
    private readonly TimeSpan _timeout;
    private bool _paused;
    private bool _waiting;
    private long _since;
    private long _waitingTimestamp;

    public ExportPauseToken(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "El límite de pausa debe ser positivo.");
        }
    }

    public bool IsPauseRequested => Volatile.Read(ref _paused);

    /// <summary>La exportación ha reconocido la pausa y ya no entrega cuadros.</summary>
    public bool IsWaiting => Volatile.Read(ref _waiting);

    internal long WaitingTimestamp => Interlocked.Read(ref _waitingTimestamp);

    public void Pause()
    {
        lock (_gate)
        {
            if (!_paused)
            {
                _since = Stopwatch.GetTimestamp();
                Interlocked.Exchange(ref _waitingTimestamp, 0);
                Volatile.Write(ref _paused, true);
            }
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            Volatile.Write(ref _paused, false);
            Monitor.PulseAll(_gate);
        }
    }

    internal void Wait(CancellationToken cancellationToken)
    {
        if (!IsPauseRequested)
        {
            return;
        }

        using CancellationTokenRegistration registration = cancellationToken.Register(Wake);
        lock (_gate)
        {
            try
            {
                while (_paused)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TimeSpan remaining = _timeout - Stopwatch.GetElapsedTime(_since);
                    if (remaining <= TimeSpan.Zero)
                    {
                        throw new OperationCanceledException("El fragmento superó el límite de pausa.");
                    }

                    if (!_waiting)
                    {
                        Interlocked.Exchange(ref _waitingTimestamp, Stopwatch.GetTimestamp());
                        Volatile.Write(ref _waiting, true);
                    }
                    Monitor.Wait(_gate, (int)Math.Clamp(Math.Ceiling(remaining.TotalMilliseconds), 1, int.MaxValue));
                }
            }
            finally
            {
                Volatile.Write(ref _waiting, false);
            }
        }
    }

    private void Wake()
    {
        lock (_gate)
        {
            Monitor.PulseAll(_gate);
        }
    }
}
