namespace Rodaje.Media.Export;

/// <summary>Matriz de R'G'B' a Y'CbCr. En los formatos RGB no se usa.</summary>
public enum ExportMatrix
{
    Bt709,

    /// <summary>BT.601 (SMPTE 170M), solo para SD.</summary>
    Bt601,
}

/// <summary>Curva con que se etiqueta la salida. La imagen es la misma: la del video SDR; solo cambia la etiqueta.</summary>
public enum ExportTransfer
{
    Bt709,

    /// <summary>sRGB, para secuencias de imágenes, GIF y WebP.</summary>
    Srgb,
}

/// <summary>Color de la salida: matriz, rango, curva y croma. Primarios siempre BT.709 (SDR, sin HDR).</summary>
/// <param name="Matrix">Matriz de Y'CbCr.</param>
/// <param name="FullRange">Rango completo (0–255) en vez del limitado (16–235 / 16–240). Por defecto, limitado.</param>
/// <param name="Transfer">Curva que se declara.</param>
public readonly record struct ExportColor(ExportMatrix Matrix, bool FullRange = false, ExportTransfer Transfer = ExportTransfer.Bt709)
{
    /// <summary>Lo de siempre en HD y 4K: BT.709 de rango limitado.</summary>
    public static ExportColor Bt709Limited { get; } = new(ExportMatrix.Bt709);

    /// <summary>Imágenes RGB: rango completo y curva sRGB.</summary>
    public static ExportColor Srgb { get; } = new(ExportMatrix.Bt709, FullRange: true, ExportTransfer.Srgb);

    /// <summary>Alto máximo que se considera SD (576 filas, PAL).</summary>
    public const int MaxStandardDefinitionHeight = 576;

    /// <summary>BT.709 limitado, o BT.601 limitado si el cuadro es SD; sRGB si el formato es RGB.</summary>
    public static ExportColor For(int width, int height, ExportPixelFormat format = ExportPixelFormat.Nv12) =>
        format.IsRgb() ? Srgb
        : height <= MaxStandardDefinitionHeight && width <= 1024 ? new(ExportMatrix.Bt601)
        : Bt709Limited;

    /// <summary>Croma 4:2:0 situado a la izquierda (cosituado en horizontal), como MPEG-2, H.264 y HEVC.</summary>
    public static string ChromaLocation => "left";

    public string FfmpegColorspace => Matrix == ExportMatrix.Bt709 ? "bt709" : "smpte170m";

    public string FfmpegPrimaries => Matrix == ExportMatrix.Bt709 ? "bt709" : "smpte170m";

    public string FfmpegTransfer => Transfer == ExportTransfer.Srgb ? "iec61966-2-1" : Matrix == ExportMatrix.Bt709 ? "bt709" : "smpte170m";

    public string FfmpegRange => FullRange ? "pc" : "tv";

    /// <summary>Argumentos de <c>ffmpeg.exe</c> que etiquetan la entrada y la salida con este color.</summary>
    public IReadOnlyList<string> FfmpegArguments() =>
    [
        "-colorspace", FfmpegColorspace, "-color_primaries", FfmpegPrimaries, "-color_trc", FfmpegTransfer,
        "-color_range", FfmpegRange, "-chroma_sample_location", ChromaLocation,
    ];
}
