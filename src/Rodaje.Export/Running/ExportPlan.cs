using System.Globalization;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Export;

namespace Rodaje.Export.Running;

/// <summary>
/// Lo que se decide antes de tocar la GPU o lanzar procesos: codificadores, formatos, cuadros de la salida, argumentos,
/// capítulos y lo que se comprobará al final. Puro y sin efectos: se prueba sin hardware.
/// </summary>
public sealed record ExportPlan
{
    public required ExportRequest Request { get; init; }

    public required Rational SequenceRate { get; init; }

    public required Rational OutputRate { get; init; }

    /// <summary>Primer cuadro del rango en la rejilla de <see cref="OutputRate"/>.</summary>
    public required long FirstFrame { get; init; }

    /// <summary>Cuadros de la salida; con solo audio, los que habría a <see cref="OutputRate"/>.</summary>
    public required long FrameCount { get; init; }

    public EncoderSpec? VideoEncoder { get; init; }

    public ExportVideoFormat? Video { get; init; }

    public EncoderSpec? AudioEncoder { get; init; }

    /// <summary>Formato del WAV temporal de la mezcla (float, a la frecuencia y canales de la salida).</summary>
    public ExportAudioFormat? Audio { get; init; }

    public required EncoderArguments Arguments { get; init; }

    public required ExportMetadata Metadata { get; init; }

    public IReadOnlyList<string> VideoFilters { get; init; } = [];

    /// <summary>Inicio del rango, alineado a un cuadro de la salida.</summary>
    public Time Start => Time.FromFrames(FirstFrame, OutputRate);

    /// <summary>Fin del rango: video y audio duran exactamente <see cref="FrameCount"/> cuadros.</summary>
    public Time End => Time.FromFrames(FirstFrame + FrameCount, OutputRate);

    public ExportSettings Settings => Request.Settings;

    /// <summary>Si la tasa de salida es otra que la de la secuencia.</summary>
    public bool ConvertsRate => OutputRate != SequenceRate;

    /// <exception cref="ExportException">
    /// <see cref="ExportErrorCode.InvalidSettings"/> (rango vacío, ajustes imposibles) o
    /// <see cref="ExportErrorCode.EncoderMissing"/> (ningún codificador del códec pasó la sonda).
    /// </exception>
    public static ExportPlan Create(ExportRequest request, IReadOnlyList<EncoderSpec> available)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(available);
        ExportSettings settings = request.Settings;
        if (settings.Video is null && settings.Audio is null)
        {
            throw Invalid("No hay ni video ni audio que exportar.");
        }

        Sequence sequence = request.Project.FindSequence(request.SequenceId)
            ?? throw Invalid($"La secuencia {request.SequenceId} no existe.");
        if (settings.Video?.Crop is { } crop && (crop.X + crop.Width > sequence.Width || crop.Y + crop.Height > sequence.Height))
        {
            throw Invalid($"El recorte se sale de la secuencia de {sequence.Width}×{sequence.Height}.");
        }

        Time duration = sequence.Duration;
        ExportRange range = request.Range ?? new ExportRange(Time.Zero, duration);
        Time end = range.End > duration ? duration : range.End;
        if (range.Start < Time.Zero || end <= range.Start)
        {
            throw Invalid("El rango que se exporta está vacío.");
        }

        Rational output = settings.Video?.FrameRate ?? sequence.FrameRate;
        long first = CeilFrame(range.Start, output);
        long last = CeilFrame(end, output);
        if (last <= first)
        {
            throw Invalid("El rango es más corto que un cuadro.");
        }

        EncoderSpec? video = settings.Video is { } v ? Choose(v.Codec, v.EncoderId, available) : null;
        EncoderSpec? audio = settings.Audio is { } a ? Choose(a.Codec, a.EncoderId, available) : null;
        IReadOnlyList<ExportProblem> problems = ExportSettingsValidator.Validate(settings, video, audio);
        if (problems.Count > 0)
        {
            throw Invalid(string.Join(" ", problems.Select(p => p.Message)));
        }

        ExportVideoFormat? format = null;
        IReadOnlyList<string> filters = [];
        if (settings.Video is { } vs && video is not null)
        {
            ExportPixelFormat pixels = video.PixelFormatFor(vs.BitDepth, vs.Chroma, vs.Alpha)!.Value;
            (int width, int height) = vs.OutputSize(sequence.Width, sequence.Height);
            format = new ExportVideoFormat(width, height, output, pixels, vs.Color ?? ExportColor.For(width, height, pixels));
            if (video.Codec == ExportCodec.Gif)
            {
                filters = ["split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=3"];
            }
        }

