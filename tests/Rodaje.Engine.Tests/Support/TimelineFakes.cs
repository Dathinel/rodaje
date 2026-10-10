using Rodaje.Audio;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;
using Rodaje.Media;
using Rodaje.Media.Probe;
using Rodaje.Media.Video;
using Rodaje.Playback;

namespace Rodaje.Engine.Tests.Support;

/// <summary>Contenido de un cuadro falso: de qué archivo y qué índice es.</summary>
internal sealed class FakeFramePayload(string path, long index, LiveCounter live) : IDisposable
{
    private int _disposed;

    public string Path { get; } = path;

    public long Index { get; } = index;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            live.Decrement();
        }
    }
}

internal sealed class LiveCounter
{
    private int _value;

    public int Value => Volatile.Read(ref _value);

    public void Increment() => Interlocked.Increment(ref _value);

    public void Decrement() => Interlocked.Decrement(ref _value);
}

/// <summary>Decodificador falso de un medio: entrega al instante cualquier cuadro dentro de su largo.</summary>
internal sealed class FakeMediaVideo(string path, MediaInfo info, FakeTimelineBackend owner) : IClipVideo
{
    private readonly List<long> _requests = [];

    public string Path { get; } = path;

    public MediaInfo Info => info;

    public VideoStreamInfo Stream => info.Video!;

    public bool UsesHardwareDecoding => true;

    public VideoDecoderStatistics Statistics => new(0, _requests.Count, 0);

    public bool Disposed { get; private set; }

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

    public ValueTask<DecodedFrame?> GetFrameAsync(long frameIndex, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        lock (_requests)
        {
            _requests.Add(frameIndex);
        }

        if (owner.FailFrames.Contains((Path, frameIndex)))
        {
            return ValueTask.FromException<DecodedFrame?>(new MediaException($"Cuadro {frameIndex} dañado."));
        }

        if (frameIndex >= Stream.FrameCount)
        {
            return ValueTask.FromResult<DecodedFrame?>(null);
        }

        owner.Live.Increment();
        Time pts = Time.FromFrames(frameIndex, Stream.FrameRate);
        var frame = new DecodedFrame(frameIndex, pts, Time.FromFrames(frameIndex + 1, Stream.FrameRate) - pts,
            new FakeFramePayload(Path, frameIndex, owner.Live), owner.FrameBytes);
        return ValueTask.FromResult<DecodedFrame?>(frame);
    }

    public void Dispose() => Disposed = true;
}

/// <summary>PCM falso: la muestra izquierda <c>n</c> vale <c>(n % 4096) / 16384</c> y la derecha, una constante del medio.</summary>
internal sealed class FakeTimelinePcm(string path, float right, long lengthFrames) : IPcmSource
{
    private long _position;

    public string Path { get; } = path;

    public int Seeks { get; private set; }

    public bool Disposed { get; private set; }

    public static float Left(long sample) => (sample % 4096) / 16384f;

    public int Read(Span<float> interleaved)
    {
        int frames = (int)Math.Min(interleaved.Length / 2, Math.Max(0, lengthFrames - _position));
        for (int i = 0; i < frames; i++)
        {
            interleaved[i * 2] = Left(_position + i);
            interleaved[(i * 2) + 1] = right;
        }

        _position += frames;
        return frames;
    }

    public void Seek(Time position)
    {
        Seeks++;
        _position = position.ToSamples(48_000);
    }

    public void Dispose() => Disposed = true;
}

/// <summary>Ejecuta el trabajo solo cuando la prueba lo pide, como un decodificador que tarda.</summary>
internal sealed class ManualExecutor : IDecodeExecutor, IDisposable
{
    private readonly Queue<Action> _work = new();

    public int Pending
    {
        get
        {
            lock (_work)
            {
                return _work.Count;
            }
        }
    }

    public bool Disposed { get; private set; }

    public void Post(Action work)
    {
        lock (_work)
        {
            _work.Enqueue(work);
        }
    }

    public int RunAll(int limit = int.MaxValue)
    {
        int ran = 0;
        while (ran < limit)
        {
            Action work;
            lock (_work)
            {
                if (_work.Count == 0)
                {
                    break;
                }

                work = _work.Dequeue();
            }

            work();
            ran++;
        }

        return ran;
    }

    public void Dispose()
    {
        RunAll();
        Disposed = true;
    }
}

internal sealed class ManualTimeProvider : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public void Advance(TimeSpan delta) => Interlocked.Add(ref _ticks, delta.Ticks);
}

internal sealed class FakeTimelineBackend : IClipBackend
{
    private readonly Dictionary<string, MediaInfo> _media = new(StringComparer.OrdinalIgnoreCase);

    public LiveCounter Live { get; } = new();

    public long FrameBytes { get; set; } = 1000;

    /// <summary>Si es verdadero, cada origen decodifica solo cuando la prueba corre su <see cref="ManualExecutor"/>.</summary>
    public bool ManualDecoding { get; init; }

