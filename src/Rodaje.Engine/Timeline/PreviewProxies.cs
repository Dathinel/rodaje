using Rodaje.Media.Proxies;

namespace Rodaje.Engine.Timeline;

/// <summary>
/// Proxies para la vista previa, listos para <see cref="TimelineSessionOptions.ProxyResolver"/>: el almacén de siempre y,
/// si está el ffmpeg.exe LGPL, el generador, que hace en segundo plano el proxy de cada medio que la política recomienda.
/// </summary>
public sealed class PreviewProxies : IDisposable
{
    private readonly ProxyGenerator? _generator;

    private PreviewProxies(MediaSourceResolver resolver, ProxyGenerator? generator)
    {
        Resolver = resolver;
        _generator = generator;
    }

    public MediaSourceResolver Resolver { get; }

    /// <summary>Si se pueden generar proxies nuevos; si no, <see cref="UnavailableReason"/> dice por qué y solo se usan los ya hechos.</summary>
    public bool CanGenerate => _generator is not null;

    public string? UnavailableReason { get; private init; }

    /// <param name="storeDirectory">Carpeta del almacén; nula para la de siempre (<see cref="ProxyStore.DefaultDirectory"/>).</param>
    /// <param name="generate">Generar el proxy de cada medio que la política recomienda en cuanto se abre.</param>
    public static PreviewProxies Create(string? storeDirectory = null, bool generate = true)
    {
        var store = new ProxyStore(storeDirectory);
        FfmpegExecutable? ffmpeg = FfmpegExecutable.TryLocate(out string? reason);
        ProxyGenerator? generator = ffmpeg is null ? null : new ProxyGenerator(store, ffmpeg);
        var resolver = new MediaSourceResolver(store, generator, unavailableReason: reason)
        {
            GenerateWhenRecommended = generate && generator is not null,
        };
        return new PreviewProxies(resolver, generator) { UnavailableReason = reason };
    }

    public void Dispose()
    {
        Resolver.Dispose();
        _generator?.Dispose();
    }
}
