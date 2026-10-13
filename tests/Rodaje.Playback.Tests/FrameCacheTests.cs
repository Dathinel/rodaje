using Rodaje.Core.Timing;
using Rodaje.Playback.Tests.Support;

namespace Rodaje.Playback.Tests;

public class FrameCacheTests
{
    private readonly PayloadTracker _tracker = new();

    private DecodedFrame Make(long index, long size = 10) => Frames.Make(index, Rational.Fps25, _tracker, size);

    [Fact]
    public void Constructor_ValidaPresupuesto()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameCache(0));
        using var cache = new FrameCache(5);
        Assert.Equal(5, cache.BudgetBytes);
        Assert.Throws<ArgumentNullException>(() => cache.Add(null!));
    }

    [Fact]
    public void Add_ExpulsaElMenosUsadoAlPasarElPresupuesto()
    {
        using var cache = new FrameCache(30);
        cache.Add(Make(1));
        cache.Add(Make(2));
        cache.Add(Make(3));
        Assert.True(cache.TryGet(1, out DecodedFrame? touched));
        touched.Dispose();

        cache.Add(Make(4));

        Assert.Equal(3, cache.Count);
        Assert.Equal(30, cache.CostBytes);
        Assert.False(cache.Contains(2));
        Assert.True(cache.Contains(1));
        Assert.True(cache.Contains(3));
        Assert.True(cache.Contains(4));
        Assert.Equal(3, _tracker.Live);
    }

    [Fact]
    public void Add_ConservaUnCuadroQueSuperaElPresupuestoSolo()
    {
        using var cache = new FrameCache(5);
        cache.Add(Make(1, 3));
        cache.Add(Make(2, 50));
        Assert.Equal(1, cache.Count);
        Assert.True(cache.Contains(2));
        Assert.Equal(1, _tracker.Live);
    }

    [Fact]
    public void Add_DuplicadoLiberaElNuevo()
    {
        using var cache = new FrameCache(100);
        Assert.True(cache.Add(Make(7)));
        Assert.False(cache.Add(Make(7)));
        Assert.Equal(1, cache.Count);
        Assert.Equal(1, _tracker.Live);
    }

    [Fact]
    public void CuadroRetenido_SobreviveALaExpulsion()
    {
        using var cache = new FrameCache(10);
        cache.Add(Make(1));
        Assert.True(cache.TryGet(1, out DecodedFrame? held));
        cache.Add(Make(2));
        Assert.False(cache.Contains(1));
        Assert.False(held.IsReleased);
        Assert.Equal(2, _tracker.Live);
        held.Dispose();
        Assert.Equal(1, _tracker.Live);
    }

    [Fact]
    public void TryGetLatest_DevuelveElMayorDelRango()
    {
        using var cache = new FrameCache(1_000);
        foreach (long i in new long[] { 2, 5, 9, 40 })
        {
            cache.Add(Make(i));
        }

        AssertLatest(cache, 0, 8, 5);
        AssertLatest(cache, 5, 5, 5);
        AssertLatest(cache, 0, 1_000_000, 40);
        AssertLatest(cache, 10, 39, null);
        AssertLatest(cache, 6, 8, null);
        AssertLatest(cache, 9, 2, null);
        Assert.False(cache.TryGet(3, out _));
    }

    [Fact]
    public void RemoveYClear_LiberanTodo()
    {
        var cache = new FrameCache(1_000);
        cache.Add(Make(1));
        cache.Add(Make(2));
        Assert.True(cache.Remove(1));
        Assert.False(cache.Remove(1));
        Assert.Equal(1, _tracker.Live);
        cache.Add(Make(3));
        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.CostBytes);
        Assert.Equal(0, _tracker.Live);
        cache.Add(Make(4));
        cache.Dispose();
        Assert.Equal(0, _tracker.Live);
    }

    [Fact]
    public async Task RemoveRange_SacaSoloLoDelRangoYNoRecorreElRangoEntero()
    {
        // Sin «using»: si el defecto vuelve, el hilo que recorre el rango sigue con el candado tomado y Dispose se colgaría
        // esperándolo; la caché se libera a mano al final, solo cuando todo salió bien.
        var cache = new FrameCache(1_000);
        foreach (long i in new long[] { 3, 4, 7, 10, 2_000_000_000 })
        {
            cache.Add(Make(i));
        }

        Assert.Equal(2, cache.RemoveRange(4, 10));
        Assert.True(cache.Contains(3));
        Assert.False(cache.Contains(4));
        Assert.False(cache.Contains(7));
        Assert.True(cache.Contains(10));
        Assert.Equal(0, cache.RemoveRange(10, 10));
        Assert.Equal(0, cache.RemoveRange(11, 5));

        // Recorrer el rango hasta long.MaxValue no terminaría nunca: la llamada va en su propio hilo y la prueba falla a los
        // 10 s en vez de colgarse hasta el límite del trabajo de la CI. La cota es holgada porque en las vueltas lentas de la
        // CI un hilo llegó a quedarse más de 2 s sin CPU. Si salta, el hilo queda girando hasta que termina el proceso.
        Task<int> removing = Task.Factory.StartNew(
            () => cache.RemoveRange(5, long.MaxValue),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Task finished = await Task.WhenAny(removing, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(finished == removing, "Sacar un rango enorme no terminó en 10 s: RemoveRange recorre el rango entero.");
        Assert.Equal(2, await removing);
        Assert.Equal(1, cache.Count);
        Assert.True(cache.Contains(3));
        Assert.Equal(1, _tracker.Live);
        Assert.Equal(10, cache.CostBytes);
        cache.Dispose();
    }

    [Fact]
    public void AccesoConcurrente_RespetaPresupuestoYNoFiltra()
    {
        using (var cache = new FrameCache(200))
        {
            Parallel.For(0, 20_000, i =>
            {
                long index = i % 97;
                if (i % 3 == 0)
                {
                    cache.Add(Make(index));
                }
                else if (cache.TryGetLatest(index - 10, index, out DecodedFrame? frame))
                {
                    Assert.False(frame.IsReleased);
                    frame.Dispose();
                }

                Assert.True(cache.CostBytes <= 200);
            });
        }

        Assert.Equal(0, _tracker.Live);
    }

    private static void AssertLatest(FrameCache cache, long min, long max, long? expected)
    {
        bool found = cache.TryGetLatest(min, max, out DecodedFrame? frame);
        Assert.Equal(expected, frame?.Index);
        Assert.Equal(expected is not null, found);
        frame?.Dispose();
    }
}
