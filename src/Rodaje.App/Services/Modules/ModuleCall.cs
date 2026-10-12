using System.Collections.Concurrent;
using System.Text.Json;
using Rodaje.Modules.Protocol;

namespace Rodaje.App.Services.Modules;

/// <summary>
/// Lo que recibe el trabajo de <see cref="ModuleManager.RunAsync{T}"/>: el módulo ya abierto, regiones de memoria
/// compartida (se liberan solas al terminar), llamadas con el avance llevado a la fila de Trabajos y la cancelación de esa
/// fila. Varias llamadas seguidas (por ejemplo, lotes de cuadros) cuentan como un solo trabajo.
/// </summary>
public sealed class ModuleCall
{
    private readonly ModuleProcess _process;
    private readonly ConcurrentBag<SharedMemoryRegion> _regions = [];

    internal ModuleCall(ModuleDescriptor module, ModuleProcess process, ModuleJob job)
    {
        Module = module;
        _process = process;
        Job = job;
    }

    public ModuleDescriptor Module { get; }

    /// <summary>Lo que el módulo dijo de sí mismo al saludar (funciones, entradas y salidas, dispositivo).</summary>
    public ModuleInfo Info => _process.Client.Module;

    public ModuleJob Job { get; }

    /// <summary>Se cancela con el botón de la fila o con el token de quien lanzó el trabajo.</summary>
    public CancellationToken CancellationToken => Job.Token;

    /// <summary>Una región que crea el editor (el módulo solo escribe en los búferes que se le pasan).</summary>
    public SharedMemoryRegion CreateRegion(long capacity)
    {
        SharedMemoryRegion region = _process.Client.CreateRegion(capacity);
        _regions.Add(region);
        return region;
    }

    /// <summary>Avance propio de la app (por ejemplo «Leyendo el audio»), de 0 a 1 sobre el trabajo entero.</summary>
    public void Report(double fraction, string? stage = null) => Job.Report(fraction, stage);

    /// <summary>
    /// Llama a <paramref name="function"/>; su avance 0–1 se lleva al tramo <paramref name="progressFrom"/>–<paramref name="progressTo"/> del trabajo.
    /// </summary>
    /// <exception cref="ModuleCallException">La función falló; el módulo sigue abierto.</exception>
    /// <exception cref="ModuleDisconnectedException">El módulo murió o se colgó (en ese caso, <see cref="ModuleHungException"/>).</exception>
    public Task<ModuleResult> InvokeAsync(string function, JsonElement? parameters = null, IReadOnlyList<SharedBufferRef>? buffers = null,
        double progressFrom = 0, double progressTo = 1)
    {
        ArgumentException.ThrowIfNullOrEmpty(function);
        return _process.Client.InvokeAsync(function, parameters, buffers, new RangeProgress(Job, progressFrom, progressTo), Job.Token);
    }

    internal void ReleaseRegions()
    {
        while (_regions.TryTake(out SharedMemoryRegion? region))
        {
            _process.Client.ReleaseRegion(region);
        }
    }

    private sealed class RangeProgress(ModuleJob job, double from, double to) : IProgress<ModuleProgress>
    {
        public void Report(ModuleProgress value) => job.Report(from + (Math.Clamp(value.Fraction, 0, 1) * (to - from)), value.Stage);
    }
}

/// <summary>El módulo dejó de responder al <c>ping</c> y se mató. El proyecto no cambió.</summary>
public sealed class ModuleHungException : ModuleDisconnectedException
{
    public ModuleHungException() : base("El módulo dejó de responder.") { }

    public ModuleHungException(string message) : base(message) { }

    public ModuleHungException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Baja la vista previa (proxies y Mitad) mientras corre un módulo de GPU; se deshace al soltar lo devuelto.</summary>
public interface IModulePreview
{
    IDisposable Lower();
}

/// <summary>Un trabajo de módulo que falló, con el texto para la persona.</summary>
public sealed record ModuleFailure(ModuleDescriptor Module, string Title, string Message, Exception Error);

public sealed record ModuleManagerOptions
{
    public IReadOnlyList<ModuleRoot> Roots { get; init; } = [];

    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>VRAM dedicada (para el presupuesto); 0 si no se sabe.</summary>
    public long DedicatedVideoMemory { get; init; }

    public ModuleClientOptions ClientOptions { get; init; } = new();
}
