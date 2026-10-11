namespace Rodaje.Media.Export;

/// <summary>
/// Un cuadro ya convertido y bajado a la CPU, prestado mientras dura la llamada que lo entrega (normalmente es la
/// memoria mapeada de una textura <c>Staging</c>): quien lo recibe lo copia o lo escribe antes de volver.
/// </summary>
/// <remarks>
/// Cada plano empieza en su span y sus filas están separadas por su pitch, que puede ser mayor que la fila útil
/// (<see cref="ExportPixelFormats.RowBytes"/>). El último plano puede terminar justo tras su última fila útil.
/// </remarks>
public readonly ref struct ExportVideoFrame
{
    public ExportVideoFrame(long index, ExportVideoFormat format, ReadOnlySpan<byte> plane0, int pitch0,
        ReadOnlySpan<byte> plane1 = default, int pitch1 = 0, ReadOnlySpan<byte> plane2 = default, int pitch2 = 0,
        ReadOnlySpan<byte> plane3 = default, int pitch3 = 0)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        Index = index;
        Format = format;
        Plane0 = plane0;
        Plane1 = plane1;
        Plane2 = plane2;
        Plane3 = plane3;
        Pitch0 = pitch0;
        Pitch1 = pitch1;
        Pitch2 = pitch2;
        Pitch3 = pitch3;
        for (int plane = 0; plane < 4; plane++)
        {
            Validate(plane);
        }
    }

    /// <summary>Número del cuadro en la salida, desde 0 (no el de la secuencia si el rango no empieza en 0).</summary>
    public long Index { get; }

    public ExportVideoFormat Format { get; }

    public ReadOnlySpan<byte> Plane0 { get; }

    public ReadOnlySpan<byte> Plane1 { get; }

    public ReadOnlySpan<byte> Plane2 { get; }

    /// <summary>Alfa, en los formatos YUVA.</summary>
    public ReadOnlySpan<byte> Plane3 { get; }

    public int Pitch0 { get; }

    public int Pitch1 { get; }

    public int Pitch2 { get; }

    public int Pitch3 { get; }

    public int PlaneCount => Format.PixelFormat.PlaneCount();

    public ReadOnlySpan<byte> Plane(int plane) => plane switch
    {
        0 => Plane0,
        1 => Plane1,
        2 => Plane2,
        3 => Plane3,
        _ => throw new ArgumentOutOfRangeException(nameof(plane)),
    };

    public int Pitch(int plane) => plane switch
    {
        0 => Pitch0,
        1 => Pitch1,
        2 => Pitch2,
        3 => Pitch3,
        _ => throw new ArgumentOutOfRangeException(nameof(plane)),
    };

    /// <summary>Fila <paramref name="row"/> del plano, sin el relleno.</summary>
    public ReadOnlySpan<byte> Row(int plane, int row)
    {
        int rowBytes = Format.PixelFormat.RowBytes(plane, Format.Width, Format.Height);
        return Plane(plane).Slice(row * Pitch(plane), rowBytes);
    }

    /// <summary>Copia el cuadro sin relleno, plano tras plano, como lo espera <c>-f rawvideo</c>.</summary>
    /// <returns>Bytes escritos (<see cref="ExportVideoFormat.FrameBytes"/>).</returns>
    public int CopyPacked(Span<byte> destination)
    {
        int offset = 0;
        ExportPixelFormat pixels = Format.PixelFormat;
        for (int plane = 0; plane < PlaneCount; plane++)
        {
            int rows = pixels.PlaneSize(plane, Format.Width, Format.Height).Height;
            int rowBytes = pixels.RowBytes(plane, Format.Width, Format.Height);
            ReadOnlySpan<byte> source = Plane(plane);
            int pitch = Pitch(plane);
            if (pitch == rowBytes)
            {
                source[..(rowBytes * rows)].CopyTo(destination[offset..]);
                offset += rowBytes * rows;
                continue;
            }

            for (int y = 0; y < rows; y++)
            {
                source.Slice(y * pitch, rowBytes).CopyTo(destination[offset..]);
                offset += rowBytes;
            }
        }

        return offset;
    }

    private void Validate(int plane)
    {
        ExportPixelFormat pixels = Format.PixelFormat;
        if (plane >= pixels.PlaneCount())
        {
            return;
        }

        int rows = pixels.PlaneSize(plane, Format.Width, Format.Height).Height;
        int rowBytes = pixels.RowBytes(plane, Format.Width, Format.Height);
        int pitch = Pitch(plane);
        if (pitch < rowBytes)
        {
            throw new ArgumentException($"El pitch del plano {plane} ({pitch}) es menor que su fila útil ({rowBytes}).");
        }

        long needed = ((long)pitch * (rows - 1)) + rowBytes;
        if (Plane(plane).Length < needed)
        {
            throw new ArgumentException($"El plano {plane} trae {Plane(plane).Length} bytes y necesita {needed}.");
        }
    }
}
