using System.Diagnostics;
using Rodaje.Core.Timing;
using Rodaje.Media.Export;
using Rodaje.Media.Proxies;

namespace Rodaje.Media.Tests.Export;

/// <summary>Codificadores de este <c>ffmpeg.exe</c> para probar el sink; el catálogo de verdad es de la tarea 13.</summary>
internal static class SinkEncoders
{
    private static EncoderSpec Video(string id, ExportCodec codec, bool hardware, params ExportPixelFormat[] pixels) => new()
    {
        Id = id, Codec = codec, IsHardware = hardware, Vendor = hardware ? EncoderVendor.Nvidia : EncoderVendor.Software,
        License = hardware ? EncoderLicense.SystemOrDriver : EncoderLicense.FfmpegLgpl, PixelFormats = pixels,
    };

    private static EncoderSpec Audio(string id, ExportCodec codec) => new()
    {
        Id = id, Codec = codec, IsHardware = false, Vendor = id.EndsWith("_mf", StringComparison.Ordinal) ? EncoderVendor.Microsoft : EncoderVendor.Software,
        License = EncoderLicense.FfmpegLgpl, MaxChannels = 8,
    };

    public static EncoderSpec For(string id) => id switch
    {
        "h264_nvenc" => Video(id, ExportCodec.H264, true, ExportPixelFormat.Nv12),
        "hevc_nvenc" => Video(id, ExportCodec.Hevc, true, ExportPixelFormat.Nv12, ExportPixelFormat.P010),
        "h264_mf" => Video(id, ExportCodec.H264, true, ExportPixelFormat.Nv12) with { Vendor = EncoderVendor.Microsoft },
        "libsvtav1" => Video(id, ExportCodec.Av1, false, ExportPixelFormat.Yuv420P, ExportPixelFormat.Yuv420P10),
        "libvpx-vp9" => Video(id, ExportCodec.Vp9, false, ExportPixelFormat.Yuv420P, ExportPixelFormat.Yuva420P) with { SupportsTwoPass = true },
        "prores_ks" => Video(id, ExportCodec.ProRes, false, ExportPixelFormat.Yuv422P10, ExportPixelFormat.Yuva444P10) with { IntraOnly = true },
        "dnxhd" => Video(id, ExportCodec.DnxHr, false, ExportPixelFormat.Yuv422P, ExportPixelFormat.Yuv422P10) with { IntraOnly = true },
        "ffv1" => Video(id, ExportCodec.Ffv1, false, ExportPixelFormat.Yuv420P, ExportPixelFormat.Yuv422P10) with { IntraOnly = true },
        "png" => Video(id, ExportCodec.Png, false, ExportPixelFormat.Rgb24, ExportPixelFormat.Rgba) with { IntraOnly = true },
        "aac_mf" => Audio(id, ExportCodec.Aac),
        "aac" => Audio(id, ExportCodec.Aac),
        "libopus" => Audio(id, ExportCodec.Opus),
        "flac" => Audio(id, ExportCodec.Flac),
        "alac" => Audio(id, ExportCodec.Alac),
        "libmp3lame" => Audio(id, ExportCodec.Mp3),
        "pcm_s16le" => Audio(id, ExportCodec.Pcm),
        "pcm_s24le" => Audio(id, ExportCodec.Pcm),
        _ => Video(id, ExportCodec.H264, false, ExportPixelFormat.Nv12),
    };
}

/// <summary>Cuadros sintéticos de cualquier <see cref="ExportPixelFormat"/>, con valores legales y relleno de pitch.</summary>
internal sealed class SyntheticFrames
{
    private readonly byte[][] _frames;
    private readonly int[] _offsets = new int[4];
    private readonly int[] _pitches = new int[4];

    public SyntheticFrames(ExportVideoFormat format, int distinct = 6, int padding = 64)
    {
        Format = format;
        ExportPixelFormat pixels = format.PixelFormat;
        int offset = 0;
        for (int plane = 0; plane < pixels.PlaneCount(); plane++)
        {
            _offsets[plane] = offset;
            _pitches[plane] = pixels.RowBytes(plane, format.Width, format.Height) + padding;
            offset += _pitches[plane] * pixels.PlaneSize(plane, format.Width, format.Height).Height;
        }

        _frames = new byte[distinct][];
        for (int i = 0; i < distinct; i++)
        {
            _frames[i] = new byte[offset];
            Fill(_frames[i], i);
        }
    }

    public ExportVideoFormat Format { get; }

