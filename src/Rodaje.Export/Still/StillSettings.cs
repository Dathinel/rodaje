using Rodaje.Media.Export;

namespace Rodaje.Export.Still;

/// <summary>Formato del archivo de un fotograma exportado.</summary>
public enum StillFormat
{
    /// <summary>Sin pérdidas; con alfa si se pide (<see cref="StillSettings.Alpha"/>).</summary>
    Png,

    /// <summary>Con pérdidas y calidad elegible (<see cref="StillSettings.JpegQuality"/>); nunca con alfa.</summary>
    Jpeg,
}

/// <summary>
/// Cómo se exporta un fotograma: formato, calidad, alfa y tamaño. El tamaño se decide como en la exportación de video
/// (<see cref="VideoExportSettings.OutputSize"/>): el de la secuencia si no se pide otro, o el de un preajuste con
/// <see cref="FromPreset"/>, con el mismo ajuste de forma (barras, recorte o estirado) y el mismo filtro de escala.
/// </summary>
public sealed record StillSettings
{
    /// <summary>Calidad JPEG por omisión: alta, sin el tamaño de un JPEG al 100 %.</summary>
    public const int DefaultJpegQuality = 92;

    public StillFormat Format { get; init; } = StillFormat.Png;

    /// <summary>Calidad JPEG de 1 (peor) a 100 (mejor); se traduce a la escala <c>-q:v</c> de FFmpeg (31 a 2).</summary>
    public int JpegQuality { get; init; } = DefaultJpegQuality;

    /// <summary>
    /// PNG con canal alfa: la composición sale con fondo transparente donde no hay capas (como el alfa de
    /// <see cref="VideoExportSettings.Alpha"/>). Sin él, el fondo es negro, como en la vista previa.
    /// </summary>
    public bool Alpha { get; init; }

    /// <summary>Tamaño de salida; <c>null</c> en los dos: el de la secuencia. Con uno solo, el otro sigue la forma de la secuencia.</summary>
    public int? Width { get; init; }

    public int? Height { get; init; }

    public ExportFit Fit { get; init; } = ExportFit.Bars;

    public ExportScaleFilter ScaleFilter { get; init; } = ExportScaleFilter.Auto;

    /// <summary>Recorte de la secuencia antes de escalar; <c>null</c> sin recorte.</summary>
    public ExportCrop? Crop { get; init; }

    /// <summary>El tamaño, la forma, el filtro, el recorte y el alfa del video de <paramref name="preset"/>.</summary>
    /// <remarks>El alfa solo se toma en PNG; en JPEG no existe.</remarks>
    public static StillSettings FromPreset(ExportPreset preset, StillFormat format = StillFormat.Png)
    {
        ArgumentNullException.ThrowIfNull(preset);
        VideoExportSettings? video = preset.Settings.Video;
        return new StillSettings
        {
            Format = format,
            Width = video?.Width,
            Height = video?.Height,
            Fit = video?.Fit ?? ExportFit.Bars,
            ScaleFilter = video?.ScaleFilter ?? ExportScaleFilter.Auto,
            Crop = video?.Crop,
            Alpha = format == StillFormat.Png && video?.Alpha == true,
        };
    }

    /// <summary>El formato que corresponde a la extensión de <paramref name="path"/>: JPEG con <c>.jpg</c> o <c>.jpeg</c>, PNG en lo demás.</summary>
    public static StillFormat FormatFor(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string extension = Path.GetExtension(path);
        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            ? StillFormat.Jpeg
            : StillFormat.Png;
    }

    /// <summary>Extensión sin punto de cada formato.</summary>
    public static string Extension(StillFormat format) => format == StillFormat.Jpeg ? "jpg" : "png";

    /// <summary>Lo que no se puede exportar con estos ajustes, en español; vacío si todo está bien.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (JpegQuality is < 1 or > 100)
        {
            problems.Add($"La calidad JPEG va de 1 a 100 y se pidió {JpegQuality}.");
        }

        if (Alpha && Format == StillFormat.Jpeg)
        {
            problems.Add("JPEG no guarda transparencia: elige PNG para exportar con alfa.");
        }

        if (Width is <= 0 or > ExportVideoFormat.MaxDimension || Height is <= 0 or > ExportVideoFormat.MaxDimension)
        {
            problems.Add($"El tamaño de la imagen debe estar entre 1 y {ExportVideoFormat.MaxDimension} píxeles por lado.");
        }

        if (Crop is { } crop && (crop.Width <= 0 || crop.Height <= 0 || crop.X < 0 || crop.Y < 0))
        {
            problems.Add("El recorte debe tener ancho y alto positivos y empezar dentro de la secuencia.");
        }

        return problems;
    }
}
