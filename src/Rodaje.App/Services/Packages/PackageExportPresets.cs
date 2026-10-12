using Rodaje.Core.Timing;
using Rodaje.Export;
using Rodaje.Media.Export;
using Rodaje.Packages;
using Rodaje.Packages.Content;

namespace Rodaje.App.Services.Packages;

/// <summary>
/// Los preajustes de exportación de los paquetes instalados (<c>export-preset</c>, <c>rodaje.exportpreset</c> v1) como
/// <see cref="ExportPreset"/> del diálogo. El formato del paquete es neutro; aquí se traduce a los ajustes de Rodaje y se
/// rechaza lo que este programa no tiene (un códec o un contenedor desconocido), con el motivo.
/// </summary>
public static class PackageExportPresets
{
    /// <summary>Todos los de lo instalado, por paquete y nombre; lo que no se pueda leer queda en <paramref name="problems"/>.</summary>
    public static IReadOnlyList<ExportPreset> Load(PackageScan installed, List<string>? problems = null)
    {
        ArgumentNullException.ThrowIfNull(installed);
        var presets = new List<ExportPreset>();
        foreach (PackageItemEntry entry in installed.Items(ItemKind.ExportPreset)
            .OrderBy(e => e.Package.Manifest.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            try
            {
                using FileStream stream = File.OpenRead(entry.FilePath ?? throw new FileNotFoundException(entry.Item.Name));
                ExportPresetDefinition definition = ContentReaders.ReadExportPreset(stream);
                string reference = entry.Reference.ToString();
                string description = entry.Item.Description is { Length: > 0 } d ? d : definition.Description;
                presets.Add(new ExportPreset(reference, entry.Item.Name, $"{entry.Package.Manifest.Name} · {description}",
                    Category(definition.Category), ExportPresetOrigin.Package, ToSettings(definition))
                {
                    PackageReference = reference,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PackageContentException)
            {
                problems?.Add($"{entry.Item.Name}: {ex.Message}");
            }
        }

        return presets;
    }

    /// <exception cref="PackageContentException">Un nombre que Rodaje no conoce.</exception>
    public static ExportSettings ToSettings(ExportPresetDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new ExportSettings
        {
            Container = Parse<ExportContainer>(definition.Container, "container"),
            Video = definition.Video is { } v ? Video(v) : null,
            Audio = definition.Audio is { } a ? Audio(a) : null,
            FastStart = definition.FastStart,
            ChaptersFromMarkers = definition.Chapters,
        };
    }

    private static VideoExportSettings Video(ExportPresetVideo v)
    {
        RateControlMode mode = Mode(v.RateMode);
        return new VideoExportSettings
        {
            Codec = Parse<ExportCodec>(v.Codec, "video.codec"),
            Profile = v.Profile,
            Level = v.Level,
            SpeedPreset = v.SpeedPreset,
            BitDepth = v.BitDepth,
            Chroma = v.Chroma switch { "422" => ChromaSubsampling.Chroma422, "444" => ChromaSubsampling.Chroma444, _ => ChromaSubsampling.Chroma420 },
            Alpha = v.Alpha,
            Width = v.Width,
            Height = v.Height,
            FrameRate = v.FrameRateNumerator > 0 ? new Rational(v.FrameRateNumerator, v.FrameRateDenominator) : null,
            FieldOrder = v.FieldOrder switch { "tff" => ExportFieldOrder.TopFirst, "bff" => ExportFieldOrder.BottomFirst, _ => ExportFieldOrder.Progressive },
            Fit = v.Fit switch { "crop" => ExportFit.Crop, "stretch" => ExportFit.Stretch, _ => ExportFit.Bars },
            RateControl = new RateControl
            {
                Mode = mode,
                Quality = v.Quality,
                BitrateKbps = v.BitrateKbps,
                MaxBitrateKbps = v.MaxBitrateKbps ?? (mode == RateControlMode.Cbr ? v.BitrateKbps : null),
                BufferKbps = v.BufferKbps,
                Passes = v.Passes,
            },
            Gop = new GopSettings { KeyframeSeconds = v.KeyframeSeconds ?? 2, Closed = v.ClosedGop, BFrames = v.BFrames },
            Projection = v.Projection == "equirectangular" ? ExportProjection.Equirectangular : ExportProjection.Flat,
        };
    }

    private static AudioExportSettings Audio(ExportPresetAudio a) => new()
    {
        Codec = Parse<ExportCodec>(a.Codec, "audio.codec"),
        SampleRate = a.SampleRate,
        Channels = a.Channels,
        PcmFormat = a.SampleFormat switch
        {
            "s16" => ExportSampleFormat.Pcm16,
            "s32" => ExportSampleFormat.Pcm32,
            "float" => ExportSampleFormat.PcmFloat,
            _ => ExportSampleFormat.Pcm24,
        },
        RateControl = new RateControl { Mode = Mode(a.RateMode), BitrateKbps = a.BitrateKbps, Quality = a.Quality },
        Loudness = a.LoudnessLufs is { } lufs ? new LoudnessTarget(lufs, a.LoudnessTruePeak) : null,
    };

    private static RateControlMode Mode(string mode) => mode switch
    {
        "vbr" => RateControlMode.Vbr,
        "cbr" => RateControlMode.Cbr,
        "abr" => RateControlMode.Abr,
        "lossless" => RateControlMode.Lossless,
        "profile" => RateControlMode.ProfileDefined,
        _ => RateControlMode.ConstantQuality,
    };

    private static ExportPresetCategory Category(string category) => category switch
    {
        "platform" => ExportPresetCategory.Platform,
        "quality" => ExportPresetCategory.Quality,
        "web" => ExportPresetCategory.Web,
        "audio" => ExportPresetCategory.AudioOnly,
        "broadcast" => ExportPresetCategory.Broadcast,
        "cinema" => ExportPresetCategory.Cinema,
        "archive" => ExportPresetCategory.Archive,
        _ => ExportPresetCategory.Master,
    };

    // Los nombres del formato son los del enum en minúsculas (mp4, mpegts, prores, dnxhr…); nunca un número.
    private static T Parse<T>(string text, string where)
        where T : struct, Enum =>
        text.Length > 0 && char.IsAsciiLetter(text[0]) && Enum.TryParse(text, ignoreCase: true, out T value)
            ? value
            : throw new PackageContentException($"{where} «{text}» no existe en esta versión de Rodaje: actualiza el programa.");
}
