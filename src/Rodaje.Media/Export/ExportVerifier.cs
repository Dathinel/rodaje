using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Rodaje.Core.Timing;
using Rodaje.Media.Proxies;

namespace Rodaje.Media.Export;

/// <summary>
/// La pista de video de un archivo exportado según ffprobe. <c>FrameCount</c> son los paquetes contados (uno por cuadro);
/// <c>MaxGop</c>, los cuadros entre dos cuadros clave, el mayor (el último tramo cuenta hasta el final).
/// </summary>
public sealed record ExportVideoReport(
    string Codec,
    string? Profile,
    int Width,
    int Height,
    string PixelFormat,
    string? ColorSpace,
    string? ColorTransfer,
    string? ColorPrimaries,
    string? ColorRange,
    string? ChromaLocation,
    Rational? FrameRate,
    long FrameCount,
    double StartSeconds,
    double DurationSeconds,
    int MaxGop,
    int KeyframeCount,
    bool HasBFrames);

/// <summary>La pista de audio de un archivo exportado según ffprobe.</summary>
public sealed record ExportAudioReport(string Codec, string? Profile, int SampleRate, int Channels, double StartSeconds, double DurationSeconds);

/// <summary>
/// Lo que ffprobe y la lectura de las cajas dicen de un archivo exportado. <c>MoovAtStart</c>: en MP4/MOV, si <c>moov</c>
/// va antes de <c>mdat</c>; <c>null</c> en otros contenedores.
/// </summary>
public sealed record ExportReport(
    string FormatName,
    double DurationSeconds,
    long Bytes,
    bool? MoovAtStart,
    ExportVideoReport? Video,
    ExportAudioReport? Audio,
    int Chapters,
    IReadOnlyDictionary<string, string> Tags);

/// <summary>Lo que se espera de un archivo exportado. Lo que es <c>null</c> no se comprueba.</summary>
public sealed record ExportExpectation
{
    public string? VideoCodec { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public string? PixelFormat { get; init; }

    public long? FrameCount { get; init; }

    public Rational? FrameRate { get; init; }

    /// <summary>Etiquetas de color: matriz, curva, primarios, rango y, en 4:2:0, croma «left».</summary>
    public ExportColor? Color { get; init; }

    /// <summary>Cuadros entre cuadros clave como máximo.</summary>
    public int? MaxGop { get; init; }

    public string? AudioCodec { get; init; }

    public int? SampleRate { get; init; }

    public int? Channels { get; init; }

    /// <summary>Duración del audio en segundos (±1 cuadro de audio de 1024 muestras más el retardo del códec).</summary>
    public double? AudioDurationSeconds { get; init; }

    /// <summary>En MP4/MOV, <c>moov</c> antes de <c>mdat</c>.</summary>
    public bool FastStart { get; init; }

    /// <summary>Diferencia máxima entre el inicio del video y el del audio, y de cada uno con 0.</summary>
    public double StartToleranceSeconds { get; init; } = 0.001;

    public int? Chapters { get; init; }
}

/// <summary>
/// Comprueba con <c>ffprobe.exe</c> (el de la misma compilación LGPL que <c>ffmpeg.exe</c>) que un archivo exportado es
/// lo que se pidió: flujos, duración, etiquetas de color, cuadros clave, inicio de audio y video y <c>moov</c> al principio.
/// </summary>
public sealed class ExportVerifier
{
    public ExportVerifier(string ffprobePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffprobePath);
        FfprobePath = Path.GetFullPath(ffprobePath);
    }

    public string FfprobePath { get; }

