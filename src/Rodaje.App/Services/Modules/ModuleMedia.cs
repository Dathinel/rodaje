using Rodaje.Core.Timing;
using Rodaje.Media.Audio;
using Rodaje.Media.Video;
using Rodaje.Modules.Protocol;

namespace Rodaje.App.Services.Modules;

/// <summary>
/// El editor decodifica y entrega al módulo (los módulos no abren archivos del usuario ni traen FFmpeg): audio f32 a 16 o
/// 48 kHz, mono o estéreo, y cuadros BGRA a la resolución de análisis, todo en regiones de memoria compartida de la llamada.
/// </summary>
public static class ModuleMedia
{
    /// <summary>Lado mayor de los cuadros de análisis (fase 7: 512–1024 px).</summary>
    public const int MinAnalysisSize = 64;
    public const int MaxAnalysisSize = 2048;

    /// <summary>Lo más largo que se entrega de una vez (en un búfer de hasta 2 GB): unas 3 h a 48 kHz estéreo.</summary>
    public static readonly Time MaxAudioSpan = Time.FromSeconds(3 * 3600);

    /// <summary>
    /// Decodifica <paramref name="sourceStart"/>–<paramref name="sourceEnd"/> (tiempo del medio, 0 = origen) y lo deja en
    /// una región nueva como f32 intercalado. Corre en un hilo del pool; informa el avance en el tramo dado del trabajo.
    /// </summary>
    /// <param name="sampleRate">16 000 (voz) o 48 000.</param>
    /// <param name="channels">1 (mezcla de los dos) o 2.</param>
    public static Task<SharedBufferRef> WriteAudioAsync(ModuleCall call, string path, Time sourceStart, Time sourceEnd, int sampleRate = 16_000, int channels = 1,
        double progressFrom = 0, double progressTo = 0.1)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (sampleRate is not (16_000 or 48_000))
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "16 000 o 48 000.");
        }

        if (channels is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "1 o 2.");
        }

        if (sourceEnd <= sourceStart || sourceEnd - sourceStart > MaxAudioSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceEnd), "Tramo de audio vacío o demasiado largo.");
        }

        CancellationToken token = call.CancellationToken;
        return Task.Run(() =>
        {
            int factor = AudioDecoder.OutputSampleRate / sampleRate;
            long sourceFrames = sourceEnd.ToSamples(AudioDecoder.OutputSampleRate) - sourceStart.ToSamples(AudioDecoder.OutputSampleRate);
            long frames = sourceFrames / factor;
            long bytes = frames * channels * sizeof(float);
            SharedMemoryRegion region = call.CreateRegion(Math.Max(bytes, 1));
            using AudioDecoder decoder = AudioDecoder.Open(path);
            decoder.Seek(sourceStart);
            float[] block = new float[AudioDecoder.OutputChannels * factor * 4096];
            var writer = new AudioResampler(factor, channels);
            long written = 0;
            call.Report(progressFrom, "Leyendo el audio");
            while (written < frames)
            {
                token.ThrowIfCancellationRequested();
                int want = (int)Math.Min(4096, frames - written) * factor;
                int got = decoder.Read(block.AsSpan(0, want * AudioDecoder.OutputChannels));
                if (got == 0)
                {
                    break;
                }

                Span<byte> target = region.GetSpan(written * channels * sizeof(float), (long)(got / factor) * channels * sizeof(float));
                written += writer.Convert(block.AsSpan(0, got * AudioDecoder.OutputChannels), System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(target));
                call.Report(progressFrom + ((progressTo - progressFrom) * written / Math.Max(1, frames)), "Leyendo el audio");
            }

            // Lo que falte al final (audio más corto que el tramo) queda en silencio: la región nace en ceros.
            return region.Describe(0, Math.Max(bytes, channels * sizeof(float)), audio: new AudioLayout(sampleRate, channels, Math.Max(frames, 1)));
        }, token);
    }

    /// <summary>Bytes de un cuadro BGRA cuyo lado mayor es <paramref name="maxSide"/> (para dimensionar un anillo de regiones).</summary>
    public static long FrameBytes(int maxSide) => 4L * maxSide * maxSide;

    /// <summary>
    /// Decodifica el cuadro que se ve en <paramref name="sourceTime"/> con el alto que deja su lado mayor en
    /// <paramref name="maxSide"/> y lo copia en <paramref name="region"/> desde <paramref name="offset"/>. <c>null</c> si
    /// el archivo no tiene cuadros. El decodificador lo da quien llama (se reutiliza entre cuadros).
    /// </summary>
    public static SharedBufferRef? WriteFrame(ThumbnailDecoder decoder, SharedMemoryRegion region, long offset, Time sourceTime, int maxSide,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(region);
        maxSide = Math.Clamp(maxSide, MinAnalysisSize, MaxAnalysisSize);
        int width = decoder.Stream.DisplayWidth;
        int height = decoder.Stream.DisplayHeight;
        int targetHeight = width > height && width > 0 ? Math.Max(2, (int)((long)maxSide * height / width)) : maxSide;
        if (decoder.Decode(sourceTime, targetHeight, cancellationToken) is not { } image)
        {
            return null;
        }

        long length = (long)image.Width * image.Height * 4;
        image.Pixels.AsSpan(0, (int)length).CopyTo(region.GetSpan(offset, length));
        return region.Describe(offset, length, new VideoLayout(image.Width, image.Height, image.Width * 4, PixelFormats.Bgra8));
    }

    /// <summary>48 kHz estéreo → 16/48 kHz mono/estéreo. El paso a 16 kHz promedia de a 3 (filtro de caja suficiente para voz).</summary>
    private sealed class AudioResampler(int factor, int channels)
    {
        public long Convert(ReadOnlySpan<float> stereo, Span<float> output)
        {
            int frames = stereo.Length / 2 / factor;
            for (int i = 0; i < frames; i++)
            {
                float l = 0, r = 0;
                for (int k = 0; k < factor; k++)
                {
                    l += stereo[((i * factor) + k) * 2];
                    r += stereo[(((i * factor) + k) * 2) + 1];
                }

                l /= factor;
                r /= factor;
                if (channels == 1)
                {
                    output[i] = (l + r) * 0.5f;
                }
                else
                {
                    output[i * 2] = l;
                    output[(i * 2) + 1] = r;
                }
            }

            return frames;
        }
    }
}
