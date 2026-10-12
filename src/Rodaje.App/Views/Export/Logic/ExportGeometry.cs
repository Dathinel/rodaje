using Rodaje.Core.Timing;
using Rodaje.Export;

namespace Rodaje.App.Views.Export;

/// <summary>Un tamaño de salida que se ofrece en la lista; sin medidas es «como la secuencia».</summary>
public sealed record SizeChoice(string Label, int? Width, int? Height);

/// <summary>Una tasa de salida que se ofrece; <c>null</c> es la de la secuencia.</summary>
public sealed record FrameRateChoice(string Label, Rational? Rate);

/// <summary>Tamaño y forma de la salida frente a los de la secuencia.</summary>
public static class ExportGeometry
{
    public static IReadOnlyList<FrameRateChoice> FrameRates { get; } =
    [
        new("Como la secuencia", null),
        new("23,976", Rational.Ntsc24), new("24", Rational.Fps24), new("25", Rational.Fps25), new("29,97", Rational.Ntsc30),
        new("30", Rational.Fps30), new("50", Rational.Fps50), new("59,94", Rational.Ntsc60), new("60", Rational.Fps60),
    ];

    /// <summary>Tamaños de la lista: el de la secuencia, los horizontales habituales y los de redes (9:16, 4:5, 1:1).</summary>
    public static IReadOnlyList<SizeChoice> Sizes(int sequenceWidth, int sequenceHeight) =>
    [
        new($"Como la secuencia ({sequenceWidth}×{sequenceHeight})", null, null),
        new("2160p · 3840×2160", 3840, 2160),
        new("1440p · 2560×1440", 2560, 1440),
        new("1080p · 1920×1080", 1920, 1080),
        new("720p · 1280×720", 1280, 720),
        new("480p · 854×480", 854, 480),
        new("Vertical 9:16 · 1080×1920", 1080, 1920),
        new("Retrato 4:5 · 1080×1350", 1080, 1350),
        new("Cuadrado 1:1 · 1080×1080", 1080, 1080),
    ];

    /// <summary>
    /// Tamaño final en píxeles: el de la secuencia (o del recorte) si no se pide otro; con una sola medida, la otra sigue
    /// la forma de origen. Siempre par, salvo en los formatos RGB que no lo necesitan.
    /// </summary>
    public static (int Width, int Height) OutputSize(VideoExportSettings video, int sequenceWidth, int sequenceHeight)
    {
        ArgumentNullException.ThrowIfNull(video);
        return video.OutputSize(sequenceWidth, sequenceHeight);
    }

    /// <summary>Si la salida tiene otra forma que la secuencia (más de un 1 % de diferencia): entonces importan barras, recorte o estirar.</summary>
    public static bool ShapeDiffers(int outputWidth, int outputHeight, int sequenceWidth, int sequenceHeight)
    {
        double output = (double)outputWidth / outputHeight;
        double sequence = (double)sequenceWidth / sequenceHeight;
        return Math.Abs(output - sequence) / sequence > 0.01;
    }

    /// <summary>«16:9», «9:16», «1:1», «4:5» o el cociente con dos decimales.</summary>
    public static string AspectName(int width, int height)
    {
        int gcd = Gcd(width, height);
        (int w, int h) = (width / gcd, height / gcd);
        return w <= 32 && h <= 32 ? $"{w}:{h}" : ExportText.Number((double)width / height, 2) + ":1";
    }

    /// <summary>
    /// El formato lo decide el proyecto: si un preajuste (o lo recordado) trae medidas de otra forma, se exporta con la
    /// forma de la secuencia y el lado corto del preajuste (YouTube 4K sobre un proyecto 9:16 sale 2160×3840). Lo que solo
    /// fija un lado (el GIF de 480 de ancho) ya sigue a la secuencia.
    /// </summary>
    public static ExportSettings FollowSequence(ExportSettings settings, int sequenceWidth, int sequenceHeight)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Video is not { Width: { } width, Height: { } height } video || !ShapeDiffers(width, height, sequenceWidth, sequenceHeight)
            || sequenceWidth <= 0 || sequenceHeight <= 0)
        {
            return settings;
        }

        int shortSide = Math.Min(width, height);
        (int w, int h) = sequenceWidth >= sequenceHeight
            ? ((int)Math.Round(shortSide * (double)sequenceWidth / sequenceHeight), shortSide)
            : (shortSide, (int)Math.Round(shortSide * (double)sequenceHeight / sequenceWidth));
        return settings with { Video = video with { Width = Math.Max(2, w & ~1), Height = Math.Max(2, h & ~1) } };
    }

    private static int Gcd(int a, int b) => b == 0 ? Math.Max(1, a) : Gcd(b, a % b);
}
