using System.Numerics;
using Rodaje.Audio;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine;
using Rodaje.Engine.Timeline;
using Rodaje.Media.Probe;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.TestMedia;

namespace Rodaje.Acceptance.Tests.Support;

/// <summary>
/// Un caso de velocidad: constante (<see cref="Rate"/>), cuadro congelado o la curva de un preajuste; <see cref="Offset"/>
/// como si el clip viniera de dividir otro.
/// </summary>
internal sealed record SpeedCase(string Name, Rational Rate, bool Freeze = false, long Offset = 0, SpeedPreset? Preset = null, bool Reverse = false)
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
        new("bala", new Rational(1, 1), Preset: SpeedPreset.Bullet),
        new("reversa", new Rational(1, 1), Reverse: true),
        new("reversa-1,37x", new Rational(137, 100), Offset: 123_456_789, Reverse: true),
    ];
}

/// <summary>
/// Secuencia de 30 fps con clips del medio de velocidad (y su audio enlazado, si se pide) seguidos desde 0. El cuadro
/// esperado se calcula aparte con aritmética entera (BigInteger en las curvas) y se compara con
/// <see cref="ClipTimeMap"/>, el contrato.
/// </summary>
internal sealed class SpeedTimeline
{
    public const string SequenceId = "sec";
    public static readonly Rational SequenceRate = Rational.Fps30;

    private static readonly Time SourceIn = Time.FromSeconds(1.3);

    /// <summary>Material del clip con preajuste de <see cref="Build"/>.</summary>
    private static readonly Time PresetMaterial = Time.FromSeconds(40);

    private SpeedTimeline(Project project, IReadOnlyList<Clip> clips, Rational sourceRate)
    {
        Project = project;
        Clips = clips;
        Clip = clips[0];
        Map = Clip.TimeMap;
        SourceRate = sourceRate;
    }

    public Project Project { get; }

    /// <summary>Los clips de video, en orden.</summary>
    public IReadOnlyList<Clip> Clips { get; }

    /// <summary>El primer clip de video.</summary>
    public Clip Clip { get; }

    public ClipTimeMap Map { get; }

    public Rational SourceRate { get; }

    public Time End => Clips[^1].End;

    /// <summary>Cuadros de la secuencia que empiezan dentro de los clips.</summary>
    public long FrameCount => (End - Time.FromTicks(1)).ToFrames(SequenceRate) + 1;

    /// <param name="withAudio">Con el audio enlazado; un congelado no lo lleva (el audio no se congela).</param>
    /// <param name="maxTimeline">Si se da, el clip se recorta para no pasar de esa duración en la línea de tiempo.</param>
    public static SpeedTimeline Build(string mediaPath, SpeedCase speedCase, bool withAudio, Time? maxTimeline = null, bool preservePitch = true)
    {
        (Project project, MediaRef media, Sequence sequence, Rational sourceRate) = NewProject(mediaPath, speedCase.Name);
        if (speedCase.Preset is { } preset)
        {
            Clip curved = AddWithPreset(project, sequence, 0, SourceIn, PresetMaterial, preset, withAudio, preservePitch);
            return Validated(new SpeedTimeline(project, [curved], sourceRate), speedCase.Name);
        }

        var speed = new ClipSpeed { Rate = speedCase.Rate, Freeze = speedCase.Freeze, Offset = Time.FromTicks(speedCase.Offset), PreservePitch = preservePitch, Reverse = speedCase.Reverse };
        ClipTimeMap map = speedCase.Freeze
            ? ClipTimeMap.Create(Time.Zero, maxTimeline ?? Time.FromSeconds(20), SourceIn, speed)
            : ClipTimeMap.Fit(Time.Zero, SourceIn, media.Duration - Time.FromSeconds(0.5), speed);
        if (maxTimeline is { } max && map.Duration > max)
        {
            map = map.TrimOut(max);
        }

        withAudio &= !speedCase.Freeze;
        Clip video = NewClip("v", speed, map, withAudio ? "l" : null);
        sequence.VideoTracks[0].Clips.Add(video);
        if (withAudio)
        {
            sequence.AudioTracks[0].Clips.Add(NewClip("a", speed.Clone(), map, "l"));
        }

        return Validated(new SpeedTimeline(project, [video], sourceRate), speedCase.Name);
    }

