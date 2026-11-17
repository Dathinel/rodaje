using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Rodaje.Audio.Capture;
using Rodaje.Audio.Tests.Hardware;
using Rodaje.Core.Timing;
using Rodaje.Playback;
using Xunit.Abstractions;

namespace Rodaje.Audio.Tests.Pulse;

/// <remarks>
/// Corren en Linux con un servidor PulseAudio o PipeWire. En la CI no hay tarjeta de sonido: ci.yml arranca PulseAudio
/// con dos sumideros nulos (<c>rodaje_a</c>, predeterminado, y <c>rodaje_b</c>), que avanzan con el reloj del sistema, y la
/// captura lee el monitor del sumidero. Sin servidor se omiten. Las cifras salen en la salida de cada prueba.
/// </remarks>
[Collection(nameof(DispositivoDeAudioReal))]
[Trait("Categoria", "AudioPulse")]
public sealed class PulseOutputTests(ITestOutputHelper output)
{
    private const int Rate = 48_000;

    [PulseFact]
    public void La_fabrica_da_la_salida_de_Pulse_en_Linux()
    {
        using IAudioOutput audio = AudioOutput.Open(new SilenceSource());

        Assert.IsType<PulseOutput>(audio);
        Assert.True(audio.HasDevice);
        Assert.Null(audio.FallbackReason);
        Assert.IsType<DeviceAudioClock>(audio.Clock);
        AudioDeviceInfo device = audio.Device!;
        output.WriteLine(Describe(device));
        Assert.False(string.IsNullOrWhiteSpace(device.Name));
        Assert.True(device.BufferDuration > TimeSpan.Zero && device.Period > TimeSpan.Zero);
    }

    [PulseFact]
    public void El_reloj_sigue_al_servidor_sin_deriva_ni_saltos()
    {
        using PulseOutput audio = PulseOutput.Open(new ToneSource(Rate, 440, amplitude: 0f));

        ClockMeasurement m = ClockMeasurement.Run(audio.Clock, TimeSpan.FromSeconds(10));
        TimeSpan? latency = audio.LastReportedLatency;
        audio.Clock.Pause();
        output.WriteLine(Describe(audio.Device!));
        output.WriteLine(m.Describe());
        output.WriteLine(Describe(audio.Stats));
        output.WriteLine(Ms("Latencia de la salida (pa_stream_get_latency)", latency));

        Assert.Equal(0, m.Backwards);
        Assert.InRange(m.StartupMs, -5, 300); // el ajuste lineal puede cruzar un poco antes del Start
        Assert.InRange(Math.Abs(m.DriftPpm), 0, 1_000);
        Assert.InRange(m.MaxResidualMs, 0, 15);
        Assert.InRange(m.EndPositionSeconds, 9.6, 10.05);
        Assert.Equal(0, audio.Stats.DeviceRestarts);
        Assert.Equal(0, audio.Stats.SourceErrors);
        Assert.NotNull(latency);
    }

    [PulseFact]
    public void Pausa_y_salto_con_el_servidor()
    {
        var source = new SilenceSource();
        using PulseOutput audio = PulseOutput.Open(source, new AudioOutputOptions { Volume = 0.5f });
        IAudioClock clock = audio.Clock;

        clock.Start();
        Thread.Sleep(300);
        clock.Pause();
        Time paused = clock.Position;
        Assert.InRange(paused.ToSeconds(), 0.1, 0.35);
        Thread.Sleep(200);
        Assert.Equal(paused, clock.Position);

        clock.Seek(Time.FromSeconds(10));
        Assert.Equal(Time.FromSeconds(10), clock.Position);
        Assert.Equal(Time.FromSeconds(10), source.LastSeek);
        Thread.Sleep(50);
        Assert.Equal(Time.FromSeconds(10), clock.Position);

        clock.Start();
        Thread.Sleep(400);
        Assert.InRange(clock.Position.ToSeconds(), 10.2, 10.45);

        clock.Seek(Time.FromSeconds(20));
        Time afterSeek = clock.Position;
        Assert.InRange(afterSeek.ToSeconds(), 20, 20.05);
        Thread.Sleep(200);
        Assert.True(clock.Position > afterSeek);
        clock.Pause();

        output.WriteLine(Describe(audio.Stats));
        Assert.Equal(0, audio.Stats.ControlTimeouts);
        Assert.Equal(0, audio.Stats.DeviceRestarts);

        audio.Dispose();
        Assert.Throws<ObjectDisposedException>(() => clock.Seek(Time.Zero));
    }

