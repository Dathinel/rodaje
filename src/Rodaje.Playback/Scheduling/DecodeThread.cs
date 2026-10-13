using System.Diagnostics;

namespace Rodaje.Playback;

/// <summary>Hilo dedicado que ejecuta en orden el trabajo de decodificación.</summary>
/// <remarks>
/// Cerrarlo espera a que termine lo ya encolado, pero no para siempre: si un trabajo se cuelga (un archivo que no termina
/// de abrir, un controlador que no suelta el decodificador) <see cref="Dispose"/> vuelve tras <see cref="JoinTimeout"/>, lo
/// deja escrito en el registro (<see cref="Trace"/>, que la app lleva al registro de la sesión) y el hilo, que es de
/// fondo, sigue con su cola cuando el trabajo colgado vuelva. Nada de lo encolado se pierde ni se ejecuta dos veces: quien
/// cierra no toca lo que el hilo todavía tiene, solo deja de esperarlo.
/// </remarks>
public sealed class DecodeThread : IDecodeExecutor, IDisposable
{
    /// <summary>
    /// Lo que espera <see cref="Dispose"/> por omisión. Liberar un decodificador real (su pool de D3D11VA) tarda decenas de
    /// milisegundos; 5 s es mucho más que eso y poco para quien cierra la vista previa o el programa.
    /// </summary>
    public static readonly TimeSpan DefaultJoinTimeout = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly Queue<Action> _work = new();
    private readonly Thread _thread;
    private bool _stopping;

    public DecodeThread(string name = "Rodaje decodificación")
        : this(name, DefaultJoinTimeout)
    {
    }

    public DecodeThread(string name, TimeSpan joinTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(joinTimeout, TimeSpan.Zero);
        JoinTimeout = joinTimeout;
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
    }

    /// <summary>Lo que espera <see cref="Dispose"/> a que el hilo termine.</summary>
    public TimeSpan JoinTimeout { get; }

    /// <summary>Nombre del hilo, para el registro.</summary>
    public string Name => _thread.Name ?? string.Empty;

    /// <summary>Verdadero mientras el hilo no haya terminado (también si se cerró y sigue colgado en un trabajo).</summary>
    public bool IsAlive => _thread.IsAlive;

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

    /// <summary>Termina lo ya encolado y detiene el hilo; espera como mucho <see cref="JoinTimeout"/>.</summary>
    /// <remarks>
    /// Cada llamada espera, también la segunda: antes, si otro hilo ya lo estaba cerrando, volvía de inmediato con el
    /// trabajo todavía en curso (lo que era correcto solo por el orden en que el programador cierra sus ejecutores). Desde
    /// el propio hilo no espera, porque sería esperarse a sí mismo.
    /// </remarks>
    public void Dispose() => Stop(JoinTimeout);

    /// <summary>Como <see cref="Dispose"/>, con otro plazo. Devuelve falso (y lo registra) si el hilo no terminó a tiempo.</summary>
    public bool Stop(TimeSpan timeout)
    {
        lock (_gate)
        {
            _stopping = true;
            Monitor.Pulse(_gate);
        }

        if (Thread.CurrentThread == _thread || _thread.Join(timeout))
        {
            return true;
        }

        Trace.WriteLine($"Rodaje: el hilo «{Name}» no terminó en {timeout.TotalSeconds:0.###} s al cerrarse; sigue en segundo plano con lo que tenía en cola.");
        return false;
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
