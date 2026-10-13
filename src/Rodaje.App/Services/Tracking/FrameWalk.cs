using Rodaje.Analysis.Video;
using Rodaje.Core.Timing;
using Rodaje.Media.Video;
using Rodaje.Playback;

namespace Rodaje.App.Services.Tracking;

/// <summary>Un cuadro que entrega <see cref="FrameWalk"/>: su índice en el medio y la luma (válida solo durante la llamada).</summary>
public delegate void LumaVisitor(long index, LumaFrame luma);

/// <summary>
/// Recorre los cuadros de un video desde uno de partida hacia adelante o hacia atrás y entrega la luma de cada uno, en la
/// CPU. Hacia atrás lee tramos hacia adelante (el decodificador es secuencial), copia la luma y los recorre al revés. Lo
/// usan los dos seguidores (región y puntos).
/// </summary>
public sealed partial class FrameWalk : IDisposable
{
    /// <summary>Lumas que se guardan por tramo al ir hacia atrás.</summary>
    private const int BackwardChunkBytes = 192 * 1024 * 1024;

    private readonly VideoDecoder _decoder;

    public FrameWalk(string mediaPath)
    {
        _decoder = VideoDecoder.Open(mediaPath, new VideoDecoderOptions { AllowHardwareDecoding = false });
        Rate = _decoder.FrameRate;
        Rotation = _decoder.Stream.Rotation;
    }

    public Rational Rate { get; }

    /// <summary>Rotación del archivo en grados (horaria).</summary>
    public int Rotation { get; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>El índice del cuadro del instante <paramref name="time"/>, dentro del video.</summary>
    public long FrameOf(Time time)
    {
        long index = Math.Max(0, time.ToFrames(Rate));
        return _decoder.FrameCount > 0 ? Math.Min(index, _decoder.FrameCount - 1) : index;
    }

    /// <summary>Lee el cuadro <paramref name="index"/> y lo entrega; falso si no se pudo.</summary>
    public bool Read(long index, LumaVisitor visit, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(visit);
        using DecodedFrame? decoded = _decoder.GetFrame(index, token);
        if (decoded?.Payload is not CpuFramePayload payload)
        {
            return false;
        }

        Width = payload.Width;
        Height = payload.Height;
        visit(index, Luma(payload));
        return true;
    }

    /// <summary>
    /// Entrega los cuadros después de <paramref name="first"/> hasta <paramref name="last"/> (incluido) en el sentido de
    /// <paramref name="forward"/>. Se detiene si el video se acaba o cambia de tamaño, o si <paramref name="visit"/> pide parar.
    /// </summary>
    public void Walk(long first, long last, bool forward, Func<long, LumaFrame, bool> visit, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(visit);
        if (forward)
        {
            for (long i = first + 1; i <= last; i++)
            {
                token.ThrowIfCancellationRequested();
                using DecodedFrame? decoded = _decoder.GetFrame(i, token);
                if (decoded?.Payload is not CpuFramePayload payload || payload.Width != Width || payload.Height != Height || !visit(i, Luma(payload)))
                {
                    return;
                }
            }

            return;
        }

        int chunk = (int)Math.Clamp(BackwardChunkBytes / Math.Max(1L, (long)Width * Height * 2), 4, 120);
        long end = first - 1;
        while (end >= last)
        {
            long begin = Math.Max(last, end - chunk + 1);
            var frames = new List<(long Index, byte[] Data, int Stride, LumaFormat Format)>();
            for (long i = begin; i <= end; i++)
            {
                token.ThrowIfCancellationRequested();
                using DecodedFrame? decoded = _decoder.GetFrame(i, token);
                if (decoded?.Payload is CpuFramePayload payload && payload.Width == Width && payload.Height == Height)
                {
                    LumaFrame luma = Luma(payload);
                    frames.Add((i, luma.Data.ToArray(), luma.Stride, luma.Format));
                }
            }

            for (int k = frames.Count - 1; k >= 0; k--)
            {
                token.ThrowIfCancellationRequested();
                (long index, byte[] data, int stride, LumaFormat format) = frames[k];
                if (!visit(index, new LumaFrame(data, Width, Height, stride, format)))
                {
                    return;
                }
            }

            end = begin - 1;
        }
    }

    public void Dispose() => _decoder.Dispose();

    private static LumaFrame Luma(CpuFramePayload payload)
    {
        ReadOnlySpan<byte> luma = payload.GetPlane(0, out int stride);
        LumaFormat format = payload.PixelFormat switch
        {
            FramePixelFormat.P010 => LumaFormat.Gray16High,
            FramePixelFormat.Yuv420P10 => LumaFormat.Gray16Low10,
            FramePixelFormat.Nv12 or FramePixelFormat.Yuv420P => LumaFormat.Gray8,
            _ => throw new NotSupportedException($"No se puede seguir el formato {payload.FfmpegPixelFormat}."),
        };
        return new LumaFrame(luma, payload.Width, payload.Height, stride, format);
    }
}
