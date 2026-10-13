namespace Rodaje.App.Services.Packages;

/// <summary>
/// Avance para la interfaz que no la satura: guarda solo el último valor y lo entrega en el hilo donde se creó como mucho
/// una vez cada <c>interval</c> (100 ms por defecto). Una descarga informa cada 64 KB, cientos de veces por segundo; con
/// <see cref="Progress{T}"/> cada informe era un mensaje en la cola de la interfaz y la ventana dejaba de responder.
/// </summary>
public sealed class ThrottledProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;
    private readonly SynchronizationContext? _context;
    private readonly long _intervalMs;
    private readonly Lock _gate = new();
    private T _latest = default!;
    private bool _scheduled;
    private bool _closed;
    private long _lastDelivered = long.MinValue / 2;

    public ThrottledProgress(Action<T> handler, TimeSpan? interval = null)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _context = SynchronizationContext.Current;
        _intervalMs = (long)(interval ?? TimeSpan.FromMilliseconds(100)).TotalMilliseconds;
    }

    public void Report(T value)
    {
        long wait;
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _latest = value;
            if (_scheduled)
            {
                return;
            }

            _scheduled = true;
            wait = _lastDelivered + _intervalMs - Now();
        }

        if (wait <= 0)
        {
            Post();
        }
        else
        {
            _ = PostLaterAsync(wait);
        }
    }

    /// <summary>No entrega nada más (la operación terminó y la fila ya muestra otra cosa).</summary>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
        }
    }

    private static long Now() => System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency;

    private async Task PostLaterAsync(long milliseconds)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(milliseconds)).ConfigureAwait(false);
        Post();
    }

    private void Post()
    {
        if (_context is null)
        {
            ThreadPool.QueueUserWorkItem(static s => s.Deliver(), this, preferLocal: false);
        }
        else
        {
            _context.Post(static s => ((ThrottledProgress<T>)s!).Deliver(), this);
        }
    }

    private void Deliver()
    {
        T value;
        lock (_gate)
        {
            _scheduled = false;
            if (_closed)
            {
                return;
            }

            value = _latest;
            _lastDelivered = Now();
        }

        _handler(value);
    }
}
