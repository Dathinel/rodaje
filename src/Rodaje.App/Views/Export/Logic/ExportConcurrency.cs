using Rodaje.Export;

namespace Rodaje.App.Views.Export;

/// <summary>
/// Cuántas exportaciones a la vez: una, o dos cuando sobra equipo (al menos 8 hilos de CPU y 6 GB de VRAM) y ninguna
/// de las dos pide alto rendimiento, que ya ocupa todo lo que hay.
/// </summary>
/// <param name="LogicalCores">Hilos de la CPU.</param>
/// <param name="DedicatedVideoMemory">VRAM de la GPU; 0 si no se sabe (entonces una sola).</param>
public sealed record ExportConcurrency(int LogicalCores, Func<long> DedicatedVideoMemory)
{
    public const int MinCoresForTwo = 8;

    public const long MinVramForTwo = 6L << 30;

    /// <summary>Siempre una a la vez (las pruebas y los equipos sin datos).</summary>
    public static ExportConcurrency OneAtATime { get; } = new(1, () => 0);

    public bool Allows(IReadOnlyList<ExportSettings> running, ExportSettings next)
    {
        ArgumentNullException.ThrowIfNull(running);
        ArgumentNullException.ThrowIfNull(next);
        return running.Count switch
        {
            0 => true,
            1 => LogicalCores >= MinCoresForTwo && DedicatedVideoMemory() >= MinVramForTwo
                && running[0].Performance != ExportPerformance.Fastest && next.Performance != ExportPerformance.Fastest,
            _ => false,
        };
    }
}
