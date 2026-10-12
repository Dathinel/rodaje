namespace Rodaje.TestMedia;

/// <summary>
/// Número de cuadro dibujado como una rejilla de 8 × 4 bloques blancos y negros en la luma.
/// Los bloques miden al menos 8 px y van alineados a 8 px, así que sobreviven a la compresión
/// fuerte, al desenfoque y al submuestreo de croma (la zona del código lleva croma neutra).
/// </summary>
/// <remarks>
/// Celdas 0 y 31 negras y 7 y 24 blancas: calibran el umbral, así el lector no depende del
/// rango de luma (16–235 o 0–255). Las 28 restantes llevan 20 bits del índice y un CRC-8.
/// </remarks>
public static class FrameCode
{
    public const int Columns = 8;
    public const int Rows = 4;
    public const int IndexBits = 20;
    public const int MaxIndex = (1 << IndexBits) - 1;

    public const byte Black = 16;
    public const byte White = 235;
    public const byte Border = 128;

    private const int CrcBits = 8;
    private static readonly int[] BlackCells = [0, Columns * Rows - 1];
    private static readonly int[] WhiteCells = [Columns - 1, Columns * (Rows - 1)];

    /// <summary>Lado de cada celda y posición del código para un tamaño de cuadro.</summary>
    public readonly record struct Layout(int Cell, int X, int Y)
    {
        public int Width => Cell * Columns;
        public int Height => Cell * Rows;

        /// <summary>Rectángulo con el borde gris de media celda alrededor del código.</summary>
        public (int X, int Y, int Width, int Height) Panel =>
            (X - Cell / 2, Y - Cell / 2, Width + Cell, Height + Cell);
    }

