using System.Globalization;
using Rodaje.Core.Model;

namespace Rodaje.Media.Export;

/// <summary>Matriz de R'G'B' a Y'CbCr y, con ella, los primarios. En los formatos RGB no se usa.</summary>
public enum ExportMatrix
{
    Bt709,

    /// <summary>BT.601 (SMPTE 170M), solo para SD.</summary>
    Bt601,

    /// <summary>BT.2020 no constante, solo con las curvas HDR.</summary>
    Bt2020,
}

/// <summary>Curva de la salida. En SDR la imagen es la misma, solo cambia la etiqueta; PQ y HLG la codifican desde la composición.</summary>
public enum ExportTransfer
{
    Bt709,

    /// <summary>sRGB, para secuencias de imágenes, GIF y WebP.</summary>
    Srgb,

    /// <summary>ST 2084 (HDR10), con metadatos de pantalla de masterizado y de nivel de luz.</summary>
    Pq,

    /// <summary>ARIB STD-B67, sin metadatos de masterizado.</summary>
    Hlg,
}

/// <summary>Color de la salida: matriz y primarios, rango, curva y croma.</summary>
/// <param name="Matrix">Matriz de Y'CbCr (y primarios: BT.2020 solo en HDR).</param>
/// <param name="FullRange">Rango completo (0–255) en vez del limitado (16–235 / 16–240). Por defecto, limitado.</param>
/// <param name="Transfer">Curva que se declara (SDR) o se codifica (PQ, HLG).</param>
/// <param name="MaxCll">HDR10: nivel máximo de luz del contenido en nits; 0 lo deja sin declarar.</param>
/// <param name="MaxFall">HDR10: media por cuadro máxima en nits; 0 lo deja sin declarar.</param>
public readonly record struct ExportColor(ExportMatrix Matrix, bool FullRange = false, ExportTransfer Transfer = ExportTransfer.Bt709,
    int MaxCll = 0, int MaxFall = 0)
{
    /// <summary>Lo de siempre en HD y 4K: BT.709 de rango limitado.</summary>
    public static ExportColor Bt709Limited { get; } = new(ExportMatrix.Bt709);

    /// <summary>Imágenes RGB: rango completo y curva sRGB.</summary>
    public static ExportColor Srgb { get; } = new(ExportMatrix.Bt709, FullRange: true, ExportTransfer.Srgb);

    /// <summary>HDR10: BT.2020 limitado con PQ.</summary>
    public static ExportColor Hdr10 { get; } = new(ExportMatrix.Bt2020, Transfer: ExportTransfer.Pq);

    /// <summary>BT.2020 limitado con HLG.</summary>
    public static ExportColor Hlg { get; } = new(ExportMatrix.Bt2020, Transfer: ExportTransfer.Hlg);

    /// <summary>Alto máximo que se considera SD (576 filas, PAL).</summary>
    public const int MaxStandardDefinitionHeight = 576;

    /// <summary>Pantalla de masterizado de HDR10 por defecto: primarios BT.2020, blanco D65, 1000 y 0,0001 nits (unidades de ST 2086).</summary>
    public const string DefaultMasteringDisplay = "G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1)";

    /// <summary>
    /// BT.709 limitado, o BT.601 limitado si el cuadro es SD; sRGB si el formato es RGB. Una secuencia HDR sale en PQ o HLG
    /// solo si el formato es Y'CbCr de 10 bits o más; en 8 bits o RGB se queda en SDR.
    /// </summary>
    public static ExportColor For(int width, int height, ExportPixelFormat format = ExportPixelFormat.Nv12,
        SequenceColorSpace colorSpace = SequenceColorSpace.Sdr709) =>
        format.IsRgb() ? Srgb
        : colorSpace != SequenceColorSpace.Sdr709 && format.BitDepth() >= 10 ? (colorSpace == SequenceColorSpace.HdrPq ? Hdr10 : Hlg)
        : height <= MaxStandardDefinitionHeight && width <= 1024 ? new(ExportMatrix.Bt601)
        : Bt709Limited;

    /// <summary>Croma 4:2:0 situado a la izquierda (cosituado en horizontal), como MPEG-2, H.264 y HEVC.</summary>
    public static string ChromaLocation => "left";

    public bool IsHdr => Transfer is ExportTransfer.Pq or ExportTransfer.Hlg;

    public string FfmpegColorspace => Matrix switch { ExportMatrix.Bt709 => "bt709", ExportMatrix.Bt2020 => "bt2020nc", _ => "smpte170m" };

    public string FfmpegPrimaries => Matrix switch { ExportMatrix.Bt709 => "bt709", ExportMatrix.Bt2020 => "bt2020", _ => "smpte170m" };

    public string FfmpegTransfer => Transfer switch
    {
        ExportTransfer.Srgb => "iec61966-2-1",
        ExportTransfer.Pq => "smpte2084",
        ExportTransfer.Hlg => "arib-std-b67",
        _ => Matrix == ExportMatrix.Bt709 ? "bt709" : "smpte170m",
    };

    public string FfmpegRange => FullRange ? "pc" : "tv";

    /// <summary>Argumentos de <c>ffmpeg.exe</c> que etiquetan la entrada y la salida con este color.</summary>
    public IReadOnlyList<string> FfmpegArguments() =>
    [
        "-colorspace", FfmpegColorspace, "-color_primaries", FfmpegPrimaries, "-color_trc", FfmpegTransfer,
        "-color_range", FfmpegRange, "-chroma_sample_location", ChromaLocation,
    ];

    /// <summary>
    /// Metadatos HDR10 de los cuadros de entrada (opciones de entrada de <c>ffmpeg.exe</c>): los codificadores los escriben como
    /// SEI y los contenedores como <c>mdcv</c>/<c>clli</c>. Vacío fuera de PQ; HLG no lleva pantalla de masterizado.
    /// </summary>
    public IReadOnlyList<string> FfmpegHdrInputArguments()
    {
        if (Transfer != ExportTransfer.Pq)
        {
            return [];
        }

        List<string> args = ["-mastering_display", DefaultMasteringDisplay];
        if (MaxCll > 0 || MaxFall > 0)
        {
            args.AddRange(["-content_light", string.Create(CultureInfo.InvariantCulture, $"{MaxCll},{MaxFall}")]);
        }

        return args;
    }
}
