using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Rodaje.Audio.Interop;

namespace Rodaje.Audio.Capture;

/// <summary>Un micrófono (o entrada de línea) activo.</summary>
public sealed record AudioCaptureDevice(string Id, string Name, bool IsDefault);

/// <summary>
/// Recibe un paquete capturado, ya en mono float a <see cref="IAudioCapture.SampleRate"/>, con el instante QPC (en
/// unidades de 100 ns) en que el dispositivo capturó su primera muestra. Llega desde el hilo de captura.
/// </summary>
/// <param name="mono">Las muestras, mono.</param>
/// <param name="qpc100ns">Instante de captura de la primera muestra, en unidades de 100 ns del QPC.</param>
/// <param name="discontinuity">Windows perdió muestras antes de este paquete (el sistema estuvo ocupado).</param>
public delegate void CapturePacketHandler(ReadOnlySpan<float> mono, long qpc100ns, bool discontinuity);

/// <summary>Una captura de audio abierta; <see cref="StartCapture"/> empieza a entregar paquetes.</summary>
public interface IAudioCapture : IDisposable
{
    int SampleRate { get; }

    string DeviceName { get; }

    void StartCapture(CapturePacketHandler handler);

    void StopCapture();
}

/// <summary>Captura WASAPI en modo compartido por eventos, convertida por Windows a float a la frecuencia pedida.</summary>
public static class AudioCapture
{
    /// <summary>Los dispositivos de entrada activos, el predeterminado primero. Vacío si no hay o el servicio de audio no responde.</summary>
    public static IReadOnlyList<AudioCaptureDevice> ListDevices()
    {
        var devices = new List<AudioCaptureDevice>();
        RunMta(wrappers =>
        {
            IMMDeviceEnumerator enumerator = CreateEnumerator(wrappers);
            try
            {
                string? defaultId = null;
                if (enumerator.GetDefaultAudioEndpoint(AudioConstants.FlowCapture, AudioConstants.RoleConsole, out nint defaultPointer) >= 0)
                {
                    IMMDevice device = Com.Wrap<IMMDevice>(wrappers, defaultPointer);
                    defaultId = WasapiSession.ReadId(device);
                    Com.Release(device);
                }

                Com.Check(enumerator.EnumAudioEndpoints(AudioConstants.FlowCapture, AudioConstants.DeviceStateActive, out nint collectionPointer));
                IMMDeviceCollection collection = Com.Wrap<IMMDeviceCollection>(wrappers, collectionPointer);
                try
                {
                    Com.Check(collection.GetCount(out uint count));
                    for (uint i = 0; i < count; i++)
                    {
                        if (collection.Item(i, out nint pointer) < 0)
                        {
                            continue;
                        }

                        IMMDevice device = Com.Wrap<IMMDevice>(wrappers, pointer);
                        try
                        {
                            if (WasapiSession.ReadId(device) is { } id)
                            {
                                devices.Add(new AudioCaptureDevice(id, WasapiSession.ReadFriendlyName(wrappers, device) ?? "Micrófono", id == defaultId));
                            }
                        }
                        finally
                        {
                            Com.Release(device);
                        }
                    }
                }
                finally
                {
                    Com.Release(collection);
                }
            }
            finally
            {
                Com.Release(enumerator);
            }
        });
        return [.. devices.OrderByDescending(d => d.IsDefault)];
    }

    /// <summary>Abre el dispositivo (el predeterminado si <paramref name="deviceId"/> es nulo) sin empezar a capturar.</summary>
    /// <exception cref="AudioDeviceUnavailableException">No hay micrófono, está en uso exclusivo o Windows lo rechazó.</exception>
    public static IAudioCapture Open(string? deviceId, int sampleRate = 48_000) => new WasapiCapture(deviceId, sampleRate);

    internal static IMMDeviceEnumerator CreateEnumerator(ComWrappers wrappers)
    {
        Com.Check(NativeMethods.CoCreateInstance(
            AudioConstants.ClsidDeviceEnumerator, 0, AudioConstants.ClsctxAll, AudioConstants.IidDeviceEnumerator, out nint pointer));
        return Com.Wrap<IMMDeviceEnumerator>(wrappers, pointer);
    }

    /// <summary>Corre <paramref name="action"/> en un hilo MTA propio y espera; las excepciones de audio se tragan (lista vacía).</summary>
    private static void RunMta(Action<ComWrappers> action)
    {
        var thread = new Thread(() =>
        {
            int co = NativeMethods.CoInitializeEx(0, NativeMethods.CoinitMultithreaded);
            try
            {
                action(new StrategyBasedComWrappers());
            }
            catch (AudioDeviceUnavailableException)
            {
            }
            catch (COMException)
            {
            }
            finally
            {
                if (co >= 0)
                {
                    NativeMethods.CoUninitialize();
                }
            }
        })
        { IsBackground = true, Name = "Rodaje lista de micrófonos" };
        thread.Start();
        thread.Join();
    }
}

/// <summary>
/// Un hilo propio (MTA, MMCSS «Pro Audio») abre el cliente, espera el evento del dispositivo y entrega cada paquete
/// convertido a mono. El búfer de 200 ms da margen para que escribir el archivo no pierda muestras.
/// </summary>
internal sealed unsafe class WasapiCapture : IAudioCapture
{
    private static readonly TimeSpan Buffer = TimeSpan.FromMilliseconds(200);

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _opened = new();
    private readonly nint _event;
    private readonly nint _stopEvent;
    private Exception? _openError;
    private volatile CapturePacketHandler? _handler;
    private volatile bool _stopping;
    private int _channels;
    private bool _disposed;