    /// <summary>
    /// Clips seguidos con el preajuste, cada uno con <paramref name="material"/> del origen a continuación del anterior
    /// (al acabarse el medio vuelve al principio), hasta cubrir <paramref name="length"/> de la línea de tiempo.
    /// </summary>
    public static SpeedTimeline BuildRepeated(string mediaPath, SpeedPreset preset, Time length, Time material, bool withAudio = true)
    {
        (Project project, MediaRef media, Sequence sequence, Rational sourceRate) = NewProject(mediaPath, SpeedPresets.Name(preset));
        var clips = new List<Clip>();
        Time source = SourceIn;
        while (clips.Count == 0 || clips[^1].End < length)
        {
            if (source + material > media.Duration - Time.FromSeconds(0.5))
            {
                source = SourceIn;
            }

            clips.Add(AddWithPreset(project, sequence, clips.Count, source, material, preset, withAudio, preservePitch: true));
            source += material;
        }

        return Validated(new SpeedTimeline(project, clips, sourceRate), SpeedPresets.Name(preset));
    }

    /// <summary>Un clip sin velocidad de <paramref name="length"/> desde el mismo origen: el control a 1×.</summary>
    public static SpeedTimeline BuildPlain(string mediaPath, Time length, bool withAudio)
    {
        (Project project, _, Sequence sequence, Rational sourceRate) = NewProject(mediaPath, "Control");
        Clip video = new() { Id = "v", MediaId = "m", SourceIn = SourceIn, SourceOut = SourceIn + length, LinkId = withAudio ? "l" : null };
        sequence.VideoTracks[0].Clips.Add(video);
        if (withAudio)
        {
            sequence.AudioTracks[0].Clips.Add(new Clip { Id = "a", MediaId = "m", SourceIn = SourceIn, SourceOut = SourceIn + length, LinkId = "l" });
        }

        return new SpeedTimeline(project, [video], sourceRate);
    }

    /// <summary>Cuadro del origen que debe verse en <paramref name="timeline"/>, calculado sin <see cref="ClipTimeMap"/>.</summary>
    public long ExpectedFrame(Time timeline) => FrameOf(ExpectedSource(ClipAt(timeline), timeline));

    /// <summary>Lo mismo según el contrato; si difiere de <see cref="ExpectedFrame"/>, el error está en el núcleo.</summary>
    public long ContractFrame(Time timeline) => FrameOf(ClipAt(timeline).TimeMap.Source(timeline));

    /// <summary>
    /// Instantes de la línea de tiempo en que empieza a sonar el chirp de cada segundo del origen que suena entero dentro
    /// de su clip, en orden.
    /// </summary>
    public List<Time> ChirpInstants()
    {
        var instants = new List<Time>();
        foreach (Clip clip in Clips)
        {
            ClipTimeMap map = clip.TimeMap;
            for (long second = (long)Math.Ceiling(map.SourceIn.ToSeconds()); Time.FromSeconds(second + 0.03) < map.SourceOut; second++)
            {
                // En reversa el chirp suena al revés y empieza por su final.
                Time at = map.TimelineOf(Time.FromSeconds(map.Reverse ? second + 0.02 : second));
                if (at < map.End && map.TimelineOf(Time.FromSeconds(map.Reverse ? second : second + 0.03)) < map.End)
                {
                    instants.Add(at);
                }
            }
        }

        return instants;
    }

    private Clip ClipAt(Time timeline)
    {
        foreach (Clip clip in Clips)
        {
            if (timeline < clip.End)
            {
                return clip;
            }
        }

        return Clips[^1];
    }

    /// <summary>
    /// Sin velocidad, <c>SourceIn + τ</c>; congelado, <c>SourceIn</c>; constante n/d con desplazamiento o,
    /// <c>SourceIn + ⌊(o + τ)·n/d⌋ − ⌊o·n/d⌋</c>; con curva, lo mismo con su integral (<see cref="Walked"/>).
    /// </summary>
    private static Time ExpectedSource(Clip clip, Time timeline)
    {
        long tau = Math.Clamp(timeline.Ticks - clip.Start.Ticks, 0, clip.Duration.Ticks - 1);
        if (clip.Speed is not { } speed)
        {
            return clip.SourceIn + Time.FromTicks(tau);
        }

        if (speed.Freeze)
        {
            return clip.SourceIn;
        }

        long offset = speed.Offset.Ticks;
        if (speed.Curve is { } curve)
        {
            return clip.SourceIn + Time.FromTicks((long)(Walked(curve, offset + tau) - Walked(curve, offset)));
        }

        Int128 progress = FloorDiv((offset + (Int128)tau) * speed.Rate.Num, speed.Rate.Den) - FloorDiv((Int128)offset * speed.Rate.Num, speed.Rate.Den);
        return speed.Reverse ? clip.SourceOut - Time.FromTicks(1 + (long)progress) : clip.SourceIn + Time.FromTicks((long)progress);
    }

