using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Rodaje.Audio.Interop;

namespace Rodaje.Audio.Tests;

/// <summary>
/// <see cref="WasapiSession.Open"/> cuando el primer <c>IAudioClient</c> falla al inicializarse y el segundo
/// <c>IMMDevice::Activate</c> también falla (el dispositivo se desconecta a mitad de abrirlo). El cliente viejo ya se soltó
/// con <c>FinalRelease</c>: si quedara en el campo, <c>Dispose</c> llamaría a <c>Stop</c> sobre él y saldría un
/// <see cref="ObjectDisposedException"/> en vez del error de audio, que el hilo de audio no atrapa y cierra el proceso.
/// </summary>
/// <remarks>
/// Sin dispositivo: el enumerador, el dispositivo y el cliente son objetos de la prueba expuestos como COM con
/// <see cref="StrategyBasedComWrappers"/> y envueltos de vuelta en <c>ComObject</c>, como los de Windows.
/// </remarks>
public sealed partial class WasapiSessionReactivationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_failed_reactivation_reports_the_audio_error_instead_of_touching_the_released_client(bool lowLatency)
    {
        var wrappers = new StrategyBasedComWrappers();
        var device = new FakeDevice(lowLatency);
        IMMDeviceEnumerator enumerator = Com.Wrap<IMMDeviceEnumerator>(
            wrappers, wrappers.GetOrCreateComInterfaceForObject(new FakeEnumerator(wrappers, device), CreateComInterfaceFlags.None));
        var options = new AudioOutputOptions { SampleRate = 48_000, PreferLowLatency = lowLatency };

        var error = Assert.Throws<AudioDeviceUnavailableException>(() => WasapiSession.Open(wrappers, enumerator, options, 0));

        Assert.Equal(AudioHResults.DeviceInvalidated, error.HResult);
        Assert.Equal(2, device.Activations);
        FakeClient first = Assert.Single(device.Clients);
        Assert.Equal(0, first.StopCalls);
        Com.Release(enumerator);
    }

    [GeneratedComClass]
    internal sealed partial class FakeEnumerator(ComWrappers wrappers, FakeDevice device) : IMMDeviceEnumerator
    {
        public int EnumAudioEndpoints(int dataFlow, uint stateMask, out nint devices)
        {
            devices = 0;
            return NotImplemented;
        }

        public int GetDefaultAudioEndpoint(int dataFlow, int role, out nint endpoint)
        {
            endpoint = wrappers.GetOrCreateComInterfaceForObject(device, CreateComInterfaceFlags.None);
            return AudioHResults.SOk;
        }

        public int GetDevice(nint id, out nint device)
        {
            device = 0;
            return NotImplemented;
        }

        public int RegisterEndpointNotificationCallback(nint client) => NotImplemented;

        public int UnregisterEndpointNotificationCallback(nint client) => NotImplemented;
    }

    /// <summary>Da un cliente en la primera activación; en la segunda, el dispositivo ya no está.</summary>
    [GeneratedComClass]
    internal sealed partial class FakeDevice(bool lowLatency) : IMMDevice
    {
        private readonly StrategyBasedComWrappers _wrappers = new();

        public List<FakeClient> Clients { get; } = [];

        public int Activations { get; private set; }

        public int Activate(in Guid iid, uint clsCtx, nint activationParams, out nint instance)
        {
            Activations++;
            if (Activations > 1)
            {
                instance = 0;
                return AudioHResults.DeviceInvalidated;
            }

            var client = new FakeClient(lowLatency);
            Clients.Add(client);
            instance = _wrappers.GetOrCreateComInterfaceForObject(client, CreateComInterfaceFlags.None);
            return AudioHResults.SOk;
        }

        public int OpenPropertyStore(uint access, out nint properties)
        {
            properties = 0;
            return NotImplemented;
        }

        public int GetId(out nint id)
        {
            id = 0;
            return NotImplemented;
        }
    }

    /// <summary>Un cliente cuya inicialización falla: la normal con formato no admitido, la de baja latencia sin más.</summary>
    [GeneratedComClass]
    internal sealed unsafe partial class FakeClient(bool lowLatency) : IAudioClient3
    {
        public int StopCalls { get; private set; }

        public int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, WaveFormatExtensible* format, Guid* sessionGuid) =>
            AudioHResults.UnsupportedFormat;

        public int GetBufferSize(out uint frames)
        {
            frames = 0;
            return NotImplemented;
        }

        public int GetStreamLatency(out long latency)
        {
            latency = 0;
            return NotImplemented;
        }

        public int GetCurrentPadding(out uint frames)
        {
            frames = 0;
            return NotImplemented;
        }

        public int IsFormatSupported(int shareMode, WaveFormatExtensible* format, WaveFormatExtensible** closestMatch) => NotImplemented;

        public int GetMixFormat(WaveFormatExtensible** format)
        {
            var mix = (WaveFormatExtensible*)Marshal.AllocCoTaskMem(sizeof(WaveFormatExtensible));
            *mix = WaveFormatExtensible.Float32(48_000, 2, 0x3);
            *format = mix;
            return AudioHResults.SOk;
        }

        public int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod)
        {
            defaultPeriod = 0;
            minimumPeriod = 0;
            return NotImplemented;
        }

        public int Start() => NotImplemented;

        public int Stop()
        {
            StopCalls++;
            return AudioHResults.SOk;
        }

        public int Reset() => NotImplemented;

        public int SetEventHandle(nint eventHandle) => NotImplemented;

        public int GetService(in Guid iid, out nint service)
        {
            service = 0;
            return NotImplemented;
        }

        public int IsOffloadCapable(int category, out int offloadCapable)
        {
            offloadCapable = 0;
            return NotImplemented;
        }

        public int SetClientProperties(nint properties) => NotImplemented;

        public int GetBufferSizeLimits(WaveFormatExtensible* format, int eventDriven, out long minDuration, out long maxDuration)
        {
            minDuration = 0;
            maxDuration = 0;
            return NotImplemented;
        }

        public int GetSharedModeEnginePeriod(WaveFormatExtensible* format, out uint defaultPeriod, out uint fundamentalPeriod, out uint minPeriod,
            out uint maxPeriod)
        {
            defaultPeriod = 480;
            fundamentalPeriod = 48;
            minPeriod = 48;
            maxPeriod = 480;
            return lowLatency ? AudioHResults.SOk : NotImplemented;
        }

        public int GetCurrentSharedModeEnginePeriod(WaveFormatExtensible** format, out uint currentPeriod)
        {
            currentPeriod = 0;
            return NotImplemented;
        }

        public int InitializeSharedAudioStream(uint streamFlags, uint periodInFrames, WaveFormatExtensible* format, Guid* sessionGuid) =>
            AudioHResults.EndpointCreateFailed;
    }

    private const int NotImplemented = unchecked((int)0x80004001);
}
