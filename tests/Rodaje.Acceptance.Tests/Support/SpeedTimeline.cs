using Rodaje.Audio;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine;
using Rodaje.Engine.Timeline;
using Rodaje.Media.Probe;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.TestMedia;

namespace Rodaje.Acceptance.Tests.Support;

/// <summary>Un caso de velocidad: constante (<see cref="Rate"/>) o cuadro congelado; <see cref="Offset"/> como si el clip viniera de dividir otro.</summary>
internal sealed record SpeedCase(string Name, Rational Rate, bool Freeze = false, long Offset = 0)
{
    public static SpeedCase Named(string name) => All.Single(c => c.Name == name);

    public static IReadOnlyList<SpeedCase> All { get; } =
    [
        new("0,25x", new Rational(1, 4)),
        new("0,5x", new Rational(1, 2)),
        new("1,37x", new Rational(137, 100), Offset: 123_456_789),
        new("2x", new Rational(2, 1)),
        new("4x", new Rational(4, 1)),
        new("1001/1000x", new Rational(1001, 1000), Offset: 7),
        new("100x", new Rational(100, 1)),
        new("congelado", new Rational(1, 1), Freeze: true),
        new("1,5x", new Rational(3, 2)),
    ];
}

/// <summary>
/// Secuencia de 30 fps con un clip del medio de velocidad (y su audio enlazado, si se pide) que empieza en 0. El cuadro
/// esperado se calcula aparte con aritmética entera y se compara con <see cref="ClipTimeMap"/>, el contrato.
/// </summary>
internal sealed class SpeedTimeline
{
    public const string SequenceId = "sec";
    public static readonly Rational SequenceRate = Rational.Fps30;

    private static readonly Time SourceIn = Time.FromSeconds(1.3);

    private SpeedTimeline(Project project, Clip clip, Rational sourceRate)
    {
        Project = project;
        Clip = clip;
        Map = clip.TimeMap;
        SourceRate = sourceRate;
    }

    public Project Project { get; }

    public Clip Clip { get; }

    public ClipTimeMap Map { get; }

    public Rational SourceRate { get; }

    /// <summary>Cuadros de la secuencia que empiezan dentro del clip.</summary>
    public long FrameCount => (Clip.End - Time.FromTicks(1)).ToFrames(SequenceRate) + 1;

