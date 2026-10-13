namespace Rodaje.App.Services.Crashes;

/// <summary>
/// Vigilante del filtro de fallos nativos: si el informe y el minivolcado tardan más que <see cref="Timeout"/> (volcarse
/// a sí mismo se traba si otro hilo tiene el cargador o el montón), termina el proceso. Su hilo se crea al instalar el
/// filtro y espera dormido: en medio de un fallo no se crea nada.
/// </summary>
public sealed partial class CrashWatchdog : IDisposable
{
    /// <summary>Lo que se le da al filtro antes de terminar el proceso.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly ManualResetEvent _armed = new(false);
    private readonly ManualResetEvent _done = new(false);
    private readonly Action<uint> _terminate;
    private readonly Thread _thread;
    private uint _code;
    private volatile bool _disposed;
    private volatile bool _isArmed;

    /// <param name="timeout">Tiempo que se espera al filtro.</param>
    /// <param name="terminate">Termina el proceso con ese código de salida (en la app, <c>TerminateProcess</c>).</param>
    public CrashWatchdog(TimeSpan timeout, Action<uint> terminate)
    {
        ArgumentNullException.ThrowIfNull(terminate);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        Timeout = timeout;
        _terminate = terminate;
        _thread = new Thread(Run) { IsBackground = true, Name = "Rodaje: vigilante del volcado" };
        _thread.Start();
    }

    public TimeSpan Timeout { get; }

    /// <summary>
    /// Si está contando (entre <see cref="Arm"/> y <see cref="Disarm"/>). Deja comprobar el desarme sin esperar a que
    /// venza el plazo.
    /// </summary>
    public bool IsArmed => _isArmed;

    /// <summary>Empieza a contar: si no llega <see cref="Disarm"/> a tiempo, termina el proceso con <paramref name="exitCode"/>.</summary>
    public void Arm(uint exitCode)
    {
        _code = exitCode;
        _done.Reset();
        _isArmed = true;
        _armed.Set();
    }

    /// <summary>El filtro terminó a tiempo.</summary>
    public void Disarm()
    {
        _isArmed = false;
        _done.Set();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _done.Set();
        _armed.Set();
        if (_thread.Join(TimeSpan.FromSeconds(5)))
        {
            _armed.Dispose();
            _done.Dispose();
        }
    }

    private void Run()
    {
        while (true)
        {
            _armed.WaitOne();
            _armed.Reset();
            if (_disposed)
            {
                return;
            }

            if (!_done.WaitOne(Timeout) && !_disposed)
            {
                _terminate(_code);
            }
        }
    }
}
