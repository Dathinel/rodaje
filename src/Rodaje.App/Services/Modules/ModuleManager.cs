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

    /// <summary>
    /// Lo que debe esperar a <see cref="DisposeAsync"/> quien lo espere con un tope (la ventana al cerrarse): los módulos
    /// se cierran a la vez, y cada uno tarda a lo sumo <see cref="ModuleProcess.DisposeBudget"/> si no sale tras
    /// <c>shutdown</c>, más <see cref="ModuleProcess.DisposeMargin"/>. Con menos, la ventana seguía con el módulo colgado
    /// todavía vivo y solo lo cubría el Job Object (tarea 197).
    /// </summary>
    public TimeSpan CloseTimeout => ModuleProcess.DisposeBudget(_options.ClientOptions) + ModuleProcess.DisposeMargin;

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

            VramBudget created = _options.Ai?.Plan() is { } plan
                ? new VramBudget(plan.GpuBudgetMb, plan.MaxHeavyAtOnce)
                : new VramBudget(_options.DedicatedVideoMemory());
            Interlocked.CompareExchange(ref _budget, created, null);
            return _budget;
        }
    }

    private SemaphoreSlim? _cpuSlots;

    /// <summary>Trabajos de IA por CPU a la vez (<see cref="Rodaje.Media.Ai.AiPlan.CpuSlots"/>; sin perfil, sin límite).</summary>
    private SemaphoreSlim? CpuSlots
    {
        get
        {
            if (_options.Ai is null)
            {
                return null;
            }

            if (Volatile.Read(ref _cpuSlots) is { } slots)
            {
                return slots;
            }

            int count = _options.Ai.Plan().CpuSlots;
            var created = new SemaphoreSlim(count, count);
            if (Interlocked.CompareExchange(ref _cpuSlots, created, null) is not null)
            {
                created.Dispose();
            }

            return _cpuSlots;
        }
    }

    /// <summary>El perfil de IA del equipo (plan por módulo, estimaciones), o <c>null</c> en las pruebas que no lo usan.</summary>
    public AiProfile? Ai => _options.Ai;

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
    public Task<T> RunAsync<T>(string moduleId, string title, Func<ModuleCall, Task<T>> work, CancellationToken cancellationToken = default) =>
        RunAsync(moduleId, title, null, work, cancellationToken);

    /// <summary>
    /// Como <see cref="RunAsync{T}(string, string, Func{ModuleCall, Task{T}}, CancellationToken)"/>, con lo que pide este
    /// trabajo según el perfil de IA (<paramref name="demand"/>; sin él, el <c>vramMb</c> del módulo): la VRAM que se reserva
    /// y si espera un hueco de CPU.
    /// </summary>
    public async Task<T> RunAsync<T>(string moduleId, string title, ModuleDemand? demand, Func<ModuleCall, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ModuleDescriptor module = Find(moduleId) ?? throw new ModuleException("not-installed", $"El módulo {moduleId} no está instalado.");
        ModuleDemand need = demand ?? ModuleDemand.For(module);
        ModuleJob job = Jobs.Add(title, module.Name, cancellationToken);
        ModuleConnection connection = _connections.GetOrAdd(module.Id, _ => new ModuleConnection(module, _options, _job, OnClosed));
        connection.BeginCall();
        ModuleCall? call = null;
        ModuleProcess? process = null;
        SemaphoreSlim? cpu = need.UsesCpu ? CpuSlots : null;
        bool cpuTaken = false;
        try
        {
            await ReserveVramAsync(module, need.VramMb, job).ConfigureAwait(false);
            if (cpu is not null && !cpu.Wait(0, CancellationToken.None))
            {
                job.SetWaiting("Esperando la CPU");
                await cpu.WaitAsync(job.Token).ConfigureAwait(false);
            }

            cpuTaken = cpu is not null;
            job.SetWaiting("Abriendo el módulo");
            process = await connection.EnsureStartedAsync(job.Token).ConfigureAwait(false);
            using IDisposable? lowered = need.VramMb > 0 ? _preview?.Lower() : null;
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
            if (cpuTaken)
            {
                cpu!.Release();
            }

            call?.ReleaseRegions();
            // La reserva es de las llamadas en curso y, después, del proceso abierto: la suelta la última llamada si no
            // quedó proceso, o OnClosed al cerrarse sin llamadas.
            connection.EndCall();
            if (connection.ActiveCalls == 0 && !connection.IsAlive)
            {
                Budget.Release(module.Id);
            }
        }
    }

    /// <summary>
    /// Cierra el proceso de un módulo aunque esté trabajando (sus trabajos se cancelan), para desinstalarlo o reemplazar
    /// sus archivos. La próxima llamada lo vuelve a abrir desde donde esté entonces.
    /// </summary>
    public async Task CloseModuleAsync(string moduleId)
    {
        ArgumentNullException.ThrowIfNull(moduleId);
        if (!_connections.TryRemove(moduleId, out ModuleConnection? connection))
        {
            return;
        }

        foreach (ModuleJob job in Jobs.Jobs.Where(j => !j.IsFinished && j.ModuleName == connection.Descriptor.Name))
        {
            job.Cancel();
        }

        await connection.CloseAsync().ConfigureAwait(false);
        Budget.Release(moduleId);
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
        _cpuSlots?.Dispose();
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
            return connection.WasHung(process) && ex is ModuleDisconnectedException ? new ModuleHungException($"«{module.Name}» dejó de responder y se cerró.", ex) : ex;
        }

        if (process is not null && await Task.WhenAny(process.Exited, Task.Delay(ExitCodeWait)).ConfigureAwait(false) == process.Exited)
        {
            return connection.WasHung(process)
                ? new ModuleHungException($"«{module.Name}» dejó de responder y se cerró.", ex)
                : new ModuleExitedException(process.Exited.Result, null);
        }

        return connection.WasHung(process) ? new ModuleHungException($"«{module.Name}» dejó de responder y se cerró.", ex) : ex;
    }

    private async Task ReserveVramAsync(ModuleDescriptor module, int vramMb, ModuleJob job)
    {
        while (!Budget.TryReserve(module.Id, vramMb))
        {
            job.SetWaiting("Esperando memoria de video");
            Task changed = Budget.Changed;
            bool freed = false;
            foreach (string id in Budget.Blocking(vramMb))
            {
                if (id != module.Id && _connections.TryGetValue(id, out ModuleConnection? other) && other.ActiveCalls == 0)
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

    /// <summary>
    /// Se cerró o se retiró el proceso. Con llamadas en curso la reserva no se toca: es de ellas (la llamada que retira un
    /// proceso muerto en EnsureStartedAsync ya reservó para el que abre), y la suelta la última al terminar si no quedó proceso.
    /// Tampoco si ya vive otro: el aviso de un proceso retirado llega cuando terminó de morir, y para entonces la llamada que
    /// lo reemplazó pudo terminar dejando abierto el nuevo, que es dueño de la reserva.
    /// </summary>
    private void OnClosed(ModuleConnection connection)
    {
        if (connection.ActiveCalls == 0 && !connection.IsAlive)
        {
            Budget.Release(connection.Descriptor.Id);
        }
    }

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
