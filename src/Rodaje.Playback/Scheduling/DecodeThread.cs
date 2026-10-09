namespace Rodaje.Playback;

/// <summary>Hilo dedicado que ejecuta en orden el trabajo de decodificación.</summary>
public sealed class DecodeThread : IDecodeExecutor, IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<Action> _work = new();
    private readonly Thread _thread;
    private bool _stopping;

    public DecodeThread(string name = "Rodaje decodificación")
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
    }

    /// <summary>Encola el trabajo. Tras <see cref="Dispose"/> se descarta en silencio.</summary>
    public void Post(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            if (_stopping)
            {
                return;
            }

            _work.Enqueue(work);
            Monitor.Pulse(_gate);
        }
    }

    /// <summary>Termina lo ya encolado y detiene el hilo.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_stopping)
            {
                return;
            }

            _stopping = true;
            Monitor.Pulse(_gate);
        }

        if (Thread.CurrentThread != _thread)
        {
            _thread.Join();
        }
    }

    private void Run()
    {
        while (true)
        {
            Action work;
            lock (_gate)
            {
                while (_work.Count == 0 && !_stopping)
                {
                    Monitor.Wait(_gate);
                }

                if (_work.Count == 0)
                {
                    return;
                }

                work = _work.Dequeue();
            }

            work();
        }
    }
}
