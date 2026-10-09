using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using FotoToma.Audio.Interop;
using FotoToma.Core.Timing;
using FotoToma.Playback;

namespace FotoToma.Audio;

/// <summary>Salida WASAPI en modo compartido por eventos, con su propio hilo "Pro Audio" y reloj maestro.</summary>
/// <remarks>
/// <para>Hilos: todo lo que toca COM vive en un único hilo de audio (MTA). Start, Pause y Seek del reloj
/// dejan una orden y esperan, con tope, a que ese hilo la aplique; así el hilo de alta prioridad nunca
/// espera un candado que tenga la interfaz. <see cref="Volume"/> y <see cref="Stats"/> se leen sin esperar.</para>
/// <para>Si el dispositivo se pierde o cambia el predeterminado, el reloj pasa en el acto al reloj de pared y
/// la fuente se consume a ese ritmo, así que ni el reloj ni el video se detienen ni saltan. Al abrir el nuevo
/// dispositivo el audio retoma en la posición del reloj; lo que estaba en cola sin oírse se repite si el reloj
/// aún no lo pasó. Sin ningún dispositivo se reintenta cada segundo y con cada aviso de Windows.</para>
/// </remarks>
public sealed unsafe class WasapiOutput : IAudioOutput, IRenderTransport
{
    private const int ControlTimeoutMilliseconds = 2000;
    private const int MaxReopenDepth = 3;

    private readonly AudioOutputOptions _options;
    private readonly FrameRenderer _renderer;
    private readonly DevicePositionModel _model;
    private readonly DeviceAudioClock _clock;
    private readonly StrategyBasedComWrappers _wrappers = new();
    private readonly Thread _thread;
    private readonly nint _wakeEvent;
    private readonly nint _audioEvent;
    private readonly ManualResetEventSlim _ready = new();
    private readonly object _control = new();
    private readonly long _leadFrames;

    private bool _wantRunning;
    private long _flushRequests;
    private Time _flushTarget;
    private long _requested;
    private long _applied;
    private bool _shutdown;
    private bool _threadExited;

    private string? _initError;
    private int _initHResult;

    private IMMDeviceEnumerator? _enumerator;
    private WasapiSession? _session;
    private bool _running;
    private bool _primed;
    private long _flushesDone;
    private long _nextRetry;
    private int _reopenDepth;
    private int _defaultChanged;
    private int _devicesChanged;
    private int _simulateLoss;
    private int _blockReopen;

    private AudioDeviceInfo? _device;
    private long _starvations;
    private int _restarts;
    private int _controlTimeouts;
    private bool _proAudio;
    private int _disposed;

