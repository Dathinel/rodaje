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

/// <summary>
/// Decodificador falso de un medio: entrega al instante cualquier cuadro dentro de su largo. Con
/// <see cref="FakeTimelineBackend.KeyframeInterval"/> conoce sus keyframes, uno cada tantos cuadros.
/// </summary>
internal sealed class FakeMediaVideo(string path, MediaInfo info, FakeTimelineBackend owner) : IClipVideo, IKeyframeSource
{
    public IKeyframeIndex? Keyframes { get; } = owner.KeyframeInterval is { } gop
        ? new SortedKeyframes(Enumerable.Range(0, (int)((info.Video!.FrameCount + gop - 1) / gop)).Select(k => (long)k * gop))
        : null;

    private readonly List<long> _requests = [];
    private volatile bool _disposed;
    private long _last = -1;

    /// <summary>Cuadros que habría decodificado uno de verdad (ver <see cref="GetFrameAsync(long, CancellationToken)"/>).</summary>
    public long DecodedFrames { get; private set; }

    /// <summary>Peticiones que habrían buscado un keyframe.</summary>
    public long Seeks { get; private set; }

    public string Path { get; } = path;

    /// <summary>Texturas extra que pidió el programador para el pool simulado; -1 si no se simuló.</summary>
    public int ExtraSurfaces { get; init; } = -1;

    public MediaInfo Info => info;

    public VideoStreamInfo Stream => info.Video!;

    public bool UsesHardwareDecoding => true;

    public int? SpareSurfaces => ExtraSurfaces >= 0
        ? Math.Min(Rodaje.Media.Video.VideoDecoder.MaxHardwarePoolSurfaces, owner.PoolReferences + ExtraSurfaces) - owner.PoolReferences
        : null;

    public VideoDecoderStatistics Statistics => new(0, _requests.Count, 0);

    public bool Disposed => _disposed;

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

    /// <summary>Exactitud de cada petición, en orden.</summary>
    public List<DecodeAccuracy> Accuracies { get; } = [];

    public ValueTask<DecodedFrame?> GetFrameAsync(long frameIndex, DecodeAccuracy accuracy, CancellationToken cancellationToken)
    {
        lock (_requests)
        {
            Accuracies.Add(accuracy);
        }

        return GetFrameAsync(frameIndex, cancellationToken);
    }

    public ValueTask<DecodedFrame?> GetFrameAsync(long frameIndex, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        lock (_requests)
        {
            _requests.Add(frameIndex);

            // Lo que costaría a un decodificador de verdad: seguir hacia adelante sin buscar hasta 64 cuadros, o buscar
            // el keyframe y decodificar desde él.
            if (_last >= 0 && frameIndex > _last && frameIndex - _last <= 64)
            {
                DecodedFrames += frameIndex - _last;
            }
            else
            {
                Seeks++;
                long keyframe = Keyframes?.AtOrBefore(frameIndex) ?? -1;
                DecodedFrames += frameIndex - (keyframe >= 0 ? keyframe : frameIndex) + 1;
            }

            _last = frameIndex;
        }

        if (owner.FailFrames.Contains((Path, frameIndex)))
        {
            return ValueTask.FromException<DecodedFrame?>(new MediaException($"Cuadro {frameIndex} dañado."));
        }

        if (owner.AsyncDecode)
        {
            // Como uno que termina en otro hilo: la continuación del programador corre allí.
            return new ValueTask<DecodedFrame?>(Task.Run(() => Decode(frameIndex, cancellationToken), CancellationToken.None));
        }

        if (owner.DecodeDelay is null)
        {
            return ValueTask.FromResult(Produce(frameIndex));
        }

        try
        {
            return ValueTask.FromResult(Decode(frameIndex, cancellationToken));
        }
        catch (OperationCanceledException ex)
        {
            return ValueTask.FromException<DecodedFrame?>(ex);
        }
    }

    public void Dispose()
    {
        if (owner.DisposeDelay > TimeSpan.Zero)
        {
            Thread.Sleep(owner.DisposeDelay);
        }

        _disposed = true;
    }