    public WasapiCapture(string? deviceId, int sampleRate)
    {
        SampleRate = sampleRate;
        _event = NativeMethods.CreateEvent(0, false, false, 0);
        _stopEvent = NativeMethods.CreateEvent(0, true, false, 0);
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

    public void StartCapture(CapturePacketHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _handler = handler;
        NativeMethods.SetEvent(_event);
    }

    public void StopCapture()
    {
        _stopping = true;
        NativeMethods.SetEvent(_stopEvent);
        _thread.Join(TimeSpan.FromSeconds(4));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopCapture();
        NativeMethods.CloseHandle(_event);
        NativeMethods.CloseHandle(_stopEvent);
        _opened.Dispose();
    }

    private void Run(string? deviceId)
    {
        int co = NativeMethods.CoInitializeEx(0, NativeMethods.CoinitMultithreaded);
        var wrappers = new StrategyBasedComWrappers();
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        uint taskIndex = 0;
        nint mmcss = 0;
        try
        {
            try
            {
                enumerator = AudioCapture.CreateEnumerator(wrappers);
                device = OpenDevice(wrappers, enumerator, deviceId);
                DeviceName = WasapiSession.ReadFriendlyName(wrappers, device) ?? "Micrófono";
                Com.Check(device.Activate(AudioConstants.IidAudioClient, AudioConstants.ClsctxAll, 0, out nint clientPointer));
                client = Com.Wrap<IAudioClient>(wrappers, clientPointer);
                WaveFormatExtensible* mix = null;
                Com.Check(client.GetMixFormat(&mix));
                _channels = Math.Max(1, (int)mix->Channels);
                uint mask = mix->FormatTag == AudioConstants.WaveFormatExtensibleTag ? mix->ChannelMask : 0;
                Marshal.FreeCoTaskMem((nint)mix);
                WaveFormatExtensible format = WaveFormatExtensible.Float32(SampleRate, _channels, mask);
                uint flags = AudioConstants.StreamFlagsEventCallback | AudioConstants.StreamFlagsAutoConvertPcm | AudioConstants.StreamFlagsSrcDefaultQuality;
                Com.Check(client.Initialize(AudioConstants.ShareModeShared, flags, Buffer.Ticks, 0, &format, null));
                Com.Check(client.SetEventHandle(_event));
                Com.Check(client.GetService(AudioConstants.IidCaptureClient, out nint capturePointer));
                capture = Com.Wrap<IAudioCaptureClient>(wrappers, capturePointer);
            }
            catch (AudioDeviceUnavailableException ex)
            {
                _openError = ex.HResult == AudioHResults.ENotFound
                    ? new AudioDeviceUnavailableException("Windows no tiene ningún micrófono activo.", ex.HResult)
                    : ex;
                return;
            }
            finally
            {
                _opened.Set();
            }

            // Espera a Start (o a que se cierre sin haber empezado).
            nint* handles = stackalloc nint[2] { _stopEvent, _event };
            while (_handler is null && !_stopping)
            {
                _ = NativeMethods.WaitForMultipleObjects(2, handles, false, 100);
            }

            if (_stopping)
            {
                return;
            }

            mmcss = NativeMethods.AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
            Com.Check(client!.Start());
            float[] mono = new float[(int)(SampleRate * Buffer.TotalSeconds) * 2];
            while (!_stopping)
            {
                _ = NativeMethods.WaitForMultipleObjects(2, handles, false, 200);
                Drain(capture!, ref mono);
            }

            _ = client.Stop();
        }
        catch (AudioDeviceUnavailableException)
        {
            // El micrófono se desconectó a mitad: lo grabado hasta ahí queda.
        }
        finally
        {
            if (mmcss != 0)
            {
                NativeMethods.AvRevertMmThreadCharacteristics(mmcss);
            }

            Com.Release(capture);
            Com.Release(client);
            Com.Release(device);
            Com.Release(enumerator);
            if (co >= 0)
            {
                NativeMethods.CoUninitialize();
            }
        }
    }

    private void Drain(IAudioCaptureClient capture, ref float[] mono)
    {
        while (capture.GetNextPacketSize(out uint packet) >= 0 && packet > 0)
        {
            byte* data;
            Com.Check(capture.GetBuffer(&data, out uint frames, out uint flags, out _, out ulong qpc));
            try
            {
                if (mono.Length < frames)
                {
                    mono = new float[frames];
                }

                Span<float> output = mono.AsSpan(0, (int)frames);
                if ((flags & AudioConstants.CaptureFlagSilent) != 0 || data is null)
                {
                    output.Clear();
                }
                else
                {
                    var input = new ReadOnlySpan<float>(data, (int)frames * _channels);
                    float scale = 1f / _channels;
                    for (int i = 0; i < output.Length; i++)
                    {
                        float sum = 0;
                        for (int c = 0; c < _channels; c++)
                        {
                            sum += input[(i * _channels) + c];
                        }

                        output[i] = sum * scale;
                    }
                }

                _handler?.Invoke(output, (long)qpc, (flags & AudioConstants.CaptureFlagDiscontinuity) != 0);
            }
            finally
            {
                _ = capture.ReleaseBuffer(frames);
            }
        }
    }

    private static IMMDevice OpenDevice(ComWrappers wrappers, IMMDeviceEnumerator enumerator, string? deviceId)
    {
        nint pointer;
        if (deviceId is null)
        {
            Com.Check(enumerator.GetDefaultAudioEndpoint(AudioConstants.FlowCapture, AudioConstants.RoleConsole, out pointer));
        }
        else
        {
            nint id = Marshal.StringToCoTaskMemUni(deviceId);
            try
            {
                Com.Check(enumerator.GetDevice(id, out pointer));
            }
            finally
            {
                Marshal.FreeCoTaskMem(id);
            }
        }

        return Com.Wrap<IMMDevice>(wrappers, pointer);
    }
}
