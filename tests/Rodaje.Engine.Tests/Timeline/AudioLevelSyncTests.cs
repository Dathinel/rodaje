using System.Diagnostics;
using System.Globalization;
using Rodaje.Audio;
using Rodaje.Audio.Metering;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.TestMedia;
using Xunit.Abstractions;
using static Rodaje.Engine.Tests.Support.Seconds;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>
/// Los medidores muestran lo que suena, no lo que se mezcla: con un dispositivo simulado (búfer de 20 ms, periodo de
/// 10 ms) y la mezcla 500 ms por delante, el pico de cada chirp de <see cref="AudioCode"/> aparece cuando el reloj llega
/// a él y no antes.
/// </summary>
public sealed class AudioLevelSyncTests(ITestOutputHelper output)
{
    private const int Rate = 48_000;
    private const double Threshold = 0.35;
    private static readonly double FrameAt60 = 1.0 / 60;
    private static readonly double Block = (double)AudioLevelAnalyzer.DefaultBlockFrames / Rate;

    [Fact]
    public void The_chirp_peak_shows_when_the_clock_reaches_it_not_when_it_is_mixed()
    {
        var backend = new SyncBackend(path => new AudioCodePcm());
        var b = new TimelineBuilder(1, 1);
        b.AddMedia("chirps", Rational.Fps30, 150, backend: backend.Video);
        b.AddAudio(0, "chirps", S(0.5), Time.Zero, S(3));
        using TimelineSession session = Open(b, backend);

        Result result = Run(session, backend.Device!, S(3), Time.FromSeconds(0.001));

        AssertInSync(result, [0.5, 1.5, 2.5]);
    }

