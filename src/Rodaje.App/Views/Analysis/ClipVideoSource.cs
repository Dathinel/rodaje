using Rodaje.Analysis;
using Rodaje.Analysis.Video;
using Rodaje.App.Services;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Probe;
using Rodaje.Media.Proxies;
using Rodaje.Media.Video;
using Rodaje.Playback;

namespace Rodaje.App.Views.Analysis;

/// <summary>Decodifica el video de un clip en la CPU para los análisis de imagen, fuera del hilo de la interfaz.</summary>
internal static class ClipVideoSource
{
    /// <summary>Muestras de color repartidas por el clip.</summary>
    public const int ColorSamples = 16;

    private const int ColorHeight = 270;

    /// <summary>El clip elegido: el primero seleccionado que tiene imagen (no un título).</summary>
    public static Clip? SelectedVideoClip(EditorSession session)
    {
        foreach (string id in session.SelectedClipIds)
        {
            if (session.Sequence.FindClip(id) is { } found && found.Clip.Generator is null
                && session.Project.FindMedia(found.Clip.MediaId) is { HasVideo: true, FrameRate: not null })
            {
                return found.Clip;
            }
        }

        return null;
    }

    /// <summary>
    /// El análisis de movimiento del tramo del medio que usa el clip: de la caché si ya está, o decodificando cada cuadro
    /// en la CPU (D3D11VA no hace falta y no gasta la memoria de video de la vista previa). Devuelve la clave y el análisis.
    /// </summary>
    public static Task<(string Key, MotionTrack Track)> AnalyzeMotionAsync(
        EditorSession session, Clip clip, StabilizationStore store, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        MediaRef media = session.Project.FindMedia(clip.MediaId)
            ?? throw new InvalidOperationException("El clip usa un medio que ya no está en el proyecto.");
        if (media.IsStillImage)
        {
            throw new NotSupportedException("El seguimiento necesita un video; esta es una imagen fija");
        }

        string path = media.Path;
        (Time sourceIn, Time sourceOut) = ClipAudioMap.SourceWindow(clip);
        return Task.Run(() => Motion(path, sourceIn, sourceOut, store, progress, cancellationToken), cancellationToken);
    }

    /// <summary>La estadística de color de <see cref="ColorSamples"/> cuadros repartidos por el clip, en miniaturas de 270 px.</summary>
    public static Task<ColorEstimate> AnalyzeColorAsync(EditorSession session, Clip clip, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        MediaRef media = session.Project.FindMedia(clip.MediaId)
            ?? throw new InvalidOperationException("El clip usa un medio que ya no está en el proyecto.");
        if (media.IsStillImage)
        {
            throw new NotSupportedException("El seguimiento necesita un video; esta es una imagen fija");
        }

        string path = media.Path;
        (Time sourceIn, Time sourceOut) = ClipAudioMap.SourceWindow(clip);
        return Task.Run(() =>
        {
            using ThumbnailDecoder decoder = ThumbnailDecoder.Open(path);
            var statistics = new ColorStatistics();
            long span = Math.Max(1, (sourceOut - sourceIn).Ticks);
            for (int i = 0; i < ColorSamples; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Time at = sourceIn + Time.FromTicks(span * ((2 * i) + 1) / (2 * ColorSamples));
                if (decoder.Decode(at, ColorHeight, cancellationToken) is { } image)
                {
                    statistics.PushBgra(image.Pixels, image.Width, image.Height, image.Width * 4);
                }

                progress?.Report((i + 1) / (double)ColorSamples);
            }

            return statistics.Estimate();
        }, cancellationToken);
    }

