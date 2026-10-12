using System.Collections.Concurrent;
using System.Text.Json;
using Rodaje.Modules.Protocol;

namespace Rodaje.App.Services.Modules;

/// <summary>
/// Los módulos de IA desde la app (D-031): los descubre, abre un proceso por módulo con la primera llamada y lo cierra
/// tras 2 min ocioso, los mete en un Job Object para que ninguno sobreviva a Rodaje, los vigila con <c>ping</c>, reparte la
/// VRAM (<see cref="VramBudget"/>), baja la vista previa mientras corre uno de GPU y lleva cada trabajo a Trabajos con
/// avance y cancelar. Un módulo que muere, se cuelga o manda basura solo falla su trabajo: la app y el proyecto siguen.
/// Los resultados nunca editan: quien llama los convierte en una <see cref="ModuleProposal"/>.
/// </summary>
public sealed partial class ModuleManager : IAsyncDisposable
{
    private static readonly TimeSpan EvictionRetry = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ExitCodeWait = TimeSpan.FromMilliseconds(500);

    private readonly ModuleManagerOptions _options;
    private readonly IModulePreview? _preview;
    private readonly ModuleJobObject _job = new();
    private readonly ConcurrentDictionary<string, ModuleConnection> _connections = new(StringComparer.Ordinal);
    private readonly SynchronizationContext? _context;
    private IReadOnlyList<ModuleDescriptor> _modules = [];
    private bool _disposed;

    public ModuleManager(ModuleManagerOptions options, IModulePreview? preview = null, SynchronizationContext? context = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _preview = preview;
        _context = context;
        Jobs = new ModuleJobList(context);
    }

    public IReadOnlyList<ModuleDescriptor> Modules => Volatile.Read(ref _modules);

    public IReadOnlyList<string> Problems { get; private set; } = [];

    public ModuleJobList Jobs { get; }

    private VramBudget? _budget;

    /// <summary>Se arma con la primera llamada: la GPU del reproductor abre después que la ventana.</summary>
    public VramBudget Budget
    {
        get
        {
            if (Volatile.Read(ref _budget) is { } budget)
            {
                return budget;
            }

            Interlocked.CompareExchange(ref _budget, new VramBudget(_options.DedicatedVideoMemory()), null);
            return _budget;
        }
    }

    /// <summary>Windows dio el Job Object «matar al cerrar».</summary>
    public bool KillsOnClose => _job.IsAvailable;

    /// <summary>Procesos de módulos abiertos ahora.</summary>
    public IReadOnlyList<int> ProcessIds => [.. _connections.Values.Select(c => c.ProcessId).OfType<int>()];

    /// <summary>Cambió la lista de módulos.</summary>
    public event EventHandler? ModulesChanged;

    /// <summary>Un trabajo falló (en el contexto de la interfaz); la ventana lo muestra como aviso.</summary>
    public event EventHandler<ModuleFailure>? Failed;

    /// <summary>Vuelve a buscar los módulos en disco (por ejemplo, tras instalar uno). Lee el disco en el hilo que llama.</summary>
    public void Refresh()
    {
        var problems = new List<string>();
        Volatile.Write(ref _modules, ModuleCatalog.Scan(_options.Roots, problems));
        Problems = problems;
        Post(() => ModulesChanged?.Invoke(this, EventArgs.Empty));
    }

    public ModuleDescriptor? Find(string moduleId) => Modules.FirstOrDefault(m => string.Equals(m.Id, moduleId, StringComparison.Ordinal));

    /// <summary>El primer módulo instalado que ofrece <paramref name="function"/>.</summary>
    public ModuleDescriptor? FindByFunction(string function) => Modules.FirstOrDefault(m => m.Offers(function));

    /// <summary>Una sola llamada como trabajo (sin búferes).</summary>
    public Task<ModuleResult> InvokeAsync(string moduleId, string function, JsonElement? parameters, string title, CancellationToken cancellationToken = default) =>
        RunAsync(moduleId, title, call => call.InvokeAsync(function, parameters), cancellationToken);

