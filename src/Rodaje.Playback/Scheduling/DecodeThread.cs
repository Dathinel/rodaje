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
    private bool _exited;
    private Action? _onExit;
    private int _hangReported;

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

    /// <summary>Como <see cref="Dispose"/>, con otro plazo. Devuelve falso (y lo registra la primera vez) si el hilo no terminó a tiempo.</summary>
    /// <remarks>
    /// Lo registra una sola vez por hilo: quien cierra puede volver a pedirlo (el cierre de la sesión tras el de un
    /// decodificador ocioso) y repetir la misma línea no dice nada nuevo.
    /// </remarks>
    public bool Stop(TimeSpan timeout)
    {
        RequestStop();
        if (Thread.CurrentThread == _thread || _thread.Join(timeout))
        {
            return true;
        }

        if (Interlocked.Exchange(ref _hangReported, 1) == 0)
        {
            Trace.WriteLine($"Rodaje: el hilo «{Name}» no terminó en {timeout.TotalSeconds:0.###} s al cerrarse; sigue en segundo plano con lo que tenía en cola.");
        }

        return false;
    }

    /// <summary>
    /// Como <see cref="Stop(TimeSpan)"/>, y además corre <paramref name="release"/> cuando el hilo ya no puede usar lo que
    /// libera: en el propio hilo, detrás de todo lo encolado, o aquí mismo si el hilo ya había terminado.
    /// </summary>
    /// <remarks>
    /// Para liberar lo que usan los trabajos (el decodificador y sus contextos de FFmpeg): si el hilo sigue colgado en una
    /// llamada nativa cuando vence el plazo, liberarlo desde quien cierra sería una violación de acceso.
    /// </remarks>
    public bool Stop(TimeSpan timeout, Action release)
    {
        ArgumentNullException.ThrowIfNull(release);
        bool exited;
        lock (_gate)
        {
            exited = _exited;
            if (!exited)
            {
                _onExit += release;
            }
        }

        if (exited)
        {
            release();
        }

        return Stop(timeout);
    }

    /// <summary>
    /// Pide que el hilo termine en cuanto acabe lo ya encolado, sin esperarlo: lo nuevo que llegue se descarta. Para quien
    /// no puede quedarse esperando (el ejecutor de servicio, que atiende otros cierres y lecturas).
    /// </summary>
    public void RequestStop()
    {
        lock (_gate)
        {
            _stopping = true;
            Monitor.Pulse(_gate);
        }
    }

    /// <remarks>
    /// Un trabajo que lanza no detiene el hilo: la excepción se escribe en el registro y se sigue con la cola. Antes salía
    /// del hilo sin atrapar, lo que en .NET tumba el proceso, y lo encolado detrás (la liberación del decodificador entre
    /// ello) no corría. Quien encola no puede verla (<see cref="Post"/> no devuelve nada y los bombeos atrapan sus propios
    /// errores), así que el registro es donde queda.
    /// </remarks>
    private void Run()
    {
        try
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
                        break;
                    }

                    work = _work.Dequeue();
                }

                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Rodaje: un trabajo del hilo «{Name}» lanzó; se sigue con la cola: {ex}");
                }
            }
        }
        finally
        {
            // Pase lo que pase con el bucle, lo encargado con Stop(TimeSpan, Action) corre al salir, y lo que llegue
            // después se descarta (o se libera en quien cierra) en vez de quedar en una cola que nadie atiende.
            Action? onExit;
            lock (_gate)
            {
                _stopping = true;
                _exited = true;
                onExit = _onExit;
                _onExit = null;
            }

            RunExit(onExit);
        }
    }

    /// <summary>
    /// Corre cada liberación por separado: si una lanza, se registra y las demás corren igual. Una excepción aquí, en el
    /// propio hilo, tumbaría el proceso.
    /// </summary>
    private void RunExit(Action? onExit)
    {
        if (onExit is null)
        {
            return;
        }

        foreach (Delegate release in onExit.GetInvocationList())
        {
            try
            {
                ((Action)release)();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Rodaje: al liberar lo del hilo «{Name}» al cerrarse: {ex}");
            }
        }
    }
}