    internal static (string Key, MotionTrack Track) Motion(string path, Time sourceIn, Time sourceOut, StabilizationStore store,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using VideoDecoder decoder = VideoDecoder.Open(path, new VideoDecoderOptions { AllowHardwareDecoding = false });
        if (decoder.Info.IsStillImage)
        {
            throw new NotSupportedException("El seguimiento necesita un video; esta es una imagen fija");
        }

        VideoStreamInfo stream = decoder.Stream;
        Rational rate = decoder.FrameRate;
        long first = Math.Max(0, sourceIn.ToFrames(rate));
        long end = Math.Max(first + 1, sourceOut.ToFrames(rate));
        if (stream.FrameCount > 0)
        {
            end = Math.Min(end, stream.FrameCount);
        }

        string key = StabilizationStore.KeyFor(SourceFingerprint.Compute(path).Key, first, end - first);
        if (store.Load(key) is { } cached)
        {
            progress?.Report(1);
            return (key, cached);
        }

        MotionEstimator? estimator = null;
        int reported = -1;
        for (long i = first; i < end; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using DecodedFrame? frame = decoder.GetFrame(i, cancellationToken);
            if (frame?.Payload is not CpuFramePayload payload)
            {
                if (estimator is null)
                {
                    continue;
                }

                break;
            }

            ReadOnlySpan<byte> luma = payload.GetPlane(0, out int stride);
            LumaFormat format = LumaFormatOf(payload);
            estimator ??= new MotionEstimator(payload.Width, payload.Height);
            if (payload.Width != estimator.SourceWidth || payload.Height != estimator.SourceHeight)
            {
                break;
            }

            estimator.Push(new LumaFrame(luma, payload.Width, payload.Height, stride, format));
            int percent = (int)((i - first + 1) * 100 / (end - first));
            if (percent != reported)
            {
                reported = percent;
                progress?.Report(percent / 100.0);
            }
        }

        if (estimator is null || estimator.Motions.Count == 0)
        {
            throw new InvalidOperationException("El clip no tiene fotogramas que se puedan analizar.");
        }

        (int width, int height, FrameMotion[] motions) = ToDisplay(stream, estimator);
        var track = new MotionTrack(width, height, rate, first, motions);
        store.Save(key, track);
        return (key, track);
    }

    /// <summary>La luma de cualquier YUV plano: 4:2:0, y también 4:2:2, 4:4:4 y con alfa (ProRes, DNxHR, lo que sale de CinemaDNG).</summary>
    internal static LumaFormat LumaFormatOf(CpuFramePayload payload) => payload.PixelFormat switch
    {
        FramePixelFormat.P010 => LumaFormat.Gray16High,
        FramePixelFormat.Yuv420P10 => LumaFormat.Gray16Low10,
        FramePixelFormat.Nv12 or FramePixelFormat.Yuv420P => LumaFormat.Gray8,
        _ when payload.TryGetLumaLayout(out int depth, out int step, out int offset) && offset == 0 => (depth, step) switch
        {
            (8, 1) => LumaFormat.Gray8,
            (10, 2) => LumaFormat.Gray16Low10,
            (12, 2) => LumaFormat.Gray16Low12,
            (16, 2) => LumaFormat.Gray16High,
            _ => throw new NotSupportedException($"No se puede analizar el formato {payload.FfmpegPixelFormat}."),
        },
        _ => throw new NotSupportedException($"No se puede analizar el formato {payload.FfmpegPixelFormat}."),
    };

    /// <summary>
    /// El análisis mide en la imagen codificada; la corrección va en la que se ve. Con un giro de 90° o 270° la traslación
    /// gira con la imagen y cambia de unidad (los altos se miden en otro lado). El giro y la escala no cambian.
    /// </summary>
    private static (int Width, int Height, FrameMotion[] Motions) ToDisplay(VideoStreamInfo stream, MotionEstimator estimator)
    {
        int rotation = ((stream.Rotation % 360) + 360) % 360;
        int width = estimator.SourceWidth;
        int height = estimator.SourceHeight;
        if (rotation is not (90 or 180 or 270))
        {
            return (width, height, [.. estimator.Motions]);
        }

        bool swap = rotation != 180;
        double unit = swap ? (double)height / width : 1;
        double radians = rotation * Math.PI / 180;
        double cos = Math.Round(Math.Cos(radians));
        double sin = Math.Round(Math.Sin(radians));
        FrameMotion[] motions = [.. estimator.Motions.Select(m => m with
        {
            Motion = m.Motion with
            {
                X = ((m.Motion.X * cos) - (m.Motion.Y * sin)) * unit,
                Y = ((m.Motion.X * sin) + (m.Motion.Y * cos)) * unit,
            },
        })];
        return swap ? (height, width, motions) : (width, height, motions);
    }
}