    /// <summary>Tarda lo que diga <see cref="FakeTimelineBackend.DecodeDelay"/>; se corta si cancelan la petición.</summary>
    private DecodedFrame? Decode(long frameIndex, CancellationToken cancellationToken)
    {
        if (owner.DecodeDelay?.Invoke(frameIndex) is { } delay && delay > TimeSpan.Zero)
        {
            Interlocked.Increment(ref owner.DelaysInProgress);
            try
            {
                cancellationToken.WaitHandle.WaitOne(delay);
            }
            finally
            {
                Interlocked.Decrement(ref owner.DelaysInProgress);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Produce(frameIndex);
    }

    private DecodedFrame? Produce(long frameIndex)
    {
        if (frameIndex >= Stream.FrameCount)
        {
            return null;
        }

        owner.Live.Increment();
        Time pts = Time.FromFrames(frameIndex, Stream.FrameRate);
        var frame = new DecodedFrame(frameIndex, pts, Time.FromFrames(frameIndex + 1, Stream.FrameRate) - pts,
            new FakeFramePayload(Path, frameIndex, owner.Live), owner.FrameBytes);
        return frame;
    }
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

    /// <summary>Hilos reales (<see cref="DecodeThread"/>) en lugar de ejecutar en el acto.</summary>
    public bool RealThreads { get; init; }

    /// <summary>Lo que tarda cada cuadro (por su índice), como un decodificador lento; respeta la cancelación.</summary>
    public Func<long, TimeSpan>? DecodeDelay { get; init; }

    /// <summary>Cada cuadro termina en otro hilo (del grupo), no en el del decodificador.</summary>
    public bool AsyncDecode { get; init; }

    /// <summary>Cuadros que están dentro de la espera de <see cref="DecodeDelay"/> (se lee con <see cref="Volatile"/>).</summary>
    internal int DelaysInProgress;

    /// <summary>Lo que tarda en cerrarse cada decodificador, como uno real que libera su pool.</summary>
    public TimeSpan DisposeDelay { get; init; }

    /// <summary>Si se da, cada decodificador simula un pool de D3D11VA con superficies de este tamaño y pide su tamaño al programador.</summary>
    public long? PoolSurfaceBytes { get; init; }

    public int PoolReferences { get; init; } = 10;

    /// <summary>Si se da, cada decodificador conoce sus keyframes: uno cada tantos cuadros.</summary>
    public int? KeyframeInterval { get; init; }

    public HashSet<string> FailOpen { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<(string Path, long Index)> FailFrames { get; } = [];

    public List<FakeMediaVideo> Videos { get; } = [];

    public List<FakeTimelinePcm> Pcms { get; } = [];

    public List<ManualExecutor> Executors { get; } = [];

    public FakeOutput? Output { get; private set; }

    public DisposalLog Log { get; } = new();

    public ManualClock Clock => Output!.ManualClock;

    public void Register(string path, MediaInfo info) => _media[path] = info;

    public IEnumerable<FakeMediaVideo> OpenVideos
    {
        get
        {
            lock (Videos)
            {
                return [.. Videos.Where(v => !v.Disposed)];
            }
        }
    }

    public IClipVideo OpenVideo(string path, VideoDecoderOptions options)
    {
        if (FailOpen.Contains(path) || !_media.TryGetValue(path, out MediaInfo? info))
        {
            throw new MediaException($"No se pudo abrir {path}.");
        }

        int extra = PoolSurfaceBytes is { } surface && options.HardwarePoolSizer is { } sizer
            ? sizer(new HardwarePoolRequest(1920, 1088, surface, PoolReferences))
            : -1;
        var video = new FakeMediaVideo(path, info, this) { ExtraSurfaces = extra };
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
        if (RealThreads)
        {
            return new DecodeThread();
        }

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

    /// <remarks>Fuera de Windows «C:\» no es una ruta absoluta: allí los medios van en <c>/medios</c>, para correr las pruebas en Linux.</remarks>
    public static string PathOf(string mediaId) => OperatingSystem.IsWindows() ? $@"C:\medios\{mediaId}.mp4" : $"/medios/{mediaId}.mp4";

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

    /// <summary>Un título (clip con generador de texto) en una pista de video; su origen virtual empieza en <paramref name="sourceIn"/>.</summary>
    public Clip AddTitle(int track, Time start, Time duration, string text, TextStyle? style = null, string? id = null, Time? sourceIn = null)
    {
        Time origin = sourceIn ?? Time.Zero;
        var clip = new Clip
        {
            Id = id ?? $"c{++_clips}",
            Start = start,
            SourceIn = origin,
            SourceOut = origin + duration,
            Generator = new ClipGenerator { Text = text, Style = style ?? new TextStyle() },
        };
        Track target = Sequence.VideoTracks[track];
        target.Clips.Add(clip);
        target.SortClips();
        return clip;
    }

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