    [Fact]
    public void At_ui_rate_each_chirp_appears_in_the_first_refresh_after_it_sounds()
    {
        var backend = new SyncBackend(path => new AudioCodePcm());
        var b = new TimelineBuilder(1, 1);
        b.AddMedia("chirps", Rational.Fps30, 150, backend: backend.Video);
        b.AddAudio(0, "chirps", S(0.25), S(0.3), S(3.5));
        using TimelineSession session = Open(b, backend);

        Result result = Run(session, backend.Device!, S(3.5), Time.FromSeconds(FrameAt60));

        double[] chirps = [0.95, 1.95, 2.95];
        Assert.Equal(chirps.Length, result.Onsets.Count);
        for (int i = 0; i < chirps.Length; i++)
        {
            double delay = result.Onsets[i] - (chirps[i] + ThresholdCrossing());
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"MEDIDA medidor_60hz_retraso_ms = {delay * 1000:0.00}"));
            Assert.InRange(delay, -Block, FrameAt60);
        }
    }

    [Fact]
    public void An_edit_while_playing_changes_the_meter_only_from_what_is_remixed()
    {
        var backend = new SyncBackend(path => new AudioCodePcm());
        var b = new TimelineBuilder(1, 1);
        b.AddMedia("chirps", Rational.Fps30, 150, backend: backend.Video);
        Clip clip = b.AddAudio(0, "chirps", Time.Zero, Time.Zero, S(4));
        using TimelineSession session = Open(b, backend);
        SimulatedDevice device = backend.Device!;
        WaitForMix(session, Rate / 4);
        session.Play();
        Pump(session, device, S(1.2), Time.FromSeconds(0.005), _ => { });

        // Silenciar el clip: lo que ya está en el dispositivo y los 50 ms que conserva la mezcla suenan igual; lo demás
        // se vuelve a mezclar en silencio y sus bloques tapan los que se habían medido por delante.
        Time edited = session.Position;
        clip.Enabled = false;
        session.Reload(b.Project);
        WaitForMix(session, Rate / 10);
        double loudBefore = 0;
        double loudAfter = 0;
        Pump(session, device, S(3), Time.FromSeconds(0.005), levels =>
        {
            if (session.Position < edited + S(0.06))
            {
                loudBefore = Math.Max(loudBefore, levels.Left.TruePeak);
            }
            else if (session.Position > edited + S(0.1))
            {
                loudAfter = Math.Max(loudAfter, levels.Left.TruePeak);
            }
        });

        Assert.True(loudBefore > 0.2, $"Lo que ya estaba en camino debía verse en el medidor: {loudBefore}");
        Assert.True(loudAfter < 0.01, $"El medidor siguió viendo audio después de silenciar el clip: {loudAfter}");
    }

    [FfmpegLibraryFact(Audio = AudioCodec.Aac)]
    public void A_real_aac_clip_keeps_the_meter_within_a_frame_of_the_clock()
    {
        GeneratedMedia media = TestMediaGenerator.CreateDefault()!.GetOrCreate(TestMediaPresets.H264Ntsc24Aac);
        var backend = new SyncBackend(MediaClipBackend.Instance.OpenAudio);
        var b = new TimelineBuilder(1, 1);
        b.AddMedia("aac", Rational.Ntsc24, 720, backend: backend.Video);
        b.Project.Media[0].Path = media.Path;
        b.AddAudio(0, "aac", S(0.5), S(2), S(4));
        using TimelineSession session = Open(b, backend);

        Result result = Run(session, backend.Device!, S(4.5), Time.FromSeconds(0.001));

        // AAC desplaza algo el comienzo del chirp (prerrelleno y filtro), así que se admite ±1 bloque más.
        AssertInSync(result, [0.5, 1.5, 2.5, 3.5], extraTolerance: Block);
    }

    /// <summary>Cuánto después del comienzo del chirp pasa la señal el umbral: el chirp entra con una rampa de 2 ms.</summary>
    private static double ThresholdCrossing()
    {
        int sample = 0;
        while (Math.Abs(AudioCode.SampleAt(sample, Rate)) <= Threshold)
        {
            sample++;
        }

        return (double)sample / Rate;
    }

    private void AssertInSync(Result result, double[] chirps, double extraTolerance = 0)
    {
        double crossing = ThresholdCrossing();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"MEDIDA mezcla_por_delante_ms = {result.MixAheadSeconds * 1000:0.0}; umbral_tras_inicio_ms = {crossing * 1000:0.00}; inicios = {string.Join(", ", result.Onsets.Select(o => o.ToString("0.0000", CultureInfo.InvariantCulture)))}"));
        Assert.Equal(chirps.Length, result.Onsets.Count);
        for (int i = 0; i < chirps.Length; i++)
        {
            double delay = result.Onsets[i] - (chirps[i] + crossing);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"MEDIDA medidor_retraso_ms[{i}] = {delay * 1000:0.00}"));

            // Puede adelantarse hasta un bloque (el bloque que contiene el comienzo) y atrasarse una lectura (1 ms).
            Assert.InRange(delay, -Block - extraTolerance, 0.0011 + extraTolerance);
            Assert.InRange(Math.Abs(delay), 0, FrameAt60);
        }

        // Sin sincronizar con el reloj, el pico se habría visto cuando se mezcló, cientos de milisegundos antes.
        Assert.True(result.MixAheadSeconds > 0.2, $"La mezcla iba solo {result.MixAheadSeconds:0.000} s por delante.");
    }

    /// <summary>Reproduce hasta <paramref name="end"/> leyendo los medidores cada <paramref name="step"/> de reloj.</summary>
    private static Result Run(TimelineSession session, SimulatedDevice device, Time end, Time step)
    {
        WaitForMix(session, Rate / 4);
        session.Play();
        var onsets = new List<double>();
        bool above = false;
        double mixAhead = 0;
        Pump(session, device, end, step, levels =>
        {
            bool now = levels.Left.TruePeak > Threshold;
            if (now && !above)
            {
                onsets.Add(session.Position.ToSeconds());
                mixAhead = Math.Max(mixAhead, (session.BufferedAudioFrames + device.QueuedFrames) / (double)Rate);
            }

            above = now;
        });
        return new Result(onsets, mixAhead);
    }

    private static void Pump(TimelineSession session, SimulatedDevice device, Time end, Time step, Action<AudioLevels> onLevels)
    {
        Time previous = session.Position;
        int frames = (int)step.ToSamples(Rate);
        while (session.Position < end)
        {
            device.Advance(frames);
            WaitForMix(session, Rate / 4);
            Time now = session.Position;
            onLevels(session.MeasureAudio(previous, now, TimeSpan.FromMilliseconds(300)));
            previous = now;
        }
    }

    /// <summary>Espera a que la mezcla tenga por delante al menos <paramref name="frames"/>, como en tiempo real.</summary>
    private static void WaitForMix(TimelineSession session, int frames)
    {
        var watch = Stopwatch.StartNew();
        while (session.BufferedAudioFrames < frames && watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            Thread.Yield();
        }
    }

    private static TimelineSession Open(TimelineBuilder builder, SyncBackend backend) =>
        new(builder.Project, builder.Sequence.Id, null, new TimelineSessionOptions { ProjectDirectory = @"C:\proyecto" }, backend);

    private sealed record Result(List<double> Onsets, double MixAheadSeconds);

    /// <summary>PCM con las marcas de <see cref="AudioCode"/>: un chirp de 20 ms al empezar cada segundo del origen.</summary>
    private sealed class AudioCodePcm : IPcmSource
    {
        private long _position;

        public int Read(Span<float> interleaved)
        {
            AudioCode.Generate(interleaved, _position, Rate, 2);
            int frames = interleaved.Length / 2;
            _position += frames;
            return frames;
        }

        public void Seek(Time position) => _position = position.ToSamples(Rate);

        public void Dispose()
        {
        }
    }

    private sealed class SyncBackend(Func<string, IPcmSource> openAudio) : IClipBackend
    {
        public FakeTimelineBackend Video { get; } = new();

        public SimulatedDevice? Device { get; private set; }

        public IClipVideo OpenVideo(string path, VideoDecoderOptions options) => Video.OpenVideo(path, options);

        public IPcmSource OpenAudio(string path) => openAudio(path);

        public IAudioOutput OpenOutput(IAudioSampleSource source, AudioOutputOptions options) => Device = new SimulatedDevice(source);

        public IDecodeExecutor CreateExecutor() => Video.CreateExecutor();
    }
}

