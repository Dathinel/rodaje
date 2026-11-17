using System.Diagnostics;
using System.Runtime.Versioning;
using Rodaje.Audio.Interop;
using Rodaje.Core.Timing;
using Rodaje.Playback;

namespace Rodaje.Audio;

/// <summary>
/// Salida por la API asíncrona de PulseAudio (también sobre PipeWire), con el mismo contrato que <see cref="WasapiOutput"/>:
/// el reloj sigue la posición que el servidor dice que suena, la pausa congela, el salto vacía y la salida se reengancha
/// si cambia el sumidero predeterminado o se pierde el servidor.
/// </summary>
/// <remarks>
/// Un solo hilo es dueño del bucle, el contexto y el flujo de libpulse; las órdenes de transporte llegan por
/// <c>pa_mainloop_wakeup</c>. Cada salto o reapertura crea un flujo nuevo, que cuenta desde cero como un dispositivo
/// recién abierto: así <see cref="DevicePositionModel"/> se usa igual que con WASAPI.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed unsafe class PulseOutput : IAudioOutput, IRenderTransport
{
    private const int ControlTimeoutMilliseconds = 2000;
    private const int FrameBytes = 2 * sizeof(float);
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(1);

    // En un servidor compartido 20 ms dan cortes; 50 ms es lo que pide la mayoría de reproductores.
    private static readonly TimeSpan MinBuffer = TimeSpan.FromMilliseconds(50);

    private readonly AudioOutputOptions _options;
    private readonly FrameRenderer _renderer;
    private readonly DevicePositionModel _model;
    private readonly DeviceAudioClock _clock;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly object _control = new();
    private readonly long _leadFrames;
    private readonly float[] _buffer;

    private bool _wantRunning;
    private long _flushRequests;
    private Time _flushTarget;
    private long _requested;
    private long _applied;
    private bool _shutdown;
    private bool _threadExited;
    private string? _initError;

    private nint _mainloop;
    private nint _context;
    private nint _stream;
    private string? _streamDevice;
    private bool _running;
    private long _flushesDone;
    private long _nextRetry;
    private int _simulateLoss;
    private int _blockReopen;

    private AudioDeviceInfo? _device;
    private int _restarts;
    private int _controlTimeouts;
    private int _disposed;

    private PulseOutput(IAudioSampleSource source, AudioOutputOptions options)
    {
        _options = options;
        int rate = options.SampleRate;
        _renderer = new FrameRenderer(source, historyFrames: rate / 2, rampFrames: Math.Max(1, rate / 100), options.Volume);
        _model = new DevicePositionModel(rate, Stopwatch.Frequency, maxExtrapolationFrames: rate / 20, ToFrames(options.ExtraLatency, rate));
        _clock = new DeviceAudioClock(this);
        _leadFrames = rate / 50;
        _buffer = new float[rate / 5 * 2];
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Rodaje audio",
            Priority = ThreadPriority.Highest,
        };
    }

    public event EventHandler<AudioDeviceChangedEventArgs>? DeviceChanged;

    public IAudioClock Clock => _clock;

    public bool HasDevice => true;

    public string? FallbackReason => null;

    public AudioDeviceInfo? Device => Volatile.Read(ref _device);

    public int SampleRate => _options.SampleRate;

    public float Volume
    {
        get => _renderer.Volume;
        set => _renderer.Volume = value;
    }

    /// <remarks><see cref="AudioOutputStats.Starvations"/> queda en 0: libpulse solo lo avisa por una llamada de vuelta que aquí no se usa.</remarks>
    public AudioOutputStats Stats => new(
        _renderer.Written,
        _renderer.Underruns,
        _renderer.UnderrunFrames,
        _renderer.SourceErrors,
        0,
        Volatile.Read(ref _restarts),
        false,
        Volatile.Read(ref _controlTimeouts));

    /// <summary>Abre el sumidero predeterminado o lanza <see cref="AudioDeviceUnavailableException"/> explicando por qué no se pudo.</summary>
    public static PulseOutput Open(IAudioSampleSource source, AudioOutputOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new AudioOutputOptions();
        options.Validate();
        var output = new PulseOutput(source, options);
        output._thread.Start();
        output._ready.Wait();
        if (output._initError is { } error)
        {
            output.Dispose();
            throw new AudioDeviceUnavailableException(error);
        }

        return output;
    }

    /// <summary>Si hay un servidor de sonido al que conectarse; para decidir si correr las pruebas de Pulse.</summary>
    public static bool IsServerAvailable()
    {
        nint mainloop = 0;
        nint context = 0;
        try
        {
            mainloop = Pulse.pa_mainloop_new();
            if (mainloop == 0)
            {
                return false;
            }

            context = Pulse.Connect(mainloop, "Rodaje (sondeo)", TimeSpan.FromSeconds(2), out _);
            return context != 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        finally
        {
            Pulse.Release(ref context);
            if (mainloop != 0)
            {
                Pulse.pa_mainloop_free(mainloop);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_control)
        {
            _shutdown = true;
            Wake();
        }

        if (_thread.IsAlive)
        {
            _thread.Join();
        }

        _ready.Dispose();
    }

    long IRenderTransport.ReadFramesHeard() => _model.Read(Stopwatch.GetTimestamp());

    void IRenderTransport.Start() => Request(running: true, flush: null);

    void IRenderTransport.Pause()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            Request(running: false, flush: null);
        }
    }

    void IRenderTransport.Flush(Time position) => Request(running: null, flush: position);

    /// <summary>Para pruebas: el hilo de audio actúa como si el servidor hubiera cerrado la salida.</summary>
    /// <param name="blockReopen">Si es verdadero, no hay salida a la que volver hasta <see cref="AllowReopen"/>.</param>
    internal void SimulateDeviceLoss(bool blockReopen)
    {
        Volatile.Write(ref _blockReopen, blockReopen ? 1 : 0);
        Interlocked.Exchange(ref _simulateLoss, 1);
        lock (_control)
        {
            Wake();
        }
    }

    internal void AllowReopen()
    {
        Volatile.Write(ref _blockReopen, 0);
        lock (_control)
        {
            _nextRetry = 0;
            Wake();
        }
    }

    /// <summary>Latencia que informa el servidor para el flujo abierto (lo escrito que aún no suena); nula sin flujo.</summary>
    internal TimeSpan? LastReportedLatency { get; private set; }

    private static long ToFrames(TimeSpan duration, int rate) => (long)Math.Round(duration.TotalSeconds * rate);

    private static long Now() => Stopwatch.GetTimestamp();

    /// <summary>Solo con <see cref="_control"/> tomado y sin <see cref="_shutdown"/>: el hilo no libera el bucle hasta verlo.</summary>
    private void Wake()
    {
        if (_mainloop != 0 && !_threadExited)
        {
            Pulse.pa_mainloop_wakeup(_mainloop);
        }
    }

    private void Request(bool? running, Time? flush)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_control)
        {
            // Igual que WasapiOutput.DecideRequest: cerrado, una pausa sin salto se ignora y lo demás lanza.
            if (_shutdown && running == false && flush is null)
            {
                return;
            }

            ObjectDisposedException.ThrowIf(_shutdown, this);

            if (running is { } r)
            {
                _wantRunning = r;
            }

            if (flush is { } target)
            {
                _flushRequests++;
                _flushTarget = target;
            }

            long generation = ++_requested;
            Wake();
            long deadline = Environment.TickCount64 + ControlTimeoutMilliseconds;
            while (_applied < generation && !_threadExited)
            {
                int remaining = (int)(deadline - Environment.TickCount64);
                if (remaining <= 0 || !Monitor.Wait(_control, remaining))
                {
                    if (_applied < generation)
                    {
                        Interlocked.Increment(ref _controlTimeouts);
                    }

                    break;
                }
            }
        }
    }

    private void Run()
    {
        try
        {
            try
            {
                nint mainloop = Pulse.pa_mainloop_new();
                if (mainloop == 0)
                {
                    _initError = "libpulse no pudo crear su bucle de eventos.";
                    return;
                }

                lock (_control)
                {
                    _mainloop = mainloop;
                }

                if (!TryOpenStream(out string? error))
                {
                    _initError = error;
                    return;
                }
            }
            catch (DllNotFoundException)
            {
                _initError = "No está libpulse (el cliente de PulseAudio o PipeWire): instala el paquete libpulse0.";
                return;
            }
            finally
            {
                _ready.Set();
            }

            Loop();
        }
        finally
        {
            Pulse.Close(ref _stream);
            Pulse.Release(ref _context);
            lock (_control)
            {
                _threadExited = true;
                if (_mainloop != 0)
                {
                    Pulse.pa_mainloop_free(_mainloop);
                    _mainloop = 0;
                }

                Monitor.PulseAll(_control);
            }
        }
    }

    private void Loop()
    {
        while (true)
        {
            int timeout = _stream != 0 ? (_running ? 5 : 100) : _model.IsRunning ? 10 : 250;
            Pulse.Iterate(_mainloop, timeout);
            if (ApplyControl())
            {
                return;
            }

            if (Interlocked.Exchange(ref _simulateLoss, 0) != 0 && _stream != 0)
            {
                Reopen(lost: true, "se simuló la pérdida");
                continue;
            }

            if (_stream != 0)
            {
                Service();
            }
            else
            {
                FreeRun();
            }
        }
    }

    /// <returns>Verdadero si hay que terminar el hilo.</returns>
    private bool ApplyControl()
    {
        bool wantRunning;
        long flushes;
        Time target;
        long generation;
        lock (_control)
        {
            if (_shutdown)
            {
                return true;
            }

            wantRunning = _wantRunning;
            flushes = _flushRequests;
            target = _flushTarget;
            generation = _requested;
        }

        if (flushes != _flushesDone)
        {
            _flushesDone = flushes;
            DoFlush(target);
        }

        if (wantRunning != _running)
        {
            _running = wantRunning;
            if (wantRunning)
            {
                DoStart();
            }
            else
            {
                DoStop();
            }
        }

        lock (_control)
        {
            _applied = generation;
            Monitor.PulseAll(_control);
        }

        return false;
    }

    private void DoStart()
    {
        if (_stream != 0)
        {
            StartStream();
        }
        else
        {
            _model.Start(Now());
        }
    }

    private void DoStop()
    {
        if (_stream != 0)
        {
            Observe();
        }

        _model.Stop(Now());
        if (_stream != 0 && !Pulse.Await(_mainloop, Pulse.pa_stream_cork(_stream, 1, 0, 0), OperationTimeout))
        {
            Reopen(lost: true, "el servidor no confirmó la pausa");
        }
    }

    private void DoFlush(Time target)
    {
        bool hadStream = _stream != 0;
        Pulse.Close(ref _stream);
        _renderer.Seek(target);
        if (!hadStream)
        {
            _model.RebaseFreeRun(Now());
            return;
        }

        if (!TryOpenStream(out string? error))
        {
            _model.BeginSegment(_renderer.Written, discontinuity: true);
            EnterFreeRun(Now());
            RaiseDeviceChanged(null, $"Se perdió la salida de sonido ({error}). La reproducción sigue sin sonido y Rodaje volverá a intentarlo solo.");
            return;
        }

        _model.BeginSegment(_renderer.Written, discontinuity: true);
        if (_running)
        {
            StartStream();
        }
    }

    private void StartStream()
    {
        if (!Fill())
        {
            return;
        }

        if (!Pulse.Await(_mainloop, Pulse.pa_stream_cork(_stream, 0, 0, 0), OperationTimeout))
        {
            Reopen(lost: true, "el servidor no confirmó el arranque");
            return;
        }

        _model.Start(Now());
    }

    private void Service()
    {
        if (Pulse.pa_stream_get_state(_stream) != Pulse.StreamReady)
        {
            Reopen(lost: true, "el servidor cerró la salida");
            return;
        }

        // El servidor mueve el flujo cuando cambia el sumidero predeterminado: se reabre en el nuevo como hace WASAPI.
        string device = Pulse.Text(Pulse.pa_stream_get_device_name(_stream));
        if (device.Length > 0 && !string.Equals(device, _streamDevice, StringComparison.Ordinal))
        {
            if (_running)
            {
                Observe();
            }

            Reopen(lost: false, null);
            return;
        }

        if (Fill() && _running)
        {
            Observe();
        }
    }

    /// <returns>Falso si el flujo falló (y ya se intentó reabrir).</returns>
    private bool Fill()
    {
        if (!_running)
        {
            return true;
        }

        nuint writable = Pulse.pa_stream_writable_size(_stream);
        if (writable == nuint.MaxValue)
        {
            Reopen(lost: true, "el servidor rechazó la escritura");
            return false;
        }

        int frames = (int)Math.Min((long)writable / FrameBytes, _buffer.Length / 2);
        if (frames <= 0)
        {
            return true;
        }

        int produced = _renderer.Render(_buffer.AsSpan(0, frames * 2), frames, 2);
        int hr;
        fixed (float* data = _buffer)
        {
            hr = Pulse.pa_stream_write(_stream, data, (nuint)(frames * FrameBytes), 0, 0, Pulse.SeekRelative);
        }

        _model.AddWritten(produced);
        if (hr < 0)
        {
            Reopen(lost: true, "el servidor rechazó la escritura");
            return false;
        }

        return true;
    }

    private void Observe()
    {
        ulong microseconds;
        if (Pulse.pa_stream_get_time(_stream, &microseconds) < 0)
        {
            return;
        }

        long frames = (long)((UInt128)microseconds * (ulong)_options.SampleRate / 1_000_000);
        _model.Observe(frames, Now());

        int negative;
        ulong latency;
        if (Pulse.pa_stream_get_latency(_stream, &latency, &negative) >= 0)
        {
            LastReportedLatency = negative != 0 ? TimeSpan.Zero : TimeSpan.FromMicroseconds((long)latency);
        }
    }

    private void FreeRun()
    {
        long now = Now();
        if (_model.IsRunning)
        {
            long missing = _model.ReadHeard(now) + _leadFrames - _renderer.Written;
            if (missing > 0)
            {
                _renderer.Discard(missing);
                _model.AddWritten(missing);
            }
        }

        if (now >= _nextRetry)
        {
            _nextRetry = now + Stopwatch.Frequency;
            if (TryOpenStream(out _))
            {
                Resume();
                if (Device is { } info)
                {
                    RaiseDeviceChanged(info, $"El audio volvió: suena por {info.Name}.");
                }
            }
        }
    }

    /// <summary>Cierra el flujo y abre otro en el sumidero predeterminado; mientras tanto el reloj sigue con el de pared.</summary>
    private void Reopen(bool lost, string? why)
    {
        string previous = _device?.Name ?? "la salida de sonido";
        Pulse.Close(ref _stream);
        Interlocked.Increment(ref _restarts);
        EnterFreeRun(Now());
        if (TryOpenStream(out string? error))
        {
            Resume();
            if (Device is { } info)
            {
                RaiseDeviceChanged(info, lost ? $"Se perdió {previous} ({why}). El audio sigue por {info.Name}." : $"El audio ahora suena por {info.Name}.");
            }

            return;
        }

        RaiseDeviceChanged(null, $"Se perdió {previous} ({why ?? error}). La reproducción sigue sin sonido y Rodaje volverá a intentarlo solo.");
    }

    /// <summary>Pasa de correr libre al flujo recién abierto, empezando en el cuadro que marca el reloj.</summary>
    private void Resume()
    {
        long heard = _model.ReadHeard(Now());
        long missing = heard - _renderer.Written;
        if (missing > 0)
        {
            _renderer.Discard(missing);
            _model.AddWritten(missing);
        }

        long start = _renderer.BeginReplay(heard);
        _model.BeginSegment(start, discontinuity: false);
        if (_running)
        {
            StartStream();
        }
    }

    private void EnterFreeRun(long now)
    {
        _renderer.CancelReplay();
        _model.EnterFreeRun(now);
        if (_running && !_model.IsRunning)
        {
            _model.Start(now);
        }

        _nextRetry = now + Stopwatch.Frequency;
        Volatile.Write(ref _device, null);
        LastReportedLatency = null;
    }

    private bool TryOpenStream(out string? error)
    {
        if (Volatile.Read(ref _blockReopen) != 0)
        {
            error = "no hay salida disponible";
            return false;
        }

        if (_context != 0 && Pulse.pa_context_get_state(_context) != Pulse.ContextReady)
        {
            Pulse.Release(ref _context);
        }

        if (_context == 0 && (_context = Pulse.Connect(_mainloop, "Rodaje", OpenTimeout, out error)) == 0)
        {
            return false;
        }

        int rate = _options.SampleRate;
        var spec = new PulseSampleSpec { Format = Pulse.SampleFloat32Le, Rate = (uint)rate, Channels = 2 };
        nint stream = Pulse.pa_stream_new(_context, "Rodaje", &spec, 0);
        if (stream == 0)
        {
            error = Pulse.Error(_context);
            return false;
        }

        TimeSpan buffer = _options.BufferDuration > MinBuffer ? _options.BufferDuration : MinBuffer;
        // Sin prebuf: si la fuente se atrasa el servidor sigue con silencio y el reloj no se detiene, como en WASAPI.
        var attr = new PulseBufferAttr
        {
            MaxLength = Pulse.Default,
            TLength = (uint)(ToFrames(buffer, rate) * FrameBytes),
            PreBuf = 0,
            MinReq = Pulse.Default,
            FragSize = Pulse.Default,
        };
        int flags = Pulse.StreamStartCorked | Pulse.StreamInterpolateTiming | Pulse.StreamAutoTimingUpdate | Pulse.StreamAdjustLatency;
        if (Pulse.pa_stream_connect_playback(stream, null, &attr, flags, 0, 0) < 0 || !Pulse.WaitReady(_mainloop, stream, OpenTimeout))
        {
            error = $"No se pudo abrir la salida: {Pulse.Error(_context)}.";
            Pulse.Close(ref stream);
            return false;
        }

        _stream = stream;
        PulseBufferAttr* actual = Pulse.pa_stream_get_buffer_attr(stream);
        long bufferFrames = actual != null ? actual->TLength / FrameBytes : ToFrames(buffer, rate);
        long periodFrames = actual != null && actual->MinReq != Pulse.Default ? actual->MinReq / FrameBytes : bufferFrames / 4;
        _streamDevice = Pulse.Text(Pulse.pa_stream_get_device_name(stream));
        // Sin lecturas nuevas se interpola como mucho un buffer y un periodo más un margen: más allá, el servidor está atascado.
        _model.Configure(bufferFrames + periodFrames + (rate / 200), ToFrames(_options.ExtraLatency, rate));
        Volatile.Write(ref _device, new AudioDeviceInfo(
            _streamDevice,
            _streamDevice.Length > 0 ? _streamDevice : "Salida de sonido",
            2,
            rate,
            TimeSpan.FromSeconds((double)bufferFrames / rate),
            TimeSpan.FromSeconds((double)periodFrames / rate),
            false,
            TimeSpan.Zero));
        error = null;
        return true;
    }

    private void RaiseDeviceChanged(AudioDeviceInfo? device, string message)
    {
        if (DeviceChanged is not { } handler)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(
            static state => state.Handler(state.Sender, state.Args),
            (Handler: handler, Sender: (object)this, Args: new AudioDeviceChangedEventArgs(device, message)),
            preferLocal: false);
    }
}
