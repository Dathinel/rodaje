using System.Diagnostics;
using Rodaje.App.Services.Crashes;
using Rodaje.Modules.Protocol;

namespace Rodaje.App.Services.Modules;

/// <summary>
/// El proceso de un módulo: se abre con la primera llamada, se cierra tras <see cref="ModuleManagerOptions.IdleTimeout"/>
/// sin llamadas y se vigila con <c>ping</c> mientras hay alguna (sin respuesta ni ningún otro mensaje en
/// <see cref="ModuleManagerOptions.PingTimeout"/> se da por colgado y se mata). Si muere, la próxima llamada lo vuelve a abrir.
/// </summary>
internal sealed partial class ModuleConnection(ModuleDescriptor descriptor, ModuleManagerOptions options, ModuleJobObject? job, Action<ModuleConnection> closed) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lock = new();
    private ModuleProcess? _process;
    private int _active;
    private int _idleGeneration;
    private Timer? _idleTimer;
    private Task _watchdog = Task.CompletedTask;
    private ModuleProcess? _watched;
    private ModuleProcess? _hungProcess;

    public ModuleDescriptor Descriptor { get; } = descriptor;

    public bool IsAlive => Volatile.Read(ref _process) is not null;

    public int ActiveCalls => Volatile.Read(ref _active);

    public int? ProcessId => Volatile.Read(ref _process)?.ProcessId;

    /// <summary>Mata el árbol de procesos de un módulo retirado. Costura para las pruebas (por reflexión).</summary>
    internal Action<ModuleProcess> KillProcess { get; set; } = static p => p.Kill();

    /// <summary>
    /// <paramref name="process"/> se mató por no responder al <c>ping</c>. Va por proceso y no por conexión: si la llamada
    /// siguiente ya abrió otro, la que falló con el colgado sigue sabiendo por qué.
    /// </summary>
    public bool WasHung(ModuleProcess? process) => process is not null && ReferenceEquals(Volatile.Read(ref _hungProcess), process);

    public void BeginCall()
    {
        lock (_lock)
        {
            _active++;
            _idleGeneration++;
            _idleTimer?.Dispose();
            _idleTimer = null;
        }
    }

    public void EndCall()
    {
        lock (_lock)
        {
            if (--_active > 0)
            {
                return;
            }

            int generation = ++_idleGeneration;
            _idleTimer?.Dispose();
            _idleTimer = new Timer(_ => UiTask.Run(CloseIfIdleAsync(generation)), null, options.IdleTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>El proceso abierto (lo abre si hace falta). Arranca la vigilancia de la llamada en curso.</summary>
    public async Task<ModuleProcess> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Un proceso muerto o matado por colgado se suelta aquí mismo: OnExited llega por continuaciones asíncronas
            // (RunContinuationsAsynchronously) y, con el pool ocupado, la llamada siguiente podía tomar todavía el proceso
            // muerto y fallar al instante con «El módulo cerró la conexión» (CI de Windows tras #111).
            if (_process is { } stale && IsDead(stale))
            {
                OnExited(stale);
            }

            if (_process is null)
            {
                ModuleProcess process = await ModuleProcess.StartAsync(Descriptor.EntryPath, null,
                    options.ClientOptions with { HandshakeTimeout = options.StartTimeout },
                    p => job?.TryAssign(p), cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _process, process);
                // El corte del cliente puede llegar como fallo de Completion: es el cierre esperado (se lee para darlo por
                // visto) y OnExited no lanza; UiTask.Run observa además la continuación.
                UiTask.Run(process.Exited.ContinueWith(_ => OnExited(process), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
                UiTask.Run(process.Client.Completion.ContinueWith(t =>
                {
                    _ = t.Exception;
                    OnExited(process);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
            }

            ModuleProcess current = _process;
            lock (_lock)
            {
                // Uno por proceso: el del anterior sale solo al ver que su proceso ya no es el actual, pero con el pool
                // cargado puede no haber despertado todavía, y esperar a que termine dejaba al nuevo sin ping.
                if (_watchdog.IsCompleted || !ReferenceEquals(_watched, current))
                {
                    _watched = current;
                    _watchdog = Task.Run(() => WatchAsync(current), CancellationToken.None);
                }
            }

            return current;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Cierra el proceso si no hay llamadas (para liberar VRAM o al ocioso). <c>true</c> si quedó cerrado.</summary>
    public async Task<bool> CloseIfIdleAsync() => await CloseIfIdleAsync(null).ConfigureAwait(false);

    /// <summary>Cierra el proceso aunque haya llamadas (al salir de Rodaje).</summary>
    public async Task CloseAsync()
    {
        lock (_lock)
        {
            _idleTimer?.Dispose();
            _idleTimer = null;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeProcessAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Tras <see cref="CloseAsync"/>.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _idleTimer?.Dispose();
            _idleTimer = null;
        }

        _gate.Dispose();
    }

    private async Task<bool> CloseIfIdleAsync(int? generation)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                if (_active > 0 || (generation is { } g && g != _idleGeneration))
                {
                    return false;
                }
            }

            await DisposeProcessAsync().ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DisposeProcessAsync()
    {
        ModuleProcess? process = Interlocked.Exchange(ref _process, null);
        if (process is null)
        {
            return;
        }

        await process.DisposeAsync().ConfigureAwait(false);
        closed(this);
    }

    private bool IsDead(ModuleProcess process) =>
        process.Exited.IsCompleted || process.Client.Completion.IsCompleted || ReferenceEquals(Volatile.Read(ref _hungProcess), process);

    private void OnExited(ModuleProcess process)
    {
        if (Interlocked.CompareExchange(ref _process, null, process) != process)
        {
            return;
        }

        // Matar el árbol de procesos puede tardar segundos con la CPU llena: nunca en línea. Desde EnsureStartedAsync
        // esto corre con _gate tomado y quizá en el hilo de la interfaz. En un hilo propio, como KillWhenFaulted, y
        // observado con UiTask para que una excepción del cierre no se pierda.
        Action<ModuleProcess> kill = KillProcess;
        UiTask.Run(Task.Factory.StartNew(() =>
        {
            kill(process);
            return process.DisposeAsync().AsTask();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap());

        // Corre en una continuación que nadie espera o dentro de EnsureStartedAsync: un fallo del aviso de cierre se
        // informa aquí y no se pierde (ni hace fallar la llamada que abre otro proceso).
        try
        {
            closed(this);
        }
#pragma warning disable CA1031 // Ver arriba.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            UiTask.Report(ex, notify: false);
        }
    }

    private async Task WatchAsync(ModuleProcess process)
    {
        while (ActiveCalls > 0 && ReferenceEquals(Volatile.Read(ref _process), process))
        {
            try
            {
                await Task.WhenAny(process.Exited, Task.Delay(options.PingInterval)).ConfigureAwait(false);
                if (ActiveCalls == 0 || process.Exited.IsCompleted)
                {
                    return;
                }

                ModuleClient client = process.Client;
                if (await PingRoundAsync(() => client.PingAsync(), () => client.ReceivedMessages,
                    () => Task.WhenAny(process.Exited, Task.Delay(options.PingTimeout))).ConfigureAwait(false))
                {
                    continue;
                }

                if (process.Exited.IsCompleted)
                {
                    return;
                }

                Volatile.Write(ref _hungProcess, process);
                Trace.WriteLine($"Rodaje: el módulo {Descriptor.Id} no respondió al ping ni mandó nada en {options.PingTimeout.TotalSeconds:0.#} s; se cierra.");
                process.Kill();
                return;
            }
            catch (ModuleException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Una vuelta del vigilante: manda un <c>ping</c> y espera el <c>pong</c> por ventanas (<paramref name="window"/>, de
    /// <see cref="ModuleManagerOptions.PingTimeout"/>). Si una ventana vence sin <c>pong</c> pero el módulo mandó algo en
    /// ella (<paramref name="activity"/>: avances, registros, resultados), está ocupado y no colgado: se sigue esperando el
    /// mismo <c>ping</c>. Solo una ventana entera sin ningún mensaje lo da por colgado. Con la CPU llena (CinemaDNG en la
    /// CI de Windows), el <c>pong</c> de un módulo sano que revelaba y avisaba su avance se atrasaba más que el tope y se
    /// mataba a mitad de trabajo. Las ventanas se inyectan para que las pruebas no dependan del reloj.
    /// </summary>
    /// <returns><c>false</c> si el módulo quedó colgado (o una ventana terminó porque el proceso salió sin contestar).</returns>
    /// <exception cref="ModuleException">El <c>ping</c> falló porque la conexión se cortó.</exception>
    internal static async Task<bool> PingRoundAsync(Func<Task> ping, Func<long> activity, Func<Task> window)
    {
        long seen = activity();
        Task pong = ping();
        while (true)
        {
            if (await Task.WhenAny(pong, window()).ConfigureAwait(false) == pong)
            {
                await pong.ConfigureAwait(false);
                return true;
            }

            long now = activity();
            if (now == seen)
            {
                // El ping queda pendiente hasta que la conexión se corte al matar el proceso: su fallo no se pierde.
                _ = pong.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return false;
            }

            seen = now;
        }
    }
}
