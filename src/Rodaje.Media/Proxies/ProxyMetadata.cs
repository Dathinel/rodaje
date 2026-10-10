using System.Text.Json.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.Media.Proxies;

/// <summary>JSON que acompaña a cada proxy: de qué origen salió, con qué ajustes y qué se comprobó al generarlo.</summary>
/// <remarks>Los tiempos van en ticks de <see cref="Time"/> (1/705 600 000 s).</remarks>
public sealed record ProxyMetadata
{
    public int FormatVersion { get; init; } = ProxySettings.FormatVersion;

    public required string SourcePath { get; init; }

    public required string SourceKey { get; init; }

    public required long SourceSize { get; init; }

    public required DateTime SourceLastWriteUtc { get; init; }

    public required string SettingsTag { get; init; }

    public required int TargetHeight { get; init; }

    public required int GopLength { get; init; }

    public required string Encoder { get; init; }

    public string FfmpegVersion { get; init; } = "";

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required long FrameRateNum { get; init; }

    public required long FrameRateDen { get; init; }

    /// <summary>Cuadros del proxy según su contenedor, comprobados contra los que escribió ffmpeg.</summary>
    public required long FrameCount { get; init; }

    /// <summary>Cuadros del original según su contenedor (estimados si el contenedor no los declara).</summary>
    public required long SourceFrameCount { get; init; }

    public required long SourceStartTicks { get; init; }

    public required long ProxyStartTicks { get; init; }

    public required long SourceDurationTicks { get; init; }

    public required long ProxyDurationTicks { get; init; }

    public long ProxyBytes { get; init; }

    public required DateTime CreatedUtc { get; init; }

    public double GenerationSeconds { get; init; }

    /// <summary>Velocidad de la generación en veces el tiempo real.</summary>
    public double Speed { get; init; }

    [JsonIgnore]
    public Rational FrameRate => new(FrameRateNum, FrameRateDen);

    [JsonIgnore]
    public Time SourceStart => Time.FromTicks(SourceStartTicks);

    [JsonIgnore]
    public Time ProxyStart => Time.FromTicks(ProxyStartTicks);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    NewLine = "\n",
    RespectNullableAnnotations = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(ProxyMetadata))]
internal sealed partial class ProxyJsonContext : JsonSerializerContext
{
}
