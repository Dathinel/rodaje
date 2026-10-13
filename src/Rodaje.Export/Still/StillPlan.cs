using System.Globalization;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Export;

namespace Rodaje.Export.Still;

/// <summary>Un fotograma que exportar: el cuadro de la secuencia que está en <paramref name="Position"/> (el cursor).</summary>
/// <param name="Project">Una copia del proyecto (<see cref="Project.Clone"/>): se lee en otro hilo.</param>
/// <param name="SequenceId">La secuencia.</param>
/// <param name="Position">Instante del cursor; sale el cuadro que se está mostrando ahí (redondeo hacia abajo, como el visor).</param>
/// <param name="OutputPath">Archivo final, con la extensión del formato (<c>.png</c>, o <c>.jpg</c>/<c>.jpeg</c>; si no, <see cref="StillPlan.Create"/> lo rechaza); se escribe en un temporal y se renombra al terminar.</param>
/// <param name="Settings">Formato, calidad, alfa y tamaño.</param>
public sealed record StillRequest(Project Project, string SequenceId, Time Position, string OutputPath, StillSettings Settings)
{
    /// <summary>Carpeta del proyecto, para resolver rutas relativas de medios.</summary>
    public string? ProjectDirectory { get; init; }

    /// <summary>«Exportar sin él»: la LUT, la transición, el efecto o la fuente que falta se omite, como en la vista previa.</summary>
    public bool ContinueWithoutMissing { get; init; }
}

/// <summary>Lo que dejó la exportación de un fotograma.</summary>
/// <param name="OutputPath">El archivo final.</param>
/// <param name="Bytes">Su tamaño.</param>
/// <param name="Frame">El cuadro de la secuencia que se exportó.</param>
/// <param name="Width">Ancho de la imagen.</param>
/// <param name="Height">Alto de la imagen.</param>
public sealed record StillResult(string OutputPath, long Bytes, long Frame, int Width, int Height);

/// <summary>
/// Lo que se decide sin GPU ni procesos: el cuadro, el tamaño, el formato de píxel que baja la GPU y la línea de
/// <c>ffmpeg.exe</c> (codificador, calidad y conversión de color) del fotograma.
/// </summary>
/// <remarks>
/// <para>
/// La GPU baja siempre RGB (D-028: RGB para imágenes y alfa), en <see cref="ExportPixelFormat.Rgba"/> con alfa recto o en
/// <see cref="ExportPixelFormat.Rgb24"/>, por el mismo <c>ExportFrameConverter</c> que la exportación de video.
/// </para>
/// <para>
/// JPEG: el JPEG/JFIF que leen todos los visores es Y'CbCr BT.601 de rango completo. El codificador <c>mjpeg</c> del
/// catálogo recibe Y'CbCr limitado con <c>-strict unofficial</c> (bien para un video MJPEG, mal para una foto: se vería
/// lavada), así que aquí recibe RGB y <c>ffmpeg.exe</c> convierte con <c>scale</c> a BT.601 completo en 4:4:4 (sin
/// submuestrear el croma, que en los bordes de los títulos se nota), etiquetado <c>pc</c>/<c>bt470bg</c>.
/// </para>
/// </remarks>
public sealed record StillPlan
{
    /// <summary>Calidad de <c>-q:v</c> de <c>mjpeg</c> con calidad 100 (la mejor que da sin cuantizar de más).</summary>
    public const int BestJpegScale = 2;

    /// <summary>Calidad de <c>-q:v</c> de <c>mjpeg</c> con calidad 1.</summary>
    public const int WorstJpegScale = 31;

    private StillPlan(long frame, int sequenceWidth, int sequenceHeight, ExportVideoFormat output, EncoderSpec encoder,
        IReadOnlyList<string> arguments, IReadOnlyList<string> filters, StillSettings settings)
    {
        Frame = frame;
        SequenceWidth = sequenceWidth;
        SequenceHeight = sequenceHeight;
        Output = output;
        Encoder = encoder;
        Arguments = arguments;
        Filters = filters;
        Settings = settings;
    }

    /// <summary>Cuadro de la secuencia (sin recortar al final; eso lo hace quien conoce la duración).</summary>
    public long Frame { get; }

    public int SequenceWidth { get; }

    public int SequenceHeight { get; }

    /// <summary>Tamaño y formato de píxel que baja la GPU y recibe <c>ffmpeg.exe</c>.</summary>
    public ExportVideoFormat Output { get; }

    /// <summary><c>png</c> o <c>mjpeg</c>, del catálogo, adaptado a recibir RGB.</summary>
    public EncoderSpec Encoder { get; }

