using Rodaje.App.Services.Media;
using Rodaje.Core.Timing;
using Rodaje.Media.Proxies;

namespace Rodaje.App.Tests.Leaks;

/// <summary>
/// <see cref="AutoProxyService"/> se une al resolvedor del reproductor en <see cref="AutoProxyService.Tick"/>. Si el
/// resolvedor cambia, el anterior quedaba suscrito: seguía avisando estados viejos y retenía el servicio.
/// </summary>
public sealed class AutoProxyLeakTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("rodaje-autoproxy-fuga-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Changing_the_resolver_unsubscribes_from_the_previous_one()
    {
        MediaSourceResolver? current = null;
        var service = new AutoProxyService(() => current, () => 8L << 30, () => true, null);
        int seen = 0;
        service.StatusChanged += (_, _) => seen++;

        using var first = new MediaSourceResolver(new ProxyStore(Path.Combine(_folder, "uno")), generator: null);
        using var second = new MediaSourceResolver(new ProxyStore(Path.Combine(_folder, "dos")), generator: null);
        current = first;
        service.Tick();
        service.Tick();
        Assert.Equal(Raised(first, "a.mp4"), Delta(ref seen));

        current = second;
        service.Tick();
        Raised(first, "viejo.mp4");
        Assert.Equal(0, Delta(ref seen));
        Assert.Equal(Raised(second, "nuevo.mp4"), Delta(ref seen));

        // Volver al primero lo une otra vez, una sola.
        current = first;
        service.Tick();
        Assert.Equal(Raised(first, "otra.mp4"), Delta(ref seen));
        Raised(second, "otra.mp4");
        Assert.Equal(0, Delta(ref seen));
    }

    [Fact]
    public void Switching_resolvers_a_hundred_times_does_not_retain_the_service()
    {
        using var kept = new MediaSourceResolver(new ProxyStore(Path.Combine(_folder, "fijo")), generator: null);
        WeakReference service = AttachAndSwitch(kept);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Sin el arreglo, el resolvedor que ya no se usa conserva su manejador y, con él, el servicio.
        Assert.False(service.IsAlive);
    }

    [Fact]
    public void Leaving_the_resolver_for_null_keeps_it_attached_once()
    {
        // ProxyUsage.Never devuelve null un rato: no es otro resolvedor, así que no se vuelve a unir.
        using var resolver = new MediaSourceResolver(new ProxyStore(Path.Combine(_folder, "nulo")), generator: null);
        MediaSourceResolver? current = resolver;
        var service = new AutoProxyService(() => current, () => 8L << 30, () => true, null);
        int seen = 0;
        service.StatusChanged += (_, _) => seen++;

        service.Tick();
        current = null;
        service.Tick();
        current = resolver;
        service.Tick();

        Assert.Equal(Raised(resolver, "a.mp4"), seen);
    }

    // Cuántos avisos da el resolvedor al evaluar un archivo, contados con una suscripción propia.
    private int Raised(MediaSourceResolver resolver, string name)
    {
        int count = 0;
        EventHandler<ProxyStatusChangedEventArgs> handler = (_, _) => count++;
        resolver.StatusChanged += handler;
        resolver.Assess(Path.Combine(_folder, name), Candidate());
        resolver.StatusChanged -= handler;
        Assert.True(count > 0);
        return count;
    }

    private static int Delta(ref int seen)
    {
        int value = seen;
        seen = 0;
        return value;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private WeakReference AttachAndSwitch(MediaSourceResolver kept)
    {
        MediaSourceResolver? current = kept;
        var service = new AutoProxyService(() => current, () => 8L << 30, () => true, null);
        for (int i = 0; i < 100; i++)
        {
            using var other = new MediaSourceResolver(new ProxyStore(Path.Combine(_folder, $"r{i}")), generator: null);
            current = other;
            service.Tick();
            current = kept;
            service.Tick();
        }

        // Al final se cambia a otro resolvedor que se suelta enseguida: el que queda vivo es el primero.
        using (var last = new MediaSourceResolver(new ProxyStore(Path.Combine(_folder, "ultimo")), generator: null))
        {
            current = last;
            service.Tick();
        }

        return new WeakReference(service);
    }

    private static ProxyCandidate Candidate() => new(3840, 2160, "h264", 8, Rational.Fps30);
}
