using Rodaje.App.Tests.Support;
using Xunit.Sdk;

namespace Rodaje.App.Tests.Library;

/// <summary>
/// Lo que pasó con «Subclips repetidos» en la CI de Windows, reproducido en cualquier sistema: una carga lineal en la que
/// solo la corrida grande paga una recolección. Por reloj (<see cref="Growth"/>) parece cuadrática; <see cref="WorkGrowth"/>
/// no se deja engañar y sigue atrapando lo cuadrático de verdad.
/// <para>
/// Fuerzan recolecciones completas, que detienen a todo el proceso: van solas para no alargar las pruebas de reloj que
/// corren a la vez.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
[Collection(Name)]
public sealed class WorkGrowthTests
{
    private const string Name = "Recolecciones forzadas";

    private static long _sink;

    private static void Linear(int count)
    {
        long sum = 0;
        for (int i = 0; i < count * 50; i++)
        {
            sum += i ^ (sum >> 3);
        }

        _sink += sum;
    }

    /// <summary>Lineal, pero la corrida grande despierta al recolector, que recorre todo el montón del proceso.</summary>
    private static void LinearWithCollectionWhenLarge(int count)
    {
        Linear(count);
        if (count > 1_000)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
    }

    [Fact]
    public void ACollectionThatOnlyTheLargeRunPays_FoolsTheClockButNotWorkGrowth()
    {
        // Un montón vivo grande, como el de un proceso con 1 700 pruebas: la recolección completa lo recorre entero.
        object[] ballast = [.. Enumerable.Range(0, 1_500_000).Select(i => new int[i % 4 + 1])];
        Assert.ThrowsAny<XunitException>(() => Growth.AssertLinear(LinearWithCollectionWhenLarge, 1_000, 8_000, "Reloj"));
        WorkGrowth.AssertLinear(static n => n, LinearWithCollectionWhenLarge, 1_000, 8_000, "Sin pausas");
        GC.KeepAlive(ballast);
    }

    [Fact]
    public void AQuadraticLoadStillFails()
    {
        var error = Assert.ThrowsAny<XunitException>(() => WorkGrowth.AssertLinear(static n => n, static count =>
        {
            for (int i = 0; i < count; i++)
            {
                Linear(count / 50);
            }
        }, 500, 4_000, "Cuadrático"));
        Assert.Contains("Cuadrático", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void QuadraticAllocationsFailEvenWhenFast()
    {
        Assert.ThrowsAny<XunitException>(() => WorkGrowth.AssertLinear(static n => n, static count =>
        {
            for (int i = 0; i < count; i++)
            {
                _sink += new byte[count].Length;
            }
        }, 200, 1_600, "Reservas"));
    }

    [Fact]
    public void MeasureSeparatesThePauses()
    {
        (long bytes, TimeSpan time) = WorkGrowth.Measure(static _ => GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true), 0, out TimeSpan pauses);

        Assert.True(pauses > TimeSpan.Zero, "La recolección forzada debió contar como pausa.");
        Assert.True(time < pauses, $"El tiempo sin pausas ({time.TotalMilliseconds} ms) debió quedar por debajo de las pausas ({pauses.TotalMilliseconds} ms).");
        Assert.True(bytes < 64 * 1024, $"Recolectar no reserva: {bytes} bytes.");
    }
}
