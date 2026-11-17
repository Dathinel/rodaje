using System.Collections.Concurrent;
using System.Globalization;
using Rodaje.Audio.Capture;
using Rodaje.Audio.Interop;
using Rodaje.Audio.Tests.Hardware;
using Rodaje.Core.Timing;
using Rodaje.Playback;
using Xunit.Abstractions;

namespace Rodaje.Audio.Tests.MacAudio;

/// <remarks>
/// Corren en macOS con un dispositivo de salida. El corredor de la CI no tiene tarjeta de sonido: ci.yml instala dos
/// dispositivos virtuales de bucle (BlackHole de 2 y de 16 canales, solo en el corredor), así que lo que sale por uno se
/// captura por su entrada. Sin dispositivo se omiten. Las cifras salen en la salida de cada prueba.
/// </remarks>
[Collection(nameof(DispositivoDeAudioReal))]
[Trait("Categoria", "AudioMac")]
public sealed class CoreAudioOutputTests(ITestOutputHelper output)
{
    private const int Rate = 48_000;

    [CoreAudioFact]
    public void La_fabrica_da_la_salida_de_CoreAudio_en_macOS()
    {
        using IAudioOutput audio = AudioOutput.Open(new SilenceSource());

        Assert.IsType<CoreAudioOutput>(audio);
        Assert.True(audio.HasDevice);
        Assert.IsType<DeviceAudioClock>(audio.Clock);
        output.WriteLine(Describe(audio.Device!));
        Assert.False(string.IsNullOrWhiteSpace(audio.Device!.Name));
    }

    [CoreAudioFact]
    public void El_reloj_sigue_al_dispositivo_sin_deriva_ni_saltos()
    {
        using CoreAudioOutput audio = CoreAudioOutput.Open(new ToneSource(Rate, 440, amplitude: 0f));

        ClockMeasurement m = ClockMeasurement.Run(audio.Clock, TimeSpan.FromSeconds(10));
        audio.Clock.Pause();
        output.WriteLine(Describe(audio.Device!));
        output.WriteLine(m.Describe());
        output.WriteLine(Describe(audio.Stats));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Latencia del dispositivo: {audio.OutputLatency.TotalMilliseconds:0.0} ms."));

        Assert.Equal(0, m.Backwards);
        Assert.InRange(m.StartupMs, -5, 300); // el ajuste lineal puede cruzar un poco antes del Start
        Assert.InRange(Math.Abs(m.DriftPpm), 0, 1_000);
        Assert.InRange(m.MaxResidualMs, 0, 15);
        Assert.InRange(m.EndPositionSeconds, 9.6, 10.05);
        Assert.Equal(0, audio.Stats.DeviceRestarts);
    }

    [CoreAudioFact]
    public void Pausa_y_salto_con_el_dispositivo()
    {
        var source = new SilenceSource();
        using CoreAudioOutput audio = CoreAudioOutput.Open(source);
        IAudioClock clock = audio.Clock;

        // En el corredor de macOS Thread.Sleep se pasa a veces más de 100 ms: se compara con lo que pasó de verdad.
        var first = System.Diagnostics.Stopwatch.StartNew();
        clock.Start();
        Thread.Sleep(300);
        clock.Pause();
        double firstElapsed = first.Elapsed.TotalSeconds;
        Time paused = clock.Position;
        Assert.InRange(paused.ToSeconds(), firstElapsed - 0.15, firstElapsed + 0.02);
        Thread.Sleep(200);
        Assert.Equal(paused, clock.Position);

        clock.Seek(Time.FromSeconds(10));
        Assert.Equal(Time.FromSeconds(10), clock.Position);
        Assert.Equal(Time.FromSeconds(10), source.LastSeek);

        var wall = System.Diagnostics.Stopwatch.StartNew();
        clock.Start();
        Thread.Sleep(400);
        double advanced = clock.Position.ToSeconds() - 10;
        double elapsed = wall.Elapsed.TotalSeconds;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Tras el salto: {advanced * 1_000:0.0} ms de reloj en {elapsed * 1_000:0.0} ms de pared."));
        Assert.InRange(advanced, elapsed - 0.1, elapsed + 0.02);

        clock.Seek(Time.FromSeconds(20));
        Time afterSeek = clock.Position;
        Assert.InRange(afterSeek.ToSeconds(), 20, 20.05);
        Thread.Sleep(200);
        Assert.True(clock.Position > afterSeek);
        clock.Pause();

        output.WriteLine(Describe(audio.Stats));
        Assert.Equal(0, audio.Stats.ControlTimeouts);
        Assert.Equal(0, audio.Stats.DeviceRestarts);
    }

