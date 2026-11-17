using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Rodaje.Audio.Interop;

namespace Rodaje.Audio.Capture;

/// <summary>
/// Captura por la API asíncrona de PulseAudio (también sobre PipeWire), en mono float a la frecuencia pedida. Cada
/// paquete lleva el instante de su primera muestra en la base de <see cref="System.Diagnostics.Stopwatch"/> (100 ns),
/// la misma del reloj de la salida, como el QPC de WASAPI.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed unsafe class PulseCapture : IAudioCapture
{
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Fragment = TimeSpan.FromMilliseconds(10);

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _opened = new();
    private readonly object _gate = new();
    private Exception? _openError;
    private volatile CapturePacketHandler? _handler;
    private volatile bool _stopping;
    private nint _mainloop;
    private bool _disposed;

    public PulseCapture(string? deviceId, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8_000);
        SampleRate = sampleRate;
        _thread = new Thread(() => Run(deviceId)) { IsBackground = true, Name = "Rodaje captura", Priority = ThreadPriority.Highest };
        _thread.Start();
        _opened.Wait();
        if (_openError is { } error)
        {
            Dispose();
            throw error;
        }
    }

    public int SampleRate { get; }

    public string DeviceName { get; private set; } = "Micrófono";

    /// <summary>Latencia de la fuente que informó el servidor con el último paquete.</summary>
    internal TimeSpan LastReportedLatency { get; private set; }

    /// <summary>Las fuentes del servidor sin los monitores de los sumideros, la predeterminada primero; vacío si no hay servidor.</summary>
    public static IReadOnlyList<AudioCaptureDevice> ListDevices()
    {
        nint mainloop = 0;
        nint context = 0;
        var found = new Found();
        GCHandle handle = GCHandle.Alloc(found);
        try
        {
            mainloop = Pulse.pa_mainloop_new();
            if (mainloop == 0 || (context = Pulse.Connect(mainloop, "Rodaje (fuentes)", OpenTimeout, out _)) == 0)
            {
                return [];
            }

            nint userdata = GCHandle.ToIntPtr(handle);
            _ = Pulse.Await(mainloop, Pulse.pa_context_get_server_info(context, &OnServer, userdata), OpenTimeout);
            _ = Pulse.Await(mainloop, Pulse.pa_context_get_source_info_list(context, &OnSource, userdata), OpenTimeout);
            return [.. found.Sources
                .Where(s => !s.Name.EndsWith(".monitor", StringComparison.Ordinal))
                .Select(s => new AudioCaptureDevice(s.Name, s.Description, s.Name == found.DefaultSource))
                .OrderByDescending(d => d.IsDefault)];
        }
        catch (DllNotFoundException)
        {
            return [];
        }
        finally
        {
            handle.Free();
            Pulse.Release(ref context);
            if (mainloop != 0)
            {
                Pulse.pa_mainloop_free(mainloop);
            }
        }
    }

    public void StartCapture(CapturePacketHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _handler = handler;
        Wake();
    }

    public void StopCapture()
    {
        _stopping = true;
        Wake();
        if (_thread.IsAlive && _thread != Thread.CurrentThread)
        {
            _thread.Join(TimeSpan.FromSeconds(4));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopCapture();
        _opened.Dispose();
    }

    // pa_server_info: cuatro punteros, pa_sample_spec (12 bytes) y luego default_sink_name y default_source_name.
    [UnmanagedCallersOnly]
    private static void OnServer(nint context, nint info, nint userdata)
    {
        if (info != 0 && GCHandle.FromIntPtr(userdata).Target is Found found)
        {
            found.DefaultSource = Pulse.Text(*(nint*)(info + 56));
        }
    }

    // pa_source_info empieza por name, index (con relleno) y description.
    [UnmanagedCallersOnly]
    private static void OnSource(nint context, nint info, int eol, nint userdata)
    {
        if (eol == 0 && info != 0 && GCHandle.FromIntPtr(userdata).Target is Found found)
        {
            string name = Pulse.Text(*(nint*)info);
            string description = Pulse.Text(*(nint*)(info + 16));
            found.Sources.Add((name, description.Length > 0 ? description : name));
        }
    }

    private void Wake()
    {
        lock (_gate)
        {
            if (_mainloop != 0)
            {
                Pulse.pa_mainloop_wakeup(_mainloop);
            }
        }
    }

    private void Run(string? deviceId)
    {
        nint mainloop = 0;
        nint context = 0;
        nint stream = 0;
        try
        {
            try
            {
                mainloop = Pulse.pa_mainloop_new();
                if (mainloop == 0)
                {
                    _openError = new AudioDeviceUnavailableException("libpulse no pudo crear su bucle de eventos.");
                    return;
                }

                context = Pulse.Connect(mainloop, "Rodaje", OpenTimeout, out string? error);
                if (context == 0)
                {
                    _openError = new AudioDeviceUnavailableException(error!);
                    return;
                }

                var spec = new PulseSampleSpec { Format = Pulse.SampleFloat32Le, Rate = (uint)SampleRate, Channels = 1 };
                stream = Pulse.pa_stream_new(context, "Rodaje captura", &spec, 0);
                var attr = new PulseBufferAttr
                {
                    MaxLength = Pulse.Default,
                    TLength = Pulse.Default,
                    PreBuf = Pulse.Default,
                    MinReq = Pulse.Default,
                    FragSize = (uint)(SampleRate * Fragment.TotalSeconds * sizeof(float)),
                };
                int flags = Pulse.StreamStartCorked | Pulse.StreamInterpolateTiming | Pulse.StreamAutoTimingUpdate | Pulse.StreamAdjustLatency;
                if (stream == 0 || Pulse.pa_stream_connect_record(stream, deviceId, &attr, flags) < 0 || !Pulse.WaitReady(mainloop, stream, OpenTimeout))
                {
                    _openError = new AudioDeviceUnavailableException(
                        $"No se pudo abrir {(deviceId is null ? "el micrófono" : $"«{deviceId}»")}: {Pulse.Error(context)}.");
                    return;
                }

                string name = Pulse.Text(Pulse.pa_stream_get_device_name(stream));
                DeviceName = name.Length > 0 ? name : DeviceName;
                lock (_gate)
                {
                    _mainloop = mainloop;
                }
            }
            catch (DllNotFoundException)
            {
                _openError = new AudioDeviceUnavailableException("No está libpulse (el cliente de PulseAudio o PipeWire): instala el paquete libpulse0.");
                return;
            }
            finally
            {
                _opened.Set();
            }

            Loop(mainloop, stream);
        }
        finally
        {
            lock (_gate)
            {
                _mainloop = 0;
            }

            Pulse.Close(ref stream);
            Pulse.Release(ref context);
            if (mainloop != 0)
            {
                Pulse.pa_mainloop_free(mainloop);
            }
        }
    }

    private void Loop(nint mainloop, nint stream)
    {
        bool corked = true;
        bool discontinuity = false;
        while (!_stopping)
        {
            Pulse.Iterate(mainloop, corked ? 100 : 10);
            if (_stopping)
            {
                return;
            }

            if (Pulse.pa_stream_get_state(stream) != Pulse.StreamReady)
            {
                return;
            }

            CapturePacketHandler? handler = _handler;
            if (handler is null)
            {
                continue;
            }

            if (corked)
            {
                corked = !Pulse.Await(mainloop, Pulse.pa_stream_cork(stream, 0, 0, 0), OpenTimeout);
                continue;
            }

            while (Pulse.pa_stream_readable_size(stream) is var readable && readable > 0 && readable != nuint.MaxValue)
            {
                void* data;
                nuint bytes;
                if (Pulse.pa_stream_peek(stream, &data, &bytes) < 0 || bytes == 0)
                {
                    break;
                }

                if (data == null)
                {
                    // Un hueco en el búfer del servidor: se perdieron muestras.
                    discontinuity = true;
                    _ = Pulse.pa_stream_drop(stream);
                    continue;
                }

                // Lo capturado más antiguo que queda sin leer (la primera muestra de este paquete) se tomó hace «latencia».
                ulong latency;
                int negative;
                long now = Pulse.Now100ns();
                long age = Pulse.pa_stream_get_latency(stream, &latency, &negative) >= 0 && negative == 0 ? (long)latency * 10 : 0;
                LastReportedLatency = TimeSpan.FromTicks(age);
                handler(new ReadOnlySpan<float>(data, (int)(bytes / sizeof(float))), now - age, discontinuity);
                discontinuity = false;
                _ = Pulse.pa_stream_drop(stream);
            }
        }
    }

    private sealed class Found
    {
        public List<(string Name, string Description)> Sources { get; } = [];

        public string? DefaultSource { get; set; }
    }
}
