using System.Globalization;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.Media.Probe;

namespace Rodaje.Export.Running;

/// <summary>La decisión de copiar el video sin recodificar («smart render») y por qué, en español.</summary>
/// <param name="Copy">El tramo que se copia, o <c>null</c> si se recodifica.</param>
/// <param name="Explanation">Lo que se le dice a la persona: por qué se copia o por qué no.</param>
public sealed record StreamCopyDecision(ExportStreamCopy? Copy, string Explanation)
{
    public bool Copies => Copy is not null;

    internal static StreamCopyDecision No(string why) => new(null, "Se recodifica: " + why + ".");
}

/// <summary>Lo que el planificador necesita leer de los archivos; en las pruebas, un doble.</summary>
public interface IStreamCopyMedia
{
    MediaInfo Probe(string path);

    StreamCopyCheck Inspect(string path, long firstFrame, long frameCount, CancellationToken cancellationToken);
}

/// <summary>
/// Decide si el video de una exportación se copia del original sin recodificar: todo el rango es un solo tramo sin cambios
/// de un archivo cuyo formato es el de la salida, y los cortes caen en keyframes. Se copia: misma calidad que el original
/// y segundos en lugar de minutos. El audio sigue saliendo de la mezcla.
/// </summary>
/// <remarks>
/// Solo se copia lo que daría el mismo resultado que recodificar a calidad constante: sin efectos, transiciones, velocidad,
/// títulos ni otras capas visibles; mismo tamaño, tasa, profundidad, croma y color; sin una tasa de bits, un perfil, un
/// nivel o un GOP pedidos a mano. Varios tramos seguidos del mismo archivo cuentan como uno.
/// </remarks>
public static class StreamCopyPlanner
{
    private static readonly IStreamCopyMedia Files = new FileMedia();

    public static StreamCopyDecision Decide(ExportPlan plan, CancellationToken cancellationToken = default) =>
        Decide(plan, Files, cancellationToken);

    public static StreamCopyDecision Decide(ExportPlan plan, IStreamCopyMedia media, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(media);
        ExportSettings settings = plan.Settings;
        if (settings.Video is not { } video || plan.Video is not { } format || plan.VideoEncoder is not { } encoder)
        {
            return StreamCopyDecision.No("no hay video");
        }

        if (!settings.SmartRender)
        {
            return StreamCopyDecision.No("la copia directa está desactivada");
        }

        if (SettingsProblem(video) is { } settingsProblem)
        {
            return StreamCopyDecision.No(settingsProblem);
        }

        Sequence sequence = plan.Request.Project.FindSequence(plan.Request.SequenceId)!;
        if (plan.ConvertsRate)
        {
            return StreamCopyDecision.No("la tasa de cuadros de la salida es otra");
        }

        (Clip? clip, MediaRef? mediaRef, string? timelineProblem) = SingleSource(plan, sequence);
        if (clip is null || mediaRef is null)
        {
            return StreamCopyDecision.No(timelineProblem ?? "la línea de tiempo no es un solo tramo sin cambios");
        }

        string path = Path.GetFullPath(mediaRef.Path, plan.Request.ProjectDirectory ?? Environment.CurrentDirectory);
        MediaInfo info;
        try
        {
            info = media.Probe(path);
        }
        catch (Exception ex) when (ex is Rodaje.Media.MediaException or IOException or UnauthorizedAccessException)
        {
            return StreamCopyDecision.No($"no se pudo leer {Path.GetFileName(path)}");
        }

        if (info.Video is not { } source)
        {
            return StreamCopyDecision.No("el clip no tiene video");
        }

        if (FormatProblem(source, encoder, format, sequence) is { } formatProblem)
        {
            return StreamCopyDecision.No(formatProblem);
        }

        Time sourceStart = clip.ToSourceTime(plan.Start);
        long first = sourceStart.ToFrames(source.FrameRate);
        if (Time.FromFrames(first, source.FrameRate) != sourceStart)
        {
            return StreamCopyDecision.No("el corte no cae justo en un cuadro del original");
        }

        StreamCopyCheck check;
        try
        {
            check = media.Inspect(path, first, plan.FrameCount, cancellationToken);
        }
        catch (Exception ex) when (ex is Rodaje.Media.MediaException or IOException or UnauthorizedAccessException)
        {
            return StreamCopyDecision.No($"no se pudieron leer los keyframes de {Path.GetFileName(path)}");
        }

        if (!check.CanCopy)
        {
            return StreamCopyDecision.No(check.Reason!);
        }

        var copy = new ExportStreamCopy(path, check.StreamIndex, check.SeekSeconds, ColorFilters(encoder.Codec, format.Color));
        return new StreamCopyDecision(copy, string.Create(CultureInfo.InvariantCulture,
            $"Copia directa: el tramo no tiene cambios y {Path.GetFileName(path)} ya es {ExportEncoderCatalog.DisplayName(encoder).Split(' ')[^1].Trim('(', ')')} "
            + $"{format.Width}×{format.Height} a la misma tasa, con cortes en keyframes; el video no se recodifica (misma calidad y mucho más rápido)."));
    }

