using Rodaje.Core.Timing;
using Rodaje.Media.Probe;

namespace Rodaje.Media.Proxies;

/// <summary>Motivos por los que conviene un proxy; varios pueden darse a la vez.</summary>
[Flags]
public enum ProxyReasons
{
    None = 0,

    /// <summary>Más de 1080p.</summary>
    Resolution = 1,

    /// <summary>HEVC de más de 8 bits con varias capas a la vez.</summary>
    HighBitDepthHevc = 2,

    /// <summary>Keyframes muy separados: cada seek decodifica muchos cuadros.</summary>
    LongGop = 4,

    /// <summary>Tasa de bits muy alta.</summary>
    HighBitrate = 8,

    /// <summary>La decodificación medida no da el tiempo real que piden las capas.</summary>
    SlowDecode = 16,
}

/// <summary>Umbrales de <see cref="ProxyPolicy"/>; cada uno se puede ajustar y probar por separado.</summary>
public sealed record ProxyThresholds
{
    public static ProxyThresholds Default { get; } = new();

    /// <summary>Lado largo máximo sin proxy (1920 = 1080p).</summary>
    public int MaxLongSide { get; init; } = 1920;

    /// <summary>Lado corto máximo sin proxy (1088 admite el alto codificado de 1080p).</summary>
    public int MaxShortSide { get; init; } = 1088;

    /// <summary>Capas simultáneas desde las que un HEVC de más de 8 bits pide proxy.</summary>
    public int HighBitDepthHevcLayers { get; init; } = 2;

    /// <summary>Distancia máxima entre keyframes, en cuadros, sin proxy.</summary>
    public int MaxKeyframeInterval { get; init; } = 60;

    /// <summary>Tasa de bits máxima sin proxy, en bits por segundo.</summary>
    public long MaxBitrate { get; init; } = 80_000_000;

    /// <summary>Velocidad mínima de decodificación por capa, en veces el tiempo real.</summary>
    /// <remarks>Con 3 capas y 1,5 hace falta decodificar a 4,5× para no perder cuadros.</remarks>
    public double MinDecodeSpeedPerLayer { get; init; } = 1.5;
}

/// <summary>Lo que la política sabe de un medio: lo que dice el archivo y lo que se midió en este equipo.</summary>
/// <param name="Width">Ancho en pantalla.</param>
/// <param name="Height">Alto en pantalla.</param>
/// <param name="Codec">Nombre del códec según FFmpeg (<c>h264</c>, <c>hevc</c>, …).</param>
/// <param name="BitDepth">Bits por muestra de luma.</param>
/// <param name="FrameRate">Tasa de cuadros.</param>
/// <param name="Bitrate">Bits por segundo del video, si se conocen.</param>
/// <param name="KeyframeInterval">Mayor distancia entre keyframes observada, en cuadros, si se midió.</param>
/// <param name="DecodeSpeed">Velocidad medida de decodificación en veces el tiempo real, si se midió.</param>
/// <param name="Layers">Capas que se espera reproducir a la vez (1 si es la única).</param>
public sealed record ProxyCandidate(
    int Width,
    int Height,
    string Codec,
    int BitDepth,
    Rational FrameRate,
    long? Bitrate = null,
    int? KeyframeInterval = null,
    double? DecodeSpeed = null,
    int Layers = 1)
{
    /// <summary>Candidato con los metadatos del archivo y, si se tienen, las medidas.</summary>
    public static ProxyCandidate From(MediaInfo info, StreamStructure? structure = null, double? decodeSpeed = null, int layers = 1)
    {
        ArgumentNullException.ThrowIfNull(info);
        VideoStreamInfo video = info.Video ?? throw new ArgumentException($"{info.Path} no tiene video.", nameof(info));
        return new ProxyCandidate(
            video.DisplayWidth,
            video.DisplayHeight,
            video.Codec,
            video.BitDepth,
            video.FrameRate,
            structure?.Bitrate,
            structure?.MaxKeyframeInterval,
            decodeSpeed,
            layers);
    }
}

/// <summary>Resultado de la política: si conviene un proxy y por qué, en español para la interfaz.</summary>
public sealed record ProxyRecommendation(ProxyReasons Reasons, IReadOnlyList<string> Explanations)
{
    public bool IsRecommended => Reasons != ProxyReasons.None;
}

/// <summary>Decide cuándo conviene un proxy para la vista previa.</summary>
/// <remarks>La exportación usa siempre el original; el proxy solo alivia la decodificación de la vista previa.</remarks>
public sealed class ProxyPolicy(ProxyThresholds? thresholds = null)
{
    public ProxyThresholds Thresholds { get; } = thresholds ?? ProxyThresholds.Default;

    public ProxyRecommendation Evaluate(ProxyCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentOutOfRangeException.ThrowIfLessThan(candidate.Layers, 1);
        ProxyThresholds t = Thresholds;
        var reasons = ProxyReasons.None;
        var text = new List<string>();
        void Add(ProxyReasons reason, string explanation)
        {
            reasons |= reason;
            text.Add(explanation);
        }

        int longSide = Math.Max(candidate.Width, candidate.Height);
        int shortSide = Math.Min(candidate.Width, candidate.Height);
        if (longSide > t.MaxLongSide || shortSide > t.MaxShortSide)
        {
            Add(ProxyReasons.Resolution, F($"Resolución de {candidate.Width}×{candidate.Height}, mayor que 1080p."));
        }

        if (candidate.BitDepth > 8 && IsHevc(candidate.Codec) && candidate.Layers >= t.HighBitDepthHevcLayers)
        {
            Add(ProxyReasons.HighBitDepthHevc,
                F($"HEVC de {candidate.BitDepth} bits en {candidate.Layers} capas a la vez."));
        }

        if (candidate.KeyframeInterval is { } gop && gop > t.MaxKeyframeInterval)
        {
            Add(ProxyReasons.LongGop, F($"Keyframes cada {gop} cuadros (más de {t.MaxKeyframeInterval}): los saltos son lentos."));
        }

        if (candidate.Bitrate is { } bitrate && bitrate > t.MaxBitrate)
        {
            Add(ProxyReasons.HighBitrate, F($"Tasa de bits de {bitrate / 1e6:0} Mb/s (más de {t.MaxBitrate / 1e6:0})."));
        }

        double required = t.MinDecodeSpeedPerLayer * candidate.Layers;
        if (candidate.DecodeSpeed is { } speed && speed < required)
        {
            Add(ProxyReasons.SlowDecode,
                F($"Este equipo lo decodifica a {speed:0.0}× el tiempo real y {candidate.Layers} capa(s) piden {required:0.0}×."));
        }

        return new ProxyRecommendation(reasons, text);
    }

    private static bool IsHevc(string codec) =>
        codec.Equals("hevc", StringComparison.OrdinalIgnoreCase) || codec.Equals("h265", StringComparison.OrdinalIgnoreCase);

    private static string F(FormattableString value) => FormattableString.Invariant(value);
}