        ExportAudioFormat? wave = settings.Audio is { } au ? new ExportAudioFormat(au.SampleRate, au.Channels) : null;
        long count = last - first;
        TimeSpan seconds = TimeSpan.FromSeconds(count / output.ToDouble());
        EncoderArguments arguments = FfmpegArguments.Build(settings, video, format?.PixelFormat, audio, output, seconds);
        ExportMetadata metadata = settings.Metadata;
        if (settings.ChaptersFromMarkers && FfmpegPipeSinkOptions.AllowsChapters(settings.Container))
        {
            metadata = metadata with { Chapters = Chapters(sequence.Markers, Time.FromFrames(first, output), Time.FromFrames(last, output)) };
        }

        return new ExportPlan
        {
            Request = request,
            SequenceRate = sequence.FrameRate,
            OutputRate = output,
            FirstFrame = first,
            FrameCount = count,
            VideoEncoder = video,
            Video = format,
            AudioEncoder = audio,
            Audio = wave,
            Arguments = arguments,
            Metadata = metadata,
            VideoFilters = filters,
        };
    }

    /// <summary>
    /// El codificador de un códec: el pedido si pasó la sonda; si no se pidió ninguno, el primero de los de por defecto
    /// (la lista va en orden de preferencia: GPU primero; en AAC, <c>aac_mf</c> antes que <c>aac</c>).
    /// </summary>
    public static EncoderSpec Choose(ExportCodec codec, string? encoderId, IReadOnlyList<EncoderSpec> available)
    {
        ArgumentNullException.ThrowIfNull(available);
        if (encoderId is not null)
        {
            return available.FirstOrDefault(e => e.Id == encoderId)
                ?? throw new ExportException(ExportErrorCode.EncoderMissing, $"El codificador {encoderId} no funciona en este equipo.") { Culprit = encoderId };
        }

        return available.FirstOrDefault(e => e.Codec == codec && e.InDefaults)
            ?? throw new ExportException(ExportErrorCode.EncoderMissing, $"Ningún codificador de {codec} funciona en este equipo.");
    }

    /// <summary>
    /// Capítulos desde los marcadores que caen en <c>[start, end)</c>, relativos al inicio: cada uno dura hasta el siguiente
    /// y el último hasta el final. Si el primero no está al principio, se añade «Inicio» (las plataformas lo exigen).
    /// </summary>
    public static IReadOnlyList<ExportChapter> Chapters(IEnumerable<Marker> markers, Time start, Time end)
    {
        ArgumentNullException.ThrowIfNull(markers);
        List<(Time Time, string Name)> points = [.. markers
            .Where(m => m.Time >= start && m.Time < end)
            .OrderBy(m => m.Time)
            .Select(m => (m.Time - start, m.Name))];
        if (points.Count == 0)
        {
            return [];
        }

        if (points[0].Time > Time.Zero)
        {
            points.Insert(0, (Time.Zero, "Inicio"));
        }

        Time length = end - start;
        var chapters = new List<ExportChapter>(points.Count);
        for (int i = 0; i < points.Count; i++)
        {
            Time to = i + 1 < points.Count ? points[i + 1].Time : length;
            if (to <= points[i].Time)
            {
                continue;
            }

            string name = string.IsNullOrWhiteSpace(points[i].Name)
                ? string.Create(CultureInfo.InvariantCulture, $"Capítulo {chapters.Count + 1}")
                : points[i].Name.Trim();
            chapters.Add(new ExportChapter(points[i].Time, to, name));
        }

        return chapters;
    }

    /// <summary>Lo que el verificador debe encontrar en el archivo terminado; <c>null</c> si no se verifica (imágenes, GIF).</summary>
    public ExportExpectation? Expectation()
    {
        ExportContainer container = Settings.Container;
        if (container is ExportContainer.ImageSequence or ExportContainer.Gif or ExportContainer.WebP)
        {
            return null;
        }

        bool video = Video is not null;
        return new ExportExpectation
        {
            Width = Video?.Width,
            Height = Video?.Height,
            FrameCount = video ? FrameCount : null,
            FrameRate = video ? OutputRate : null,
            Color = video && VideoEncoder!.Codec is not (ExportCodec.Png or ExportCodec.Tiff or ExportCodec.QtAnimation) ? Video!.Color : null,
            SampleRate = Audio?.SampleRate,
            Channels = Audio?.Channels,
            AudioDurationSeconds = Audio is null ? null : (End - Start).ToSeconds(),
            FastStart = Settings.FastStart && container is ExportContainer.Mp4 or ExportContainer.Mov or ExportContainer.M4a,
            Chapters = Metadata.Chapters.Count > 0 ? Metadata.Chapters.Count : null,
        };
    }

    // El primer cuadro que empieza en o después de time.
    private static long CeilFrame(Time time, Rational rate)
    {
        long frame = time.ToFrames(rate);
        return Time.FromFrames(frame, rate) < time ? frame + 1 : frame;
    }

    private static ExportException Invalid(string message) => new(ExportErrorCode.InvalidSettings, message);
}
