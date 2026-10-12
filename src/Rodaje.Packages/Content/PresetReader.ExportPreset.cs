using System.Globalization;
using System.Text.Json;

namespace Rodaje.Packages.Content;

/// <summary>Lectura de <c>rodaje.exportpreset</c> v1, con las mismas reglas que los otros preajustes.</summary>
internal static partial class PresetReader
{
    public const int MinExportSide = 16;
    public const int MaxExportSide = 16384;
    public const int MaxBitrateKbps = 2_000_000;

    private static readonly string[] ExportCategories = ["platform", "quality", "master", "web", "audio", "broadcast", "cinema", "archive"];

    public static ExportPresetDefinition ReadExportPreset(Stream stream)
    {
        using JsonDocument document = Parse(stream, ExportPresetDefinition.Format);
        JsonElement root = document.RootElement;
        Header(root, ExportPresetDefinition.Format, ExportPresetDefinition.Version);
        (string id, string name) = Identity(root);
        string description = String(root, "description") is { } d ? SafeText.Clean(d) : "";
        if (description.Length > SafeText.MaxDescription)
        {
            throw Fail($"description pasa de {SafeText.MaxDescription} caracteres.");
        }

        string category = String(root, "category") ?? "master";
        if (Array.IndexOf(ExportCategories, category) < 0)
        {
            throw Fail($"category «{Show(category)}» no existe: {string.Join(", ", ExportCategories)}.");
        }

        string container = Word(root, "container", "container", required: true)!;
        ExportPresetVideo? video = Section(root, "video") is { } v ? ExportVideo(v) : null;
        ExportPresetAudio? audio = Section(root, "audio") is { } a ? ExportAudio(a) : null;
        if (video is null && audio is null)
        {
            throw Fail("El preajuste necesita video, audio o los dos.");
        }

        return new ExportPresetDefinition(id, name, description, category, container)
        {
            Video = video,
            Audio = audio,
            FastStart = Flag(root, "fastStart", true),
            Chapters = Flag(root, "chapters", false),
        };
    }

    private static ExportPresetVideo ExportVideo(JsonElement v)
    {
        (int num, int den) = FrameRate(v);
        int? width = OptionalInt(v, "width", MinExportSide, MaxExportSide, "video.width");
        int? height = OptionalInt(v, "height", MinExportSide, MaxExportSide, "video.height");
        string rateMode = Choice(v, "rateControl", "video.rateControl", "crf", ["crf", "vbr", "cbr", "abr", "lossless", "profile"]);
        int? bitrate = OptionalInt(v, "bitrateKbps", 1, MaxBitrateKbps, "video.bitrateKbps");
        if (rateMode is "vbr" or "cbr" or "abr" && bitrate is null)
        {
            throw Fail($"video.bitrateKbps hace falta con rateControl «{rateMode}».");
        }

        int passes = OptionalInt(v, "passes", 1, 2, "video.passes") ?? 1;
        if (passes == 2 && rateMode is not ("vbr" or "abr"))
        {
            throw Fail("video.passes 2 solo vale con rateControl vbr o abr.");
        }

        string fieldOrder = Choice(v, "fieldOrder", "video.fieldOrder", "progressive", ["progressive", "tff", "bff"]);
        if (fieldOrder != "progressive" && num == 0)
        {
            throw Fail("Un video entrelazado necesita video.frameRate (la tasa de cuadros, no de campos).");
        }

        int depth = OptionalInt(v, "bitDepth", 8, 16, "video.bitDepth") ?? 8;
        if (depth is not (8 or 10 or 12 or 16))
        {
            throw Fail("video.bitDepth tiene que ser 8, 10, 12 o 16.");
        }

        return new ExportPresetVideo(Word(v, "codec", "video.codec", required: true)!)
        {
            Profile = Word(v, "profile", "video.profile", required: false, digitFirst: true),
            Level = Token(v, "level", "video.level"),
            SpeedPreset = Word(v, "speedPreset", "video.speedPreset", required: false),
            BitDepth = depth,
            Chroma = Choice(v, "chroma", "video.chroma", "420", ["420", "422", "444"]),
            Alpha = Flag(v, "alpha", false),
            Width = width,
            Height = height,
            FrameRateNumerator = num,
            FrameRateDenominator = den,
            FieldOrder = fieldOrder,
            Fit = Choice(v, "fit", "video.fit", "bars", ["bars", "crop", "stretch"]),
            RateMode = rateMode,
            Quality = OptionalNumber(v, "quality", 0, 100, "video.quality"),
            BitrateKbps = bitrate,
            MaxBitrateKbps = OptionalInt(v, "maxBitrateKbps", 1, MaxBitrateKbps, "video.maxBitrateKbps"),
            BufferKbps = OptionalInt(v, "bufferKbps", 1, MaxBitrateKbps, "video.bufferKbps"),
            Passes = passes,
            KeyframeSeconds = OptionalNumber(v, "keyframeSeconds", 0.01, 60, "video.keyframeSeconds"),
            ClosedGop = Flag(v, "closedGop", true),
            BFrames = OptionalInt(v, "bFrames", 0, 16, "video.bFrames"),
            Projection = Choice(v, "projection", "video.projection", "flat", ["flat", "equirectangular"]),
        };
    }

