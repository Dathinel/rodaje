using Rodaje.App.Views.Analysis;

namespace Rodaje.App.Tests.Leaks;

/// <summary>
/// «Reducir ruido» carga su escucha A/B después de limpiar la voz. Si el diálogo se cerraba mientras se cargaba, la vista
/// previa llegaba tarde, después del <c>Dispose</c> del cierre, y quedaba sin soltar. <see cref="DialogOwned{T}"/> la
/// suelta al llegar.
/// </summary>
public sealed class NoisePreviewLeakTests
{
    [Fact]
    public async Task A_preview_that_arrives_after_closing_is_disposed()
    {
        var owned = new DialogOwned<Probe>();
        var loading = new TaskCompletionSource<Probe>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> prepare = PrepareAsync(owned, loading.Task);

        // Se cierra el diálogo antes de que termine de cargar, como al pulsar Cancelar.
        owned.Close();
        var late = new Probe();
        loading.SetResult(late);

        Assert.False(await prepare);
        Assert.True(late.Disposed);
        Assert.Null(owned.Value);
    }

    [Fact]
    public void Closing_disposes_the_loaded_preview_once()
    {
        var owned = new DialogOwned<Probe>();
        var first = new Probe();
        var second = new Probe();

        Assert.True(owned.TrySet(first));
        Assert.True(owned.TrySet(second));
        Assert.True(first.Disposed);
        Assert.Same(second, owned.Value);

        owned.Close();
        owned.Close();
        Assert.Equal(1, second.DisposeCount);
        Assert.True(owned.IsClosed);
    }

    [Fact]
    public void Setting_the_same_preview_twice_does_not_dispose_it()
    {
        var owned = new DialogOwned<Probe>();
        var probe = new Probe();

        Assert.True(owned.TrySet(probe));
        Assert.True(owned.TrySet(probe));
        Assert.False(probe.Disposed);
    }

    // Como Prepare en NoiseReductionDialog: espera la carga y la entrega al dueño.
    private static async Task<bool> PrepareAsync(DialogOwned<Probe> owned, Task<Probe> loading) => owned.TrySet(await loading);

    private sealed class Probe : IDisposable
    {
        public int DisposeCount { get; private set; }

        public bool Disposed => DisposeCount > 0;

        public void Dispose() => DisposeCount++;
    }
}