    public static Layout GetLayout(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"Tamaño de cuadro inválido: {width}×{height}.");
        }

        int cell = Math.Max(8, Math.Min(width, height) / 16 / 8 * 8);
        if (cell * (Columns + 2) > width || cell * (Rows + 2) > height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width), $"El cuadro de {width}×{height} es muy pequeño para el código de cuadro (mínimo {8 * (Columns + 2)}×{8 * (Rows + 2)}).");
        }

        return new Layout(cell, cell, cell);
    }

    /// <summary>Dibuja el índice sobre la luma (8 bits, una muestra por píxel).</summary>
    public static void Draw(Span<byte> luma, int width, int height, int stride, int index)
    {
        if ((uint)index > MaxIndex)
        {
            throw new ArgumentOutOfRangeException(nameof(index), $"El índice {index} no cabe en {IndexBits} bits.");
        }

        CheckPlane(luma.Length, width, height, stride);
        Layout layout = GetLayout(width, height);
        (int px, int py, int pw, int ph) = layout.Panel;
        Fill(luma, stride, px, py, pw, ph, Border);

        bool[] cells = Encode(index);
        for (int i = 0; i < cells.Length; i++)
        {
            int cx = layout.X + i % Columns * layout.Cell;
            int cy = layout.Y + i / Columns * layout.Cell;
            Fill(luma, stride, cx, cy, layout.Cell, layout.Cell, cells[i] ? White : Black);
        }
    }

    /// <summary>Pone croma neutra (128) bajo el panel del código en un plano de croma 4:2:0.</summary>
    public static void NeutralizeChroma(Span<byte> chroma, int width, int height, int chromaStride)
    {
        CheckPlane(chroma.Length, (width + 1) / 2, (height + 1) / 2, chromaStride);
        (int px, int py, int pw, int ph) = GetLayout(width, height).Panel;
        Fill(chroma, chromaStride, px / 2, py / 2, (pw + 1) / 2, (ph + 1) / 2, 128);
    }

    /// <summary>Dibuja el índice en un cuadro I420 (YUV 4:2:0 planar, sin relleno entre filas).</summary>
    public static void DrawI420(Span<byte> frame, int width, int height, int index)
    {
        int lumaSize = width * height;
        int chromaWidth = (width + 1) / 2;
        int chromaSize = chromaWidth * ((height + 1) / 2);
        if (frame.Length < lumaSize + 2 * chromaSize)
        {
            throw new ArgumentException($"El búfer I420 mide {frame.Length} bytes y se necesitan {lumaSize + 2 * chromaSize}.", nameof(frame));
        }

        Draw(frame[..lumaSize], width, height, width, index);
        NeutralizeChroma(frame.Slice(lumaSize, chromaSize), width, height, chromaWidth);
        NeutralizeChroma(frame.Slice(lumaSize + chromaSize, chromaSize), width, height, chromaWidth);
    }

    /// <summary>Lee el índice de la luma, o null si el código no se distingue o el CRC no cuadra.</summary>
    public static int? Read(ReadOnlySpan<byte> luma, int width, int height, int stride)
    {
        CheckPlane(luma.Length, width, height, stride);
        Layout layout = GetLayout(width, height);

        var means = new double[Columns * Rows];
        for (int i = 0; i < means.Length; i++)
        {
            int cx = layout.X + i % Columns * layout.Cell;
            int cy = layout.Y + i / Columns * layout.Cell;
            means[i] = CenterMean(luma, stride, cx, cy, layout.Cell);
        }

        double black = (means[BlackCells[0]] + means[BlackCells[1]]) / 2;
        double white = (means[WhiteCells[0]] + means[WhiteCells[1]]) / 2;
        double contrast = white - black;
        if (contrast < 40)
        {
            return null;
        }

        double threshold = (black + white) / 2;
        // Una celda cerca del umbral es una lectura dudosa: mejor null que un índice equivocado.
        double margin = contrast * 0.2;
        var bits = new bool[means.Length];
        for (int i = 0; i < means.Length; i++)
        {
            if (Math.Abs(means[i] - threshold) < margin)
            {
                return null;
            }

            bits[i] = means[i] > threshold;
        }

        foreach (int i in BlackCells)
        {
            if (bits[i])
            {
                return null;
            }
        }

        foreach (int i in WhiteCells)
        {
            if (!bits[i])
            {
                return null;
            }
        }

        int index = 0;
        int crc = 0;
        int dataBit = 0;
        for (int i = 0; i < bits.Length; i++)
        {
            if (IsSyncCell(i))
            {
                continue;
            }

            if (dataBit < IndexBits)
            {
                index = (index << 1) | (bits[i] ? 1 : 0);
            }
            else
            {
                crc = (crc << 1) | (bits[i] ? 1 : 0);
            }

            dataBit++;
        }

        return crc == Crc8(index) ? index : null;
    }

    internal static bool[] Encode(int index)
    {
        var cells = new bool[Columns * Rows];
        foreach (int i in WhiteCells)
        {
            cells[i] = true;
        }

        int payload = (index << CrcBits) | Crc8(index);
        int bit = IndexBits + CrcBits - 1;
        for (int i = 0; i < cells.Length; i++)
        {
            if (!IsSyncCell(i))
            {
                cells[i] = ((payload >> bit) & 1) != 0;
                bit--;
            }
        }

        return cells;
    }

    internal static int Crc8(int index)
    {
        int crc = 0;
        for (int shift = 16; shift >= 0; shift -= 8)
        {
            crc ^= (index >> shift) & 0xFF;
            for (int b = 0; b < 8; b++)
            {
                crc = (crc & 0x80) != 0 ? ((crc << 1) ^ 0x07) & 0xFF : (crc << 1) & 0xFF;
            }
        }

        return crc;
    }

    private static bool IsSyncCell(int i) =>
        i == BlackCells[0] || i == BlackCells[1] || i == WhiteCells[0] || i == WhiteCells[1];

    private static double CenterMean(ReadOnlySpan<byte> plane, int stride, int x, int y, int cell)
    {
        int from = cell / 4;
        int to = cell - cell / 4;
        long sum = 0;
        for (int row = y + from; row < y + to; row++)
        {
            ReadOnlySpan<byte> line = plane.Slice(row * stride + x + from, to - from);
            foreach (byte v in line)
            {
                sum += v;
            }
        }

        return (double)sum / ((to - from) * (to - from));
    }

    private static void Fill(Span<byte> plane, int stride, int x, int y, int w, int h, byte value)
    {
        for (int row = y; row < y + h; row++)
        {
            plane.Slice(row * stride + x, w).Fill(value);
        }
    }

    private static void CheckPlane(int length, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || stride < width)
        {
            throw new ArgumentOutOfRangeException(nameof(stride), $"Plano inválido: {width}×{height} con paso {stride}.");
        }

        if (length < (long)stride * (height - 1) + width)
        {
            throw new ArgumentException($"El plano mide {length} bytes y {width}×{height} con paso {stride} necesita más.");
        }
    }
}
