using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Rodaje.Core.Timing;

namespace Rodaje.TestMedia;

public enum VideoCodec
{
    H264,
    Hevc,
}

public enum AudioCodec
{
    None,
    Aac,
    Opus,
}

public enum ContainerFormat
{
    Mp4,
    Mkv,
    Mov,
}

/// <summary>Parámetros de un archivo de prueba; dos especificaciones iguales dan el mismo archivo en caché.</summary>
public sealed record MediaSpec
{
    public const int AudioSampleRate = 48000;
    public const int AudioChannels = 2;

    public MediaSpec(string name, int width, int height, Rational frameRate, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (width <= 0 || height <= 0 || width % 2 != 0 || height % 2 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"El tamaño debe ser par y positivo; se recibió {width}×{height}.");
        }

        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), $"La duración debe ser positiva; se recibió {duration}.");
        }

        Name = name;
        Width = width;
        Height = height;
        FrameRate = frameRate;
        Duration = duration;
    }

    public string Name { get; }
    public int Width { get; }
    public int Height { get; }
    public Rational FrameRate { get; }
    public TimeSpan Duration { get; }

    public VideoCodec Codec { get; init; } = VideoCodec.H264;

    /// <summary>Codificador de ffmpeg concreto; null elige el primero que funcione (ver <see cref="FfmpegTool.FindVideoEncoder"/>).</summary>
    public string? Encoder { get; init; }

    public int GopLength { get; init; } = 60;
    public int BFrames { get; init; }
    public AudioCodec Audio { get; init; } = AudioCodec.None;
    public ContainerFormat Container { get; init; } = ContainerFormat.Mp4;

    /// <summary>Desplazamiento de las marcas de tiempo del contenedor (el primer cuadro empieza aquí).</summary>
    public TimeSpan StartOffset { get; init; } = TimeSpan.Zero;

    public int? VideoBitrateKbps { get; init; }
    public int AudioBitrateKbps { get; init; } = 128;

    /// <summary>Cuadros redondeados al más cercano: 20 s a 30000/1001 son 599 cuadros.</summary>
    public int FrameCount => checked((int)RoundDiv((Int128)Duration.Ticks * FrameRate.Num, (Int128)TimeSpan.TicksPerSecond * FrameRate.Den));

    /// <summary>Muestras por canal que cubren exactamente los <see cref="FrameCount"/> cuadros.</summary>
    public long AudioSampleCount => (long)RoundDiv((Int128)FrameCount * FrameRate.Den * AudioSampleRate, FrameRate.Num);

    public int EffectiveVideoBitrateKbps =>
        VideoBitrateKbps ?? (int)Math.Max(300, Width * (long)Height * FrameRate.Num / FrameRate.Den / 10_000);

    public string FileExtension => Container switch
    {
        ContainerFormat.Mp4 => ".mp4",
        ContainerFormat.Mkv => ".mkv",
        _ => ".mov",
    };

    internal string CacheKey(string encoder)
    {
        string canonical = string.Join(
            '|',
            TestMediaGenerator.FormatVersion.ToString(CultureInfo.InvariantCulture),
            Width.ToString(CultureInfo.InvariantCulture),
            Height.ToString(CultureInfo.InvariantCulture),
            FrameRate.ToString(),
            FrameCount.ToString(CultureInfo.InvariantCulture),
            Codec.ToString(),
            encoder,
            GopLength.ToString(CultureInfo.InvariantCulture),
            BFrames.ToString(CultureInfo.InvariantCulture),
            Audio.ToString(),
            Container.ToString(),
            StartOffset.Ticks.ToString(CultureInfo.InvariantCulture),
            EffectiveVideoBitrateKbps.ToString(CultureInfo.InvariantCulture),
            AudioBitrateKbps.ToString(CultureInfo.InvariantCulture));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(GopLength, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(BFrames);
        ArgumentOutOfRangeException.ThrowIfLessThan(AudioBitrateKbps, 8);
        if (StartOffset < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(StartOffset), "El desplazamiento inicial no puede ser negativo.");
        }

        if (FrameCount > FrameCode.MaxIndex + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Duration), $"{FrameCount} cuadros no caben en el código de {FrameCode.IndexBits} bits.");
        }

        if (Audio == AudioCodec.Opus && Container == ContainerFormat.Mov)
        {
            throw new ArgumentException("Opus no se puede guardar en MOV; usa MP4 o MKV.");
        }

        FrameCode.GetLayout(Width, Height);
    }

    private static Int128 RoundDiv(Int128 numerator, Int128 denominator) => (2 * numerator + denominator) / (2 * denominator);
}
