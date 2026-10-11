using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Proxies;

namespace Rodaje.App.Services.Inspector;

/// <summary>Lo que dice la pestaña Velocidad sobre el proxy de un clip invertido; <see cref="IsWarning"/> lo pinta como aviso.</summary>
public sealed record ReverseNotice(string Text, bool IsWarning);

/// <summary>
/// El aviso del proxy obligatorio de la reversa (por encima de 1080p, <see cref="ProxyRecommendation.IsRequired"/>): en
/// cola, generándose, listo o por qué no hay.
/// </summary>
public static class ReverseProxyNotice
{
    private static readonly Rational FallbackRate = new(30, 1);

    /// <summary>El aviso para <paramref name="clip"/>, o <c>null</c> si no va en reversa o su video no necesita proxy.</summary>
    /// <param name="status">Estado del proxy del medio; <c>null</c> si la vista previa no usa proxies.</param>
    public static ReverseNotice? For(Clip clip, MediaRef? media, ProxyStatus? status, ProxyPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(policy);
        if (clip.Speed is not { Reverse: true, Freeze: false } || media is not { HasVideo: true } || !Required(media, status, policy))
        {
            return null;
        }

        const string Choppy = "Hasta que esté, la vista previa puede ir a saltos.";
        return status?.State switch
        {
            ProxyState.Ready => new("La reversa usa su proxy, ya listo. La exportación usa el original.", false),
            ProxyState.Generating => new($"Generando el proxy de la reversa: {Percent(status.Progress)} %. {Choppy}", true),
            ProxyState.Queued => new($"El proxy de la reversa está en cola. {Choppy}", true),
            ProxyState.Failed => new($"No se pudo generar el proxy de la reversa: {status.Message ?? "error desconocido"}. La vista previa puede ir a saltos; la exportación no cambia.", true),
            ProxyState.Unavailable => new((status.Message ?? "No se pueden generar proxies.") + " Sin proxy, la reversa por encima de 1080p puede ir a saltos en la vista previa.", true),
            null => new("La reversa por encima de 1080p necesita un proxy y la vista previa no los usa: puede ir a saltos.", true),
            _ => new($"La reversa por encima de 1080p necesita un proxy; se genera solo. {Choppy}", true),
        };
    }

    /// <summary>Con el tamaño conocido decide la política; si no, la última evaluación del medio.</summary>
    public static bool Required(MediaRef media, ProxyStatus? status, ProxyPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(policy);
        if (media.Width <= 0 || media.Height <= 0)
        {
            return status?.Recommendation?.IsRequired == true;
        }

        var candidate = new ProxyCandidate(media.Width, media.Height, "", 8, media.FrameRate ?? FallbackRate, Reverse: true);
        return policy.Evaluate(candidate).IsRequired;
    }

    private static int Percent(double progress) => (int)Math.Round(Math.Clamp(progress, 0, 1) * 100);
}
