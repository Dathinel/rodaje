using Rodaje.Media.Probe;
using Rodaje.Media.Proxies;

namespace Rodaje.App.Services.Media;

/// <summary>
/// Proxies automáticos al importar (<see cref="AutoProxyRule"/>): los videos que lo merecen se encolan en el
/// <see cref="MediaSourceResolver"/> del reproductor, así salen en Trabajos y la vista previa los toma al terminar. El
/// reproductor crea su resolvedor en su hilo; hasta entonces lo importado espera y <see cref="Tick"/> lo vuelve a intentar.
/// </summary>
/// <param name="resolver">El resolvedor del reproductor, o nulo si todavía no existe (o la vista previa no usa proxies).</param>
/// <param name="dedicatedVideoMemory">VRAM de la GPU del reproductor; 0 si no se sabe.</param>
/// <param name="enabled">El ajuste «Generar proxies al importar».</param>
/// <param name="context">Hilo de la interfaz para <see cref="StatusChanged"/>; nulo lo avisa en el hilo que llegue.</param>
public sealed class AutoProxyService(Func<MediaSourceResolver?> resolver, Func<long> dedicatedVideoMemory, Func<bool> enabled,
    SynchronizationContext? context)
{
    private readonly List<string> _pending = [];
    private MediaSourceResolver? _attached;

    /// <summary>El estado del proxy de un medio cambió (en cola, porcentaje, listo, fallo), en el hilo de la interfaz.</summary>
    public event EventHandler<ProxyStatus>? StatusChanged;

    /// <summary>Lo que espera a que exista el resolvedor del reproductor.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>Evalúa los medios recién importados; los que merecen proxy se encolan (ahora o en el próximo <see cref="Tick"/>).</summary>
    /// <returns>Cuántos se van a generar.</returns>
    public int OnImported(IEnumerable<MediaInfo> media)
    {
        ArgumentNullException.ThrowIfNull(media);
        int count = 0;
        if (!enabled())
        {
            return 0;
        }

        foreach (MediaInfo info in media)
        {
            if (info.Video is null)
            {
                continue;
            }

            ProxyCandidate candidate = ProxyCandidate.From(info);
            if (AutoProxyRule.Decide(candidate, dedicatedVideoMemory()).Generate)
            {
                _pending.Add(info.Path);
                count++;
            }
        }

        Tick();
        return count;
    }

    /// <summary>Se une al resolvedor si ya existe y le pasa lo que espera. Desde el hilo de la interfaz.</summary>
    public void Tick()
    {
        if (resolver() is not { } current)
        {
            return;
        }

        if (!ReferenceEquals(current, _attached))
        {
            _attached = current;
            current.StatusChanged += (_, e) => Post(e.Status);
        }

        if (_pending.Count == 0)
        {
            return;
        }

        string[] batch = [.. _pending];
        _pending.Clear();
        if (!current.CanGenerate)
        {
            return;
        }

        foreach (string path in batch)
        {
            // Los fallos quedan en el estado del proxy (Trabajos y la portada): aquí solo se observa la tarea.
            _ = current.RequestProxyAsync(path).ContinueWith(
                static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }

    private void Post(ProxyStatus status)
    {
        if (context is null)
        {
            StatusChanged?.Invoke(this, status);
        }
        else
        {
            context.Post(_ => StatusChanged?.Invoke(this, status), null);
        }
    }
}
