using System.Diagnostics;

namespace Rodaje.Engine.Tests.Support;

/// <summary>
/// Marca el ritmo de un bucle de refrescos con el reloj de pared, como un vsync real: <see cref="Next"/> espera al
/// siguiente refresco y, si el equipo se atrasó, salta los que ya pasaron en lugar de correrlos seguidos. Así una pausa
/// del sistema no se convierte en una ráfaga de refrescos sin tiempo para decodificar.
/// </summary>
internal sealed class WallClockPacer(int refreshesPerSecond)
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private long _slot;

    /// <summary>Refrescos que se saltaron porque el bucle llegó tarde.</summary>
    public long Skipped { get; private set; }

    public TimeSpan Elapsed => _watch.Elapsed;

    /// <summary>Espera al siguiente refresco y devuelve su número, contando desde 1.</summary>
    public long Next()
    {
        long passed = (long)Math.Floor(_watch.Elapsed.TotalSeconds * refreshesPerSecond);
        long next = Math.Max(_slot, passed) + 1;
        Skipped += next - (_slot + 1);
        _slot = next;
        TimeSpan due = TimeSpan.FromTicks(next * TimeSpan.TicksPerSecond / refreshesPerSecond);
        while (_watch.Elapsed < due)
        {
            Thread.Sleep(1);
        }

        return next;
    }
}
