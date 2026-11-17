using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Rodaje.Audio.Interop;

namespace Rodaje.Audio.Capture;

/// <summary>
/// Captura por CoreAudio (la unidad HAL con la entrada activa), en mono float a la frecuencia pedida. Cada paquete lleva el
/// instante de su primera muestra en la base de <see cref="Stopwatch"/> (100 ns), ya descontada la latencia de entrada.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class CoreAudioCapture : IAudioCapture
{
    private const int MaxFrames = 16_384;

    private readonly float[] _buffer = new float[MaxFrames];
    private readonly long _inputLatencyTicks;
    private GCHandle _self;
    private nint _unit;
    private volatile CapturePacketHandler? _handler;
    private bool _started;
    private bool _lost;
    private bool _disposed;

    public CoreAudioCapture(string? deviceId, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8_000);
        SampleRate = sampleRate;
        uint device = deviceId is null ? CoreAudio.DefaultDevice(input: true) : Find(deviceId);
        if (device == 0)
        {
            throw new AudioDeviceUnavailableException(deviceId is null ? "No hay ningún micrófono." : $"No se encontró «{deviceId}».");
        }

        _self = GCHandle.Alloc(this);
        _unit = CoreAudio.NewUnit(CoreAudio.SubTypeHalOutput);
        int status = _unit == 0 ? -1 : Configure(device, sampleRate);
        if (status != 0)
        {
            Dispose();
            throw new AudioDeviceUnavailableException($"CoreAudio no abrió la entrada ({CoreAudio.Describe(status)}).");
        }

        string name = CoreAudio.DeviceText(device, CoreAudio.SelectorName);
        DeviceName = name.Length > 0 ? name : DeviceName;
        uint latency = CoreAudio.DeviceUInt(device, CoreAudio.SelectorLatency, CoreAudio.ScopeObjectInput)
            + CoreAudio.DeviceUInt(device, CoreAudio.SelectorSafetyOffset, CoreAudio.ScopeObjectInput);
        _inputLatencyTicks = (long)((double)latency / sampleRate * Stopwatch.Frequency);
    }

    public int SampleRate { get; }

    public string DeviceName { get; } = "Micrófono";

    /// <summary>Los dispositivos con entrada, el predeterminado primero; el id es el UID de CoreAudio.</summary>
    public static IReadOnlyList<AudioCaptureDevice> ListDevices()
    {
        uint defaultInput = CoreAudio.DefaultDevice(input: true);
        return [.. CoreAudio.Devices()
            .Where(d => CoreAudio.HasStreams(d, input: true))
            .Select(d => new AudioCaptureDevice(CoreAudio.DeviceText(d, CoreAudio.SelectorDeviceUid), CoreAudio.DeviceText(d, CoreAudio.SelectorName), d == defaultInput))
            .Where(d => d.Id.Length > 0)
            .OrderByDescending(d => d.IsDefault)];
    }

    public void StartCapture(CapturePacketHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _handler = handler;
        if (!_started)
        {
            int status = CoreAudio.AudioOutputUnitStart(_unit);
            if (status != 0)
            {
                throw new AudioDeviceUnavailableException($"CoreAudio no empezó a capturar ({CoreAudio.Describe(status)}).");
            }

            _started = true;
        }
    }

    public void StopCapture()
    {
        if (_started && _unit != 0)
        {
            _ = CoreAudio.AudioOutputUnitStop(_unit);
            _started = false;
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
        CoreAudio.DisposeUnit(ref _unit);
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    private static uint Find(string uid) =>
        CoreAudio.Devices().FirstOrDefault(d => CoreAudio.HasStreams(d, input: true) && CoreAudio.DeviceText(d, CoreAudio.SelectorDeviceUid) == uid);

    [UnmanagedCallersOnly]
    private static int OnInput(void* refCon, uint* flags, AudioTimeStampHead* time, uint bus, uint frames, AudioBufferListOne* data)
    {
        return GCHandle.FromIntPtr((nint)refCon).Target is CoreAudioCapture self ? self.Pull(flags, time, bus, frames) : 0;
    }

    /// <summary>Activa la entrada (bus 1), apaga la salida (bus 0), elige el dispositivo y pide mono float.</summary>
    private int Configure(uint device, int sampleRate)
    {
        uint on = 1;
        uint off = 0;
        int status = CoreAudio.AudioUnitSetProperty(_unit, CoreAudio.PropertyEnableIo, CoreAudio.ScopeInput, 1, &on, sizeof(uint));
        status = status != 0 ? status : CoreAudio.AudioUnitSetProperty(_unit, CoreAudio.PropertyEnableIo, CoreAudio.ScopeOutput, 0, &off, sizeof(uint));
        status = status != 0 ? status : CoreAudio.AudioUnitSetProperty(_unit, CoreAudio.PropertyCurrentDevice, CoreAudio.ScopeGlobal, 0, &device, sizeof(uint));
        AudioStreamBasicDescription format = CoreAudio.FloatFormat(sampleRate, 1);
        status = status != 0 ? status
            : CoreAudio.AudioUnitSetProperty(_unit, CoreAudio.PropertyStreamFormat, CoreAudio.ScopeOutput, 1, &format, (uint)sizeof(AudioStreamBasicDescription));
        var callback = new AURenderCallbackStruct
        {
            InputProc = (void*)(delegate* unmanaged<void*, uint*, AudioTimeStampHead*, uint, uint, AudioBufferListOne*, int>)&OnInput,
            InputProcRefCon = (void*)GCHandle.ToIntPtr(_self),
        };
        status = status != 0 ? status
            : CoreAudio.AudioUnitSetProperty(_unit, CoreAudio.PropertySetInputCallback, CoreAudio.ScopeGlobal, 0, &callback, (uint)sizeof(AURenderCallbackStruct));
        return status != 0 ? status : CoreAudio.AudioUnitInitialize(_unit);
    }

    private int Pull(uint* flags, AudioTimeStampHead* time, uint bus, uint frames)
    {
        int count = (int)Math.Min(frames, (uint)MaxFrames);
        fixed (float* samples = _buffer)
        {
            var list = new AudioBufferListOne { NumberBuffers = 1, NumberChannels = 1, DataByteSize = (uint)(count * sizeof(float)), Data = samples };
            int status = CoreAudio.AudioUnitRender(_unit, flags, time, bus, (uint)count, &list);
            if (status != 0)
            {
                // Ese bloque se perdió: el siguiente lo dice, para que quien graba rellene el hueco.
                _lost = true;
                return status;
            }

            if (_handler is not { } handler)
            {
                return 0;
            }

            // HostTime marca cuándo el dispositivo entregó la primera muestra; se capturó una latencia de entrada antes.
            long captured = CoreAudio.HostToStopwatch(time->HostTime) - _inputLatencyTicks;
            long ticks100ns = (long)((Int128)captured * TimeSpan.TicksPerSecond / Stopwatch.Frequency);
            bool discontinuity = _lost;
            _lost = false;
            try
            {
                handler(new ReadOnlySpan<float>(samples, (int)(list.DataByteSize / sizeof(float))), ticks100ns, discontinuity);
            }
#pragma warning disable CA1031 // Una excepción no puede salir al hilo de CoreAudio: se cuenta el bloque como perdido.
            catch (Exception)
#pragma warning restore CA1031
            {
                _lost = true;
            }

            return 0;
        }
    }
}
