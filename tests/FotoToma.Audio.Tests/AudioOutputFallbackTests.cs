using FotoToma.Audio.Interop;
using FotoToma.Core.Timing;
using FotoToma.Playback;

namespace FotoToma.Audio.Tests;

public class AudioOutputFallbackTests
{
    [Fact]
    public void SinDispositivoUsaElRelojDelSistemaYExplicaPorQue()
    {
        const string Reason = "Windows no tiene ningún dispositivo de salida de audio activo (altavoces, auriculares o HDMI).";
        using IAudioOutput output = AudioOutput.Open(
            new SilentSource(),
            new AudioOutputOptions { Volume = 0.3f },
            (_, _) => throw new AudioDeviceUnavailableException(Reason, AudioHResults.ENotFound));

        Assert.IsType<NullAudioOutput>(output);
        Assert.False(output.HasDevice);
        Assert.Equal(Reason, output.FallbackReason);
        Assert.Null(output.Device);
        Assert.IsType<SystemClock>(output.Clock);
        Assert.Equal(0.3f, output.Volume);
        Assert.Equal(default, output.Stats);

        output.Clock.Start();
        Thread.Sleep(20);
        Assert.True(output.Clock.Position > Time.Zero);
        output.Clock.Pause();
        Time paused = output.Clock.Position;
        Thread.Sleep(20);
        Assert.Equal(paused, output.Clock.Position);
    }

    [Fact]
    public void LaSalidaMudaValidaYAceptaSuscriptores()
    {
        using var output = new NullAudioOutput("motivo");
        EventHandler<AudioDeviceChangedEventArgs> handler = (_, _) => { };
        output.DeviceChanged += handler;
        output.DeviceChanged -= handler;
        output.Volume = 4f;
        Assert.Equal(1f, output.Volume);
        Assert.Throws<ArgumentOutOfRangeException>(() => output.Volume = float.NaN);
        Assert.Throws<ArgumentException>(() => new NullAudioOutput(" "));
    }

    [Fact]
    public void OtrosErroresNoSeTapan()
    {
        Assert.Throws<InvalidOperationException>(() => AudioOutput.Open(
            new SilentSource(), new AudioOutputOptions(), (_, _) => throw new InvalidOperationException("defecto")));
    }

    [Fact]
    public void LasOpcionesSeValidanAntesDeAbrir()
    {
        static IAudioOutput Never(IAudioSampleSource s, AudioOutputOptions o) => throw new InvalidOperationException("No debía abrir.");

        Assert.Throws<ArgumentNullException>(() => AudioOutput.Open(null!, new AudioOutputOptions(), Never));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioOutput.Open(new SilentSource(), new AudioOutputOptions { SampleRate = 1_000 }, Never));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioOutput.Open(new SilentSource(), new AudioOutputOptions { BufferDuration = TimeSpan.FromMilliseconds(1) }, Never));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioOutput.Open(new SilentSource(), new AudioOutputOptions { LowLatencyPeriod = TimeSpan.Zero }, Never));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioOutput.Open(new SilentSource(), new AudioOutputOptions { ExtraLatency = TimeSpan.FromSeconds(-1) }, Never));
        Assert.Throws<ArgumentNullException>(() => WasapiOutput.Open(null!));
    }

    [Theory]
    [InlineData(AudioHResults.ENotFound, "ningún dispositivo")]
    [InlineData(AudioHResults.ServiceNotRunning, "Audiosrv")]
    [InlineData(AudioHResults.DeviceInUse, "modo exclusivo")]
    [InlineData(AudioHResults.DeviceInvalidated, "desconectó")]
    [InlineData(AudioHResults.ResourcesInvalidated, "desconectó")]
    [InlineData(AudioHResults.EndpointCreateFailed, "no pudo preparar")]
    [InlineData(AudioHResults.UnsupportedFormat, "coma flotante")]
    [InlineData(AudioHResults.CpuUsageExceeded, "saturado")]
    [InlineData(unchecked((int)0x80040154), "MMDevice")]
    [InlineData(unchecked((int)0x80004005), "0x80004005")]
    public void LosCodigosDeWindowsSeExplicanEnEspanol(int hr, string expected)
    {
        Assert.Contains(expected, Com.Describe(hr), StringComparison.Ordinal);
        var ex = Assert.Throws<AudioDeviceUnavailableException>(() => Com.Check(hr));
        Assert.Equal(hr, ex.HResult);
        Com.Check(0);
        Com.Check(AudioHResults.SFalse);
    }

    [Fact]
    public void LaExcepcionTieneLosConstructoresHabituales()
    {
        Assert.NotNull(new AudioDeviceUnavailableException().Message);
        var inner = new InvalidOperationException();
        Assert.Same(inner, new AudioDeviceUnavailableException("m", inner).InnerException);
        var args = new AudioDeviceChangedEventArgs(null, "sin sonido");
        Assert.Null(args.Device);
        Assert.Equal("sin sonido", args.Message);
    }

    private sealed class SilentSource : IAudioSampleSource
    {
        public int Read(Span<float> interleaved)
        {
            interleaved.Clear();
            return interleaved.Length / 2;
        }
    }
}
