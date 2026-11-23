using System.Diagnostics;
using Rodaje.App.Services;

namespace Rodaje.App.Tests.Session;

/// <summary>
/// Un recurso de red caído deja <c>File.Exists</c> colgado decenas de segundos: <see cref="DiskProbe"/> lo pregunta fuera del
/// hilo de la interfaz y sigue con la respuesta de respaldo al llegar al tope.
/// </summary>
public class DiskProbeTests
{
    [Fact]
    public async Task Una_pregunta_que_no_vuelve_da_el_respaldo_al_tope()
    {
        using var hung = new ManualResetEventSlim(false);
        var watch = Stopwatch.StartNew();

        bool exists = await DiskProbe.WithinAsync(() => hung.Wait(TimeSpan.FromSeconds(30)), fallback: false, TimeSpan.FromMilliseconds(200));

        Assert.False(exists);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"{watch.ElapsedMilliseconds} ms");
        hung.Set();
    }

    [Fact]
    public async Task Una_pregunta_rapida_da_su_respuesta() =>
        Assert.True(await DiskProbe.WithinAsync(() => true, fallback: false));
}
