using Rodaje.Core.Timing;

namespace Rodaje.Audio.Tests;

public class DeviceAudioClockTests
{
    private const long Ms = 10_000;

    [Fact]
    public void SigueAlDispositivoDesdeElDestinoDelSeek()
    {
        var device = new FakeTransport();
        var clock = new DeviceAudioClock(device);
        Assert.Equal(48_000, clock.SampleRate);
        Assert.Equal(Time.Zero, clock.Position);

        clock.Seek(Time.FromSeconds(10));
        Assert.Equal(Time.FromSeconds(10), clock.Position);

        clock.Start();
        Assert.True(clock.IsRunning);
        device.Play(Ms * 500, framesPerMs: 48);
        Assert.Equal(Time.FromSeconds(10.5), clock.Position);
        Assert.Equal(["flush 00:00:10.000", "start"], device.Calls);
    }

    [Fact]
    public void EnPausaQuedaCongeladoYAlSeguirContinua()
    {
        var device = new FakeTransport();
        var clock = new DeviceAudioClock(device);
        clock.Start();
        device.Play(Ms * 100, framesPerMs: 48);
        clock.Pause();
        Time paused = clock.Position;
        Assert.Equal(Time.FromSeconds(0.1), paused);

        device.Now += Ms * 1_000;
        Assert.Equal(paused, clock.Position);

        clock.Start();
        device.Play(Ms * 100, framesPerMs: 48);
        Assert.Equal(Time.FromSeconds(0.2), clock.Position);
    }

    [Fact]
    public void SeekEnPausaYDespuesArrancaDesdeAhi()
    {
        var device = new FakeTransport();
        var clock = new DeviceAudioClock(device);
        clock.Start();
        device.Play(Ms * 100, framesPerMs: 48);
        clock.Pause();
        clock.Seek(Time.FromSeconds(3));
        Assert.Equal(Time.FromSeconds(3), clock.Position);
        Assert.False(clock.IsRunning);

        clock.Start();
        Assert.Equal(Time.FromSeconds(3), clock.Position);
        device.Play(Ms * 250, framesPerMs: 48);
        Assert.Equal(Time.FromSeconds(3.25), clock.Position);
    }

    [Fact]
    public void ReiniciarElDispositivoNoMueveLaPosicion()
    {
        var device = new FakeTransport();
        var clock = new DeviceAudioClock(device);
        clock.Seek(Time.FromSeconds(5));
        clock.Start();
        device.Play(Ms * 300, framesPerMs: 48);
        Time before = clock.Position;

        device.RestartDevice();
        Assert.Equal(before, clock.Position);
        device.Play(Ms * 200, framesPerMs: 48);
        Assert.Equal(before + Time.FromSeconds(0.2), clock.Position);
    }

    [Fact]
    public void LecturasConcurrentesSonMonotonas()
    {
        // El escritor va en un hilo propio, no en el pool: con pocos núcleos una tarea del pool podía no arrancar
        // antes de terminar las lecturas. Se lee al menos 200 000 veces y hasta que el reloj avance.
        var device = new FakeTransport();
        var clock = new DeviceAudioClock(device);
        clock.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        bool stop = false;
        var writer = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                device.Play(Ms / 2, framesPerMs: 48);
            }
        })
        { IsBackground = true, Name = "Escritor del dispositivo" };
        writer.Start();

        Time last = Time.Zero;
        for (int i = 0; (i < 200_000 || last == Time.Zero) && !deadline.IsCancellationRequested; i++)
        {
            Time now = clock.Position;
            Assert.True(now >= last);
            last = now;
        }

        Volatile.Write(ref stop, true);
        writer.Join();
        Assert.True(last > Time.Zero);
    }

    /// <summary>Dispositivo falso: el transporte de una salida real, sobre el mismo modelo, con un reloj de pared manual.</summary>
    private sealed class FakeTransport : IRenderTransport
    {
        private const long Plenty = 1_000_000_000;
        private readonly Lock _gate = new();
        private readonly DevicePositionModel _model = new(48_000, 10_000_000, maxExtrapolationFrames: 4_800);
        private long _deviceFrames;
        private long _now;

        public FakeTransport() => _model.AddWritten(Plenty);

        public int SampleRate => 48_000;

        public List<string> Calls { get; } = [];

        public long Now
        {
            get => Volatile.Read(ref _now);
            set => Volatile.Write(ref _now, value);
        }

        public long ReadFramesHeard() => _model.Read(Now);

        public void Start()
        {
            Calls.Add("start");
            _model.Start(Now);
        }

        public void Pause()
        {
            Calls.Add("pause");
            _model.Stop(Now);
        }

        public void Flush(Time position)
        {
            Calls.Add($"flush {position}");
            lock (_gate)
            {
                _deviceFrames = 0;
                _model.BeginSegment(_model.Written, discontinuity: true);
                _model.AddWritten(Plenty);
            }
        }

        /// <summary>Avanza el tiempo; si el modelo corre, el dispositivo reproduce y publica una lectura con su marca.</summary>
        public void Play(long ticks, long framesPerMs)
        {
            lock (_gate)
            {
                Now += ticks;
                if (_model.IsRunning)
                {
                    _deviceFrames += ticks * framesPerMs / Ms;
                }

                _model.Observe(_deviceFrames, Now);
            }
        }

        public void RestartDevice()
        {
            lock (_gate)
            {
                long heard = _model.ReadHeard(Now);
                _deviceFrames = 0;
                _model.BeginSegment(heard, discontinuity: false);
                _model.Start(Now);
            }
        }
    }
}
