using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Rodaje.Audio.Interop;
using Rodaje.Core.Timing;
using Rodaje.Playback;

namespace Rodaje.Audio;

/// <summary>
/// Salida por CoreAudio (la unidad de salida predeterminada de AudioToolbox), con el mismo contrato que <see cref="WasapiOutput"/>
/// y <see cref="PulseOutput"/>: el reloj sigue lo que suena según la marca de tiempo de cada pedido del dispositivo, la pausa
/// congela, el salto vacía y la salida se reabre si cambia el dispositivo predeterminado.
/// </summary>
/// <remarks>
/// CoreAudio pide el audio desde su propio hilo (<see cref="OnRender"/>), que es el único que usa <see cref="FrameRenderer"/>
/// mientras la unidad suena. El hilo de control aplica las órdenes con la unidad detenida, vigila el dispositivo predeterminado
/// y corre libre con el reloj del sistema cuando no hay salida.
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed unsafe class CoreAudioOutput : IAudioOutput, IRenderTransport
{
    private const int ControlTimeoutMilliseconds = 2000;
    private const int MaxReopenDepth = 3;
    // Cada vuelta del hilo de control (50 ms): al cambiar el predeterminado la unidad deja de pedir audio y el reloj se pararía.
    private static readonly long DevicePollInterval = Stopwatch.Frequency / 20;

    private readonly AudioOutputOptions _options;
    private readonly FrameRenderer _renderer;
    private readonly DevicePositionModel _model;
    private readonly DeviceAudioClock _clock;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _ready = new();
    private readonly object _control = new();
    private readonly long _leadFrames;
    private GCHandle _self;

    private bool _wantRunning;
    private long _flushRequests;
    private Time _flushTarget;
    private long _requested;
    private long _applied;
    private bool _shutdown;
    private bool _threadExited;
    private string? _initError;

    private nint _unit;
    private uint _deviceId;
    private long _outputLatencyTicks;
    private long _segmentRendered;
    private bool _running;
    private bool _unitStarted;
    private long _flushesDone;
    private long _nextRetry;
    private long _nextPoll;
    private int _simulateLoss;
    private int _blockReopen;

    private AudioDeviceInfo? _device;
    private int _restarts;
    private int _reopenDepth;
    private int _controlTimeouts;
    private int _disposed;

    private CoreAudioOutput(IAudioSampleSource source, AudioOutputOptions options)
    {
        _options = options;
        int rate = options.SampleRate;
        _renderer = new FrameRenderer(source, historyFrames: rate / 2, rampFrames: Math.Max(1, rate / 100), options.Volume);
        _model = new DevicePositionModel(rate, Stopwatch.Frequency, maxExtrapolationFrames: rate / 20, ToFrames(options.ExtraLatency, rate));
        _clock = new DeviceAudioClock(this);
        _leadFrames = rate / 50;
        _self = GCHandle.Alloc(this);
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

    /// <remarks><see cref="AudioOutputStats.Starvations"/> queda en 0: CoreAudio no avisa de eso sin escuchar sus sobrecargas.</remarks>
    public AudioOutputStats Stats => new(
        _renderer.Written,
        _renderer.Underruns,
        _renderer.UnderrunFrames,
        _renderer.SourceErrors,
        0,
        Volatile.Read(ref _restarts),
        false,
        Volatile.Read(ref _controlTimeouts));

    /// <summary>Latencia del dispositivo (la suya más el margen de seguridad) que el reloj descuenta.</summary>
    internal TimeSpan OutputLatency => TimeSpan.FromSeconds((double)Volatile.Read(ref _outputLatencyTicks) / Stopwatch.Frequency);

    /// <summary>Abre el dispositivo de salida predeterminado o lanza <see cref="AudioDeviceUnavailableException"/> explicando por qué no se pudo.</summary>
    public static CoreAudioOutput Open(IAudioSampleSource source, AudioOutputOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new AudioOutputOptions();
        options.Validate();
        var output = new CoreAudioOutput(source, options);
        output._thread.Start();
        output._ready.Wait();
        if (output._initError is { } error)
        {
            output.Dispose();
            throw new AudioDeviceUnavailableException(error);
        }

        return output;
    }

    /// <summary>Si hay un dispositivo de salida; para decidir si correr las pruebas de CoreAudio.</summary>
    public static bool HasOutputDevice() => CoreAudio.DefaultDevice(input: false) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_control)
        {
            _shutdown = true;
        }

        _wake.Set();
        if (_thread.IsAlive)
        {
            _thread.Join();
        }

        // Cerrada, el reloj se congela como en WasapiOutput (si corría libre seguiría avanzando).
        _model.Stop(Now());

        _wake.Dispose();
        _ready.Dispose();
        if (_self.IsAllocated)
        {
            _self.Free();
        }
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

    /// <summary>Para pruebas: el hilo de control actúa como si el dispositivo se hubiera perdido.</summary>
    internal void SimulateDeviceLoss(bool blockReopen)
    {
        Volatile.Write(ref _blockReopen, blockReopen ? 1 : 0);
        Interlocked.Exchange(ref _simulateLoss, 1);
        _wake.Set();
    }

    internal void AllowReopen()
    {
        Volatile.Write(ref _blockReopen, 0);
        Volatile.Write(ref _nextRetry, 0);
        _wake.Set();
    }

    private static long ToFrames(TimeSpan duration, int rate) => (long)Math.Round(duration.TotalSeconds * rate);

    private static long Now() => Stopwatch.GetTimestamp();

    [UnmanagedCallersOnly]
    private static int Render(void* refCon, uint* flags, AudioTimeStampHead* time, uint bus, uint frames, AudioBufferListOne* data)
    {
        if (GCHandle.FromIntPtr((nint)refCon).Target is CoreAudioOutput self)
        {
            self.OnRender(time, frames, data);
        }

        return 0;
    }

    /// <summary>El pedido de CoreAudio: lee lo que suena según su marca de tiempo y llena el búfer.</summary>
    private void OnRender(AudioTimeStampHead* time, uint frames, AudioBufferListOne* data)
    {
        long now = Now();
        if (data == null || data->NumberBuffers == 0 || data->Data == null)
        {
            return;
        }

        // El primer cuadro de este búfer sale por el dispositivo en HostTime más su latencia: lo que suena ahora va por detrás.
        long play = CoreAudio.HostToStopwatch(time->HostTime) + Volatile.Read(ref _outputLatencyTicks);
        long inFlight = (long)((Int128)(play - now) * _options.SampleRate / Stopwatch.Frequency);
        long heard = _segmentRendered - inFlight;
        if (heard > 0)
        {
            _model.Observe(heard, now);
        }

        int channels = (int)Math.Max(1, data->NumberChannels);
        int count = (int)Math.Min(frames, data->DataByteSize / (uint)(channels * sizeof(float)));
        int produced = _renderer.Render(new Span<float>(data->Data, count * channels), count, channels);
        _model.AddWritten(produced);
        _segmentRendered += count;
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
            _wake.Set();
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
                if (!TryOpenUnit(out string? error))
                {
                    _initError = error;
                    return;
                }
            }
            catch (DllNotFoundException)
            {
                _initError = "No se pudo cargar CoreAudio.";
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
            CloseUnit();
            lock (_control)
            {
                _threadExited = true;
                Monitor.PulseAll(_control);
            }
        }
    }

    private void Loop()
    {
        while (true)
        {
            _wake.WaitOne(_unit != 0 ? 50 : _model.IsRunning ? 10 : 250);
            if (ApplyControl())
            {
                return;
            }

            if (Interlocked.Exchange(ref _simulateLoss, 0) != 0 && _unit != 0)
            {
                Reopen(lost: true, "se simuló la pérdida");
                continue;
            }

            long now = Now();
            if (_unit != 0)
            {
                if (now >= _nextPoll)
                {
                    _nextPoll = now + DevicePollInterval;
                    uint current = CoreAudio.DefaultDevice(input: false);
                    if (current != _deviceId)
                    {
                        Reopen(lost: current == 0, current == 0 ? "ya no hay dispositivo de salida" : null);
                    }
                }
            }
            else
            {
                FreeRun(now);
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
                StartUnit();
            }
            else
            {
                StopUnit();
                _model.Stop(Now());
            }
        }

        lock (_control)
        {
            _applied = generation;
            Monitor.PulseAll(_control);
        }

        return false;
    }

    private void DoFlush(Time target)
    {
        StopUnit();
        if (_unit != 0)
        {
            // Lo que quedó en el conversor de la unidad no debe sonar tras el salto.
            _ = CoreAudio.AudioUnitReset(_unit, CoreAudio.ScopeGlobal, 0);
        }

        _renderer.Seek(target);
        if (_unit == 0)
        {
            _model.RebaseFreeRun(Now());
            return;
        }

        _segmentRendered = 0;
        _model.BeginSegment(_renderer.Written, discontinuity: true);
        if (_running)
        {
            StartUnit();
        }
    }

    private void StartUnit()
    {
        if (_unit == 0)
        {
            _model.Start(Now());
            return;
        }

        int status = CoreAudio.AudioOutputUnitStart(_unit);
        if (status != 0)
        {
            Reopen(lost: true, CoreAudio.Describe(status));
            return;
        }

        _unitStarted = true;
        _model.Start(Now());
    }

    /// <summary>Detiene la unidad; al volver, CoreAudio ya no llama a <see cref="OnRender"/>.</summary>
    private void StopUnit()
    {
        if (_unit != 0 && _unitStarted)
        {
            _ = CoreAudio.AudioOutputUnitStop(_unit);
            _unitStarted = false;
        }
    }

    private void FreeRun(long now)
    {
        if (_model.IsRunning)
        {
            long missing = _model.ReadHeard(now) + _leadFrames - _renderer.Written;
            if (missing > 0)
            {
                _renderer.Discard(missing);
                _model.AddWritten(missing);
            }
        }

        if (now >= Volatile.Read(ref _nextRetry))
        {
            _nextRetry = now + Stopwatch.Frequency;
            if (TryOpenUnit(out _))
            {
                Resume();
                if (Device is { } info)
                {
                    RaiseDeviceChanged(info, $"El audio volvió: suena por {info.Name}.");
                }
            }
        }
    }

    private void Reopen(bool lost, string? why)
    {
        string previous = _device?.Name ?? "el dispositivo de audio";
        // Primero el reloj de pared: cerrar la unidad mientras macOS cambia de dispositivo tarda unos 300 ms.
        _model.EnterFreeRun(Now());
        CloseUnit();
        Interlocked.Increment(ref _restarts);
        EnterFreeRun(Now());
        // Abrir bien y arrancar mal vuelve a entrar aquí (Resume → Start → Reopen): con tope, como WASAPI; si no, corre
        // libre y el bucle reintenta cada segundo.
        string? error = "se intentó reabrir demasiadas veces seguidas";
        if (_reopenDepth < MaxReopenDepth && TryOpenUnit(out error))
        {
            _reopenDepth++;
            try
            {
                Resume();
            }
            finally
            {
                _reopenDepth--;
            }

            if (Device is { } info)
            {
                RaiseDeviceChanged(info, lost ? $"Se perdió {previous} ({why}). El audio sigue por {info.Name}." : $"El audio ahora suena por {info.Name}.");
            }

            return;
        }

        RaiseDeviceChanged(null, $"Se perdió {previous} ({why ?? error}). La reproducción sigue sin sonido y Rodaje volverá a intentarlo solo.");
    }

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
        _segmentRendered = 0;
        _model.BeginSegment(start, discontinuity: false);
        if (_running)
        {
            StartUnit();
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
    }

    private bool TryOpenUnit(out string? error)
    {
        if (Volatile.Read(ref _blockReopen) != 0)
        {
            error = "no hay dispositivo disponible";
            return false;
        }

        uint device = CoreAudio.DefaultDevice(input: false);
        if (device == 0)
        {
            error = "No hay ningún dispositivo de salida de audio.";
            return false;
        }

        nint unit = CoreAudio.NewUnit(CoreAudio.SubTypeDefaultOutput);
        if (unit == 0)
        {
            error = "No se pudo crear la unidad de salida de CoreAudio.";
            return false;
        }

        int rate = _options.SampleRate;
        AudioStreamBasicDescription format = CoreAudio.FloatFormat(rate, 2);
        var callback = new AURenderCallbackStruct
        {
            InputProc = (void*)(delegate* unmanaged<void*, uint*, AudioTimeStampHead*, uint, uint, AudioBufferListOne*, int>)&Render,
            InputProcRefCon = (void*)GCHandle.ToIntPtr(_self),
        };
        int status = CoreAudio.AudioUnitSetProperty(unit, CoreAudio.PropertyStreamFormat, CoreAudio.ScopeInput, 0, &format, (uint)sizeof(AudioStreamBasicDescription));
        if (status == 0)
        {
            status = CoreAudio.AudioUnitSetProperty(unit, CoreAudio.PropertySetRenderCallback, CoreAudio.ScopeInput, 0, &callback, (uint)sizeof(AURenderCallbackStruct));
        }

        if (status == 0)
        {
            status = CoreAudio.AudioUnitInitialize(unit);
        }

        if (status != 0)
        {
            CoreAudio.DisposeUnit(ref unit);
            error = $"CoreAudio no aceptó la salida ({CoreAudio.Describe(status)}).";
            return false;
        }

        uint size = sizeof(uint);
        uint current = device;
        _ = CoreAudio.AudioUnitGetProperty(unit, CoreAudio.PropertyCurrentDevice, CoreAudio.ScopeGlobal, 0, &current, &size);
        uint latency = CoreAudio.DeviceUInt(current, CoreAudio.SelectorLatency, CoreAudio.ScopeObjectOutput)
            + CoreAudio.DeviceUInt(current, CoreAudio.SelectorSafetyOffset, CoreAudio.ScopeObjectOutput);
        uint bufferFrames = Math.Max(1, CoreAudio.DeviceUInt(current, CoreAudio.SelectorBufferFrameSize, CoreAudio.ScopeObjectGlobal));
        string name = CoreAudio.DeviceText(current, CoreAudio.SelectorName);

        _unit = unit;
        _deviceId = current;
        _unitStarted = false;
        _nextPoll = Now() + DevicePollInterval;
        Volatile.Write(ref _outputLatencyTicks, (long)((double)latency / rate * Stopwatch.Frequency));
        // Entre pedidos se interpola como mucho dos búferes más un margen: más allá, el dispositivo está atascado.
        _model.Configure((2 * bufferFrames) + (rate / 200), ToFrames(_options.ExtraLatency, rate));
        Volatile.Write(ref _device, new AudioDeviceInfo(
            CoreAudio.DeviceText(current, CoreAudio.SelectorDeviceUid),
            name.Length > 0 ? name : "Salida de audio",
            2,
            rate,
            TimeSpan.FromSeconds((double)bufferFrames / rate),
            TimeSpan.FromSeconds((double)bufferFrames / rate),
            false,
            TimeSpan.FromSeconds((double)latency / rate)));
        error = null;
        return true;
    }

    private void CloseUnit()
    {
        StopUnit();
        CoreAudio.DisposeUnit(ref _unit);
        _deviceId = 0;
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
