using Rodaje.Core.Timing;
using Rodaje.Media.Probe;
using Rodaje.Media.Proxies;
using Rodaje.Playback;
using Rodaje.Playback.Composition;

namespace Rodaje.Engine.Timeline;

// Proxies: qué variante de cada medio se decodifica y cuándo se cambia.
internal sealed partial class TimelineScheduler
{
    private void OnProxyStatusChanged(object? sender, ProxyStatusChangedEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            bool ready = e.Status.State == ProxyState.Ready;
            foreach (MediaState media in _media.Values)
            {
                if (media.Variant is { } variant && variant.IsProxy != ready
                    && string.Equals(media.Key.Path, e.Status.SourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    media.ResolvePending = true;
                    _demandDirty = true;
                }
            }
        }
    }

    /// <summary>En el ejecutor de servicio: vuelve a resolver el medio y, si cambió de archivo, retira sus decodificadores.</summary>
    private void Reresolve(MediaState media, int layers)
    {
        Variant variant = ResolveVariant(media, layers);
        lock (_gate)
        {
            media.Resolving = false;
            media.ResolvePending = false;
            if (_disposed || media.Failure is not null || media.Variant == variant)
            {
                return;
            }

            media.Variant = variant;
            foreach (DecoderState decoder in media.Decoders)
            {
                // Lo que ya adelantó el archivo anterior sigue sirviendo hasta que el nuevo decodifique los mismos cuadros.
                decoder.Retiring |= decoder.Variant is not null && decoder.Variant != variant;
            }

            _demandDirty = true;
        }
    }

    /// <summary>Qué archivo decodificar para la vista previa del medio. Lee el disco la primera vez: nunca bajo el candado.</summary>
    private Variant ResolveVariant(MediaState media, int layers)
    {
        var original = new Variant(media.Key.Path, IsProxy: false, media.DisplayWidth, media.DisplayHeight);
        if (_resolver is null)
        {
            return original;
        }

        try
        {
            MediaSourceResolution resolution = _resolver.Resolve(media.Key.Path, MediaPurpose.Preview);
            if (resolution.Proxy is not { } proxy || !ProxyMatches(proxy.Metadata, media))
            {
                return original;
            }

            if (_proxyUsage == ProxyUsage.Auto && !Recommended(resolution.Status, media, layers))
            {
                return original;
            }

            return new Variant(proxy.ProxyPath, IsProxy: true, proxy.Metadata.Width, proxy.Metadata.Height);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or ObjectDisposedException)
        {
            return original;
        }
    }

    /// <summary>El cuadro n del proxy es el n del original: misma tasa y los mismos cuadros.</summary>
    private static bool ProxyMatches(ProxyMetadata metadata, MediaState media) =>
        metadata.FrameRate == media.FrameRate && metadata.FrameCount == metadata.SourceFrameCount;

    private bool Recommended(ProxyStatus status, MediaState media, int layers)
    {
        if (status.Recommendation?.IsRecommended == true)
        {
            return true;
        }

        return media.DisplayWidth > 0 && media.DisplayHeight > 0 && _resolver!.Policy.Evaluate(
            new ProxyCandidate(media.DisplayWidth, media.DisplayHeight, "", 8, media.FrameRate, Math.Max(1, layers))).IsRecommended;
    }

    /// <summary>Le cuenta a la política de proxies cómo es el original, lo que puede encolar su proxy; una vez por medio.</summary>
    private void Assess(MediaInfo info, int layers)
    {
        try
        {
            _resolver!.Assess(info, layers: Math.Max(1, layers));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
            or ObjectDisposedException)
        {
        }
    }
}