    public HashSet<string> FailOpen { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<(string Path, long Index)> FailFrames { get; } = [];

    public List<FakeMediaVideo> Videos { get; } = [];

    public List<FakeTimelinePcm> Pcms { get; } = [];

    public List<ManualExecutor> Executors { get; } = [];

    public FakeOutput? Output { get; private set; }

    public DisposalLog Log { get; } = new();

    public ManualClock Clock => Output!.ManualClock;

    public void Register(string path, MediaInfo info) => _media[path] = info;

    public IEnumerable<FakeMediaVideo> OpenVideos => Videos.Where(v => !v.Disposed);

    public IClipVideo OpenVideo(string path, VideoDecoderOptions options)
    {
        if (FailOpen.Contains(path) || !_media.TryGetValue(path, out MediaInfo? info))
        {
            throw new MediaException($"No se pudo abrir {path}.");
        }

        var video = new FakeMediaVideo(path, info, this);
        lock (Videos)
        {
            Videos.Add(video);
        }

        return video;
    }

    public IPcmSource OpenAudio(string path)
    {
        if (FailOpen.Contains(path) || !_media.TryGetValue(path, out MediaInfo? info))
        {
            throw new MediaException($"No se pudo abrir el audio de {path}.");
        }

        var pcm = new FakeTimelinePcm(path, RightValue(path), info.Duration.ToSamples(48_000));
        lock (Pcms)
        {
            Pcms.Add(pcm);
        }

        return pcm;
    }

    public IAudioOutput OpenOutput(IAudioSampleSource source, AudioOutputOptions options) =>
        Output = new FakeOutput(source, hasDevice: true, Log);

    public IDecodeExecutor CreateExecutor()
    {
        if (!ManualDecoding)
        {
            return new InlineExecutor(Log);
        }

        var executor = new ManualExecutor();
        Executors.Add(executor);
        return executor;
    }

    /// <summary>Corre todo el trabajo pendiente de todos los orígenes hasta que no quede nada.</summary>
    public void RunDecoders()
    {
        while (Executors.ToList().Sum(e => e.RunAll()) > 0)
        {
        }
    }

    public static float RightValue(string path) => (Math.Abs(StringComparer.Ordinal.GetHashCode(path)) % 50) / 1000f;
}

/// <summary>Arma proyectos para las pruebas: medios en <c>C:\medios</c> y una secuencia con las pistas pedidas.</summary>
internal sealed class TimelineBuilder
{
    private int _clips;

    public TimelineBuilder(int videoTracks = 3, int audioTracks = 2, Rational? rate = null)
    {
        Sequence = new Sequence { Id = "sec", Name = "Secuencia", FrameRate = rate ?? Rational.Fps30, Width = 1920, Height = 1080 };
        for (int i = 0; i < videoTracks; i++)
        {
            Sequence.VideoTracks.Add(new Track { Id = $"v{i}", Kind = TrackKind.Video, Name = $"Video {i + 1}" });
        }

        for (int i = 0; i < audioTracks; i++)
        {
            Sequence.AudioTracks.Add(new Track { Id = $"a{i}", Kind = TrackKind.Audio, Name = $"Audio {i + 1}" });
        }

        Project = new Project { Id = "p", Name = "Prueba", Sequences = [Sequence] };
    }

    public Project Project { get; }

    public Sequence Sequence { get; }

    public static string PathOf(string mediaId) => $@"C:\medios\{mediaId}.mp4";

    public MediaRef AddMedia(string id, Rational rate, long frames, bool video = true, bool audio = true, FakeTimelineBackend? backend = null)
    {
        Time duration = Time.FromFrames(frames, rate);
        var media = new MediaRef
        {
            Id = id,
            Path = PathOf(id),
            Name = id,
            Duration = duration,
            HasVideo = video,
            HasAudio = audio,
            FrameRate = video ? rate : null,
            Width = 1920,
            Height = 1080,
            SampleRate = 48_000,
            AudioChannels = 2,
        };
        Project.Media.Add(media);
        backend?.Register(media.Path, Samples.Info(rate, frames, audio) with { Path = media.Path, Video = video ? Samples.Info(rate, frames).Video : null });
        return media;
    }

    public Clip AddVideo(int track, string mediaId, Time start, Time sourceIn, Time duration, string? id = null) =>
        Add(Sequence.VideoTracks[track], mediaId, start, sourceIn, duration, id);

    public Clip AddAudio(int track, string mediaId, Time start, Time sourceIn, Time duration, string? id = null) =>
        Add(Sequence.AudioTracks[track], mediaId, start, sourceIn, duration, id);

    public TimelinePlan Plan() => TimelinePlan.Build(Project, Sequence.Id, @"C:\proyecto");

    private Clip Add(Track track, string mediaId, Time start, Time sourceIn, Time duration, string? id)
    {
        var clip = new Clip
        {
            Id = id ?? $"c{++_clips}",
            MediaId = mediaId,
            Start = start,
            SourceIn = sourceIn,
            SourceOut = sourceIn + duration,
        };
        track.Clips.Add(clip);
        track.SortClips();
        return clip;
    }
}

internal static class Seconds
{
    public static Time S(double seconds) => Time.FromSeconds(seconds);

    public static Time F(long frame, Rational rate) => Time.FromFrames(frame, rate);
}
