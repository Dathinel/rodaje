using Rodaje.App.Tests.Support;
using Xunit.Sdk;

namespace Rodaje.App.Tests.Library;

/// <summary>
/// Lo que pasó con «Subclips repetidos» en la CI de Windows, reproducido en cualquier sistema: una carga lineal en la que
/// solo la corrida grande pierde tiempo por causas ajenas (una recolección, el hilo sin CPU con el runner lleno). Por reloj
/// (<see cref="Growth"/>) parece cuadrática; <see cref="WorkGrowth"/> cuenta operaciones y bytes, no se deja engañar y
/// sigue atrapando lo cuadrático de verdad.
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

    [ThreadStatic]
    private static long _operations;

    private static long _sink;

    private static long Operations() => _operations;

    private static void Linear(int count)
    {
        long sum = 0;
        for (int i = 0; i < count * 50; i++)
        {
            sum += i ^ (sum >> 3);
        }

        _operations += count;
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

    /// <summary>
    /// Lineal, pero la corrida grande se queda sin CPU un rato, como en el runner de Windows lleno: el tiempo sin pausas del
    /// recolector (lo que medía <see cref="WorkGrowth"/> hasta la tarea 177) lo cuenta entero.
    /// </summary>
    private static void LinearThatLosesTheCpuWhenLarge(int count)
    {
        Linear(count);
        if (count > 1_000)
        {
            Thread.Sleep(50);
        }
    }

    [Fact]
    public void ACollectionThatOnlyTheLargeRunPays_FoolsTheClockButNotWorkGrowth()
    {
        // Un montón vivo grande, como el de un proceso con 1 700 pruebas: la recolección completa lo recorre entero.
        object[] ballast = [.. Enumerable.Range(0, 1_500_000).Select(i => new int[i % 4 + 1])];
        Assert.ThrowsAny<XunitException>(() => Growth.AssertLinear(LinearWithCollectionWhenLarge, 1_000, 8_000, "Reloj"));
        WorkGrowth.AssertLinear(static n => n, LinearWithCollectionWhenLarge, Operations, 1_000, 8_000, "Contado");
        GC.KeepAlive(ballast);
    }

    [Fact]
    public void ALargeRunWithoutCpu_FoolsTheClockButNotWorkGrowth()
    {
        Assert.ThrowsAny<XunitException>(() => Growth.AssertLinear(LinearThatLosesTheCpuWhenLarge, 1_000, 8_000, "Reloj"));
        WorkGrowth.AssertLinear(static n => n, LinearThatLosesTheCpuWhenLarge, Operations, 1_000, 8_000, "Contado");
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
        }, Operations, 500, 4_000, "Cuadrático"));
        Assert.Contains("Cuadrático", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void QuadraticAllocationsFailEvenWithLinearOperations()
    {
        Assert.ThrowsAny<XunitException>(() => WorkGrowth.AssertLinear(static n => n, static count =>
        {
            for (int i = 0; i < count; i++)
            {
                _operations++;
                _sink += new byte[count].Length;
            }
        }, Operations, 200, 1_600, "Reservas"));
    }

    [Fact]
    public void AnUnconnectedCounterFails()
    {
        var error = Assert.ThrowsAny<XunitException>(() =>
            WorkGrowth.AssertLinear(static n => n, static _ => { }, static () => 0, 100, 800, "Sin costura"));
        Assert.Contains("no contó ninguna operación", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MeasureCountsOperationsAndBytes()
    {
        (long operations, long bytes) = WorkGrowth.Measure(static count =>
        {
            _operations += count;
            _sink += new byte[100_000].Length;
        }, 7, Operations);

        Assert.Equal(7, operations);
        Assert.InRange(bytes, 100_000, 200_000);
    }
}