    /// <summary>
    /// Corre <paramref name="work"/> como un trabajo de Trabajos con el módulo abierto. Espera VRAM si hace falta (cerrando
    /// módulos ociosos que la ocupan), baja la vista previa si el módulo usa la GPU y libera las regiones al terminar.
    /// </summary>
    /// <exception cref="OperationCanceledException">Cancelado (desde la fila o con <paramref name="cancellationToken"/>).</exception>
    /// <exception cref="ModuleException">El módulo falló, murió (<see cref="ModuleExitedException"/>) o se colgó (<see cref="ModuleHungException"/>).</exception>
    public async Task<T> RunAsync<T>(string moduleId, string title, Func<ModuleCall, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ModuleDescriptor module = Find(moduleId) ?? throw new ModuleException("not-installed", $"El módulo {moduleId} no está instalado.");
        ModuleJob job = Jobs.Add(title, module.Name, cancellationToken);
        ModuleConnection connection = _connections.GetOrAdd(module.Id, _ => new ModuleConnection(module, _options, _job, OnClosed));
        connection.BeginCall();
        ModuleCall? call = null;
        ModuleProcess? process = null;
        try
        {
            await ReserveVramAsync(module, job).ConfigureAwait(false);
            job.SetWaiting("Abriendo el módulo");
            process = await connection.EnsureStartedAsync(job.Token).ConfigureAwait(false);
            using IDisposable? lowered = module.UsesGpu ? _preview?.Lower() : null;
            call = new ModuleCall(module, process, job);
            job.SetRunning();
            T result = await work(call).ConfigureAwait(false);
            job.Finish(ModuleJobState.Completed);
            return result;
        }
        catch (OperationCanceledException) when (job.Token.IsCancellationRequested)
        {
            job.Finish(ModuleJobState.Cancelled);
            throw;
        }
        catch (ModuleException ex)
        {
            ModuleException error = await Explain(ex, connection, process, module).ConfigureAwait(false);
            string message = Friendly(module, error);
            job.Finish(ModuleJobState.Failed, message);
            Post(() => Failed?.Invoke(this, new ModuleFailure(module, title, message, error)));
            if (ReferenceEquals(error, ex))
            {
                throw;
            }

            throw error;
        }
        catch (Exception ex)
        {
            job.Finish(ModuleJobState.Failed, "No se pudo terminar: " + ex.Message);
            throw;
        }
        finally
        {
            call?.ReleaseRegions();
            connection.EndCall();
            if (!connection.IsAlive)
            {
                Budget.Release(module.Id);
            }
        }
    }

    /// <summary>Cierra todos los módulos (pidiendo <c>shutdown</c>, y matando si no salen) y el Job Object.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (ModuleJob job in Jobs.Jobs.Where(j => !j.IsFinished))
        {
            job.Cancel();
        }

        await Task.WhenAll(_connections.Values.Select(c => c.CloseAsync())).ConfigureAwait(false);
        foreach (ModuleConnection connection in _connections.Values)
        {
            connection.Dispose();
        }

        _job.Dispose();
    }

    /// <summary>Texto para la persona, sin trazas. Siempre aclara que el proyecto no cambió.</summary>
    public static string Friendly(ModuleDescriptor module, ModuleException error)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(error);
        const string Intact = " El proyecto no cambió.";
        return error switch
        {
            ModuleHungException => $"«{module.Name}» dejó de responder y se cerró." + Intact,
            ModuleExitedException { ExitCode: { } code } => $"«{module.Name}» se cerró de golpe (código {code})." + Intact,
            ModuleVersionException => $"«{module.Name}» necesita otra versión de Rodaje." + Intact,
            ModuleProtocolException => $"«{module.Name}» respondió algo inválido y se cerró." + Intact,
            ModuleDisconnectedException => $"«{module.Name}» se cerró." + Intact,
            { Code: ErrorCodes.OutOfMemory } => $"«{module.Name}» se quedó sin memoria. Cierra otros programas o prueba con un tramo más corto." + Intact,
            { Code: "not-installed" } => error.Message,
            _ => $"«{module.Name}» no pudo terminar: {error.Message}" + Intact,
        };
    }

    /// <summary>Colgado si lo mató el ping; si la tubería se cortó antes de que se supiera la salida, espera el código.</summary>
    private static async Task<ModuleException> Explain(ModuleException ex, ModuleConnection connection, ModuleProcess? process, ModuleDescriptor module)
    {
        if (ex is not ModuleDisconnectedException || ex is ModuleExitedException)
        {
            return connection.WasHung && ex is ModuleDisconnectedException ? new ModuleHungException($"«{module.Name}» dejó de responder y se cerró.", ex) : ex;
        }

        if (process is not null && await Task.WhenAny(process.Exited, Task.Delay(ExitCodeWait)).ConfigureAwait(false) == process.Exited)
        {
            return connection.WasHung
                ? new ModuleHungException($"«{module.Name}» dejó de responder y se cerró.", ex)
                : new ModuleExitedException(process.Exited.Result, null);
        }

        return connection.WasHung ? new ModuleHungException($"«{module.Name}» dejó de responder y se cerró.", ex) : ex;
    }

    private async Task ReserveVramAsync(ModuleDescriptor module, ModuleJob job)
    {
        while (!Budget.TryReserve(module.Id, module.VramMb))
        {
            job.SetWaiting("Esperando memoria de video");
            Task changed = Budget.Changed;
            bool freed = false;
            foreach (string id in Budget.Blocking(module.VramMb))
            {
                if (_connections.TryGetValue(id, out ModuleConnection? other) && other.ActiveCalls == 0)
                {
                    freed |= await other.CloseIfIdleAsync().ConfigureAwait(false);
                }
            }

            if (!freed)
            {
                // Se libera al cerrarse un módulo; uno que termina su llamada queda ocioso sin avisar, así que se reintenta.
                await Task.WhenAny(changed, Task.Delay(EvictionRetry, job.Token)).ConfigureAwait(false);
                job.Token.ThrowIfCancellationRequested();
            }
        }
    }

    private void OnClosed(ModuleConnection connection) => Budget.Release(connection.Descriptor.Id);

    private void Post(Action action)
    {
        if (_context is null)
        {
            action();
        }
        else
        {
            _context.Post(_ => action(), null);
        }
    }
}