/// <summary>
/// Un dispositivo WASAPI simulado en tiempo de prueba: cada periodo suena lo más viejo de su búfer y lo rellena desde
/// la fuente; el reloj cuenta lo que ya sonó, como <c>DeviceAudioClock</c>.
/// </summary>
internal sealed class SimulatedDevice : IAudioOutput
{
    public const int BufferFrames = 960;
    public const int PeriodFrames = 480;

    private readonly IAudioSampleSource _source;
    private readonly float[] _scratch = new float[BufferFrames * 2];
    private readonly Transport _transport;
    private long _rendered;
    private long _heard;
    private bool _running;

    public SimulatedDevice(IAudioSampleSource source)
    {
        _source = source;
        _transport = new Transport(this);
    }

    public IAudioClock Clock => _transport;

    public bool HasDevice => true;

    public string? FallbackReason => null;

    public AudioDeviceInfo? Device => new("sim", "Dispositivo simulado", 2, 48_000, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(10), false, TimeSpan.Zero);

    public float Volume { get; set; } = 1f;

    public AudioOutputStats Stats => default;

    /// <summary>Lo escrito en el dispositivo que todavía no sonó.</summary>
    public int QueuedFrames => (int)(_rendered - _heard);

    public event EventHandler<AudioDeviceChangedEventArgs>? DeviceChanged
    {
        add
        {
        }

        remove
        {
        }
    }

    /// <summary>Pasa el tiempo del dispositivo; solo suena si el reloj corre.</summary>
    public void Advance(int frames)
    {
        while (frames > 0 && _running)
        {
            int n = Math.Min(frames, PeriodFrames);
            _heard += n;
            frames -= n;
            Fill();
        }
    }

    public void Dispose()
    {
    }

    private void Fill()
    {
        int missing = BufferFrames - QueuedFrames;
        if (missing > 0)
        {
            _source.Read(_scratch.AsSpan(0, missing * 2));
            _rendered += missing;
        }
    }

    private sealed class Transport(SimulatedDevice device) : SampleCountingClock(48_000)
    {
        protected override long ReadSamplesPlayed() => device._heard;

        protected override void OnStart()
        {
            device._running = true;
            device.Fill();
        }

        protected override void OnPause() => device._running = false;

        protected override void OnSeek(Time position)
        {
            device._rendered = device._heard;
            device._source.Seek(position);
            if (device._running)
            {
                device.Fill();
            }
        }
    }
}
