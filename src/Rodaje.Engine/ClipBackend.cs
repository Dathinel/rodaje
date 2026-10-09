using Rodaje.Audio;
using Rodaje.Core.Timing;
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
}

/// <summary>Flujo de video que se puede pedir por índice de cuadro.</summary>
internal interface IClipVideo : IVideoFrameSource, IDisposable
{
    MediaInfo Info { get; }

    VideoStreamInfo Stream { get; }

    bool UsesHardwareDecoding { get; }

    VideoDecoderStatistics Statistics { get; }
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

    private sealed class DecoderVideo(VideoDecoder decoder) : IClipVideo
    {
        public MediaInfo Info => decoder.Info;

        public VideoStreamInfo Stream => decoder.Stream;

        public bool UsesHardwareDecoding => decoder.UsesHardwareDecoding;

        public VideoDecoderStatistics Statistics => decoder.Statistics;

        public ValueTask<DecodedFrame?> GetFrameAsync(long frameIndex, CancellationToken cancellationToken) =>
            decoder.GetFrameAsync(frameIndex, cancellationToken);

        public void Dispose() => decoder.Dispose();
    }

    private sealed class DecoderPcm(AudioDecoder decoder) : IPcmSource
    {
        public int Read(Span<float> interleaved) => decoder.Read(interleaved);

        public void Seek(Time position) => decoder.Seek(position);

        public void Dispose() => decoder.Dispose();
    }
}