    [CoreAudioFact(TwoDevices = true)]
    public void Al_cambiar_el_dispositivo_predeterminado_el_audio_lo_sigue_sin_saltos()
    {
        (uint first, uint second) = CoreAudioFactAttribute.TwoOutputs();
        uint original = CoreAudio.DefaultDevice(input: false);
        Assert.Equal(0, CoreAudio.SetDefaultDevice(first, input: false));
        try
        {
            using CoreAudioOutput audio = CoreAudioOutput.Open(new SilenceSource());
            var messages = new ConcurrentQueue<string>();
            audio.DeviceChanged += (_, e) => messages.Enqueue(e.Message);
            var trace = new ClockTrace(audio.Clock);

            audio.Clock.Start();
            trace.SampleFor(TimeSpan.FromMilliseconds(400));
            Assert.Equal(0, CoreAudio.SetDefaultDevice(second, input: false));
            trace.SampleUntil(() => !messages.IsEmpty, TimeSpan.FromSeconds(5));
            trace.SampleFor(TimeSpan.FromMilliseconds(400));
            audio.Clock.Pause();

            output.WriteLine(trace.Describe());
            output.WriteLine(string.Join(" | ", messages));
            output.WriteLine(Describe(audio.Stats));
            Assert.Equal(CoreAudio.DeviceText(second, CoreAudio.SelectorDeviceUid), audio.Device!.Id);
            Assert.Equal(1, audio.Stats.DeviceRestarts);
            Assert.Equal(0, trace.Backwards);
            Assert.InRange(trace.MaxForwardJumpMs, 0, 60);
            Assert.InRange(trace.MaxStallMs, 0, 120);
        }
        finally
        {
            _ = CoreAudio.SetDefaultDevice(original != 0 ? original : first, input: false);
        }
    }

    [CoreAudioFact]
    public void Perder_la_salida_no_hace_retroceder_al_reloj_y_vuelve_sola()
    {
        using CoreAudioOutput audio = CoreAudioOutput.Open(new SilenceSource());
        var messages = new ConcurrentQueue<string>();
        audio.DeviceChanged += (_, e) => messages.Enqueue(e.Message);
        var trace = new ClockTrace(audio.Clock);

        audio.Clock.Start();
        trace.SampleFor(TimeSpan.FromMilliseconds(300));
        audio.SimulateDeviceLoss(blockReopen: true);
        trace.SampleUntil(() => audio.Device is null, TimeSpan.FromSeconds(2));
        double freeRunStart = audio.Clock.Position.ToSeconds();
        trace.SampleFor(TimeSpan.FromMilliseconds(300));
        double freeRunAdvance = audio.Clock.Position.ToSeconds() - freeRunStart;
        audio.AllowReopen();
        trace.SampleUntil(() => audio.Device is not null, TimeSpan.FromSeconds(3));
        trace.SampleFor(TimeSpan.FromMilliseconds(300));
        audio.Clock.Pause();
        SpinWait.SpinUntil(() => messages.Count >= 2, TimeSpan.FromSeconds(2));

        output.WriteLine(trace.Describe());
        output.WriteLine(string.Join(" | ", messages));
        Assert.NotNull(audio.Device);
        Assert.Equal(0, trace.Backwards);
        Assert.InRange(freeRunAdvance, 0.25, 0.36);
        Assert.InRange(trace.MaxForwardJumpMs, 0, 60);
        Assert.Contains(messages, m => m.Contains("volvió", StringComparison.Ordinal));
    }

