using Rodaje.Core.Timing;

namespace Rodaje.Media.Export;

/// <summary>Contenedor del archivo exportado. Qué códecs admite cada uno está en <see cref="ExportCompatibility"/>.</summary>
public enum ExportContainer
{
    /// <summary>MP4 con <c>+faststart</c>. El de por defecto.</summary>
    Mp4,
    Mov,
    Mkv,
    WebM,
    Avi,

    /// <summary>MXF OP1a.</summary>
    Mxf,

    /// <summary>MPEG-TS.</summary>
    MpegTs,

    /// <summary>GIF animado.</summary>
    Gif,

    /// <summary>WebP animado.</summary>
    WebP,

    /// <summary>Un archivo por cuadro (PNG, TIFF, JPEG o EXR) en una carpeta, con número de 6 cifras.</summary>
    ImageSequence,

    /// <summary>Solo audio AAC o ALAC.</summary>
    M4a,
    Mp3,
    Flac,
    Wav,

    /// <summary>Solo audio Opus.</summary>
    Ogg,
    Aiff,
}

/// <summary>Cómo cabe la secuencia en un cuadro de otra forma (por ejemplo 16:9 en 9:16).</summary>
public enum ExportFit
{
    /// <summary>Entera, con barras negras (por defecto).</summary>
    Bars,

    /// <summary>Llena el cuadro y recorta lo que sobra.</summary>
    Crop,

    /// <summary>Deforma la imagen hasta llenar el cuadro.</summary>
    Stretch,
}

/// <summary>Filtro con que se cambia de tamaño en la GPU.</summary>
public enum ExportScaleFilter
{
    /// <summary>El mip 1 para reducir a la mitad exacta y Catmull-Rom en lo demás (D-028).</summary>
    Auto,
    Bilinear,
    CatmullRom,
    Lanczos,
}

/// <summary>Muestras del audio que se entrega al sink (el WAV temporal).</summary>
public enum ExportSampleFormat
{
    PcmFloat,
    Pcm16,
    Pcm24,
    Pcm32,
}

/// <summary>Video que recibe el sink: tamaño, tasa constante, formato de píxel y color.</summary>
public sealed record ExportVideoFormat
{
    public ExportVideoFormat(int width, int height, Rational frameRate, ExportPixelFormat pixelFormat, ExportColor color)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, MaxDimension);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, MaxDimension);
        if ((width % 2 != 0 && pixelFormat.NeedsEvenWidth()) || (height % 2 != 0 && pixelFormat.HalfHeightChroma()))
        {
            throw new ArgumentException(
                $"El croma submuestreado de {pixelFormat.FfmpegName()} pide medidas pares y se recibió {width}×{height}.", nameof(width));
        }

        Width = width;
        Height = height;
        FrameRate = frameRate;
        PixelFormat = pixelFormat;
        Color = color;
    }

    /// <summary>Lado máximo de una textura de Direct3D 11.</summary>
    public const int MaxDimension = 16384;

    public int Width { get; }

    public int Height { get; }

    /// <summary>Tasa constante: el cuadro <c>n</c> se muestra en <c>n / FrameRate</c>.</summary>
    public Rational FrameRate { get; }

    public ExportPixelFormat PixelFormat { get; }

    public ExportColor Color { get; }

    /// <summary>Bytes de un cuadro sin relleno.</summary>
    public long FrameBytes => PixelFormat.FrameBytes(Width, Height);
}

/// <summary>Audio del WAV temporal que recibe el sink. El codificador lo convierte a su formato.</summary>
public sealed record ExportAudioFormat
{
    public ExportAudioFormat(int sampleRate = 48_000, int channels = 2, ExportSampleFormat sampleFormat = ExportSampleFormat.PcmFloat)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8_000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sampleRate, 192_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, 8);
        SampleRate = sampleRate;
        Channels = channels;
        SampleFormat = sampleFormat;
    }

    /// <summary>La mezcla de Rodaje: estéreo float a 48 kHz.</summary>
    public static ExportAudioFormat Default { get; } = new();

    public int SampleRate { get; }

    /// <summary>1 mono, 2 estéreo, 6 para 5.1 (orden de WAVE: L R C LFE Ls Rs).</summary>
    public int Channels { get; }

    public ExportSampleFormat SampleFormat { get; }

    public int BytesPerSample => SampleFormat switch
    {
        ExportSampleFormat.Pcm16 => 2,
        ExportSampleFormat.Pcm24 => 3,
        _ => 4,
    };
}

/// <summary>Un capítulo del archivo (por ejemplo, desde un marcador), en el tiempo de la salida.</summary>
public sealed record ExportChapter(Time Start, Time End, string Title);

/// <summary>Una pista de subtítulos que va en el archivo como flujo aparte, con los tiempos de la salida.</summary>
public sealed record ExportSubtitleTrack(string Name, IReadOnlyList<Rodaje.Core.Editing.SubtitleCue> Cues);

/// <summary>Metadatos que se escriben en el contenedor, si los admite.</summary>
public sealed record ExportMetadata
{
    public static ExportMetadata Empty { get; } = new();

    public string? Title { get; init; }

    public string? Author { get; init; }

    public string? Comment { get; init; }

    /// <summary>Fecha de creación; <c>null</c> para no escribirla (los archivos salen reproducibles).</summary>
    public DateTimeOffset? Date { get; init; }

    public IReadOnlyList<ExportChapter> Chapters { get; init; } = [];

    /// <summary>Subtítulos que se activan en el reproductor (MP4 y MOV con <c>mov_text</c>, MKV con SRT, WebM con WebVTT).</summary>
    public IReadOnlyList<ExportSubtitleTrack> Subtitles { get; init; } = [];

    /// <summary>Imagen (PNG o JPEG) para incrustar como portada; <c>null</c> sin portada.</summary>
    public string? ThumbnailPath { get; init; }
}
