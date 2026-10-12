using System.Diagnostics;
using Rodaje.Modules.Protocol;

namespace Rodaje.App.Services.Modules;

/// <summary>
/// El proceso de un módulo: se abre con la primera llamada, se cierra tras <see cref="ModuleManagerOptions.IdleTimeout"/>
/// sin llamadas y se vigila con <c>ping</c> mientras hay alguna (sin respuesta en <see cref="ModuleManagerOptions.PingTimeout"/>
/// se da por colgado y se mata). Si muere, la próxima llamada lo vuelve a abrir.
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
    private volatile bool _hung;

    public ModuleDescriptor Descriptor { get; } = descriptor;

    public bool IsAlive => Volatile.Read(ref _process) is not null;

    public int ActiveCalls => Volatile.Read(ref _active);

    public int? ProcessId => Volatile.Read(ref _process)?.ProcessId;

    /// <summary>El último proceso se mató por no responder al <c>ping</c>.</summary>
    public bool WasHung => _hung;

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
            _idleTimer = new Timer(_ => _ = CloseIfIdleAsync(generation), null, options.IdleTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>El proceso abierto (lo abre si hace falta). Arranca la vigilancia de la llamada en curso.</summary>
    public async Task<ModuleProcess> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is null)
            {
                _hung = false;
                ModuleProcess process = await ModuleProcess.StartAsync(Descriptor.EntryPath, null, options.ClientOptions,
                    p => job?.TryAssign(p), cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _process, process);
                _ = process.Exited.ContinueWith(_ => OnExited(process), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                _ = process.Client.Completion.ContinueWith(_ => OnExited(process), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            ModuleProcess current = _process;
            lock (_lock)
            {
                if (_watchdog.IsCompleted)
                {
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

    private void OnExited(ModuleProcess process)
    {
        if (Interlocked.CompareExchange(ref _process, null, process) == process)
        {
            process.Kill();
            _ = process.DisposeAsync().AsTask();
            closed(this);
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

                await process.Client.PingAsync().WaitAsync(options.PingTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _hung = true;
                Trace.WriteLine($"Rodaje: el módulo {Descriptor.Id} no respondió al ping en {options.PingTimeout.TotalSeconds:0.#} s; se cierra.");
                process.Kill();
                return;
            }
            catch (ModuleException)
            {
                return;
            }
        }
    }
}
