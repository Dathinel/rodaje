using Rodaje.Core.Timing;
using Rodaje.Media.Export;

namespace Rodaje.Export;

/// <summary>Cómo se reparte el equipo mientras exporta. Se elige en el diálogo cada vez (Daniel, 11-oct; D-028).</summary>
public enum ExportPerformance
{
    /// <summary>«Bajo rendimiento (app más fluida)»: prioridad baja, se puede seguir editando.</summary>
    Background,

    /// <summary>«Alto rendimiento (menor tiempo)»: prioridad normal/alta y la vista previa en pausa o al mínimo.</summary>
    Fastest,
}

/// <summary>Cómo se pasa de la tasa de la secuencia a otra.</summary>
public enum FrameRateConversion
{
    /// <summary>Cada cuadro de salida toma el instante más cercano (repite o descarta).</summary>
    Nearest,

    /// <summary>Mezcla los dos cuadros vecinos según la distancia.</summary>
    Blend,
}

/// <summary>Control de tasa. Qué modos admite cada codificador lo dice <see cref="EncoderSpec.RateControls"/>.</summary>
public sealed record RateControl
{
    public RateControlMode Mode { get; init; } = RateControlMode.ConstantQuality;

    /// <summary>CRF, CQ o <c>q</c> en la escala del codificador (<see cref="EncoderSpec.Quality"/>); <c>null</c> usa su valor por defecto.</summary>
    public double? Quality { get; init; }

    /// <summary>Tasa media (VBR, ABR) o fija (CBR), en kbps. En audio, la tasa del códec.</summary>
    public int? BitrateKbps { get; init; }

    /// <summary>Tope de VBR, en kbps.</summary>
    public int? MaxBitrateKbps { get; init; }

    /// <summary>Búfer del decodificador (VBV), en kbps; <c>null</c> lo calcula el codificador.</summary>
    public int? BufferKbps { get; init; }

    /// <summary>1 o 2. Con 2 el video se compone dos veces (la tubería no guarda los cuadros).</summary>
    public int Passes { get; init; } = 1;

    /// <summary>Tamaño objetivo del archivo; si está, la tasa media se calcula con <see cref="BitrateForTargetSize"/>.</summary>
    public long? TargetSizeBytes { get; init; }

    public static RateControl ConstantQuality(double? quality = null) => new() { Quality = quality };

    public static RateControl Vbr(int averageKbps, int maxKbps) => new() { Mode = RateControlMode.Vbr, BitrateKbps = averageKbps, MaxBitrateKbps = maxKbps };

    public static RateControl Cbr(int kbps) => new() { Mode = RateControlMode.Cbr, BitrateKbps = kbps, MaxBitrateKbps = kbps };

    public static RateControl TargetSize(long bytes) => new() { Mode = RateControlMode.Abr, Passes = 2, TargetSizeBytes = bytes };

    public static RateControl Lossless { get; } = new() { Mode = RateControlMode.Lossless };

    public static RateControl ProfileDefined { get; } = new() { Mode = RateControlMode.ProfileDefined };

    /// <summary>
    /// Tasa de video en kbps para que el archivo mida <paramref name="targetBytes"/>: lo que queda tras el audio, menos un
    /// 2 % para el contenedor. Nunca menos de 1 kbps.
    /// </summary>
    public static int BitrateForTargetSize(long targetBytes, TimeSpan duration, int audioKbps)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetBytes);
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "La duración debe ser positiva.");
        }

        double totalKbps = targetBytes * 8 * 0.98 / 1000 / duration.TotalSeconds;
        return (int)Math.Max(1, Math.Floor(totalKbps - audioKbps));
    }
}

/// <summary>Grupo de imágenes. Sin efecto en los códecs solo intra (<see cref="EncoderSpec.IntraOnly"/>).</summary>
public sealed record GopSettings
{
    /// <summary>Un cuadro clave cada tantos segundos; <c>null</c> usa el valor del codificador. Gana sobre <see cref="Length"/> si están los dos.</summary>
    public double? KeyframeSeconds { get; init; } = 2;

    /// <summary>Longitud en cuadros; <c>null</c> usa <see cref="KeyframeSeconds"/>.</summary>
    public int? Length { get; init; }

    /// <summary>GOP cerrado (cada cuadro clave es IDR): cortes limpios en plataformas y editores.</summary>
    public bool Closed { get; init; } = true;

    /// <summary>Cuadros B seguidos; <c>null</c> usa el valor del codificador.</summary>
    public int? BFrames { get; init; }

    /// <summary>Cuadros de referencia; <c>null</c> usa el valor del codificador.</summary>
    public int? References { get; init; }

    /// <summary>Longitud en cuadros a la tasa de salida.</summary>
    public int? LengthAt(Rational frameRate) =>
        KeyframeSeconds is { } seconds ? Math.Max(1, (int)Math.Round(seconds * frameRate.ToDouble())) : Length;
}

/// <summary>Región de la secuencia que se exporta, en píxeles de la secuencia (recorte espacial).</summary>
public readonly record struct ExportCrop(int X, int Y, int Width, int Height);

/// <summary>Normalización de sonoridad tras una medición previa.</summary>
/// <param name="Lufs">Objetivo integrado: −14 para plataformas, −23 para EBU R128, −16 para podcasts.</param>
/// <param name="MaxTruePeakDbtp">Pico verdadero máximo.</param>
public sealed record LoudnessTarget(double Lufs, double MaxTruePeakDbtp = -1)
{
    public static LoudnessTarget Platforms { get; } = new(-14);