    [CoreAudioFact(Loopback = true)]
    public void La_captura_del_bucle_cae_donde_dice_el_reloj_y_se_mide_la_latencia()
    {
        (uint loop, string uid) = CoreAudioFactAttribute.LoopbackDevice();
        uint original = CoreAudio.DefaultDevice(input: false);
        Assert.Equal(0, CoreAudio.SetDefaultDevice(loop, input: false));
        try
        {
            var click = new ClickSource(Rate, atFrame: Rate);
            using CoreAudioOutput audio = CoreAudioOutput.Open(click);
            using IAudioCapture capture = AudioCapture.Open(uid, Rate);
            var packets = new ConcurrentQueue<(float[] Samples, long Start100ns)>();
            capture.StartCapture((mono, start, _) => packets.Enqueue((mono.ToArray(), start)));
            Thread.Sleep(200);

            var wall = new List<long>();
            var position = new List<double>();
            audio.Clock.Start();
            var limit = System.Diagnostics.Stopwatch.StartNew();
            while (audio.Clock.Position.ToSeconds() < 1.6 && limit.Elapsed < TimeSpan.FromSeconds(10))
            {
                wall.Add(ClickSource.Now100ns());
                position.Add(audio.Clock.Position.ToSeconds());
                Thread.Sleep(1);
            }

            audio.Clock.Pause();
            Thread.Sleep(100);
            capture.StopCapture();

            long? heard = null;
            foreach ((float[] samples, long start) in packets)
            {
                int index = Array.FindIndex(samples, s => Math.Abs(s) > 0.3f);
                if (index >= 0)
                {
                    heard = start + (index * TimeSpan.TicksPerSecond / Rate);
                    break;
                }
            }

            Assert.True(heard.HasValue, $"El clic no apareció en la entrada del bucle ({packets.Count} paquetes).");
            int crossing = position.FindIndex(p => p >= 1.0);
            Assert.True(crossing > 0, "El reloj no llegó a 1 s.");
            double fraction = (1.0 - position[crossing - 1]) / (position[crossing] - position[crossing - 1]);
            double clockAt = wall[crossing - 1] + (fraction * (wall[crossing] - wall[crossing - 1]));
            double offsetMs = (heard.Value - clockAt) / TimeSpan.TicksPerMillisecond;
            double pipelineMs = (heard.Value - click.Produced100ns) / (double)TimeSpan.TicksPerMillisecond;
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Clic capturado {offsetMs:+0.0;-0.0} ms respecto de cuando el reloj marcó 1,000 s; de entregarlo a CoreAudio a capturarlo: {pipelineMs:0.0} ms; latencia del dispositivo {audio.OutputLatency.TotalMilliseconds:0.0} ms."));
            Assert.InRange(Math.Abs(offsetMs), 0, 30);
        }
        finally
        {
            _ = CoreAudio.SetDefaultDevice(original != 0 ? original : loop, input: false);
        }
    }

    private static string Describe(AudioDeviceInfo d) => string.Create(
        CultureInfo.InvariantCulture,
        $"Dispositivo: {d.Name} ({d.Id}); búfer {d.BufferDuration.TotalMilliseconds:0.0} ms; latencia {d.StreamLatency.TotalMilliseconds:0.0} ms.");

    private static string Describe(AudioOutputStats s) => string.Create(
        CultureInfo.InvariantCulture,
        $"Cuadros producidos {s.FramesRendered}; faltas de la fuente {s.Underruns}; reaperturas {s.DeviceRestarts}; órdenes sin confirmar {s.ControlTimeouts}.");
}

/// <summary>Solo en macOS con salida de audio; con <see cref="TwoDevices"/> o <see cref="Loopback"/>, los dispositivos BlackHole de la CI.</summary>
public sealed class CoreAudioFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Device = new(() => OperatingSystem.IsMacOS() && CoreAudioOutput.HasOutputDevice());

    private bool _twoDevices;
    private bool _loopback;

    public CoreAudioFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Skip = "CoreAudio es de macOS.";
        }
        else if (!Device.Value)
        {
            Skip = "No hay dispositivo de salida de audio.";
        }
    }

    public bool TwoDevices
    {
        get => _twoDevices;
        set
        {
            _twoDevices = value;
            if (value && Skip is null && Outputs().Length < 2)
            {
                Skip = "Hacen falta dos dispositivos de salida.";
            }
        }
    }

    public bool Loopback
    {
        get => _loopback;
        set
        {
            _loopback = value;
            if (value && Skip is null && LoopbackDevice().Device == 0)
            {
                Skip = "Falta un dispositivo de bucle (BlackHole) con entrada y salida.";
            }
        }
    }

    internal static (uint First, uint Second) TwoOutputs()
    {
        uint[] outputs = Outputs();
        return (outputs[0], outputs[1]);
    }

    /// <summary>Un dispositivo BlackHole: lo que sale por él vuelve por su entrada.</summary>
    internal static (uint Device, string Uid) LoopbackDevice()
    {
        uint device = CoreAudio.Devices().FirstOrDefault(d => CoreAudio.HasStreams(d, input: true) && CoreAudio.HasStreams(d, input: false)
            && CoreAudio.DeviceText(d, CoreAudio.SelectorName).Contains("BlackHole", StringComparison.OrdinalIgnoreCase));
        return (device, device == 0 ? "" : CoreAudio.DeviceText(device, CoreAudio.SelectorDeviceUid));
    }

    private static uint[] Outputs() => [.. CoreAudio.Devices().Where(d => CoreAudio.HasStreams(d, input: false))];
}