    /// <summary>El <c>ffprobe.exe</c> junto al <c>ffmpeg.exe</c> verificado.</summary>
    public static ExportVerifier For(FfmpegExecutable ffmpeg)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        return new ExportVerifier(Path.Combine(Path.GetDirectoryName(ffmpeg.Path)!, "ffprobe.exe"));
    }

    /// <exception cref="ExportException"><see cref="ExportErrorCode.EncoderFailed"/> si ffprobe no puede leer el archivo.</exception>
    public ExportReport Inspect(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new ExportException(ExportErrorCode.EncoderFailed, $"No existe el archivo exportado {full}.");
        }

        string json = Run(["-v", "error", "-count_packets", "-show_format", "-show_streams", "-show_chapters", "-of", "json", full],
            cancellationToken);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        JsonElement format = root.GetProperty("format");
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (format.TryGetProperty("tags", out JsonElement formatTags))
        {
            foreach (JsonProperty tag in formatTags.EnumerateObject())
            {
                tags[tag.Name] = tag.Value.GetString() ?? "";
            }
        }

        ExportVideoReport? video = null;
        ExportAudioReport? audio = null;
        foreach (JsonElement stream in root.GetProperty("streams").EnumerateArray())
        {
            string type = String(stream, "codec_type") ?? "";
            bool attached = stream.TryGetProperty("disposition", out JsonElement disposition)
                && disposition.TryGetProperty("attached_pic", out JsonElement pic) && pic.GetInt32() == 1;
            if (type == "video" && video is null && !attached)
            {
                int index = stream.GetProperty("index").GetInt32();
                (int maxGop, int keyframes) = Gop(full, index, cancellationToken);
                video = new ExportVideoReport(
                    String(stream, "codec_name") ?? "",
                    String(stream, "profile"),
                    Int(stream, "width"),
                    Int(stream, "height"),
                    String(stream, "pix_fmt") ?? "",
                    String(stream, "color_space"),
                    String(stream, "color_transfer"),
                    String(stream, "color_primaries"),
                    String(stream, "color_range"),
                    String(stream, "chroma_location"),
                    ParseRate(String(stream, "r_frame_rate")),
                    Long(stream, "nb_read_packets"),
                    Double(stream, "start_time"),
                    Double(stream, "duration") is var d && d > 0 ? d : Double(format, "duration"),
                    maxGop,
                    keyframes,
                    Int(stream, "has_b_frames") > 0);
            }
            else if (type == "audio" && audio is null)
            {
                audio = new ExportAudioReport(
                    String(stream, "codec_name") ?? "",
                    String(stream, "profile"),
                    int.TryParse(String(stream, "sample_rate"), CultureInfo.InvariantCulture, out int rate) ? rate : 0,
                    Int(stream, "channels"),
                    Double(stream, "start_time"),
                    Double(stream, "duration") is var d && d > 0 ? d : Double(format, "duration"));
            }
        }

        string formatName = String(format, "format_name") ?? "";
        int chapters = root.TryGetProperty("chapters", out JsonElement list) ? list.GetArrayLength() : 0;
        return new ExportReport(formatName, Double(format, "duration"), new FileInfo(full).Length, MoovBeforeMdat(full), video, audio, chapters, tags);
    }

    /// <summary>Lo que no cumple, en español; vacía si el archivo es lo esperado.</summary>
    public IReadOnlyList<string> Check(string path, ExportExpectation expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return Check(Inspect(path, cancellationToken), expected);
    }

    /// <summary>Compara un informe con lo esperado.</summary>
    public static IReadOnlyList<string> Check(ExportReport report, ExportExpectation expected)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(expected);
        var problems = new List<string>();
        bool expectsVideo = expected.VideoCodec is not null || expected.FrameCount is not null || expected.Width is not null;
        if (expectsVideo && report.Video is null)
        {
            problems.Add("El archivo no tiene video.");
        }

        if (report.Video is { } v)
        {
            Same(problems, "códec de video", expected.VideoCodec, v.Codec);
            Same(problems, "ancho", expected.Width, v.Width);
            Same(problems, "alto", expected.Height, v.Height);
            Same(problems, "formato de píxel", expected.PixelFormat, v.PixelFormat);
            Same(problems, "cuadros", expected.FrameCount, v.FrameCount);
            if (expected.FrameRate is { } rate && v.FrameRate != rate)
            {
                problems.Add($"La tasa es {v.FrameRate} y se esperaba {rate}.");
            }

            if (expected.Color is { } color)
            {
                Same(problems, "matriz de color", color.FfmpegColorspace, v.ColorSpace == "bt470bg" ? "smpte170m" : v.ColorSpace);
                Same(problems, "curva de color", SameCurve(color.FfmpegTransfer), SameCurve(v.ColorTransfer));
                Same(problems, "primarios", color.FfmpegPrimaries, v.ColorPrimaries);
                Same(problems, "rango de color", color.FfmpegRange, v.ColorRange);
                if (v.PixelFormat.Contains("420", StringComparison.Ordinal) || v.PixelFormat is "nv12" or "p010le")
                {
                    Same(problems, "posición del croma", ExportColor.ChromaLocation, v.ChromaLocation);
                }
            }

            if (expected.MaxGop is { } gop && v.MaxGop > gop)
            {
                problems.Add($"Hay {v.MaxGop} cuadros entre dos cuadros clave y el máximo es {gop}.");
            }

            if (Math.Abs(v.StartSeconds) > expected.StartToleranceSeconds)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"El video empieza en {v.StartSeconds:0.######} s y no en 0."));
            }
        }

        if (expected.AudioCodec is not null && report.Audio is null)
        {
            problems.Add("El archivo no tiene audio.");
        }

        if (report.Audio is { } a)
        {
            Same(problems, "códec de audio", expected.AudioCodec, a.Codec);
            Same(problems, "frecuencia", expected.SampleRate, a.SampleRate);
            Same(problems, "canales", expected.Channels, a.Channels);
            if (expected.AudioDurationSeconds is { } duration)
            {
                double tolerance = (2048.0 / Math.Max(8000, a.SampleRate)) + 0.001;
                if (Math.Abs(a.DurationSeconds - duration) > tolerance)
                {
                    problems.Add(string.Create(CultureInfo.InvariantCulture,
                        $"El audio dura {a.DurationSeconds:0.###} s y se esperaba {duration:0.###} s."));
                }
            }

            if (Math.Abs(a.StartSeconds) > expected.StartToleranceSeconds)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"El audio empieza en {a.StartSeconds:0.######} s y no en 0."));
            }

            if (report.Video is { } video && Math.Abs(a.StartSeconds - video.StartSeconds) > expected.StartToleranceSeconds)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture,
                    $"Audio y video empiezan separados {Math.Abs(a.StartSeconds - video.StartSeconds) * 1000:0.###} ms."));
            }
        }

        if (expected.FastStart && report.MoovAtStart != true)
        {
            problems.Add(report.MoovAtStart is null ? "El contenedor no es MP4/MOV y no tiene moov." : "El índice (moov) está al final: no se puede reproducir mientras descarga.");
        }

        Same(problems, "capítulos", expected.Chapters, report.Chapters);
        return problems;
    }

    /// <summary>En MP4/MOV, si la caja <c>moov</c> va antes de <c>mdat</c>; <c>null</c> si no es ISO BMFF.</summary>
    public static bool? MoovBeforeMdat(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Span<byte> header = stackalloc byte[16];
        long position = 0;
        bool sawFtyp = false;
        while (position + 8 <= stream.Length)
        {
            stream.Position = position;
            if (stream.Read(header[..8]) < 8)
            {
                break;
            }

            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            string type = Encoding.ASCII.GetString(header.Slice(4, 4));
            if (size == 1)
            {
                if (stream.Read(header.Slice(8, 8)) < 8)
                {
                    break;
                }

                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.Slice(8, 8));
            }
            else if (size == 0)
            {
                size = stream.Length - position;
            }

            switch (type)
            {
                case "ftyp":
                    sawFtyp = true;
                    break;
                case "moov":
                    return true;
                case "mdat":
                    return sawFtyp ? false : null;
            }

            if (size < 8)
            {
                break;
            }

            position += size;
        }

        return null;
    }

    private (int MaxGop, int Keyframes) Gop(string path, int streamIndex, CancellationToken cancellationToken)
    {
        string csv = Run(["-v", "error", "-select_streams", streamIndex.ToString(CultureInfo.InvariantCulture),
            "-show_entries", "packet=flags", "-of", "csv=p=0", path], cancellationToken);
        int maxGop = 0;
        int run = 0;
        int keyframes = 0;
        foreach (string line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Contains('K', StringComparison.Ordinal))
            {
                keyframes++;
                maxGop = Math.Max(maxGop, run);
                run = 1;
            }
            else
            {
                run++;
            }
        }

        return (Math.Max(maxGop, run), keyframes);
    }

    private string Run(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(FfprobePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ExportException(ExportErrorCode.EncoderMissing, $"No se pudo ejecutar {FfprobePath}: {ex.Message}", ex);
        }

        ChildProcessJob.TryAssign(process);
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
            }
        });
        Task<string> error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            throw new ExportException(ExportErrorCode.EncoderFailed,
                $"ffprobe no pudo leer el archivo exportado: {FfmpegMessages.Detail(error.GetAwaiter().GetResult())}");
        }

        return output;
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

    private static long Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value)
            ? value.ValueKind == JsonValueKind.Number ? value.GetInt64()
            : long.TryParse(value.GetString(), CultureInfo.InvariantCulture, out long parsed) ? parsed : 0
            : 0;

    private static double Double(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : 0;

    private static Rational? ParseRate(string? text)
    {
        if (text is null)
        {
            return null;
        }

        string[] parts = text.Split('/');
        return parts.Length == 2 && long.TryParse(parts[0], CultureInfo.InvariantCulture, out long num)
            && long.TryParse(parts[1], CultureInfo.InvariantCulture, out long den) && num > 0 && den > 0
            ? new Rational(num, den)
            : null;
    }

    private static void Same(List<string> problems, string what, string? expected, string? actual)
    {
        if (expected is not null && !string.Equals(expected, actual, StringComparison.Ordinal))
        {
            problems.Add($"{Capital(what)}: «{actual}»; se esperaba «{expected}».");
        }
    }

    private static void Same<T>(List<string> problems, string what, T? expected, T actual)
        where T : struct
    {
        if (expected is { } value && !EqualityComparer<T>.Default.Equals(value, actual))
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{Capital(what)}: {actual}; se esperaba {value}."));
        }
    }

    // Nombres distintos para lo mismo: la matriz BT.601 de 625 y 525 líneas es igual (MXF solo escribe la de 625), y la
    // curva de BT.601 es la de BT.709.
    private static string? SameCurve(string? name) => name is "smpte170m" ? "bt709" : name;

    private static string Capital(string text) => char.ToUpperInvariant(text[0]) + text[1..];
}
