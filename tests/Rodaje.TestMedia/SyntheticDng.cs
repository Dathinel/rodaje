using System.Buffers.Binary;
using System.Text;
using Rodaje.Core.Raw;

namespace Rodaje.TestMedia;

/// <summary>
/// Cuadros CinemaDNG sintéticos (sin comprimir, Bayer RGGB) para probar el revelado sin depender de archivos de cámara.
/// La «cámara» ve sRGB lineal multiplicado por <see cref="Neutral"/>: con la matriz de color de sRGB y
/// <c>AsShotNeutral</c> = <see cref="Neutral"/>, revelar con el balance de la toma debe devolver la escena.
/// </summary>
public sealed record SyntheticDng
{
    /// <summary>De sRGB lineal a XYZ (D65), la inversa de la matriz de color que se escribe.</summary>
    private static readonly double[] XyzToSrgb =
    [
        3.2404542, -1.5371385, -0.4985314,
        -0.9692660, 1.8760108, 0.0415560,
        0.0556434, -0.2040259, 1.0572252,
    ];

    public int Width { get; init; } = 640;

    public int Height { get; init; } = 360;

    public int BlackLevel { get; init; } = 256;

    /// <summary>Blanco de 12 bits, como la mayoría de las cámaras de cine.</summary>
    public int WhiteLevel { get; init; } = 4095;

    /// <summary>Lo que la cámara ve de un blanco neutro (R, G, B).</summary>
    public IReadOnlyList<double> Neutral { get; init; } = [0.55, 1.0, 0.72];

    /// <summary>Cadencia (<c>FrameRate</c>); <c>null</c> no la escribe.</summary>
    public (int Numerator, int Denominator)? FrameRate { get; init; } = (24_000, 1_001);

    /// <summary>sRGB lineal de la escena en cada píxel (x, y) → (r, g, b), de 0 a 1.</summary>
    public required Func<int, int, (double R, double G, double B)> Scene { get; init; }

    /// <summary>Una carta de seis parches en una fila y una rampa de grises abajo.</summary>
    public static IReadOnlyList<(double R, double G, double B)> ChartPatches { get; } =
    [
        (0.18, 0.18, 0.18), (0.40, 0.10, 0.05), (0.05, 0.30, 0.08),
        (0.06, 0.08, 0.45), (0.80, 0.80, 0.80), (0.60, 0.45, 0.10),
    ];

    /// <summary>La carta: seis parches en la mitad de arriba; abajo, una rampa de gris de 0 a 1.</summary>
    public static SyntheticDng Chart(int width = 640, int height = 360) => new()
    {
        Width = width,
        Height = height,
        Scene = (x, y) =>
        {
            if (y < height / 2)
            {
                return ChartPatches[Math.Min(x * ChartPatches.Count / width, ChartPatches.Count - 1)];
            }

            double v = x / (double)(width - 1);
            return (v, v, v);
        },
    };

    /// <summary>El centro del parche <paramref name="index"/> de <see cref="Chart"/>.</summary>
    public static (int X, int Y) PatchCenter(int index, int width, int height) =>
        ((int)((index + 0.5) * width / ChartPatches.Count), height / 4);

