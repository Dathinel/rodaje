using Rodaje.Audio;
using Rodaje.Core.Timing;
using Rodaje.Media;
using Rodaje.Media.Audio;
using Rodaje.Media.Probe;
using Rodaje.Media.Video;
using Rodaje.Playback;

namespace Rodaje.Engine;

/// <summary>Las piezas que <see cref="ClipPlayer"/> conecta; las pruebas las reemplazan por dobles sin hardware.</summary>
internal interface IClipBackend
{
    IClipVideo OpenVideo(string path, VideoDecoderOptions options);

    IPcmSource OpenAudio(string path);

    IAudioOutput OpenOutput(IAudioSampleSource source, AudioOutputOptions options);

    IDecodeExecutor CreateExecutor();

    /// <summary>Lee los keyframes del archivo (decenas de milisegundos); nulo si no se pueden conocer.</summary>
    Timeline.IKeyframeIndex? ReadKeyframes(string path) => null;
}

/// <summary>Flujo de video que se puede pedir por índice de cuadro.</summary>
internal interface IClipVideo : IVideoFrameSource, IDisposable
{
    MediaInfo Info { get; }

    VideoStreamInfo Stream { get; }

    bool UsesHardwareDecoding { get; }

    VideoDecoderStatistics Statistics { get; }

    /// <summary>
    /// Superficies del pool de D3D11VA fuera de las referencias (<see cref="Rodaje.Media.Gpu.DecoderSurfacePool.SpareSurfaces"/>);
    /// nulo sin pool o antes del primer cuadro, que es cuando se crea.
    /// </summary>
    int? SpareSurfaces => null;

    /// <summary>
    /// Pide el cuadro con la exactitud dada (<see cref="DecodeAccuracy"/>): uno aproximado trae su índice real, que puede
    /// ser menor que el pedido. Sin modos rápidos es siempre exacto.
    /// </summary>
    ValueTask<DecodedFrame?> GetFrameAsync(long frameIndex, DecodeAccuracy accuracy, CancellationToken cancellationToken) =>
        GetFrameAsync(frameIndex, cancellationToken);
}

/// <summary>Audio decodificado a float estéreo a 48 kHz, con seek exacto. Se usa desde un solo hilo.</summary>
internal interface IPcmSource : IDisposable
{
    int Read(Span<float> interleaved);

    void Seek(Time position);
}

internal sealed class MediaClipBackend : IClipBackend
{
    public static MediaClipBackend Instance { get; } = new();

    public IClipVideo OpenVideo(string path, VideoDecoderOptions options) => new DecoderVideo(VideoDecoder.Open(path, options));

    public IPcmSource OpenAudio(string path) => new DecoderPcm(AudioDecoder.Open(path));

    public IAudioOutput OpenOutput(IAudioSampleSource source, AudioOutputOptions options) => AudioOutput.Open(source, options);

    public IDecodeExecutor CreateExecutor() => new DecodeThread();

    public Timeline.IKeyframeIndex? ReadKeyframes(string path)
    {
        try
        {
            return new MediaKeyframes(KeyframeIndex.Read(path));
        }
        catch (Exception ex) when (ex is MediaException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed class DecoderVideo(VideoDecoder decoder) : IClipVideo
    {
        public MediaInfo Info => decoder.Info;

        public VideoStreamInfo Stream => decoder.Stream;

        public bool UsesHardwareDecoding => decoder.UsesHardwareDecoding;

        public VideoDecoderStatistics Statistics => decoder.Statistics;

        public int? SpareSurfaces => decoder.SurfacePool?.SpareSurfaces;

        public ValueTask<DecodedFrame?> GetFrameAsync(long frameIndex, CancellationToken cancellationToken) =>
            decoder.GetFrameAsync(frameIndex, cancellationToken);

        public ValueTask<DecodedFrame?> GetFrameAsync(long frameIndex, DecodeAccuracy accuracy, CancellationToken cancellationToken)
        {
            if (accuracy == DecodeAccuracy.Exact)
            {
                return decoder.GetFrameAsync(frameIndex, cancellationToken);
            }

            try
            {
                return ValueTask.FromResult(decoder.GetFrame(frameIndex, accuracy, cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromCanceled<DecodedFrame?>(cancellationToken);
            }
            catch (Exception ex) when (ex is not ArgumentException)
            {
                return ValueTask.FromException<DecodedFrame?>(ex);
            }
        }

        public void Dispose() => decoder.Dispose();
    }

    /// <summary>El índice de keyframes de Medios visto por el motor; el decodificador lo recibe en sus opciones.</summary>
    internal sealed class MediaKeyframes(KeyframeIndex index) : Timeline.IKeyframeIndex
    {
        public KeyframeIndex Index => index;

        public long AtOrBefore(long frame) => index.AtOrBefore(frame) ?? -1;

        public long AtOrAfter(long frame) => index.After(frame - 1) ?? long.MaxValue;
    }

    private sealed class DecoderPcm(AudioDecoder decoder) : IPcmSource
    {
        public int Read(Span<float> interleaved) => decoder.Read(interleaved);

        public void Seek(Time position) => decoder.Seek(position);

        public void Dispose() => decoder.Dispose();
    }
}
