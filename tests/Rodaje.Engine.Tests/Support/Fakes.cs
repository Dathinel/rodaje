using Rodaje.Audio;
using Rodaje.Core.Timing;
using Rodaje.Media;
using Rodaje.Media.Probe;
using Rodaje.Media.Video;
using Rodaje.Playback;

namespace Rodaje.Engine.Tests.Support;

internal static class Samples
{
    public static MediaInfo Info(Rational rate, long frames, bool audio = true, int width = 1920, int height = 1080, int bitDepth = 8)
    {
        Time duration = Time.FromFrames(frames, rate);
        var video = new VideoStreamInfo
        {
            StreamIndex = 0,
            Codec = "h264",
            PixelFormat = bitDepth > 8 ? "yuv420p10le" : "yuv420p",
            BitDepth = bitDepth,
            Width = width,
            Height = height,
            SampleAspectRatio = new Rational(1, 1),
            Rotation = 0,
            DisplayWidth = width,
            DisplayHeight = height,
            FrameRate = rate,
            IsVariableFrameRate = false,
            FrameCount = frames,
            Duration = duration,
            StartTime = Time.Zero,
        };
        return new MediaInfo
        {
            Path = "falso.mp4",
            ContainerFormat = "mov,mp4",
            Duration = duration,
            ContainerStartTime = Time.Zero,
            Video = video,
            Audio = audio
                ? new AudioStreamInfo { StreamIndex = 1, Codec = "aac", SampleRate = 48_000, Channels = 2, Duration = duration, StartTime = Time.Zero }
                : null,
        };
    }
}

/// <summary>Registro compartido del orden en que se liberan las piezas.</summary>
internal sealed class DisposalLog
{
    private readonly List<string> _entries = [];

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    public void Add(string name)
    {
        lock (_entries)
        {
            _entries.Add(name);
        }
    }
}

internal sealed class FakeVideo(MediaInfo info, DisposalLog log) : IClipVideo
{
    private readonly List<long> _requests = [];
    private int _live;

    public MediaInfo Info => info;

    public VideoStreamInfo Stream => info.Video!;

    public bool UsesHardwareDecoding { get; set; } = true;

    public VideoDecoderStatistics Statistics => new(0, _requests.Count, 0);

    public IReadOnlyList<long> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public int LiveFrames => Volatile.Read(ref _live);

    public ValueTask<DecodedFrame?> GetFrameAsync(long frameIndex, CancellationToken cancellationToken)
    {
        lock (_requests)
        {
            _requests.Add(frameIndex);
        }

        if (frameIndex >= Stream.FrameCount)
        {
            return ValueTask.FromResult<DecodedFrame?>(null);
        }

        Interlocked.Increment(ref _live);
        Time pts = Time.FromFrames(frameIndex, Stream.FrameRate);
        // Como D3D11VA: la textura trae relleno hasta un múltiplo de 16 líneas (1080 → 1088).
        long size = (long)Stream.Width * ((Stream.Height + 15) / 16 * 16) * 3 / 2;
        var frame = new DecodedFrame(frameIndex, pts, Time.FromFrames(frameIndex + 1, Stream.FrameRate) - pts,
            new Payload(this), size);
        return ValueTask.FromResult<DecodedFrame?>(frame);
    }

    public void Dispose() => log.Add("video");

    private sealed class Payload(FakeVideo owner) : IDisposable
    {
        public void Dispose() => Interlocked.Decrement(ref owner._live);
    }
}

/// <summary>PCM sintético: la muestra izquierda del cuadro <c>n</c> (contado desde 0 del medio) vale <c>n</c>.</summary>
internal sealed class FakePcm(DisposalLog? log = null, long lengthFrames = long.MaxValue) : IPcmSource
{
    private long _position;

    /// <summary>Si no es nulo, cada lectura espera a que se libere (simula un decodificador lento).</summary>
    public SemaphoreSlim? Gate { get; set; }

    public Exception? ThrowOnRead { get; set; }

    public int Seeks { get; private set; }

    public int Read(Span<float> interleaved)
    {
        Gate?.Wait();
        if (ThrowOnRead is { } ex)
        {
            throw ex;
        }

        int frames = (int)Math.Min(interleaved.Length / 2, Math.Max(0, lengthFrames - _position));
        for (int i = 0; i < frames; i++)
        {
            interleaved[i * 2] = _position + i;
            interleaved[(i * 2) + 1] = -(_position + i);
        }

        _position += frames;
        return frames;
    }

    public void Seek(Time position)
    {
        Seeks++;
        _position = position.ToSamples(48_000);
    }

    public void Dispose() => log?.Add("pcm");
}

internal sealed class FakeOutput(IAudioSampleSource source, bool hasDevice, DisposalLog log) : IAudioOutput
{
    public ManualClock ManualClock { get; } = new();

    public IAudioSampleSource Source => source;

    public IAudioClock Clock => ManualClock;

    public bool HasDevice => hasDevice;

    public string? FallbackReason => hasDevice ? null : "No hay altavoces en la prueba.";

    public AudioDeviceInfo? Device => hasDevice
        ? new AudioDeviceInfo("id", "Altavoces de prueba", 2, 48_000, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(10), false, TimeSpan.Zero)
        : null;

    public float Volume { get; set; } = 1f;

    public AudioOutputStats Stats => default;

    public event EventHandler<AudioDeviceChangedEventArgs>? DeviceChanged
    {
        add
        {
        }

        remove
        {
        }
    }

    public void Dispose() => log.Add("output");
}

internal sealed class InlineExecutor(DisposalLog log) : IDecodeExecutor, IDisposable
{
    public void Post(Action work) => work();

    public void Dispose() => log.Add("executor");
}

internal sealed class FakeBackend : IClipBackend
{
    public FakeBackend(MediaInfo info) => Info = info;

    public MediaInfo Info { get; }

    public DisposalLog Log { get; } = new();

    public bool DeviceAvailable { get; set; } = true;

    public Exception? AudioOpenFailure { get; set; }

    public Exception? OutputOpenFailure { get; set; }

    public VideoDecoderOptions? VideoOptions { get; private set; }

    public FakeVideo? Video { get; private set; }

    public FakePcm? Pcm { get; private set; }

    public FakeOutput? Output { get; private set; }

    public int AudioOpens { get; private set; }

    public IClipVideo OpenVideo(string path, VideoDecoderOptions options)
    {
        VideoOptions = options;
        if (Info.Video is null)
        {
            throw new MediaException($"El archivo {path} no tiene un flujo de video.");
        }

        return Video = new FakeVideo(Info, Log);
    }

    public IPcmSource OpenAudio(string path)
    {
        AudioOpens++;
        if (AudioOpenFailure is { } failure)
        {
            throw failure;
        }

        return Pcm = new FakePcm(Log);
    }

    public IAudioOutput OpenOutput(IAudioSampleSource source, AudioOutputOptions options)
    {
        if (OutputOpenFailure is { } failure)
        {
            throw failure;
        }

        return Output = new FakeOutput(source, DeviceAvailable, Log);
    }

    public IDecodeExecutor CreateExecutor() => new InlineExecutor(Log);

    public ClipPlayer Open(ClipPlayerOptions? options = null) => new("falso.mp4", options ?? new ClipPlayerOptions(), this);
}