    public byte[] Write()
    {
        if (Width % 2 != 0 || Height % 2 != 0)
        {
            throw new ArgumentException("Ancho y alto pares (patrón 2×2).");
        }

        var entries = new List<(ushort Tag, ushort Type, uint Count, byte[] Value)>
        {
            (254, Long, 1, U32(0)),
            (256, Long, 1, U32((uint)Width)),
            (257, Long, 1, U32((uint)Height)),
            (258, Short, 1, U16(16)),
            (259, Short, 1, U16(1)),
            (262, Short, 1, U16(32803)),
            (271, Ascii, 0, Text("Rodaje")),
            (272, Ascii, 0, Text("Sintetica")),
            (273, Long, 1, U32(0)), // StripOffsets: se corrige al final.
            (274, Short, 1, U16(1)),
            (277, Short, 1, U16(1)),
            (278, Long, 1, U32((uint)Height)),
            (279, Long, 1, U32((uint)(Width * Height * 2))),
            (284, Short, 1, U16(1)),
            (33421, Short, 2, [.. U16(2), .. U16(2)]),
            (33422, Byte, 4, [0, 1, 1, 2]),
            (50706, Byte, 4, [1, 4, 0, 0]),
            (50707, Byte, 4, [1, 1, 0, 0]),
            (50708, Ascii, 0, Text("Rodaje Sintetica")),
            (50714, Long, 1, U32((uint)BlackLevel)),
            (50717, Long, 1, U32((uint)WhiteLevel)),
            (50721, SRational, 9, [.. XyzToSrgb.SelectMany(v => SRationalBytes(v))]),
            (50728, RationalType, 3, [.. Neutral.SelectMany(v => RationalBytes(v))]),
            (50778, Short, 1, U16(21)),
        };
        if (FrameRate is { } rate)
        {
            entries.Add((0xC764, SRational, 1, [.. I32(rate.Numerator), .. I32(rate.Denominator)]));
        }

        entries = [.. entries.Select(e => e.Type == Ascii ? e with { Count = (uint)e.Value.Length } : e).OrderBy(e => e.Tag)];
        int ifdSize = 2 + (entries.Count * 12) + 4;
        int extra = 8 + ifdSize;
        var extras = new List<byte>();
        var ifd = new List<byte>();
        ifd.AddRange(U16((ushort)entries.Count));
        int stripEntry = -1;
        foreach ((ushort tag, ushort type, uint count, byte[] value) in entries)
        {
            if (tag == 273)
            {
                stripEntry = ifd.Count;
            }

            ifd.AddRange(U16(tag));
            ifd.AddRange(U16(type));
            ifd.AddRange(U32(count));
            if (value.Length <= 4)
            {
                ifd.AddRange([.. value, .. new byte[4 - value.Length]]);
            }
            else
            {
                ifd.AddRange(U32((uint)(extra + extras.Count)));
                extras.AddRange(value);
                if (extras.Count % 2 != 0)
                {
                    extras.Add(0);
                }
            }
        }

        ifd.AddRange(U32(0));
        int dataOffset = extra + extras.Count;
        byte[] file = new byte[dataOffset + (Width * Height * 2)];
        file[0] = (byte)'I';
        file[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 8);
        ifd.CopyTo(file, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8 + stripEntry + 8), (uint)dataOffset);
        extras.CopyTo(file, extra);
        WriteMosaic(file.AsSpan(dataOffset));
        return file;
    }

    /// <summary>El código RAW del píxel (x, y), con el negro: RGGB, (par, par) rojo, (impar, impar) azul, el resto verde.</summary>
    public ushort RawCode(int x, int y)
    {
        (double r, double g, double b) = Scene(x, y);
        (double value, double neutral) = (y % 2, x % 2) switch
        {
            (0, 0) => (r, Neutral[0]),
            (1, 1) => (b, Neutral[2]),
            _ => (g, Neutral[1]),
        };
        return (ushort)Math.Round(BlackLevel + (Math.Clamp(value, 0, 1) * neutral * (WhiteLevel - BlackLevel)));
    }

    /// <summary>El mosaico con el negro ya restado, como lo devuelve «mosaico» de modulo-raw (ancho × alto códigos, por filas).</summary>
    public ushort[] Mosaic()
    {
        var mosaic = new ushort[Width * Height];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                mosaic[(y * Width) + x] = (ushort)(RawCode(x, y) - BlackLevel);
            }
        }

        return mosaic;
    }

    /// <summary>
    /// Los parámetros de revelado de este cuadro con el balance de la toma: el blanco sin el negro, los multiplicadores
    /// 1/<see cref="Neutral"/> y la matriz identidad (la «cámara» ve sRGB lineal).
    /// </summary>
    public RawDevelopParameters DevelopParameters(double exposure = 0, RawOutputCurve curve = RawOutputCurve.Linear) => new()
    {
        Pattern = BayerPattern.Rggb,
        White = WhiteLevel - BlackLevel,
        Multipliers = [(float)(1 / Neutral[0]), (float)(1 / Neutral[1]), (float)(1 / Neutral[2])],
        Matrix = [1, 0, 0, 0, 1, 0, 0, 0, 1],
        Exposure = exposure,
        Curve = curve,
    };

    private void WriteMosaic(Span<byte> target)
    {
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(target[(((y * Width) + x) * 2)..], RawCode(x, y));
            }
        }
    }

    private const ushort Byte = 1;
    private const ushort Ascii = 2;
    private const ushort Short = 3;
    private const ushort Long = 4;
    private const ushort RationalType = 5;
    private const ushort SRational = 10;

    private static byte[] U16(ushort v)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        return b;
    }

    private static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }

    private static byte[] I32(int v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, v);
        return b;
    }

    private static byte[] SRationalBytes(double v) => [.. I32((int)Math.Round(v * 10_000_000)), .. I32(10_000_000)];

    private static byte[] RationalBytes(double v) => [.. U32((uint)Math.Round(v * 1_000_000)), .. U32(1_000_000)];

    private static byte[] Text(string s) => [.. Encoding.ASCII.GetBytes(s), 0];
}