    [PulseFact(TwoSinks = true)]
    public void Al_cambiar_el_sumidero_predeterminado_el_audio_lo_sigue_sin_saltos()
    {
        Pactl("set-default-sink", PulseFactAttribute.SinkA);
        try
        {
            using PulseOutput audio = PulseOutput.Open(new SilenceSource());
            var messages = new ConcurrentQueue<string>();
            audio.DeviceChanged += (_, e) => messages.Enqueue(e.Message);
            var trace = new ClockTrace(audio.Clock);
            Assert.Equal(PulseFactAttribute.SinkA, audio.Device!.Id);

            audio.Clock.Start();
            trace.SampleFor(TimeSpan.FromMilliseconds(400));
            long changed = Stopwatch.GetTimestamp();
            Pactl("set-default-sink", PulseFactAttribute.SinkB);
            trace.SampleUntil(() => !messages.IsEmpty, TimeSpan.FromSeconds(5));
            double reaction = Stopwatch.GetElapsedTime(changed).TotalMilliseconds;
            trace.SampleFor(TimeSpan.FromMilliseconds(400));
            audio.Clock.Pause();

            output.WriteLine(trace.Describe());
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Aviso a los {reaction:0} ms del cambio: {string.Join(" | ", messages)}"));
            output.WriteLine(Describe(audio.Stats));
            Assert.Equal(PulseFactAttribute.SinkB, audio.Device!.Id);
            Assert.Equal(1, audio.Stats.DeviceRestarts);
            Assert.Contains(messages, m => m.Contains(PulseFactAttribute.SinkB, StringComparison.Ordinal));
            Assert.Equal(0, trace.Backwards);
            Assert.InRange(trace.MaxForwardJumpMs, 0, 60);
            Assert.InRange(trace.MaxStallMs, 0, 120);
        }
        finally
        {
            Pactl("set-default-sink", PulseFactAttribute.SinkA);
        }
    }