    /// <param name="withAudio">Con el audio enlazado; un congelado no lo lleva (el audio no se congela).</param>
    /// <param name="maxTimeline">Si se da, el clip se recorta para no pasar de esa duración en la línea de tiempo.</param>
    public static SpeedTimeline Build(string mediaPath, SpeedCase speedCase, bool withAudio, Time? maxTimeline = null)
    {
        var media = new MediaRef { Id = "m", Path = mediaPath };
        MediaInfo info = MediaProbe.Probe(mediaPath);
        info.ApplyTo(media);
        Rational sourceRate = info.Video?.FrameRate ?? throw new InvalidOperationException("El medio de velocidad no tiene video.");

        var speed = new ClipSpeed { Rate = speedCase.Rate, Freeze = speedCase.Freeze, Offset = Time.FromTicks(speedCase.Offset) };
        ClipTimeMap map = speedCase.Freeze
            ? ClipTimeMap.Create(Time.Zero, maxTimeline ?? Time.FromSeconds(20), SourceIn, speed)
            : ClipTimeMap.Fit(Time.Zero, SourceIn, media.Duration - Time.FromSeconds(0.5), speed);
        if (maxTimeline is { } max && map.Duration > max)
        {
            map = map.TrimOut(max);
        }

        var sequence = new Sequence { Id = SequenceId, Name = "Velocidad", FrameRate = SequenceRate, Width = 1280, Height = 720 };
        var project = new Project { Id = "p", Name = "Velocidad", Media = [media], Sequences = [sequence] };
        withAudio &= !speedCase.Freeze;
        Clip video = NewClip("v", speed, map, withAudio);
        sequence.VideoTracks.Add(new Track { Id = "v1", Kind = TrackKind.Video, Name = "Video", Clips = [video] });
        if (withAudio)
        {
            sequence.AudioTracks.Add(new Track { Id = "a1", Kind = TrackKind.Audio, Name = "Audio", Clips = [NewClip("a", speed.Clone(), map, linked: true)] });
        }

        IReadOnlyList<string> errors = ModelValidator.Validate(project);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"El proyecto de velocidad «{speedCase.Name}» no es válido: {string.Join("; ", errors)}");
        }

        return new SpeedTimeline(project, video, sourceRate);
    }

    /// <summary>Un clip sin velocidad de <paramref name="length"/> desde el mismo origen: el control a 1×.</summary>
    public static SpeedTimeline BuildPlain(string mediaPath, Time length, bool withAudio)
    {
        var media = new MediaRef { Id = "m", Path = mediaPath };
        MediaInfo info = MediaProbe.Probe(mediaPath);
        info.ApplyTo(media);
        var sequence = new Sequence { Id = SequenceId, Name = "Control", FrameRate = SequenceRate, Width = 1280, Height = 720 };
        var project = new Project { Id = "p", Name = "Control", Media = [media], Sequences = [sequence] };
        Clip video = new() { Id = "v", MediaId = "m", SourceIn = SourceIn, SourceOut = SourceIn + length, LinkId = withAudio ? "l" : null };
        sequence.VideoTracks.Add(new Track { Id = "v1", Kind = TrackKind.Video, Name = "Video", Clips = [video] });
        if (withAudio)
        {
            sequence.AudioTracks.Add(new Track
            {
                Id = "a1",
                Kind = TrackKind.Audio,
                Name = "Audio",
                Clips = [new Clip { Id = "a", MediaId = "m", SourceIn = SourceIn, SourceOut = SourceIn + length, LinkId = "l" }],
            });
        }

        return new SpeedTimeline(project, video, info.Video!.FrameRate);
    }

    /// <summary>Cuadro del origen que debe verse en <paramref name="timeline"/>, calculado sin <see cref="ClipTimeMap"/>.</summary>
    public long ExpectedFrame(Time timeline) => FrameOf(ExpectedSource(timeline));

    /// <summary>Lo mismo según el contrato; si difiere de <see cref="ExpectedFrame"/>, el error está en el núcleo.</summary>
    public long ContractFrame(Time timeline) => FrameOf(Map.Source(timeline));

    /// <summary>Instante de la línea de tiempo en que empieza a sonar el segundo <paramref name="second"/> del origen; null si cae fuera del clip.</summary>
    public Time? TimelineOfSourceSecond(int second)
    {
        Time source = Time.FromSeconds(second);
        if (source < Map.SourceIn || source >= Map.SourceOut)
        {
            return null;
        }

        Time at = Map.TimelineOf(source);
        return at >= Map.End ? null : at;
    }

    /// <summary>
    /// <c>SourceIn + ⌊(o + τ)·n/d⌋ − ⌊o·n/d⌋</c> para una velocidad constante n/d con desplazamiento o; en un congelado,
    /// <c>SourceIn</c>. Sin velocidad, <c>SourceIn + τ</c>.
    /// </summary>
    private Time ExpectedSource(Time timeline)
    {
        long tau = Math.Clamp(timeline.Ticks - Clip.Start.Ticks, 0, Clip.Duration.Ticks - 1);
        if (Clip.Speed is not { } speed)
        {
            return Clip.SourceIn + Time.FromTicks(tau);
        }

        if (speed.Freeze)
        {
            return Clip.SourceIn;
        }

        Int128 offset = speed.Offset.Ticks;
        Int128 progress = FloorDiv((offset + tau) * speed.Rate.Num, speed.Rate.Den) - FloorDiv(offset * speed.Rate.Num, speed.Rate.Den);
        return Clip.SourceIn + Time.FromTicks((long)progress);
    }

    private long FrameOf(Time source) =>
        (long)((Int128)source.Ticks * SourceRate.Num / ((Int128)Time.TicksPerSecond * SourceRate.Den));

    private static Int128 FloorDiv(Int128 a, Int128 b)
    {
        Int128 q = a / b;
        return (a % b != 0 && (a < 0) != (b < 0)) ? q - 1 : q;
    }

    private static Clip NewClip(string id, ClipSpeed speed, ClipTimeMap map, bool linked)
    {
        var clip = new Clip { Id = id, MediaId = "m", Speed = speed, LinkId = linked ? "l" : null };
        map.ApplyTo(clip);
        return clip;
    }
}

/// <summary>FFmpeg y D3D11VA reales, con un reloj manual en lugar del dispositivo; guarda la mezcla para leerla a mano.</summary>
internal sealed class ManualClockBackend : IClipBackend, IDisposable
{
    private ManualOutput? _output;

    public ManualClock Clock => _output?.ManualClock ?? throw new InvalidOperationException("La sesión no abrió la salida de audio.");

    /// <summary>La mezcla de audio que la salida leería; la prueba la lee en su lugar.</summary>
    public IAudioSampleSource Mix => _output?.Source ?? throw new InvalidOperationException("La sesión no abrió la salida de audio.");

    public static TimelineSession Open(Project project, ManualClockBackend backend, bool audio) =>
        new(project, SpeedTimeline.SequenceId, AcceptanceEnvironment.Gpu, new TimelineSessionOptions { EnableAudio = audio, UseProxies = ProxyUsage.Never }, backend);

    public IClipVideo OpenVideo(string path, VideoDecoderOptions options) => MediaClipBackend.Instance.OpenVideo(path, options);

    public IPcmSource OpenAudio(string path) => MediaClipBackend.Instance.OpenAudio(path);

    public IAudioOutput OpenOutput(IAudioSampleSource source, AudioOutputOptions options) => _output = new ManualOutput(source);

    public IDecodeExecutor CreateExecutor() => MediaClipBackend.Instance.CreateExecutor();

    public void Dispose() => _output?.Dispose();

    private sealed class ManualOutput(IAudioSampleSource source) : IAudioOutput
    {
        public ManualClock ManualClock { get; } = new();

        public IAudioSampleSource Source => source;

        public IAudioClock Clock => ManualClock;

        public bool HasDevice => true;

        public string? FallbackReason => null;

        public AudioDeviceInfo? Device => null;

        public float Volume { get; set; }

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

        public void Dispose()
        {
        }
    }
}
