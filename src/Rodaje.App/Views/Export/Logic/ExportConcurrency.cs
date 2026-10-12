using Rodaje.Export;
using Rodaje.Export.Tuning;
using Rodaje.Media.Gpu;

namespace Rodaje.App.Views.Export;

/// <summary>
/// Cuántas exportaciones a la vez: una, o dos cuando sobra equipo (al menos 8 hilos de CPU y 6 GB de VRAM) o cuando una
/// codifica en la GPU y la otra en la CPU (al menos 6 hilos y 3 GB de VRAM: cada una usa un recurso distinto), y ninguna
/// de las dos pide alto rendimiento, que ya ocupa todo lo que hay.
/// </summary>
/// <param name="LogicalCores">Hilos de la CPU.</param>
/// <param name="DedicatedVideoMemory">VRAM de la GPU; 0 si no se sabe (entonces una sola).</param>
public sealed record ExportConcurrency(int LogicalCores, Func<long> DedicatedVideoMemory)
{
    public const int MinCoresForTwo = 8;

    public const long MinVramForTwo = 6L << 30;

    public const int MinCoresForMixed = 6;

    public const long MinVramForMixed = 3L << 30;

    /// <summary>Siempre una a la vez (las pruebas y los equipos sin datos).</summary>
    public static ExportConcurrency OneAtATime { get; } = new(1, () => 0);

    /// <summary>Perfil del equipo (se lee al encolar, no al arrancar) para saber qué exportación codifica en la GPU; sin él no se combinan.</summary>
    public Func<GpuProfile>? Profile { get; init; }

    public bool Allows(IReadOnlyList<ExportSettings> running, ExportSettings next)
    {
        ArgumentNullException.ThrowIfNull(running);
        ArgumentNullException.ThrowIfNull(next);
        if (running.Count == 0)
        {
            return true;
        }

        if (running.Count > 1 || running[0].Performance == ExportPerformance.Fastest || next.Performance == ExportPerformance.Fastest)
        {
            return false;
        }

        long vram = DedicatedVideoMemory();
        bool spare = LogicalCores >= MinCoresForTwo && vram >= MinVramForTwo;
        bool mixed = Profile?.Invoke() is { } profile && LogicalCores >= MinCoresForMixed && vram >= MinVramForMixed
            && ExportHardwareTuning.UsesGpuEncoder(running[0], profile) != ExportHardwareTuning.UsesGpuEncoder(next, profile);
        return spare || mixed;
    }
}