    [PulseFact]
    public void Perder_la_salida_no_hace_retroceder_al_reloj_y_vuelve_sola()
    {
        using PulseOutput audio = PulseOutput.Open(new SilenceSource());
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
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Avance sin salida en 300 ms: {freeRunAdvance * 1_000:0.0} ms."));
        output.WriteLine(string.Join(" | ", messages));
        Assert.NotNull(audio.Device);
        Assert.Equal(0, trace.Backwards);
        Assert.InRange(freeRunAdvance, 0.25, 0.36);
        Assert.InRange(trace.MaxForwardJumpMs, 0, 60);
        Assert.Contains(messages, m => m.Contains("sin sonido", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("volvió", StringComparison.Ordinal));
    }

    [PulseFact(TwoSinks = true)]
    public void La_captura_del_monitor_cae_donde_dice_el_reloj_y_se_mide_la_latencia()
    {
        Pactl("set-default-sink", PulseFactAttribute.SinkA);
        var click = new ClickSource(Rate, atFrame: Rate);
        using PulseOutput audio = PulseOutput.Open(click);
        using IAudioCapture capture = AudioCapture.Open(PulseFactAttribute.SinkA + ".monitor", Rate);
        var packets = new ConcurrentQueue<(float[] Samples, long Start100ns, long Received100ns)>();
        capture.StartCapture((mono, start, _) => packets.Enqueue((mono.ToArray(), start, Now100ns())));
        Thread.Sleep(200);

        var wall = new List<long>();
        var position = new List<double>();
        audio.Clock.Start();
        var limit = Stopwatch.StartNew();
        while (audio.Clock.Position.ToSeconds() < 1.6 && limit.Elapsed < TimeSpan.FromSeconds(10))
        {
            wall.Add(Now100ns());
            position.Add(audio.Clock.Position.ToSeconds());
            Thread.Sleep(1);
        }

        TimeSpan? outputLatency = audio.LastReportedLatency;
        audio.Clock.Pause();
        Thread.Sleep(100);
        capture.StopCapture();

        long? heard = null;
        long origin = wall[0];
        foreach ((float[] samples, long start, long received) in packets)
        {
            int index = Array.FindIndex(samples, s => Math.Abs(s) > 0.5f);
            if (index >= 0)
            {
                heard = start + (index * TimeSpan.TicksPerSecond / Rate);
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"Paquete del clic: {samples.Length} muestras, índice {index}, empieza a {(start - origin) / 1e4:0.0} ms y llegó a {(received - origin) / 1e4:0.0} ms del arranque."));
                break;
            }
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Clic entregado al servidor a {(click.Produced100ns - origin) / 1e4:0.0} ms del arranque; {packets.Count} paquetes capturados."));

        Assert.True(heard.HasValue, "El clic no apareció en el monitor.");
        int crossing = position.FindIndex(p => p >= 1.0);
        Assert.True(crossing > 0);
        double fraction = (1.0 - position[crossing - 1]) / (position[crossing] - position[crossing - 1]);
        double clockAt = wall[crossing - 1] + (fraction * (wall[crossing] - wall[crossing - 1]));
        double offsetMs = (heard.Value - clockAt) / TimeSpan.TicksPerMillisecond;
        double pipelineMs = (heard.Value - click.Produced100ns) / (double)TimeSpan.TicksPerMillisecond;

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Clic en el monitor {offsetMs:+0.0;-0.0} ms respecto de cuando el reloj marcó 1,000 s."));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Latencia medida de la salida (de entregar el clic al servidor a oírlo en el monitor): {pipelineMs:0.0} ms."));
        output.WriteLine(Ms("Latencia que informa la salida", outputLatency));
        output.WriteLine(Ms("Latencia que informa la captura", ((PulseCapture)capture).LastReportedLatency));
        Assert.InRange(Math.Abs(offsetMs), 0, 30);
        Assert.InRange(pipelineMs, 0, 500);
    }

    [PulseFact]
    public void Las_fuentes_no_incluyen_los_monitores()
    {
        IReadOnlyList<AudioCaptureDevice> devices = AudioCapture.ListDevices();

        output.WriteLine(string.Join(", ", devices.Select(d => $"{d.Id} ({d.Name}){(d.IsDefault ? " *" : "")}")));
        Assert.DoesNotContain(devices, d => d.Id.EndsWith(".monitor", StringComparison.Ordinal));
        Assert.True(devices.Count(d => d.IsDefault) <= 1);
    }

    internal static void Pactl(params string[] arguments)
    {
        var info = new ProcessStartInfo("pactl") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(info)!;
        process.WaitForExit(5_000);
        Assert.True(process.ExitCode == 0, $"pactl {string.Join(' ', arguments)}: {process.StandardError.ReadToEnd()}");
    }

    internal static long Now100ns() => (long)((Int128)Stopwatch.GetTimestamp() * TimeSpan.TicksPerSecond / Stopwatch.Frequency);

    private static string Ms(string label, TimeSpan? value) =>
        string.Create(CultureInfo.InvariantCulture, $"{label}: {(value is { } v ? $"{v.TotalMilliseconds:0.0} ms" : "sin dato")}.");

    private static string Describe(AudioDeviceInfo d) => string.Create(
        CultureInfo.InvariantCulture,
        $"Sumidero: {d.Name}; buffer {d.BufferDuration.TotalMilliseconds:0.0} ms; periodo {d.Period.TotalMilliseconds:0.0} ms.");

    private static string Describe(AudioOutputStats s) => string.Create(
        CultureInfo.InvariantCulture,
        $"Cuadros producidos {s.FramesRendered}; faltas de la fuente {s.Underruns}; reaperturas {s.DeviceRestarts}; órdenes sin confirmar {s.ControlTimeouts}.");
}

/// <summary>Solo en Linux con un servidor de sonido; con <see cref="TwoSinks"/>, además los dos sumideros nulos de la CI.</summary>
public sealed class PulseFactAttribute : FactAttribute
{
    public const string SinkA = "rodaje_a";
    public const string SinkB = "rodaje_b";

    private static readonly Lazy<bool> Server = new(() => OperatingSystem.IsLinux() && PulseOutput.IsServerAvailable());
    private static readonly Lazy<bool> Sinks = new(HasCiSinks);

    public PulseFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "PulseAudio es de Linux.";
        }
        else if (!Server.Value)
        {
            Skip = "No hay servidor PulseAudio o PipeWire.";
        }
    }

    private bool _twoSinks;

    public bool TwoSinks
    {
        get => _twoSinks;
        set
        {
            _twoSinks = value;
            if (value && Skip is null && !Sinks.Value)
            {
                Skip = $"Faltan los sumideros {SinkA} y {SinkB} (los crea ci.yml).";
            }
        }
    }

    private static bool HasCiSinks()
    {
        try
        {
            var info = new ProcessStartInfo("pactl", "list short sinks") { RedirectStandardOutput = true };
            using Process process = Process.Start(info)!;
            string list = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5_000);
            return list.Contains(SinkA, StringComparison.Ordinal) && list.Contains(SinkB, StringComparison.Ordinal);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