    public static LoudnessTarget EbuR128 { get; } = new(-23);
}

/// <summary>Ajustes del video de la salida.</summary>
public sealed record VideoExportSettings
{
    public required ExportCodec Codec { get; init; }

    /// <summary>Codificador concreto (<see cref="EncoderSpec.Id"/>); <c>null</c> elige el mejor que pase la sonda entre los de por defecto.</summary>
    public string? EncoderId { get; init; }

    public RateControl RateControl { get; init; } = RateControl.ConstantQuality();

    /// <summary><see cref="EncoderProfile.Id"/>; <c>null</c> usa el primero que admita el formato de píxel.</summary>
    public string? Profile { get; init; }

    /// <summary>Nivel; <c>null</c> lo elige el codificador.</summary>
    public string? Level { get; init; }

    /// <summary>Preset de velocidad (<see cref="EncoderSpec.SpeedPresets"/>); <c>null</c> usa el de por defecto.</summary>
    public string? SpeedPreset { get; init; }

    public string? Tune { get; init; }

    public GopSettings Gop { get; init; } = new();

    /// <summary>8 o 10 (16 solo en imágenes PNG/TIFF).</summary>
    public int BitDepth { get; init; } = 8;

    public ChromaSubsampling Chroma { get; init; } = ChromaSubsampling.Chroma420;

    /// <summary>Exporta con canal alfa (fondo transparente donde no hay capas).</summary>
    public bool Alpha { get; init; }

    /// <summary><c>null</c>: BT.709 limitado (BT.601 en SD, sRGB en imágenes), según <see cref="ExportColor.For"/>.</summary>
    public ExportColor? Color { get; init; }

    /// <summary><c>null</c>: la tasa de la secuencia.</summary>
    public Rational? FrameRate { get; init; }

    public FrameRateConversion FrameRateConversion { get; init; } = FrameRateConversion.Nearest;

    /// <summary>Tamaño de salida; <c>null</c> en los dos: el de la secuencia. Con uno solo, el otro sigue la forma de la secuencia.</summary>
    public int? Width { get; init; }

    public int? Height { get; init; }

    public ExportFit Fit { get; init; } = ExportFit.Bars;

    public ExportScaleFilter ScaleFilter { get; init; } = ExportScaleFilter.Auto;

    /// <summary>Recorte de la secuencia antes de escalar; <c>null</c> sin recorte.</summary>
    public ExportCrop? Crop { get; init; }
}

/// <summary>Ajustes del audio de la salida.</summary>
public sealed record AudioExportSettings
{
    public required ExportCodec Codec { get; init; }

    /// <summary>Codificador concreto; <c>null</c> elige (para AAC, <c>aac_mf</c> antes que <c>aac</c>).</summary>
    public string? EncoderId { get; init; }

    /// <summary>Tasa (<see cref="RateControlMode.Cbr"/>/<see cref="RateControlMode.Vbr"/>), calidad o sin pérdidas.</summary>
    public RateControl RateControl { get; init; } = new() { Mode = RateControlMode.Cbr, BitrateKbps = 192 };

    /// <summary>Perfil (AAC LC o HE); <c>null</c> el de por defecto.</summary>
    public string? Profile { get; init; }

    public int SampleRate { get; init; } = 48_000;

    /// <summary>1, 2 o 6 (5.1, si el proyecto lo tiene).</summary>
    public int Channels { get; init; } = 2;

    /// <summary>Muestras de PCM (16, 24 o 32 bits, o float); ignorado en los códecs comprimidos.</summary>
    public ExportSampleFormat PcmFormat { get; init; } = ExportSampleFormat.Pcm24;

    /// <summary><c>null</c>: sin normalizar.</summary>
    public LoudnessTarget? Loudness { get; init; }
}

/// <summary>
/// Todo lo que el usuario elige para una exportación, salvo dónde y qué rango (eso va en <see cref="ExportRequest"/>).
/// Es lo que guarda un preajuste.
/// </summary>
public sealed record ExportSettings
{
    public ExportContainer Container { get; init; } = ExportContainer.Mp4;

    /// <summary><c>null</c>: solo audio.</summary>
    public VideoExportSettings? Video { get; init; }

    /// <summary><c>null</c>: sin audio.</summary>
    public AudioExportSettings? Audio { get; init; }

    public ExportMetadata Metadata { get; init; } = ExportMetadata.Empty;

    /// <summary>Capítulos desde los marcadores de la secuencia (en los contenedores que los admiten).</summary>
    public bool ChaptersFromMarkers { get; init; }

    /// <summary><c>moov</c> al principio en MP4/MOV, para reproducir mientras descarga.</summary>
    public bool FastStart { get; init; } = true;

    public ExportPerformance Performance { get; init; } = ExportPerformance.Background;
}

/// <summary>Rango de la secuencia que se exporta, <c>[Start, End)</c>.</summary>
public readonly record struct ExportRange(Time Start, Time End)
{
    public Time Duration => End - Start;
}

/// <summary>Una exportación concreta: qué secuencia, adónde, qué rango y con qué ajustes.</summary>
/// <param name="Project">Copia del proyecto (<c>Project.Clone()</c>); la edición puede seguir.</param>
/// <param name="Range"><c>null</c>: la secuencia entera.</param>
public sealed record ExportRequest(Rodaje.Core.Model.Project Project, string SequenceId, string OutputPath, ExportSettings Settings,
    ExportRange? Range = null)
{
    /// <summary>Carpeta del proyecto, para resolver rutas relativas de medios.</summary>
    public string? ProjectDirectory { get; init; }
}
