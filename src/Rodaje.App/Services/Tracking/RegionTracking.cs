using System.Numerics;
using Rodaje.Analysis.Video;
using Rodaje.Core.Effects;
using Rodaje.Core.Timing;
using Rodaje.Media.Video;
using Rodaje.Playback;

namespace Rodaje.App.Services.Tracking;

/// <summary>Qué seguir: la región y los puntos sueltos en la imagen de la capa (0–1) en el cuadro de partida.</summary>
/// <param name="MediaPath">El archivo del clip.</param>
/// <param name="Start">Instante del origen del cuadro de partida.</param>
/// <param name="Limit">Hasta dónde (incluido) en el sentido elegido: la salida o la entrada del clip.</param>
/// <param name="Forward">Hacia adelante o hacia atrás.</param>
/// <param name="Region">El contorno que se sigue.</param>
/// <param name="Anchors">Puntos que se siguen cada uno con su zona (vértices de una máscara, esquinas de un título).</param>
/// <param name="CropAt">El recorte de la capa en un instante del origen (para pasar de la imagen al cuadro codificado).</param>
public sealed record TrackRequest(string MediaPath, Time Start, Time Limit, bool Forward, IReadOnlyList<Vector2> Region,
    IReadOnlyList<Vector2> Anchors, Func<Time, CropRect> CropAt);

/// <summary>
/// Seguimiento de una región en el video de un clip, cuadro a cuadro desde el actual, hacia adelante o hacia atrás, con
/// <see cref="RegionTracker"/>. Lo usan las máscaras y sirve igual para títulos o pegatinas: devuelve en cada cuadro dónde
/// quedó cualquier punto de la imagen de partida (<see cref="TrackedFrame.Map"/>). Decodifica en la CPU fuera del hilo de
/// la interfaz; hacia atrás lee tramos hacia adelante y los recorre al revés.
/// </summary>
public static class RegionTracking
{
    /// <summary>Cuadros que se guardan por tramo al ir hacia atrás (lumas completas).</summary>
    private const int BackwardChunkBytes = 192 * 1024 * 1024;

    /// <summary>
    /// Sigue la región y llama a <paramref name="frame"/> con cada cuadro (desde el hilo de fondo). Termina al llegar a
    /// <see cref="TrackRequest.Limit"/>, al cancelar o si el video se acaba; lo seguido hasta ahí ya se entregó.
    /// </summary>
    public static Task RunAsync(TrackRequest request, Action<TrackedFrame> frame, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(frame);
        if (request.Region.Count < 3)
        {
            throw new ArgumentException("La región necesita al menos tres puntos.", nameof(request));
        }

        return Task.Run(() => Run(request, frame, progress, cancellationToken), cancellationToken);
    }

    private static void Run(TrackRequest request, Action<TrackedFrame> report, IProgress<double>? progress, CancellationToken token)
    {
        using VideoDecoder decoder = VideoDecoder.Open(request.MediaPath, new VideoDecoderOptions { AllowHardwareDecoding = false });
        Rational rate = decoder.FrameRate;
        long first = Math.Max(0, request.Start.ToFrames(rate));
        long last = Math.Max(0, request.Limit.ToFrames(rate));
        if (decoder.FrameCount > 0)
        {
            first = Math.Min(first, decoder.FrameCount - 1);
            last = Math.Min(last, decoder.FrameCount - 1);
        }

        int direction = request.Forward ? 1 : -1;
        long total = Math.Max(1, Math.Abs(last - first));
        if ((last - first) * direction <= 0)
        {
            progress?.Report(1);
            return;
        }

        int rotation = decoder.Stream.Rotation;
        RegionTracker? tracker = null;
        SourceView start = default;
        using (DecodedFrame? decoded = decoder.GetFrame(first, token))
        {
            if (decoded?.Payload is not CpuFramePayload payload)
            {
                throw new InvalidOperationException("No se pudo leer el cuadro de partida.");
            }

            start = new SourceView(payload.Width, payload.Height, rotation, request.CropAt(Time.FromFrames(first, rate)));
            tracker = new RegionTracker(payload.Width, payload.Height);
            Vector2[] region = [.. request.Region.Select(start.ToCoded)];
            Vector2[] anchors = [.. request.Anchors.Select(start.ToCoded)];
            tracker.Start(Luma(payload), region, anchors);
        }

        long done = 0;
        void Step(long index, LumaFrame luma)
        {
            RegionStep step = tracker!.Step(luma);
            Time time = Time.FromFrames(index, rate);
            var now = new SourceView(luma.Width, luma.Height, rotation, request.CropAt(time));
            report(new TrackedFrame(time, step.Motion, [.. step.Anchors.Select(now.ToImage)], start, now, step.Lost));
            done++;
            progress?.Report(Math.Min(1, done / (double)total));
        }

        if (request.Forward)
        {
            for (long i = first + 1; i <= last; i++)
            {
                token.ThrowIfCancellationRequested();
                using DecodedFrame? decoded = decoder.GetFrame(i, token);
                if (decoded?.Payload is not CpuFramePayload payload || payload.Width != tracker.SourceWidth || payload.Height != tracker.SourceHeight)
                {
                    return;
                }

                Step(i, Luma(payload));
            }

            return;
        }

        // Hacia atrás: tramos leídos hacia adelante (el decodificador es secuencial), con copia de la luma, y recorridos al revés.
        int chunk = (int)Math.Clamp(BackwardChunkBytes / Math.Max(1L, (long)tracker.SourceWidth * tracker.SourceHeight * 2), 4, 120);
        long end = first - 1;
        while (end >= last)
        {
            long begin = Math.Max(last, end - chunk + 1);
            var frames = new List<(long Index, byte[] Data, int Stride, LumaFormat Format)>();
            for (long i = begin; i <= end; i++)
            {
                token.ThrowIfCancellationRequested();
                using DecodedFrame? decoded = decoder.GetFrame(i, token);
                if (decoded?.Payload is CpuFramePayload payload && payload.Width == tracker.SourceWidth && payload.Height == tracker.SourceHeight)
                {
                    LumaFrame luma = Luma(payload);
                    frames.Add((i, luma.Data.ToArray(), luma.Stride, luma.Format));
                }
            }

            for (int k = frames.Count - 1; k >= 0; k--)
            {
                token.ThrowIfCancellationRequested();
                (long index, byte[] data, int stride, LumaFormat format) = frames[k];
                Step(index, new LumaFrame(data, tracker.SourceWidth, tracker.SourceHeight, stride, format));
            }

            end = begin - 1;
        }
    }

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