    /// <summary>Argumentos tras <c>-c:v</c>: calidad y color de la salida.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Filtros de <c>ffmpeg.exe</c>: la conversión a Y'CbCr de JPEG; vacío en PNG.</summary>
    public IReadOnlyList<string> Filters { get; }

    public StillSettings Settings { get; }

    public bool HasAlpha => Output.PixelFormat.HasAlpha();

    /// <summary>Decide el fotograma de <paramref name="request"/>.</summary>
    /// <exception cref="ExportException"><see cref="ExportErrorCode.InvalidSettings"/> con el motivo en español.</exception>
    public static StillPlan Create(StillRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        StillSettings settings = request.Settings ?? throw new ArgumentException("Faltan los ajustes del fotograma.", nameof(request));
        Sequence sequence = request.Project.Sequences.FirstOrDefault(s => s.Id == request.SequenceId)
            ?? throw new ExportException(ExportErrorCode.InvalidSettings, $"No existe la secuencia «{request.SequenceId}».");
        var problems = new List<string>(settings.Problems());
        if (string.IsNullOrWhiteSpace(request.OutputPath))
        {
            problems.Add("Falta el archivo de destino.");
        }
        else if (StillSettings.ExtensionProblem(request.OutputPath, settings.Format) is { } extension)
        {
            problems.Add(extension);
        }

        if (settings.Crop is { } crop && ((long)crop.X + crop.Width > sequence.Width || (long)crop.Y + crop.Height > sequence.Height))
        {
            problems.Add($"El recorte se sale de la secuencia de {sequence.Width}×{sequence.Height}.");
        }

        if (request.Position < Time.Zero)
        {
            problems.Add("El cursor está antes del inicio de la secuencia.");
        }

        if (problems.Count > 0)
        {
            throw new ExportException(ExportErrorCode.InvalidSettings, string.Join(" ", problems));
        }

        // El mismo cálculo del tamaño que la exportación; con un códec RGB no se fuerzan medidas pares.
        (int width, int height) = new VideoExportSettings
        {
            Codec = ExportCodec.Png, Width = settings.Width, Height = settings.Height, Crop = settings.Crop,
        }.OutputSize(sequence.Width, sequence.Height);
        if (width > ExportVideoFormat.MaxDimension || height > ExportVideoFormat.MaxDimension)
        {
            throw new ExportException(ExportErrorCode.InvalidSettings,
                $"La imagen saldría de {width}×{height}; el máximo es {ExportVideoFormat.MaxDimension} por lado.");
        }

        bool alpha = settings.Alpha && settings.Format == StillFormat.Png;
        ExportPixelFormat pixels = alpha ? ExportPixelFormat.Rgba : ExportPixelFormat.Rgb24;
        var output = new ExportVideoFormat(width, height, sequence.FrameRate, pixels, ExportColor.Srgb);
        long frame = request.Position.ToFrames(sequence.FrameRate);
        return settings.Format == StillFormat.Jpeg
            ? new StillPlan(frame, sequence.Width, sequence.Height, output, JpegEncoder,
                ["-q:v", JpegScale(settings.JpegQuality).ToString(CultureInfo.InvariantCulture), "-color_range", "pc", "-colorspace", "bt470bg"],
                ["scale=out_color_matrix=bt601:out_range=full", "format=yuv444p"], settings)
            : new StillPlan(frame, sequence.Width, sequence.Height, output, PngEncoder, [], [], settings);
    }

    /// <summary>
    /// Calidad de 1 a 100 a la escala <c>-q:v</c> de <c>mjpeg</c> (31 a 2, menor es mejor), lineal y redondeada:
    /// 100 → 2, 92 → 4, 75 → 9, 50 → 17, 1 → 31.
    /// </summary>
    public static int JpegScale(int quality)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(quality, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quality, 100);
        double scale = WorstJpegScale - ((quality - 1) * (WorstJpegScale - BestJpegScale) / 99.0);
        return (int)Math.Round(scale, MidpointRounding.AwayFromZero);
    }

    /// <summary>El <c>png</c> del catálogo: ya recibe RGB y RGBA.</summary>
    internal static EncoderSpec PngEncoder { get; } = ExportEncoderCatalog.Find("png")
        ?? throw new InvalidOperationException("El catálogo no tiene el codificador png.");

    /// <summary>El <c>mjpeg</c> del catálogo recibiendo RGB, sin <c>-strict unofficial</c>: la conversión la hacen los filtros.</summary>
    internal static EncoderSpec JpegEncoder { get; } = (ExportEncoderCatalog.Find("mjpeg")
        ?? throw new InvalidOperationException("El catálogo no tiene el codificador mjpeg.")) with
    {
        PixelFormats = [ExportPixelFormat.Rgb24],
        BaseArguments = [],
    };
}
