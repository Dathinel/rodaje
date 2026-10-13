using System.Globalization;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services.Format;

/// <summary>Cómo quedan los clips al cambiar la forma del proyecto (como «Ajustar» en CapCut).</summary>
public enum ClipFit
{
    /// <summary>Cada clip entero dentro del cuadro nuevo, con barras si hace falta.</summary>
    Fit,

    /// <summary>Cada clip llena el cuadro nuevo; lo que sobra queda fuera.</summary>
    Fill,

    /// <summary>Cada clip conserva su tamaño en píxeles.</summary>
    Keep,
}

/// <summary>Una proporción de la lista: 16:9, 9:16…; <see cref="Custom"/> es la de medidas libres.</summary>
public sealed record AspectChoice(string Label, string Use, int Width, int Height)
{
    public bool Custom => Width == 0;

    /// <summary>Medidas con ese lado corto (pares; en 21:9 el lado corto es el alto).</summary>
    public (int Width, int Height) Size(int shortSide)
    {
        if (Custom)
        {
            throw new InvalidOperationException("Una proporción personalizada no tiene medidas propias.");
        }

        return Width >= Height
            ? (Even(shortSide * (double)Width / Height), Even(shortSide))
            : (Even(shortSide), Even(shortSide * (double)Height / Width));
    }

    private static int Even(double value) => Math.Max(2, (int)Math.Round(value / 2) * 2);
}

/// <summary>Formatos del proyecto que ofrece el selector del reproductor: proporción, resolución y cuadros por segundo.</summary>
public static class ProjectFormats
{
    public const int MinSide = 16;
    public const int MaxSide = 8192;

    public static IReadOnlyList<AspectChoice> Aspects { get; } =
    [
        new("16:9", "Horizontal · video web", 16, 9),
        new("9:16", "Vertical · videos cortos", 9, 16),
        new("1:1", "Cuadrado · publicaciones", 1, 1),
        new("4:5", "Retrato · publicaciones", 4, 5),
        new("4:3", "Clásico", 4, 3),
        new("21:9", "Cine", 21, 9),
        new("Personalizado", "Ancho y alto a mano", 0, 0),
    ];

    /// <summary>Lados cortos de la lista de resoluciones (720p a 4K).</summary>
    public static IReadOnlyList<(int ShortSide, string Label)> Resolutions { get; } =
        [(720, "720p"), (1080, "1080p"), (1440, "1440p"), (2160, "4K")];

    public static IReadOnlyList<(Rational Rate, string Label)> FrameRates { get; } =
    [
        (Rational.Ntsc24, "23,976"), (Rational.Fps24, "24"), (Rational.Fps25, "25"), (Rational.Ntsc30, "29,97"), (Rational.Fps30, "30"),
        (Rational.Fps50, "50"), (Rational.Ntsc60, "59,94"), (Rational.Fps60, "60"),
    ];

    /// <summary>Cómo quedan los clips, en el orden del menú «Ajustar clips» y del diálogo, con su ayuda.</summary>
    public static IReadOnlyList<(ClipFit Fit, string Label, string Help)> FitChoices { get; } =
    [
        (ClipFit.Fit, "Encajar", "Cada clip entero, con barras si no tiene la forma del proyecto."),
        (ClipFit.Fill, "Rellenar", "Cada clip llena el cuadro; lo que sobra queda fuera."),
        (ClipFit.Keep, "Mantener tamaño", "Cada clip conserva su tamaño en píxeles."),
    ];

    /// <summary>
    /// El ajuste si es uno de la lista; si no (un número suelto en los ajustes guardados), <see cref="ClipFit.Fit"/>. Así
    /// ningún valor fuera de la lista llega al menú ni al cambio de formato.
    /// </summary>
    public static ClipFit Normalize(ClipFit fit) => Enum.IsDefined(fit) ? fit : ClipFit.Fit;

    /// <summary>Nombre del ajuste para el aviso («encajar»…); el de <see cref="ClipFit.Fit"/> si no está en la lista.</summary>
    public static string FitLabel(ClipFit fit)
    {
        ClipFit normalized = Normalize(fit);
        foreach ((ClipFit value, string label, string _) in FitChoices)
        {
            if (value == normalized)
            {
                return label;
            }
        }

        return FitChoices[0].Label;
    }

    /// <summary>La proporción de la lista con esa forma (1 % de tolerancia), o «Personalizado».</summary>
    public static AspectChoice Match(int width, int height)
    {
        if (width > 0 && height > 0)
        {
            double ratio = (double)width / height;
            foreach (AspectChoice aspect in Aspects)
            {
                if (!aspect.Custom && Math.Abs(ratio - ((double)aspect.Width / aspect.Height)) / ratio < 0.01)
                {
                    return aspect;
                }
            }
        }

        return Aspects[^1];
    }

    /// <summary>Medidas al elegir otra proporción conservando la resolución (el lado corto) de ahora.</summary>
    public static (int Width, int Height) Reshape(AspectChoice aspect, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(aspect);
        int shortSide = Math.Clamp(Math.Min(width, height), MinSide, MaxSide);
        return aspect.Custom ? (width, height) : aspect.Size(shortSide);
    }

    /// <summary>Si las medidas sirven para una secuencia (pares y entre <see cref="MinSide"/> y <see cref="MaxSide"/>).</summary>
    public static bool IsValidSize(int width, int height) =>
        width is >= MinSide and <= MaxSide && height is >= MinSide and <= MaxSide && width % 2 == 0 && height % 2 == 0;

    /// <summary>«9:16 · 1080×1920 · 30 fps».</summary>
    public static string Describe(Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        return $"{AspectName(sequence.Width, sequence.Height)} · {sequence.Width}×{sequence.Height} · {Fps(sequence.FrameRate)} fps";
    }

    /// <summary>«16:9», «9:16», «4:5» o «2,35:1».</summary>
    public static string AspectName(int width, int height)
    {
        AspectChoice match = Match(width, height);
        if (!match.Custom)
        {
            return match.Label;
        }

        int gcd = Gcd(width, height);
        (int w, int h) = (width / gcd, height / gcd);
        return w <= 32 && h <= 32 ? $"{w}:{h}" : Number((double)width / height) + ":1";
    }

    /// <summary>«30», «29,97», «23,976».</summary>
    public static string Fps(Rational rate) => FrameRates.FirstOrDefault(f => f.Rate == rate).Label ?? Number(rate.ToDouble());

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', ',');

    private static int Gcd(int a, int b) => b == 0 ? Math.Max(1, a) : Gcd(b, a % b);
}
