namespace Rodaje.Core.Raw;

/// <summary>
/// El patrón de Bayer de un mosaico: el canal (0 rojo, 1 verde, 2 azul) de cada una de las cuatro posiciones del bloque
/// de 2×2 que se repite, empezando en la esquina de arriba a la izquierda del cuadro. Siempre un rojo, un azul y dos verdes.
/// </summary>
public readonly record struct BayerPattern(int TopLeft, int TopRight, int BottomLeft, int BottomRight)
{
    public const int Red = 0;
    public const int Green = 1;
    public const int Blue = 2;

    public static BayerPattern Rggb => new(Red, Green, Green, Blue);

    /// <summary>Si los cuatro canales son un rojo, un azul y dos verdes en diagonal (lo que interpola el revelado).</summary>
    public bool IsValid =>
        Count(Red) == 1 && Count(Blue) == 1 && Count(Green) == 2
        && (TopLeft == Green ? BottomRight == Green : TopRight == Green && BottomLeft == Green);

    /// <summary>Lee <c>RGGB</c>, <c>BGGR</c>, <c>GRBG</c> o <c>GBRG</c> (cualquier orden válido de las cuatro letras).</summary>
    public static bool TryParse(string? text, out BayerPattern pattern)
    {
        pattern = default;
        if (text is not { Length: 4 })
        {
            return false;
        }

        Span<int> channels = stackalloc int[4];
        for (int i = 0; i < 4; i++)
        {
            channels[i] = char.ToUpperInvariant(text[i]) switch
            {
                'R' => Red,
                'G' => Green,
                'B' => Blue,
                _ => -1,
            };
            if (channels[i] < 0)
            {
                return false;
            }
        }

        pattern = new BayerPattern(channels[0], channels[1], channels[2], channels[3]);
        return pattern.IsValid;
    }

    /// <summary>El canal del píxel (<paramref name="x"/>, <paramref name="y"/>) del cuadro.</summary>
    public int ChannelAt(int x, int y) => ((y & 1) << 1 | (x & 1)) switch
    {
        0 => TopLeft,
        1 => TopRight,
        2 => BottomLeft,
        _ => BottomRight,
    };

    public override string ToString() => string.Create(4, this, static (span, p) =>
    {
        span[0] = Letter(p.TopLeft);
        span[1] = Letter(p.TopRight);
        span[2] = Letter(p.BottomLeft);
        span[3] = Letter(p.BottomRight);
    });

    private static char Letter(int channel) => channel switch { Red => 'R', Green => 'G', Blue => 'B', _ => '?' };

    private int Count(int channel) =>
        (TopLeft == channel ? 1 : 0) + (TopRight == channel ? 1 : 0) + (BottomLeft == channel ? 1 : 0) + (BottomRight == channel ? 1 : 0);
}
