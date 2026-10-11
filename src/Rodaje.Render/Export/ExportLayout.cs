using Rodaje.Media.Export;

namespace Rodaje.Render.Export;

/// <summary>Qué escribe el shader en un plano; los valores son los de <c>mode.x</c> en <see cref="ExportShaders"/>.</summary>
internal enum PlaneKind : uint
{
    Luma = 0,

    /// <summary>Cb y Cr entrelazados (NV12, P010).</summary>
    ChromaPairs = 1,
    Cb = 2,
    Cr = 3,
    Alpha = 4,
    Rgb24 = 5,
    Rgba8 = 6,
    Rgba16 = 7,
}

/// <summary>
/// Un plano dentro del búfer que baja a la CPU. El shader escribe palabras de 4 bytes, así que cada hilo llena una
/// «unidad» (4 u 2 muestras, un par de croma, 4 píxeles RGB en 12 bytes) y el pitch es un múltiplo de ella.
/// </summary>
internal readonly record struct ExportPlane(PlaneKind Kind, int Offset, int Pitch, int Rows, int RowBytes, int Units, int UnitBytes)
{
    public int Bytes => Pitch * Rows;
}

/// <summary>Planos del cuadro, uno tras otro en un búfer lineal (sin el pitch de una textura).</summary>
internal sealed class ExportLayout
{
    private const int PlaneAlignment = 16;

    public ExportLayout(ExportVideoFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        Format = format;
        ExportPixelFormat pixels = format.PixelFormat;
        PlaneKind[] kinds = Kinds(pixels);
        var planes = new ExportPlane[kinds.Length];
        int offset = 0;
        int bytesPerSample = pixels.BytesPerSample();
        for (int i = 0; i < kinds.Length; i++)
        {
            (int samples, int rows) = pixels.PlaneSize(i, format.Width, format.Height);
            int rowBytes = pixels.RowBytes(i, format.Width, format.Height);
            (int units, int unitBytes) = kinds[i] switch
            {
                PlaneKind.Rgb24 => ((format.Width + 3) / 4, 12),
                PlaneKind.Rgba8 => (format.Width, 4),
                PlaneKind.Rgba16 => (format.Width, 8),
                _ => (((samples * bytesPerSample) + 3) / 4, 4),
            };
            int pitch = units * unitBytes;
            planes[i] = new ExportPlane(kinds[i], offset, pitch, rows, rowBytes, units, unitBytes);
            offset = checked(offset + (pitch * rows) + PlaneAlignment - 1) / PlaneAlignment * PlaneAlignment;
        }

        Planes = planes;
        TotalBytes = offset;
    }

    public ExportVideoFormat Format { get; }

    public IReadOnlyList<ExportPlane> Planes { get; }

    /// <summary>Bytes del búfer, múltiplo de 16.</summary>
    public int TotalBytes { get; }

    /// <summary>Si las filas no llevan relleno: el búfer es ya lo que pide <c>-f rawvideo</c> (salvo la alineación entre planos).</summary>
    public bool IsTight => Planes.All(p => p.Pitch == p.RowBytes);

    public static PlaneKind[] Kinds(ExportPixelFormat format) => format switch
    {
        ExportPixelFormat.Rgb24 => [PlaneKind.Rgb24],
        ExportPixelFormat.Rgba => [PlaneKind.Rgba8],
        ExportPixelFormat.Rgba64 => [PlaneKind.Rgba16],
        ExportPixelFormat.Nv12 or ExportPixelFormat.P010 => [PlaneKind.Luma, PlaneKind.ChromaPairs],
        ExportPixelFormat.Yuva420P or ExportPixelFormat.Yuva444P10 => [PlaneKind.Luma, PlaneKind.Cb, PlaneKind.Cr, PlaneKind.Alpha],
        _ => [PlaneKind.Luma, PlaneKind.Cb, PlaneKind.Cr],
    };

    /// <summary>Envuelve el búfer ya bajado como el cuadro del contrato.</summary>
    public ExportVideoFrame Frame(long index, ReadOnlySpan<byte> buffer)
    {
        IReadOnlyList<ExportPlane> p = Planes;
        return p.Count switch
        {
            1 => new ExportVideoFrame(index, Format, Slice(buffer, p[0]), p[0].Pitch),
            2 => new ExportVideoFrame(index, Format, Slice(buffer, p[0]), p[0].Pitch, Slice(buffer, p[1]), p[1].Pitch),
            3 => new ExportVideoFrame(index, Format, Slice(buffer, p[0]), p[0].Pitch, Slice(buffer, p[1]), p[1].Pitch,
                Slice(buffer, p[2]), p[2].Pitch),
            _ => new ExportVideoFrame(index, Format, Slice(buffer, p[0]), p[0].Pitch, Slice(buffer, p[1]), p[1].Pitch,
                Slice(buffer, p[2]), p[2].Pitch, Slice(buffer, p[3]), p[3].Pitch),
        };
    }

    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> buffer, ExportPlane plane) => buffer.Slice(plane.Offset, plane.Bytes);
}
