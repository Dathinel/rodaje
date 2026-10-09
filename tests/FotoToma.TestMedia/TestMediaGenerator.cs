using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;

namespace FotoToma.TestMedia;

/// <summary>Un archivo generado (o tomado de la caché) y con qué se hizo.</summary>
public sealed record GeneratedMedia(string Path, MediaSpec Spec, string VideoEncoder, bool FromCache, TimeSpan GenerationTime);

/// <summary>
/// Genera archivos de prueba pasando cuadros de <see cref="SyntheticVideo"/> por la entrada
/// estándar de ffmpeg y el audio de <see cref="AudioCode"/> por un .wav temporal. Los guarda en
/// una caché con nombre según el hash de los parámetros, así que cada archivo se codifica una vez.
/// </summary>
public sealed class TestMediaGenerator
{
    /// <summary>Se sube cuando cambia lo que se dibuja o se codifica, para invalidar la caché.</summary>
    public const int FormatVersion = 1;

    public const string CacheEnvironmentVariable = "FOTOTOMA_MEDIA_CACHE";

    private static readonly SemaphoreSlim GenerationLock = new(1, 1);

    public TestMediaGenerator(FfmpegTool ffmpeg, string cacheDirectory)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        Ffmpeg = ffmpeg;
        CacheDirectory = Path.GetFullPath(cacheDirectory);
    }

    public FfmpegTool Ffmpeg { get; }
    public string CacheDirectory { get; }

    /// <summary>
    /// <c>FOTOTOMA_MEDIA_CACHE</c> si está definida; si no, <c>TestResults\media-cache</c> en la raíz del
    /// repositorio (la primera carpeta hacia arriba con FotoToma.slnx).
    /// </summary>
    public static string DefaultCacheDirectory
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable(CacheEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured;
            }

            for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "FotoToma.slnx")))
                {
                    return Path.Combine(dir.FullName, "TestResults", "media-cache");
                }
            }

            return Path.Combine(Path.GetTempPath(), "FotoToma", "media-cache");
        }
    }

    /// <summary>Generador con el ffmpeg por defecto, o null si no hay ffmpeg.</summary>
    public static TestMediaGenerator? CreateDefault() =>
        FfmpegTool.Default is { } ffmpeg ? new TestMediaGenerator(ffmpeg, DefaultCacheDirectory) : null;

    public GeneratedMedia GetOrCreate(MediaSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        spec.Validate();

        string encoder = spec.Encoder ?? Ffmpeg.FindVideoEncoder(spec.Codec)
            ?? throw new FfmpegException($"Ningún codificador de {spec.Codec} funciona con {Ffmpeg.ExecutablePath}.");
        if (spec.Encoder is not null && Ffmpeg.ProbeEncoder(spec.Encoder) is { } reason)
        {
            throw new FfmpegException(reason);
        }

        if (spec.Audio != AudioCodec.None && Ffmpeg.ProbeEncoder(FfmpegTool.AudioEncoderName(spec.Audio)) is { } audioReason)
        {
            throw new FfmpegException(audioReason);
        }

        string path = Path.Combine(CacheDirectory, $"{spec.Name}-{spec.CacheKey(encoder)}{spec.FileExtension}");
        if (File.Exists(path))
        {
            return new GeneratedMedia(path, spec, encoder, FromCache: true, TimeSpan.Zero);
        }

        // Un archivo a la vez: varias pruebas que piden lo mismo no lo codifican dos veces.
        GenerationLock.Wait();
        try
        {
            if (File.Exists(path))
            {
                return new GeneratedMedia(path, spec, encoder, FromCache: true, TimeSpan.Zero);
            }

            Directory.CreateDirectory(CacheDirectory);
            var stopwatch = Stopwatch.StartNew();
            string partial = path + ".parcial" + spec.FileExtension;
            string? wav = spec.Audio == AudioCodec.None ? null : Path.Combine(Path.GetTempPath(), $"fototoma-{Guid.NewGuid():N}.wav");
            try
            {
                if (wav is not null)
                {
                    WriteWav(wav, spec.AudioSampleCount);
                }

                var video = new SyntheticVideo(spec.Width, spec.Height);
                Ffmpeg.Run(BuildArguments(spec, encoder, wav, partial), input =>
                {
                    var frame = new byte[video.FrameSize];
                    for (int i = 0; i < spec.FrameCount; i++)
                    {
                        video.Render(frame, i);
                        input.Write(frame);
                    }
                });
                File.Move(partial, path, overwrite: true);
            }
            finally
            {
                File.Delete(partial);
                if (wav is not null)
                {
                    File.Delete(wav);
                }
            }

            return new GeneratedMedia(path, spec, encoder, FromCache: false, stopwatch.Elapsed);
        }
        finally
        {
            GenerationLock.Release();
        }
    }

    internal static List<string> BuildArguments(MediaSpec spec, string encoder, string? wavPath, string outputPath)
    {
        string bitrate = FfmpegTool.Invariant($"{spec.EffectiveVideoBitrateKbps}k");
        string gop = spec.GopLength.ToString(CultureInfo.InvariantCulture);
        string bFrames = spec.BFrames.ToString(CultureInfo.InvariantCulture);
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "rawvideo", "-pix_fmt", "yuv420p",
            "-s", FfmpegTool.Invariant($"{spec.Width}x{spec.Height}"),
            "-framerate", FfmpegTool.Invariant($"{spec.FrameRate.Num}/{spec.FrameRate.Den}"),
            "-i", "pipe:0",
        };
        if (wavPath is not null)
        {
            args.AddRange(["-i", wavPath]);
        }

        args.AddRange(["-map", "0:v:0"]);
        if (wavPath is not null)
        {
            args.AddRange(["-map", "1:a:0"]);
        }

        args.AddRange(["-c:v", encoder]);
        if (encoder.EndsWith("_nvenc", StringComparison.Ordinal))
        {
            args.AddRange(["-preset", "p2", "-rc", "cbr", "-b:v", bitrate, "-g", gop, "-bf", bFrames, "-no-scenecut", "1", "-strict_gop", "1"]);
        }
        else if (encoder.EndsWith("_mf", StringComparison.Ordinal))
        {
            args.AddRange(["-rate_control", "cbr", "-b:v", bitrate, "-g", gop, "-bf", bFrames]);
        }
        else if (encoder == "libx264")
        {
            args.AddRange(["-preset", "veryfast", "-b:v", bitrate, "-g", gop, "-keyint_min", gop, "-sc_threshold", "0", "-bf", bFrames]);
        }
        else if (encoder == "libx265")
        {
            args.AddRange(["-preset", "veryfast", "-b:v", bitrate, "-x265-params", FfmpegTool.Invariant($"log-level=error:keyint={spec.GopLength}:min-keyint={spec.GopLength}:scenecut=0:bframes={spec.BFrames}")]);
        }
        else
        {
            // libopenh264 y otros: sin cuadros B.
            args.AddRange(["-b:v", bitrate, "-g", gop]);
        }

        if (spec.Codec == VideoCodec.Hevc && spec.Container != ContainerFormat.Mkv)
        {
            args.AddRange(["-tag:v", "hvc1"]);
        }

        args.AddRange(["-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709", "-color_range", "tv"]);

        if (wavPath is not null)
        {
            args.AddRange([
                "-c:a", FfmpegTool.AudioEncoderName(spec.Audio),
                "-b:a", FfmpegTool.Invariant($"{spec.AudioBitrateKbps}k"),
                "-ar", MediaSpec.AudioSampleRate.ToString(CultureInfo.InvariantCulture),
                "-ac", MediaSpec.AudioChannels.ToString(CultureInfo.InvariantCulture),
            ]);
        }

        if (spec.StartOffset > TimeSpan.Zero)
        {
            args.AddRange(["-output_ts_offset", spec.StartOffset.TotalSeconds.ToString("R", CultureInfo.InvariantCulture)]);
        }

        args.AddRange([
            "-metadata", FfmpegTool.Invariant($"title=FotoToma.TestMedia {spec.Name}"),
            "-f", spec.Container switch
            {
                ContainerFormat.Mp4 => "mp4",
                ContainerFormat.Mkv => "matroska",
                _ => "mov",
            },
            outputPath,
        ]);
        return args;
    }

    /// <summary>WAV PCM de 16 bits, estéreo a 48 kHz, con las marcas de <see cref="AudioCode"/>.</summary>
    internal static void WriteWav(string path, long sampleCount)
    {
        const int channels = MediaSpec.AudioChannels;
        const int rate = MediaSpec.AudioSampleRate;
        long dataBytes = sampleCount * channels * sizeof(short);
        if (dataBytes > uint.MaxValue - 36)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount), "El audio no cabe en un WAV de 4 GB.");
        }

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)(36 + dataBytes));
        "WAVEfmt "u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], rate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], rate * channels * sizeof(short));
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], channels * sizeof(short));
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (uint)dataBytes);
        stream.Write(header);

        const int chunkFrames = rate;
        var samples = new float[chunkFrames * channels];
        var bytes = new byte[samples.Length * sizeof(short)];
        for (long first = 0; first < sampleCount; first += chunkFrames)
        {
            int frames = (int)Math.Min(chunkFrames, sampleCount - first);
            Span<float> chunk = samples.AsSpan(0, frames * channels);
            AudioCode.Generate(chunk, first, rate, channels);
            for (int i = 0; i < chunk.Length; i++)
            {
                short value = (short)Math.Clamp(MathF.Round(chunk[i] * short.MaxValue), short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * sizeof(short)), value);
            }

            stream.Write(bytes, 0, chunk.Length * sizeof(short));
        }
    }
}