    private static string? SettingsProblem(VideoExportSettings video)
    {
        if (video.FieldOrder != ExportFieldOrder.Progressive)
        {
            return "la salida es entrelazada";
        }

        RateControl rate = video.RateControl;
        if (rate.TargetSizeBytes is not null || rate.Passes > 1
            || rate.Mode is RateControlMode.Vbr or RateControlMode.Cbr or RateControlMode.Abr)
        {
            return "se pidió una tasa de bits o un tamaño concretos";
        }

        if (video.Profile is not null || video.Level is not null || video.Tune is not null || video.Gop != new GopSettings())
        {
            return "se pidió un perfil, un nivel o un GOP a mano";
        }

        if (video.Crop is not null || video.Alpha)
        {
            return "hay recorte o canal alfa";
        }

        return null;
    }

    private static (Clip? Clip, MediaRef? Media, string? Problem) SingleSource(ExportPlan plan, Sequence sequence)
    {
        Time start = plan.Start;
        Time end = plan.End;
        var pieces = new List<(Track Track, Clip Clip)>();
        foreach (Track track in sequence.VideoTracks.Where(t => !t.Hidden))
        {
            foreach (Clip clip in track.Clips.Where(c => c.Enabled && c.Start < end && c.End > start))
            {
                pieces.Add((track, clip));
            }
        }

        if (pieces.Count == 0)
        {
            return (null, null, "no hay ningún clip de video en el rango");
        }

        if (pieces.Select(p => p.Track).Distinct().Count() > 1)
        {
            return (null, null, "hay más de una capa de video");
        }

        Track only = pieces[0].Track;
        if (only.Effects.Any(e => e.Enabled))
        {
            return (null, null, "la pista tiene efectos");
        }

        if (only.Transitions.Count > 0 && pieces.Count > 1)
        {
            return (null, null, "hay transiciones");
        }

        pieces.Sort((a, b) => a.Clip.Start.CompareTo(b.Clip.Start));
        Clip head = pieces[0].Clip;
        for (int i = 0; i < pieces.Count; i++)
        {
            Clip clip = pieces[i].Clip;
            if (clip.Generator is not null || clip.IsCompound)
            {
                return (null, null, "hay títulos o clips compuestos");
            }

            if (clip.Speed is not null)
            {
                return (null, null, "un clip cambia de velocidad");
            }

            if (clip.Effects.Any(e => e.Enabled))
            {
                return (null, null, "un clip tiene efectos o ajustes de imagen");
            }

            // Tramos seguidos del mismo archivo y continuos en el original equivalen a uno solo.
            if (i > 0 && (clip.MediaId != head.MediaId || clip.Start != pieces[i - 1].Clip.End
                || clip.SourceIn != pieces[i - 1].Clip.SourceOut))
            {
                return (null, null, "el rango junta tramos de distintos archivos o con cortes");
            }
        }

        if (head.Start > start || pieces[^1].Clip.End < end)
        {
            return (null, null, "hay huecos en negro dentro del rango");
        }

        MediaRef? media = plan.Request.Project.FindMedia(head.MediaId);
        return media is null || string.IsNullOrEmpty(media.Path)
            ? (null, null, "el clip no tiene archivo")
            : (head, media, null);
    }