    private static ExportPresetAudio ExportAudio(JsonElement a)
    {
        int rate = OptionalInt(a, "sampleRate", 8_000, 192_000, "audio.sampleRate") ?? 48_000;
        int channels = OptionalInt(a, "channels", 1, 8, "audio.channels") ?? 2;
        double? lufs = null;
        double peak = -1;
        if (Section(a, "loudness") is { } loudness)
        {
            lufs = Number(loudness, "lufs", null, -70, -5, "audio.loudness.lufs");
            peak = Number(loudness, "truePeak", -1, -9, 0, "audio.loudness.truePeak");
        }

        return new ExportPresetAudio(Word(a, "codec", "audio.codec", required: true)!)
        {
            SampleRate = rate,
            Channels = channels,
            SampleFormat = Choice(a, "sampleFormat", "audio.sampleFormat", "s24", ["s16", "s24", "s32", "float"]),
            RateMode = Choice(a, "rateControl", "audio.rateControl", "cbr", ["cbr", "vbr", "crf", "lossless"]),
            BitrateKbps = OptionalInt(a, "bitrateKbps", 8, 1536, "audio.bitrateKbps"),
            Quality = OptionalNumber(a, "quality", 0, 100, "audio.quality"),
            LoudnessLufs = lufs,
            LoudnessTruePeak = peak,
        };
    }

    /// <summary>
    /// <c>frameRate</c>: un número (25, 29.97, 23.976 o 59.94, que se leen como 30000/1001 y compañía) o el texto
    /// <c>"30000/1001"</c>. Sin él, 0/0: la tasa de la secuencia.
    /// </summary>
    private static (int Num, int Den) FrameRate(JsonElement v)
    {
        if (!v.TryGetProperty("frameRate", out JsonElement e) || e.ValueKind == JsonValueKind.Null)
        {
            return (0, 0);
        }

        if (e.ValueKind == JsonValueKind.Number && double.IsFinite(e.GetDouble()))
        {
            double fps = e.GetDouble();
            foreach ((double ntsc, int num) in (ReadOnlySpan<(double, int)>)[(23.976, 24000), (29.97, 30000), (47.952, 48000), (59.94, 60000), (119.88, 120000)])
            {
                if (Math.Abs(fps - ntsc) < 0.002)
                {
                    return (num, 1001);
                }
            }

            if (fps is >= 1 and <= 240 && Math.Abs(fps - Math.Round(fps)) < 1e-9)
            {
                return ((int)Math.Round(fps), 1);
            }
        }
        else if (e.ValueKind == JsonValueKind.String && e.GetString()!.Split('/') is [{ } n, { } d]
            && int.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out int num)
            && int.TryParse(d, NumberStyles.None, CultureInfo.InvariantCulture, out int den)
            && den is >= 1 and <= 1001 && num >= den && num <= 240 * den)
        {
            return (num, den);
        }

        throw Fail("video.frameRate tiene que ser una tasa de 1 a 240 (25, 29.97, \"30000/1001\").");
    }

    /// <summary>
    /// Un nombre en minúsculas ASCII, dígitos y guiones bajos (códec, contenedor, perfil), que empieza por letra salvo que se
    /// admita un dígito (el perfil <c>4444</c> de ProRes).
    /// </summary>
    private static string? Word(JsonElement owner, string property, string where, bool required, bool digitFirst = false)
    {
        string? text = String(owner, property);
        if (text is null)
        {
            return required ? throw Fail($"Falta {where}.") : null;
        }

        bool valid = text.Length is > 0 and <= 32 && (char.IsAsciiLetterLower(text[0]) || (digitFirst && char.IsAsciiDigit(text[0])))
            && text.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');
        return valid ? text : throw Fail($"{where} «{Show(text)}» no es un nombre válido (minúsculas, dígitos y _).");
    }

    /// <summary>Un nivel como <c>4.1</c> o <c>high</c>: letras, dígitos y puntos.</summary>
    private static string? Token(JsonElement owner, string property, string where)
    {
        string? text = String(owner, property);
        if (text is null)
        {
            return null;
        }

        return text.Length is > 0 and <= 16 && text.All(c => char.IsAsciiLetterOrDigit(c) || c == '.')
            ? text
            : throw Fail($"{where} «{Show(text)}» no es válido.");
    }

    private static string Choice(JsonElement owner, string property, string where, string fallback, string[] allowed)
    {
        string? text = String(owner, property);
        if (text is null)
        {
            return owner.TryGetProperty(property, out JsonElement e) && e.ValueKind != JsonValueKind.Null
                ? throw Fail($"{where} tiene que ser texto: {string.Join(", ", allowed)}.")
                : fallback;
        }

        return Array.IndexOf(allowed, text) >= 0 ? text : throw Fail($"{where} «{Show(text)}» no existe: {string.Join(", ", allowed)}.");
    }

    private static bool Flag(JsonElement owner, string property, bool fallback)
    {
        if (!owner.TryGetProperty(property, out JsonElement e) || e.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }

        return e.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Fail($"{property} tiene que ser true o false."),
        };
    }

    private static int? OptionalInt(JsonElement owner, string property, int min, int max, string where)
    {
        if (!owner.TryGetProperty(property, out JsonElement e) || e.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out int value))
        {
            throw Fail($"{where} tiene que ser un entero.");
        }

        return value >= min && value <= max
            ? value
            : throw Fail($"{where} queda fuera de rango ({min.ToString(CultureInfo.InvariantCulture)} a {max.ToString(CultureInfo.InvariantCulture)}).");
    }

    private static double? OptionalNumber(JsonElement owner, string property, double min, double max, string where) =>
        owner.TryGetProperty(property, out JsonElement e) && e.ValueKind != JsonValueKind.Null ? Number(owner, property, null, min, max, where) : null;
}
