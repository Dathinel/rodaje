using System.Globalization;
using FotoToma.Core.Timing;
using Xunit.Abstractions;

namespace FotoToma.Audio.Tests.Hardware;

/// <remarks>
/// Corren solo si hay un dispositivo de salida real. Las que no llevan "Tono" reproducen silencio, así
/// que no hacen ruido; la del tono necesita <c>FOTOTOMA_PRUEBAS_AUDIO=1</c>. Las cifras salen en la
/// salida de cada prueba (<c>--logger "console;verbosity=detailed"</c>).
/// </remarks>
[Collection(nameof(DispositivoDeAudioReal))]
public class WasapiHardwareTests(ITestOutputHelper output)
{
    private static readonly TimeSpan MeasureFor = TimeSpan.FromSeconds(3);

    [AudioDeviceTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void ElRelojSigueAlDispositivoSinDerivaNiSaltos(bool lowLatency)
    {
        var source = new ToneSource(48_000, 440, amplitude: 0f);
        using var audio = WasapiOutput.Open(source, new AudioOutputOptions { PreferLowLatency = lowLatency });

        ClockMeasurement m = ClockMeasurement.Run(audio.Clock, MeasureFor);
        audio.Clock.Pause();
        Report(audio, m);

        AssertHealthy(audio, m);
    }

    [AudioToneFact]
    public void TonoDeTresSegundos()
    {
        var source = new ToneSource(48_000, 440, amplitude: 0.2f);
        using var audio = WasapiOutput.Open(source);

        ClockMeasurement m = ClockMeasurement.Run(audio.Clock, MeasureFor);
        audio.Clock.Pause();
        Report(audio, m);

        AssertHealthy(audio, m);
    }

    [AudioDeviceFact]
    public void PausaSeekYVolumenConElDispositivoReal()
    {
        var source = new RecordingSource();
        using var audio = WasapiOutput.Open(source, new AudioOutputOptions { Volume = 0.5f });
        Assert.True(audio.HasDevice);
        Assert.Null(audio.FallbackReason);
        Assert.Equal(48_000, audio.SampleRate);
        Assert.Equal(0.5f, audio.Volume);
        audio.Volume = 0f;
        Assert.False(string.IsNullOrWhiteSpace(audio.Device!.Name));
        var clock = audio.Clock;

        clock.Start();
        Thread.Sleep(200);
        clock.Pause();
        Time paused = clock.Position;
        Assert.InRange(paused.ToSeconds(), 0.1, 0.3);
        Thread.Sleep(150);
        Assert.Equal(paused, clock.Position);

        clock.Seek(Time.FromSeconds(10));
        Assert.Equal(Time.FromSeconds(10), clock.Position);
        Assert.Equal(Time.FromSeconds(10), source.LastSeek);
        Thread.Sleep(50);
        Assert.Equal(Time.FromSeconds(10), clock.Position);

        clock.Start();
        Thread.Sleep(300);
        Assert.InRange(clock.Position.ToSeconds(), 10.2, 10.4);

        clock.Seek(Time.FromSeconds(20));
        Time afterSeek = clock.Position;
        Assert.InRange(afterSeek.ToSeconds(), 20, 20.05);
        Thread.Sleep(100);
        Assert.True(clock.Position > afterSeek);
        Assert.True(clock.IsRunning);
        clock.Pause();

        AudioOutputStats stats = audio.Stats;
        output.WriteLine(Describe(stats));
        Assert.Equal(0, stats.Underruns);
        Assert.Equal(0, stats.ControlTimeouts);
        Assert.Equal(0, stats.DeviceRestarts);

        clock.Start();
        audio.Dispose();
        audio.Dispose();
        Time disposed = clock.Position;
        Thread.Sleep(50);
        Assert.Equal(disposed, clock.Position);
        clock.Pause();
        Assert.Throws<ObjectDisposedException>(() => clock.Seek(Time.Zero));
        Assert.Throws<ObjectDisposedException>(() => clock.Start());
    }

    [AudioDeviceFact]
    public void PerderElDispositivoNoHaceRetrocederNiSaltarAlReloj()
    {
        var source = new RecordingSource();
        using var audio = WasapiOutput.Open(source);
        var messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
        audio.DeviceChanged += (_, e) => messages.Enqueue(e.Message);
        var trace = new ClockTrace(audio.Clock);

        audio.Clock.Start();
        trace.SampleFor(TimeSpan.FromMilliseconds(300));

        audio.SimulateDeviceLoss(blockReopen: false);
        trace.SampleFor(TimeSpan.FromMilliseconds(300));
        Assert.NotNull(audio.Device);

        audio.SimulateDeviceLoss(blockReopen: true);
        trace.SampleFor(TimeSpan.FromMilliseconds(100));
        Assert.Null(audio.Device);
        double freeRunStart = audio.Clock.Position.ToSeconds();
        trace.SampleFor(TimeSpan.FromMilliseconds(300));
        double freeRunAdvance = audio.Clock.Position.ToSeconds() - freeRunStart;

        audio.AllowReopen();
        trace.SampleUntil(() => audio.Device is not null, TimeSpan.FromSeconds(3));
        Assert.NotNull(audio.Device);
        trace.SampleFor(TimeSpan.FromMilliseconds(300));
        audio.Clock.Pause();
        SpinWait.SpinUntil(() => messages.Count >= 3, TimeSpan.FromSeconds(2));

        output.WriteLine(trace.Describe());
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Avance sin dispositivo en 300 ms: {freeRunAdvance * 1_000:0.0} ms."));
        foreach (string message in messages)
        {
            output.WriteLine("Aviso: " + message);
        }

        output.WriteLine(Describe(audio.Stats));
        Assert.Equal(0, trace.Backwards);
        Assert.InRange(trace.MaxForwardJumpMs, 0, 30);
        Assert.InRange(trace.MaxStallMs, 0, 50);
        Assert.InRange(freeRunAdvance, 0.27, 0.33);
        Assert.Equal(2, audio.Stats.DeviceRestarts);
        Assert.Equal(3, messages.Count);
        Assert.Contains(messages, m => m.Contains("sin sonido", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("volvió", StringComparison.Ordinal));
    }

    [AudioDeviceFact]
    public void LaFabricaDevuelveLaSalidaRealCuandoHayDispositivo()
    {
        using IAudioOutput audio = AudioOutput.Open(new RecordingSource());
        Assert.IsType<WasapiOutput>(audio);
        Assert.True(audio.HasDevice);
        Assert.IsType<DeviceAudioClock>(audio.Clock);
        AudioDeviceInfo device = audio.Device!;
        output.WriteLine(Describe(device));
        Assert.InRange(device.Channels, 1, 32);
        Assert.True(device.BufferDuration > TimeSpan.Zero);
        Assert.True(device.Period > TimeSpan.Zero && device.Period <= device.BufferDuration);
    }

    private static void AssertHealthy(WasapiOutput audio, ClockMeasurement m)
    {
        Assert.Equal(0, m.Backwards);
        Assert.InRange(m.StartupMs, 0, 150);
        Assert.InRange(Math.Abs(m.DriftPpm), 0, 1_000);
        Assert.InRange(m.MaxResidualMs, 0, 3);
        Assert.InRange(m.EndPositionSeconds, 2.8, 3.05);
        AudioOutputStats stats = audio.Stats;
        Assert.Equal(0, stats.Underruns);
        Assert.Equal(0, stats.SourceErrors);
        Assert.Equal(0, stats.DeviceRestarts);
        // Lo leído de la fuente va como mucho un buffer (más un bloque de render) por delante de lo que suena.
        double ahead = (stats.FramesRendered / 48_000.0) - m.EndPositionSeconds;
        Assert.InRange(ahead, 0, audio.Device!.BufferDuration.TotalSeconds + 0.03);
    }

    private static string Describe(AudioDeviceInfo d) => string.Create(
        CultureInfo.InvariantCulture,
        $"Dispositivo: {d.Name}; {d.Channels} canales; mezclador a {d.MixSampleRate} Hz; buffer {d.BufferDuration.TotalMilliseconds:0.00} ms; " +
        $"periodo {d.Period.TotalMilliseconds:0.00} ms; baja latencia (IAudioClient3): {(d.LowLatency ? "sí" : "no")}; " +
        $"GetStreamLatency {d.StreamLatency.TotalMilliseconds:0.00} ms.");

    private static string Describe(AudioOutputStats s) => string.Create(
        CultureInfo.InvariantCulture,
        $"Cuadros producidos {s.FramesRendered}; faltas de la fuente {s.Underruns} ({s.UnderrunFrames} cuadros); " +
        $"buffer vaciado {s.Starvations} veces; reaperturas {s.DeviceRestarts}; MMCSS Pro Audio: {(s.ProAudioPriority ? "sí" : "no")}; " +
        $"órdenes sin confirmar {s.ControlTimeouts}.");

    private void Report(WasapiOutput audio, ClockMeasurement m)
    {
        output.WriteLine(Describe(audio.Device!));
        output.WriteLine(m.Describe());
        output.WriteLine(Describe(audio.Stats));
    }

    private sealed class RecordingSource : IAudioSampleSource
    {
        private long _lastSeekTicks = -1;

        public Time? LastSeek
        {
            get
            {
                long ticks = Interlocked.Read(ref _lastSeekTicks);
                return ticks < 0 ? null : Time.FromTicks(ticks);
            }
        }

        public int Read(Span<float> interleaved)
        {
            interleaved.Clear();
            return interleaved.Length / 2;
        }

        public void Seek(Time position) => Interlocked.Exchange(ref _lastSeekTicks, position.Ticks);
    }
}

/// <summary>Las pruebas con el dispositivo real no corren en paralelo entre sí: compartirían la salida y las medidas.</summary>
[CollectionDefinition(nameof(DispositivoDeAudioReal), DisableParallelization = true)]
public sealed class DispositivoDeAudioReal;