    public ExportVideoFrame Frame(long index)
    {
        byte[] data = _frames[index % _frames.Length];
        int count = Format.PixelFormat.PlaneCount();
        ReadOnlySpan<byte> Plane(int p) => p < count ? data.AsSpan(_offsets[p], (p + 1 < count ? _offsets[p + 1] : data.Length) - _offsets[p]) : default;
        return new ExportVideoFrame(index, Format, Plane(0), _pitches[0], Plane(1), _pitches[1], Plane(2), _pitches[2], Plane(3), _pitches[3]);
    }

    private void Fill(byte[] data, int seed)
    {
        ExportPixelFormat pixels = Format.PixelFormat;
        bool wide = pixels.BytesPerSample() == 2;
        int shift = pixels == ExportPixelFormat.P010 ? 6 : 0;
        int max = pixels.BitDepth() == 8 ? 255 : pixels.BitDepth() == 16 ? 65535 : 1023;
        for (int plane = 0; plane < pixels.PlaneCount(); plane++)
        {
            (int width, int height) = pixels.PlaneSize(plane, Format.Width, Format.Height);
            bool luma = plane == 0 || pixels.IsRgb();
            for (int y = 0; y < height; y++)
            {
                int row = _offsets[plane] + (y * _pitches[plane]);
                for (int x = 0; x < width; x++)
                {
                    double t = luma ? ((x + (seed * 7)) % 200 / 200.0 * 0.8) + 0.1 : 0.5 + (0.1 * Math.Sin((y + seed) * 0.05));
                    if (plane == 3)
                    {
                        t = 1;
                    }

                    int value = (int)(t * max);
                    if (wide)
                    {
                        ushort sample = (ushort)(value << shift);
                        data[row + (x * 2)] = (byte)sample;
                        data[row + (x * 2) + 1] = (byte)(sample >> 8);
                    }
                    else
                    {
                        data[row + x] = (byte)value;
                    }
                }
            }
        }
    }
}

/// <summary>Una carpeta temporal y un WAV de prueba, con la limpieza al final.</summary>
internal sealed class ExportScratch : IDisposable
{
    public ExportScratch()
    {
        Directory = Path.Combine(Path.GetTempPath(), "rodaje-pruebas-export", "carpeta con espacios ñ", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string Directory { get; }

    public string Combine(string name) => Path.Combine(Directory, name);

    /// <summary>Archivos que quedan en la carpeta (para comprobar que no quedan temporales).</summary>
    public string[] Leftovers(params string[] except) =>
        [.. System.IO.Directory.EnumerateFileSystemEntries(Directory).Where(p => !except.Contains(p, StringComparer.OrdinalIgnoreCase))];

    /// <summary>WAV float de un tono de 440 Hz a <paramref name="amplitude"/>.</summary>
    public string Tone(string name, double seconds, ExportAudioFormat? format = null, double amplitude = 0.25)
    {
        format ??= ExportAudioFormat.Default;
        string path = Combine(name);
        using var writer = new WaveFileWriter(path, format);
        long total = (long)Math.Round(seconds * format.SampleRate);
        float[] block = new float[4800 * format.Channels];
        for (long done = 0; done < total;)
        {
            int frames = (int)Math.Min(4800, total - done);
            for (int i = 0; i < frames; i++)
            {
                float value = (float)(amplitude * Math.Sin(2 * Math.PI * 440 * (done + i) / format.SampleRate));
                for (int c = 0; c < format.Channels; c++)
                {
                    block[(i * format.Channels) + c] = value;
                }
            }

            writer.Write(block.AsSpan(0, frames * format.Channels));
            done += frames;
        }

        writer.Complete();
        return path;
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class SinkRuns
{
    public static ExportVideoFormat Format(int width, int height, ExportPixelFormat pixels, Rational? rate = null) =>
        new(width, height, rate ?? Rational.Fps30, pixels, ExportColor.For(width, height, pixels));

    /// <summary>Escribe <paramref name="frames"/> cuadros y cierra; devuelve el resultado y el tiempo.</summary>
    public static (ExportSinkResult Result, TimeSpan Elapsed) Run(FfmpegPipeSink sink, ExportSinkRequest request, SyntheticFrames? source,
        CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        sink.Start(request, cancellationToken);
        if (source is not null)
        {
            for (long i = 0; i < request.FrameCount; i++)
            {
                sink.WriteVideo(source.Frame(i), cancellationToken);
            }
        }

        ExportSinkResult result = sink.Complete(cancellationToken);
        return (result, clock.Elapsed);
    }

    public static bool IsAlive(int? processId)
    {
        if (processId is not { } id)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(id);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static FfmpegExecutable Ffmpeg => Rodaje.TestMedia.Skip.ProxyExecutable!;
}