    private static string? FormatProblem(VideoStreamInfo source, EncoderSpec encoder, ExportVideoFormat format, Sequence sequence)
    {
        ExportCodec? codec = source.Codec switch
        {
            "h264" => ExportCodec.H264,
            "hevc" => ExportCodec.Hevc,
            "av1" => ExportCodec.Av1,
            "vp9" => ExportCodec.Vp9,
            "prores" => ExportCodec.ProRes,
            "dnxhd" => ExportCodec.DnxHr,
            "ffv1" => ExportCodec.Ffv1,
            _ => null,
        };
        if (codec != encoder.Codec)
        {
            return $"el original es {source.Codec} y la salida {ExportEncoderCatalog.DisplayName(encoder)}";
        }

        if (source.Width != format.Width || source.Height != format.Height || sequence.Width != format.Width || sequence.Height != format.Height
            || source.Rotation != 0 || source.SampleAspectRatio != new Rational(1, 1))
        {
            return "el tamaño de la salida no es el del original";
        }

        if (source.IsVariableFrameRate || source.FrameRate != format.FrameRate)
        {
            return "la tasa de cuadros del original es otra o variable";
        }

        int chroma = HardwareDecodeSupport.ChromaOf(source.PixelFormat);
        if (source.BitDepth != format.PixelFormat.BitDepth() || chroma != ChromaNumber(format.PixelFormat.Chroma()))
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"el original es de {source.BitDepth} bits {chroma / 100}:{chroma / 10 % 10}:{chroma % 10} y la salida de {format.PixelFormat.BitDepth()} bits");
        }

        bool sourceBt709 = source.Color.Matrix == Rodaje.Media.Video.YuvMatrix.Bt709;
        if (sourceBt709 != (format.Color.Matrix == ExportMatrix.Bt709) || source.Color.FullRange != format.Color.FullRange
            || source.Color.Matrix == Rodaje.Media.Video.YuvMatrix.Bt2020)
        {
            return "el color del original no es el de la salida";
        }

        return null;
    }

    private static int ChromaNumber(ChromaSubsampling chroma) => chroma switch
    {
        ChromaSubsampling.Chroma422 => 422,
        ChromaSubsampling.Chroma444 => 444,
        _ => 420,
    };

    // El original puede venir sin etiquetas de color; la vista previa lo interpretó con las de la salida y el verificador
    // las exige, así que se escriben en el flujo sin recodificar (H.264 y HEVC; los demás las llevan en el contenedor).
    private static IReadOnlyList<string> ColorFilters(ExportCodec codec, ExportColor color)
    {
        string? filter = codec switch
        {
            ExportCodec.H264 => "h264_metadata",
            ExportCodec.Hevc => "hevc_metadata",
            _ => null,
        };
        if (filter is null)
        {
            return [];
        }

        int matrix = color.Matrix == ExportMatrix.Bt709 ? 1 : 6;
        int transfer = color.Matrix == ExportMatrix.Bt709 ? 1 : 6;
        return [string.Create(CultureInfo.InvariantCulture,
            $"{filter}=colour_primaries={matrix}:transfer_characteristics={transfer}:matrix_coefficients={matrix}:video_full_range_flag={(color.FullRange ? 1 : 0)}")];
    }

    private sealed class FileMedia : IStreamCopyMedia
    {
        public MediaInfo Probe(string path) => MediaProbe.Probe(path);

        public StreamCopyCheck Inspect(string path, long firstFrame, long frameCount, CancellationToken cancellationToken) =>
            StreamCopySource.Inspect(path, firstFrame, frameCount, cancellationToken);
    }
}