    /// <summary>
    /// ⌊origen recorrido tras <paramref name="u"/> ticks de la curva⌋: se integra tramo a tramo la velocidad lineal entre
    /// nudos (en <c>⌊At·Span/10 000⌋</c>), en centésimas, con BigInteger y un solo redondeo al final.
    /// </summary>
    private static BigInteger Walked(SpeedCurve curve, long u)
    {
        BigInteger[] knots = [.. curve.Points.Select(p => (BigInteger)p.At * curve.Span.Ticks / SpeedCurve.AtScale)];
        int[] speeds = [.. curve.Points.Select(p => p.Hundredths)];
        BigInteger x = u;
        if (x <= knots[0])
        {
            return FloorDiv(x * speeds[0], 100);
        }

        // Suma de áreas en «centésimas × ticks × 2·L» para no dividir hasta el final.
        BigInteger whole = 0;
        for (int k = 1; k < knots.Length; k++)
        {
            BigInteger a = knots[k - 1];
            BigInteger b = knots[k];
            if (x >= b)
            {
                whole += (b - a) * (speeds[k - 1] + speeds[k]);
                continue;
            }

            BigInteger length = b - a;
            BigInteger d = x - a;
            BigInteger partial = (2 * speeds[k - 1] * d * length) + ((speeds[k] - speeds[k - 1]) * d * d);
            return FloorDiv((whole * length) + partial, 200 * length);
        }

        return FloorDiv(whole + (2 * speeds[^1] * (x - knots[^1])), 200);
    }

    private long FrameOf(Time source) =>
        (long)((Int128)source.Ticks * SourceRate.Num / ((Int128)Time.TicksPerSecond * SourceRate.Den));

    private static Int128 FloorDiv(Int128 a, Int128 b)
    {
        Int128 q = a / b;
        return (a % b != 0 && (a < 0) != (b < 0)) ? q - 1 : q;
    }

    private static BigInteger FloorDiv(BigInteger a, BigInteger b)
    {
        BigInteger q = BigInteger.DivRem(a, b, out BigInteger r);
        return r != 0 && (a.Sign < 0) != (b.Sign < 0) ? q - 1 : q;
    }

    private static (Project Project, MediaRef Media, Sequence Sequence, Rational SourceRate) NewProject(string mediaPath, string name)
    {
        var media = new MediaRef { Id = "m", Path = mediaPath };
        MediaInfo info = MediaProbe.Probe(mediaPath);
        info.ApplyTo(media);
        Rational sourceRate = info.Video?.FrameRate ?? throw new InvalidOperationException("El medio de velocidad no tiene video.");
        var sequence = new Sequence { Id = SequenceId, Name = name, FrameRate = SequenceRate, Width = 1280, Height = 720 };
        sequence.VideoTracks.Add(new Track { Id = "v1", Kind = TrackKind.Video, Name = "Video" });
        sequence.AudioTracks.Add(new Track { Id = "a1", Kind = TrackKind.Audio, Name = "Audio" });
        return (new Project { Id = "p", Name = name, Media = [media], Sequences = [sequence] }, media, sequence, sourceRate);
    }

    /// <summary>Pone un clip de <paramref name="material"/> al final de la pista (y su audio) y le aplica el preajuste con la edición del núcleo.</summary>
    private static Clip AddWithPreset(Project project, Sequence sequence, int index, Time source, Time material, SpeedPreset preset, bool withAudio,
        bool preservePitch)
    {
        Time start = sequence.VideoTracks[0].Clips.Count == 0 ? Time.Zero : sequence.VideoTracks[0].Clips[^1].End;
        string link = $"l{index}";
        var video = new Clip { Id = $"cv{index}", MediaId = "m", SourceIn = source, SourceOut = source + material, Start = start, LinkId = withAudio ? link : null };
        sequence.VideoTracks[0].Clips.Add(video);
        if (withAudio)
        {
            sequence.AudioTracks[0].Clips.Add(new Clip { Id = $"ca{index}", MediaId = "m", SourceIn = source, SourceOut = source + material, Start = start, LinkId = link });
        }

        Edits.ApplySpeedPreset(SequenceId, video.Id, preset).Apply(project);
        if (!preservePitch)
        {
            Edits.SetPreservePitch(SequenceId, video.Id, false).Apply(project);
        }

        return video;
    }

    private static SpeedTimeline Validated(SpeedTimeline timeline, string name)
    {
        IReadOnlyList<string> errors = ModelValidator.Validate(timeline.Project);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"El proyecto de velocidad «{name}» no es válido: {string.Join("; ", errors)}");
        }

        return timeline;
    }

    private static Clip NewClip(string id, ClipSpeed speed, ClipTimeMap map, string? link)
    {
        var clip = new Clip { Id = id, MediaId = "m", Speed = speed, LinkId = link };
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

    public IKeyframeIndex? ReadKeyframes(string path) => MediaClipBackend.Instance.ReadKeyframes(path);

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
