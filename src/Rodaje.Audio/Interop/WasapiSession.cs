using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Rodaje.Audio.Interop;

/// <summary>Un dispositivo abierto e inicializado en modo compartido por eventos. Solo lo toca el hilo de audio.</summary>
internal sealed unsafe class WasapiSession : IDisposable
{
    private IMMDevice? _device;
    private IAudioClient? _client;
    private IAudioRenderClient? _render;
    private IAudioClockNative? _clock;

    private WasapiSession()
    {
    }

    public IAudioClient Client => _client!;

    public IAudioRenderClient Render => _render!;

    public IAudioClockNative Clock => _clock!;

    public int Channels { get; private set; }

    public uint BufferFrames { get; private set; }

    public uint PeriodFrames { get; private set; }

    public ulong ClockFrequency { get; private set; }

    public AudioDeviceInfo Info { get; private set; } = null!;

    /// <summary>Abre el dispositivo de salida predeterminado. Lanza <see cref="AudioDeviceUnavailableException"/> con la causa en español.</summary>
    public static WasapiSession Open(ComWrappers wrappers, IMMDeviceEnumerator enumerator, AudioOutputOptions options, nint eventHandle)
    {
        var session = new WasapiSession();
        try
        {
            session.Initialize(wrappers, enumerator, options, eventHandle);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public static string? GetDefaultDeviceId(ComWrappers wrappers, IMMDeviceEnumerator enumerator)
    {
        if (enumerator.GetDefaultAudioEndpoint(AudioConstants.FlowRender, AudioConstants.RoleMultimedia, out nint pointer) < 0)
        {
            return null;
        }

        IMMDevice device = Com.Wrap<IMMDevice>(wrappers, pointer);
        try
        {
            return ReadId(device);
        }
        finally
        {
            Com.Release(device);
        }
    }

    public void Dispose()
    {
        if (_client is not null)
        {
            _ = _client.Stop();
        }

        Com.Release(_clock);
        Com.Release(_render);
        Com.Release(_client);
        Com.Release(_device);
        _clock = null;
        _render = null;
        _client = null;
        _device = null;
    }

    private void Initialize(ComWrappers wrappers, IMMDeviceEnumerator enumerator, AudioOutputOptions options, nint eventHandle)
    {
        int rate = options.SampleRate;
        Com.Check(enumerator.GetDefaultAudioEndpoint(AudioConstants.FlowRender, AudioConstants.RoleMultimedia, out nint devicePointer));
        _device = Com.Wrap<IMMDevice>(wrappers, devicePointer);
        string id = ReadId(_device) ?? string.Empty;
        string name = ReadFriendlyName(wrappers, _device) ?? "Dispositivo de audio";

        _client = Activate(wrappers, _device);
        WaveFormatExtensible* mix = null;
        Com.Check(_client.GetMixFormat(&mix));
        int channels;
        int mixRate;
        uint mask;
        try
        {
            channels = mix->Channels;
            mixRate = (int)mix->SamplesPerSec;
            mask = mix->FormatTag == AudioConstants.WaveFormatExtensibleTag ? mix->ChannelMask : DefaultMask(channels);
        }
        finally
        {
            Marshal.FreeCoTaskMem((nint)mix);
        }

        if (channels <= 0)
        {
            throw new AudioDeviceUnavailableException("El dispositivo de audio informó un formato sin canales.");
        }

        WaveFormatExtensible format = WaveFormatExtensible.Float32(rate, channels, mask);
        bool lowLatency = false;
        uint period = 0;
        // El modo de baja latencia exige que el motor no tenga que remuestrear.
        if (options.PreferLowLatency && mixRate == rate && _client is IAudioClient3 client3 &&
            client3.GetSharedModeEnginePeriod(&format, out uint defaultPeriod, out uint fundamental, out uint minPeriod, out uint maxPeriod) >= 0 &&
            fundamental > 0 && minPeriod < defaultPeriod)
        {
            uint wanted = (uint)Math.Ceiling(options.LowLatencyPeriod.TotalSeconds * rate / fundamental) * fundamental;
            uint chosen = Math.Clamp(wanted, minPeriod, maxPeriod);
            if (client3.InitializeSharedAudioStream(AudioConstants.StreamFlagsEventCallback, chosen, &format, null) >= 0)
            {
                lowLatency = true;
                period = chosen;
            }
            else
            {
                // Un IAudioClient que falló al inicializarse no admite otro intento.
                Com.Release(_client);
                _client = Activate(wrappers, _device);
            }
        }

        if (!lowLatency)
        {
            uint flags = AudioConstants.StreamFlagsEventCallback;
            if (mixRate != rate)
            {
                flags |= AudioConstants.StreamFlagsAutoConvertPcm | AudioConstants.StreamFlagsSrcDefaultQuality;
            }

            int hr = _client.Initialize(AudioConstants.ShareModeShared, flags, options.BufferDuration.Ticks, 0, &format, null);
            if (hr == AudioHResults.UnsupportedFormat && (flags & AudioConstants.StreamFlagsAutoConvertPcm) == 0)
            {
                Com.Release(_client);
                _client = Activate(wrappers, _device);
                flags |= AudioConstants.StreamFlagsAutoConvertPcm | AudioConstants.StreamFlagsSrcDefaultQuality;
                hr = _client.Initialize(AudioConstants.ShareModeShared, flags, options.BufferDuration.Ticks, 0, &format, null);
            }

            Com.Check(hr);
            Com.Check(_client.GetDevicePeriod(out long defaultTicks, out _));
            period = (uint)(defaultTicks * rate / TimeSpan.TicksPerSecond);
        }

        Com.Check(_client.GetBufferSize(out uint bufferFrames));
        _ = _client.GetStreamLatency(out long streamLatency);
        Com.Check(_client.SetEventHandle(eventHandle));
        Com.Check(_client.GetService(AudioConstants.IidRenderClient, out nint render));
        _render = Com.Wrap<IAudioRenderClient>(wrappers, render);
        Com.Check(_client.GetService(AudioConstants.IidAudioClock, out nint clock));
        _clock = Com.Wrap<IAudioClockNative>(wrappers, clock);
        Com.Check(_clock.GetFrequency(out ulong frequency));
        if (frequency == 0)
        {
            throw new AudioDeviceUnavailableException("El reloj del dispositivo de audio informó una frecuencia de 0.");
        }

        Channels = channels;
        BufferFrames = bufferFrames;
        PeriodFrames = Math.Max(1, Math.Min(period, bufferFrames));
        ClockFrequency = frequency;
        Info = new AudioDeviceInfo(
            id,
            name,
            channels,
            mixRate,
            TimeSpan.FromSeconds(bufferFrames / (double)rate),
            TimeSpan.FromSeconds(PeriodFrames / (double)rate),
            lowLatency,
            TimeSpan.FromTicks(Math.Max(0, streamLatency)));
    }

    private static IAudioClient Activate(ComWrappers wrappers, IMMDevice device)
    {
        Com.Check(device.Activate(AudioConstants.IidAudioClient, AudioConstants.ClsctxAll, 0, out nint pointer));
        return Com.Wrap<IAudioClient>(wrappers, pointer);
    }

    private static uint DefaultMask(int channels) => channels switch
    {
        1 => 0x4,
        2 => 0x3,
        _ => 0,
    };

    private static string? ReadId(IMMDevice device)
    {
        if (device.GetId(out nint pointer) < 0 || pointer == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    private static string? ReadFriendlyName(ComWrappers wrappers, IMMDevice device)
    {
        if (device.OpenPropertyStore(0, out nint pointer) < 0)
        {
            return null;
        }

        IPropertyStore store = Com.Wrap<IPropertyStore>(wrappers, pointer);
        try
        {
            if (store.GetValue(AudioConstants.DeviceFriendlyName, out PropVariant value) < 0)
            {
                return null;
            }

            try
            {
                return value.ValueType == AudioConstants.VtLpwstr ? Marshal.PtrToStringUni(value.Pointer) : null;
            }
            finally
            {
                _ = NativeMethods.PropVariantClear(ref value);
            }
        }
        finally
        {
            Com.Release(store);
        }
    }
}

internal static class Com
{
    /// <summary>Envuelve un puntero COM en un objeto propio (sin caché compartida) y suelta la referencia del llamador.</summary>
    public static T Wrap<T>(ComWrappers wrappers, nint pointer)
        where T : class
    {
        try
        {
            return (T)wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    /// <summary>Suelta la referencia nativa ya, sin esperar al recolector: el dispositivo se libera al cambiar de salida.</summary>
    public static void Release(object? instance)
    {
        if (instance is ComObject com)
        {
            com.FinalRelease();
        }
    }

    public static void Check(int hr)
    {
        if (hr < 0)
        {
            throw new AudioDeviceUnavailableException(Describe(hr), hr);
        }
    }

    public static string Describe(int hr) => hr switch
    {
        AudioHResults.ENotFound => "Windows no tiene ningún dispositivo de salida de audio activo (altavoces, auriculares o HDMI).",
        AudioHResults.ServiceNotRunning => "El servicio de audio de Windows (Audiosrv) está detenido o desactivado; en AtlasOS puede venir apagado.",
        AudioHResults.DeviceInUse => "Otro programa está usando el dispositivo de audio en modo exclusivo.",
        AudioHResults.DeviceInvalidated or AudioHResults.ResourcesInvalidated => "El dispositivo de audio se desconectó.",
        AudioHResults.EndpointCreateFailed => "Windows no pudo preparar el dispositivo de audio.",
        AudioHResults.UnsupportedFormat => "El dispositivo de audio no acepta audio en coma flotante a la frecuencia pedida.",
        AudioHResults.CpuUsageExceeded => "Windows detuvo el audio porque el procesador estaba saturado.",
        unchecked((int)0x80040154) => "Falta el componente de audio de Windows (MMDevice API).",
        _ => string.Create(CultureInfo.InvariantCulture, $"Windows rechazó abrir el dispositivo de audio (código 0x{hr:X8})."),
    };
}
