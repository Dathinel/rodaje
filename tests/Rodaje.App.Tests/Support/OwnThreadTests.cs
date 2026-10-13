namespace Rodaje.App.Tests.Support;

/// <summary>
/// <see cref="OwnThread"/> saca del pool las pruebas que esperan en bloque (sombreadores, títulos, procesos): el cuerpo
/// corre en un hilo que no es del pool y lo que lanza llega a la prueba.
/// </summary>
public sealed class OwnThreadTests
{
    [Fact]
    public async Task El_cuerpo_corre_fuera_del_pool_y_puede_esperar_en_bloque()
    {
        bool? onPool = null;
        using var gate = new ManualResetEventSlim();
        Task run = OwnThread.Run(() =>
        {
            onPool = Thread.CurrentThread.IsThreadPoolThread;
            Assert.True(gate.Wait(TimeSpan.FromSeconds(30)));
        });

        Assert.False(run.IsCompleted);
        gate.Set();
        await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(onPool);
    }

    [Fact]
    public async Task Lo_que_lanza_el_cuerpo_llega_a_la_prueba()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => OwnThread.Run(() => throw new InvalidOperationException("roto")));
        Assert.Equal("roto", error.Message);
    }
}
