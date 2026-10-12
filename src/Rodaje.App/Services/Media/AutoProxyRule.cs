using Rodaje.Media.Proxies;

namespace Rodaje.App.Services.Media;

/// <summary>Si un medio recién importado merece proxy, y por qué en español.</summary>
public sealed record AutoProxyDecision(bool Generate, string? Reason)
{
    public static AutoProxyDecision No { get; } = new(false, null);
}

/// <summary>
/// Cuándo generar el proxy de un video al importarlo: lo que recomienda la política de siempre (<see cref="ProxyPolicy"/>:
/// más de 1080p, tasa muy alta, HEVC de más de 8 bits en varias capas) y, con una GPU modesta (menos de 4 GB de VRAM o
/// desconocida), también los intra pesados (ProRes, DNxHD, CineForm) y el HEVC de 10 bits desde 1080p.
/// </summary>
public static class AutoProxyRule
{
    public const long ModestGpuBytes = 4L << 30;

    private static readonly string[] HeavyIntraCodecs = ["prores", "dnxhd", "dnxhr", "cfhd"];

    public static AutoProxyDecision Decide(ProxyCandidate candidate, long dedicatedVideoMemory, ProxyPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ProxyRecommendation recommendation = (policy ?? new ProxyPolicy()).Evaluate(candidate);
        if (recommendation.IsRecommended)
        {
            return new AutoProxyDecision(true, recommendation.Explanations[0]);
        }

        bool modestGpu = dedicatedVideoMemory < ModestGpuBytes;
        bool fullHd = Math.Max(candidate.Width, candidate.Height) >= 1920 || Math.Min(candidate.Width, candidate.Height) >= 1080;
        if (!modestGpu || !fullHd)
        {
            return AutoProxyDecision.No;
        }

        string codec = candidate.Codec.ToLowerInvariant();
        if (HeavyIntraCodecs.Any(codec.StartsWith))
        {
            return new AutoProxyDecision(true, $"{candidate.Codec} en 1080p pesa demasiado para esta GPU.");
        }

        return (codec is "hevc" or "h265") && candidate.BitDepth > 8
            ? new AutoProxyDecision(true, $"HEVC de {candidate.BitDepth} bits en 1080p pesa demasiado para esta GPU.")
            : AutoProxyDecision.No;
    }
}