    private WasapiOutput(IAudioSampleSource source, AudioOutputOptions options)
    {
        _options = options;
        int rate = options.SampleRate;
        _renderer = new FrameRenderer(source, historyFrames: rate / 2, rampFrames: Math.Max(1, rate / 100), options.Volume);
        _model = new DevicePositionModel(rate, Stopwatch.Frequency, maxExtrapolationFrames: rate / 20, ToFrames(options.ExtraLatency, rate));
        _clock = new DeviceAudioClock(this);
        _leadFrames = rate / 50;
        _wakeEvent = CreateEvent();
        _audioEvent = CreateEvent();
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "FotoToma audio",
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

    public AudioOutputStats Stats => new(
        _renderer.Written,
        _renderer.Underruns,
        _renderer.UnderrunFrames,
        _renderer.SourceErrors,
        Volatile.Read(ref _starvations),
        Volatile.Read(ref _restarts),
        Volatile.Read(ref _proAudio),
        Volatile.Read(ref _controlTimeouts));

    /// <summary>Abre el dispositivo predeterminado o lanza <see cref="AudioDeviceUnavailableException"/> explicando por qué no se pudo.</summary>
    public static WasapiOutput Open(IAudioSampleSource source, AudioOutputOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new AudioOutputOptions();
        options.Validate();
        var output = new WasapiOutput(source, options);
        output._thread.Start();
        output._ready.Wait();
        if (output._initError is { } error)
        {
            int hr = output._initHResult;
            output.Dispose();
            throw new AudioDeviceUnavailableException(error, hr);
        }

        return output;
    }

    /// <summary>Comprueba, sin abrir un flujo, si hay un dispositivo de salida usable; para decidir si correr pruebas con hardware.</summary>
    internal static bool ProbeDefaultDevice(out string description)
    {
        string result = string.Empty;
        bool ok = false;
        var probe = new Thread(() =>
        {
            int co = NativeMethods.CoInitializeEx(0, NativeMethods.CoinitMultithreaded);
            var wrappers = new StrategyBasedComWrappers();
            try
            {
                Com.Check(NativeMethods.CoCreateInstance(
                    AudioConstants.ClsidDeviceEnumerator, 0, AudioConstants.ClsctxAll, AudioConstants.IidDeviceEnumerator, out nint pointer));
                IMMDeviceEnumerator enumerator = Com.Wrap<IMMDeviceEnumerator>(wrappers, pointer);
                try
                {
                    Com.Check(enumerator.GetDefaultAudioEndpoint(AudioConstants.FlowRender, AudioConstants.RoleMultimedia, out nint devicePointer));
                    IMMDevice device = Com.Wrap<IMMDevice>(wrappers, devicePointer);
                    try
                    {
                        Com.Check(device.Activate(AudioConstants.IidAudioClient, AudioConstants.ClsctxAll, 0, out nint clientPointer));
                        Marshal.Release(clientPointer);
                        result = WasapiSession.GetDefaultDeviceId(wrappers, enumerator) ?? string.Empty;
                        ok = true;
                    }
                    finally
                    {
                        Com.Release(device);
                    }
                }
                finally
                {
                    Com.Release(enumerator);
                }
            }
            catch (AudioDeviceUnavailableException ex)
            {
                result = ex.Message;
            }
            finally
            {
                if (co >= 0)
                {
                    NativeMethods.CoUninitialize();
                }
            }
        });
        probe.Start();
        probe.Join();
        description = result;
        return ok;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        bool stopped = true;
        if (_thread.ThreadState != System.Threading.ThreadState.Unstarted)
        {
            lock (_control)
            {
                _shutdown = true;
            }

            NativeMethods.SetEvent(_wakeEvent);
            stopped = _thread.Join(ControlTimeoutMilliseconds * 2);
        }

        _model.Stop(Now());

        // Si el hilo quedó colgado dentro del controlador, sus eventos se dejan abiertos: cerrarlos bajo él sería peor.
        if (stopped)
        {
            NativeMethods.CloseHandle(_wakeEvent);
            NativeMethods.CloseHandle(_audioEvent);
            _ready.Dispose();
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

    internal void OnDefaultDeviceChanged()
    {
        Interlocked.Exchange(ref _defaultChanged, 1);
        NativeMethods.SetEvent(_wakeEvent);
    }

    /// <summary>Para pruebas con hardware: el hilo de audio actúa como si el dispositivo se hubiera desconectado.</summary>
    /// <param name="blockReopen">Si es verdadero, no hay dispositivo al que volver hasta <see cref="AllowReopen"/>.</param>
    internal void SimulateDeviceLoss(bool blockReopen)
    {
        Volatile.Write(ref _blockReopen, blockReopen ? 1 : 0);
        Interlocked.Exchange(ref _simulateLoss, 1);
        NativeMethods.SetEvent(_wakeEvent);
    }

    internal void AllowReopen()
    {
        Volatile.Write(ref _blockReopen, 0);
        OnDevicesChanged();
    }

    internal void OnDevicesChanged()
    {
        Interlocked.Exchange(ref _devicesChanged, 1);
        NativeMethods.SetEvent(_wakeEvent);
    }

    private static long ToFrames(TimeSpan duration, int rate) => (long)Math.Round(duration.TotalSeconds * rate);

    private static long Now() => Stopwatch.GetTimestamp();

    private static nint CreateEvent()
    {
        nint handle = NativeMethods.CreateEvent(0, false, false, 0);
        return handle != 0 ? handle : throw new InvalidOperationException("Windows no pudo crear un evento para el hilo de audio.");
    }

    private void Request(bool? running, Time? flush)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_control)
        {
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
            NativeMethods.SetEvent(_wakeEvent);
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
        int co = NativeMethods.CoInitializeEx(0, NativeMethods.CoinitMultithreaded);
        nint mmcss = EnterProAudio();
        nint notifications = 0;
        try
        {
            try
            {
                Com.Check(NativeMethods.CoCreateInstance(
                    AudioConstants.ClsidDeviceEnumerator, 0, AudioConstants.ClsctxAll, AudioConstants.IidDeviceEnumerator, out nint pointer));
                _enumerator = Com.Wrap<IMMDeviceEnumerator>(_wrappers, pointer);
                OpenSession();
            }
            catch (AudioDeviceUnavailableException ex)
            {
                _initError = ex.Message;
                _initHResult = ex.HResult;
                return;
            }
            finally
            {
                _ready.Set();
            }

            notifications = RegisterNotifications();
            Loop();
        }
        finally
        {
            if (notifications != 0)
            {
                _ = _enumerator!.UnregisterEndpointNotificationCallback(notifications);
                Marshal.Release(notifications);
            }

            CloseSession();
            Com.Release(_enumerator);
            _enumerator = null;
            if (mmcss != 0)
            {
                NativeMethods.AvRevertMmThreadCharacteristics(mmcss);
            }

            if (co >= 0)
            {
                NativeMethods.CoUninitialize();
            }

            lock (_control)
            {
                _threadExited = true;
                Monitor.PulseAll(_control);
            }
        }
    }

    private void Loop()
    {
        nint* handles = stackalloc nint[2];
        handles[0] = _wakeEvent;
        handles[1] = _audioEvent;
        while (true)
        {
            uint timeout = _session is not null ? 100u : _model.IsRunning ? 10u : 250u;
            _ = NativeMethods.WaitForMultipleObjects(2, handles, false, timeout);
            if (ApplyControl())
            {
                return;
            }

            HandleNotifications();
            if (_session is { } session)
            {
                Service(session);
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
        _running = true;
        if (_session is { } session)
        {
            StartSession(session);
        }
        else
        {
            _model.Start(Now());
        }
    }

    private void DoStop()
    {
        _running = false;
        if (_session is { } session)
        {
            if (!ObservePosition(session))
            {
                _model.Stop(Now());
                return;
            }

            _model.Stop(Now());
            int hr = session.Client.Stop();
            if (hr < 0)
            {
                Reopen(hr, lost: true);
            }
        }
        else
        {
            _model.Stop(Now());
        }
    }

    private void DoFlush(Time target)
    {
        int hr = 0;
        if (_session is { } session)
        {
            hr = session.Client.Stop();
            if (hr >= 0)
            {
                hr = session.Client.Reset();
            }
        }

        _renderer.Seek(target);
        if (_session is null)
        {
            _model.RebaseFreeRun(Now());
            return;
        }

        _model.BeginSegment(_renderer.Written, discontinuity: true);
        _primed = false;
        if (hr < 0)
        {
            Reopen(hr, lost: true);
        }
        else if (_running)
        {
            StartSession(_session);
        }
    }

    private void StartSession(WasapiSession session)
    {
        if (!Fill(session, countStarvation: false))
        {
            return;
        }

        int hr = session.Client.Start();
        if (hr < 0)
        {
            Reopen(hr, lost: true);
            return;
        }

        _primed = true;
        _model.Start(Now());
    }

    private void Service(WasapiSession session)
    {
        if (Fill(session, countStarvation: true) && _running)
        {
            ObservePosition(session);
        }
    }

    /// <returns>Falso si el dispositivo falló (y ya se intentó reabrir).</returns>
    private bool Fill(WasapiSession session, bool countStarvation)
    {
        int hr = session.Client.GetCurrentPadding(out uint padding);
        if (hr < 0)
        {
            Reopen(hr, lost: true);
            return false;
        }

        if (!_running)
        {
            return true;
        }

        if (countStarvation && _primed && padding == 0)
        {
            Interlocked.Increment(ref _starvations);
        }

        uint free = session.BufferFrames - Math.Min(padding, session.BufferFrames);
        if (free == 0)
        {
            return true;
        }

        byte* data;
        hr = session.Render.GetBuffer(free, &data);
        if (hr < 0)
        {
            Reopen(hr, lost: true);
            return false;
        }

        int channels = session.Channels;
        int produced = _renderer.Render(new Span<float>(data, (int)free * channels), (int)free, channels);
        hr = session.Render.ReleaseBuffer(free, 0);
        _model.AddWritten(produced);
        if (hr < 0)
        {
            Reopen(hr, lost: true);
            return false;
        }

        return true;
    }

    private bool ObservePosition(WasapiSession session)
    {
        int hr = session.Clock.GetPosition(out ulong position, out ulong qpc100ns);
        if (hr < 0)
        {
            Reopen(hr, lost: true);
            return false;
        }

        long frames = (long)((UInt128)position * (ulong)_options.SampleRate / session.ClockFrequency);
        // La marca viene en unidades de 100 ns del contador QPC; Stopwatch usa ese mismo contador.
        long timestamp = (long)((UInt128)qpc100ns * (ulong)Stopwatch.Frequency / TimeSpan.TicksPerSecond);
        _model.Observe(frames, timestamp);
        return true;
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

        bool changed = Interlocked.Exchange(ref _devicesChanged, 0) != 0 | Interlocked.Exchange(ref _defaultChanged, 0) != 0;
        if (changed || now >= _nextRetry)
        {
            _nextRetry = now + Stopwatch.Frequency;
            TryRecover();
        }
    }

    private void HandleNotifications()
    {
        if (Interlocked.Exchange(ref _simulateLoss, 0) != 0 && _session is not null)
        {
            Reopen(AudioHResults.DeviceInvalidated, lost: true);
            return;
        }

        // Sin dispositivo, FreeRun consume estas marcas para reintentar enseguida.
        if (_session is not { } session)
        {
            return;
        }

        Interlocked.Exchange(ref _devicesChanged, 0);
        if (Interlocked.Exchange(ref _defaultChanged, 0) == 0)
        {
            return;
        }

        string? id = WasapiSession.GetDefaultDeviceId(_wrappers, _enumerator!);
        if (id is not null && !string.Equals(id, session.Info.Id, StringComparison.OrdinalIgnoreCase))
        {
            if (_running)
            {
                ObservePosition(session);
            }

            if (ReferenceEquals(_session, session))
            {
                Reopen(0, lost: false);
            }
        }
    }

    /// <summary>Cierra el dispositivo actual y abre el predeterminado.</summary>
    /// <remarks>
    /// Mientras se abre el nuevo (unos 200 ms en este equipo) el reloj sigue con el reloj de pared, para que el
    /// video no se congele; el audio retoma en la posición del reloj y repite lo que quedó en cola sin oírse.
    /// </remarks>
    private void Reopen(int hr, bool lost)
    {
        string previous = _session?.Info.Name ?? "el dispositivo de audio";
        CloseSession();
        Interlocked.Increment(ref _restarts);
        EnterFreeRun(Now());

        string? error = "se intentó reabrir demasiadas veces seguidas";
        if (_reopenDepth < MaxReopenDepth && TryOpenSession(out error))
        {
            _reopenDepth++;
            try
            {
                Resume(_session!);
            }
            finally
            {
                _reopenDepth--;
            }

            // Si el dispositivo nuevo también falló, la llamada anidada ya avisó y quedó corriendo libre.
            if (_session?.Info is not { } info)
            {
                return;
            }

            RaiseDeviceChanged(info, lost
                ? $"Se perdió {previous} ({Com.Describe(hr)}). El audio sigue por {info.Name}."
                : $"El audio ahora suena por {info.Name}.");
            return;
        }

        RaiseDeviceChanged(null, $"Se perdió {previous} ({(lost ? Com.Describe(hr) : error)}). " +
            "La reproducción sigue sin sonido y FotoToma volverá a intentarlo solo.");
    }

    private void TryRecover()
    {
        if (!TryOpenSession(out _))
        {
            return;
        }

        Resume(_session!);
        if (_session?.Info is { } info)
        {
            RaiseDeviceChanged(info, $"El audio volvió: suena por {info.Name}.");
        }
    }

    /// <summary>Pasa de correr libre al dispositivo recién abierto, empezando en el cuadro que marca el reloj.</summary>
    private void Resume(WasapiSession session)
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
            StartSession(session);
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

    private void OpenSession()
    {
        _session = WasapiSession.Open(_wrappers, _enumerator!, _options, _audioEvent);
        int rate = _options.SampleRate;
        // Sin lecturas nuevas se interpola como mucho un buffer y un periodo más un margen: más allá, el dispositivo está atascado.
        long maxExtrapolation = _session.BufferFrames + _session.PeriodFrames + (rate / 200);
        _model.Configure(maxExtrapolation, ToFrames(_options.ExtraLatency, rate));
        _primed = false;
        Volatile.Write(ref _device, _session.Info);
    }

    private bool TryOpenSession(out string? error)
    {
        if (Volatile.Read(ref _blockReopen) != 0)
        {
            error = Com.Describe(AudioHResults.ENotFound);
            return false;
        }

        try
        {
            OpenSession();
            error = null;
            return true;
        }
        catch (AudioDeviceUnavailableException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void CloseSession()
    {
        _session?.Dispose();
        _session = null;
        _primed = false;
    }

    private nint RegisterNotifications()
    {
        var sink = new DeviceNotificationSink(this);
        nint unknown = _wrappers.GetOrCreateComInterfaceForObject(sink, CreateComInterfaceFlags.None);
        try
        {
            if (Marshal.QueryInterface(unknown, AudioConstants.IidNotificationClient, out nint client) < 0)
            {
                return 0;
            }

            if (_enumerator!.RegisterEndpointNotificationCallback(client) < 0)
            {
                Marshal.Release(client);
                return 0;
            }

            return client;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    private nint EnterProAudio()
    {
        try
        {
            uint taskIndex = 0;
            nint handle = NativeMethods.AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
            Volatile.Write(ref _proAudio, handle != 0);
            return handle;
        }
        catch (DllNotFoundException)
        {
            return 0;
        }
        catch (EntryPointNotFoundException)
        {
            return 0;
        }
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

[GeneratedComClass]
internal sealed partial class DeviceNotificationSink(WasapiOutput owner) : IMMNotificationClient
{
    public int OnDeviceStateChanged(nint deviceId, uint newState)
    {
        owner.OnDevicesChanged();
        return AudioHResults.SOk;
    }

    public int OnDeviceAdded(nint deviceId)
    {
        owner.OnDevicesChanged();
        return AudioHResults.SOk;
    }

    public int OnDeviceRemoved(nint deviceId)
    {
        owner.OnDevicesChanged();
        return AudioHResults.SOk;
    }

    public int OnDefaultDeviceChanged(int flow, int role, nint defaultDeviceId)
    {
        if (flow == AudioConstants.FlowRender && role == AudioConstants.RoleMultimedia)
        {
            owner.OnDefaultDeviceChanged();
        }

        return AudioHResults.SOk;
    }

    public int OnPropertyValueChanged(nint deviceId, PropertyKey key) => AudioHResults.SOk;
}
