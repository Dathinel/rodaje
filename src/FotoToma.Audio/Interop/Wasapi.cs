using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace FotoToma.Audio.Interop;

// Declaraciones propias de Core Audio (mmdeviceapi.h, audioclient.h, propsys.h). Cada interfaz lista sus
// métodos en el orden exacto de la vtable y solo hasta el último que se usa.

internal static class AudioHResults
{
    public const int SOk = 0;
    public const int SFalse = 1;
    public const int ENotFound = unchecked((int)0x80070490);
    public const int ENoInterface = unchecked((int)0x80004002);
    public const int RpcEChangedMode = unchecked((int)0x80010106);
    public const int DeviceInvalidated = unchecked((int)0x88890004);
    public const int UnsupportedFormat = unchecked((int)0x88890008);
    public const int DeviceInUse = unchecked((int)0x8889000A);
    public const int EndpointCreateFailed = unchecked((int)0x8889000F);
    public const int ServiceNotRunning = unchecked((int)0x88890010);
    public const int CpuUsageExceeded = unchecked((int)0x88890017);
    public const int ResourcesInvalidated = unchecked((int)0x88890026);
}

internal static class AudioConstants
{
    public const uint ClsctxAll = 0x17;
    public const int ShareModeShared = 0;
    public const int FlowRender = 0;
    public const int RoleMultimedia = 1;
    public const uint StreamFlagsEventCallback = 0x00040000;
    public const uint StreamFlagsAutoConvertPcm = 0x80000000;
    public const uint StreamFlagsSrcDefaultQuality = 0x08000000;
    public const ushort WaveFormatExtensibleTag = 0xFFFE;
    public const ushort WaveFormatIeeeFloatTag = 0x0003;
    public const uint DeviceStateActive = 0x1;
    public const ushort VtLpwstr = 31;

    public static readonly Guid ClsidDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public static readonly Guid IidDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    public static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    public static readonly Guid IidAudioClient3 = new("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42");
    public static readonly Guid IidRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    public static readonly Guid IidAudioClock = new("CD63314F-3FBA-4A1B-812C-EF96358728E7");
    public static readonly Guid IidNotificationClient = new("7991EEC9-7E89-4D85-8390-6C703CEC60C0");
    public static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00AA00389B71");
    public static readonly PropertyKey DeviceFriendlyName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WaveFormatExtensible
{
    public ushort FormatTag;
    public ushort Channels;
    public uint SamplesPerSec;
    public uint AvgBytesPerSec;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort ExtraSize;
    public ushort ValidBitsPerSample;
    public uint ChannelMask;
    public Guid SubFormat;

    public static WaveFormatExtensible Float32(int sampleRate, int channels, uint channelMask) => new()
    {
        FormatTag = AudioConstants.WaveFormatExtensibleTag,
        Channels = (ushort)channels,
        SamplesPerSec = (uint)sampleRate,
        AvgBytesPerSec = (uint)(sampleRate * channels * sizeof(float)),
        BlockAlign = (ushort)(channels * sizeof(float)),
        BitsPerSample = 32,
        ExtraSize = 22,
        ValidBitsPerSample = 32,
        ChannelMask = channelMask,
        SubFormat = AudioConstants.SubtypeIeeeFloat,
    };
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct PropertyKey(Guid formatId, uint propertyId)
{
    public readonly Guid FormatId = formatId;
    public readonly uint PropertyId = propertyId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort ValueType;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public nint Pointer;
    public nint Pointer2;
}

[GeneratedComInterface, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal partial interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(int dataFlow, uint stateMask, out nint devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(int dataFlow, int role, out nint endpoint);

    [PreserveSig]
    int GetDevice(nint id, out nint device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(nint client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(nint client);
}

[GeneratedComInterface, Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal partial interface IMMDevice
{
    [PreserveSig]
    int Activate(in Guid iid, uint clsCtx, nint activationParams, out nint instance);

    [PreserveSig]
    int OpenPropertyStore(uint access, out nint properties);

    [PreserveSig]
    int GetId(out nint id);
}

[GeneratedComInterface, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
internal partial interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int GetAt(uint index, out PropertyKey key);

    [PreserveSig]
    int GetValue(in PropertyKey key, out PropVariant value);
}

[GeneratedComInterface, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
internal unsafe partial interface IAudioClient
{
    [PreserveSig]
    int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, WaveFormatExtensible* format, Guid* sessionGuid);

    [PreserveSig]
    int GetBufferSize(out uint frames);

    [PreserveSig]
    int GetStreamLatency(out long latency);

    [PreserveSig]
    int GetCurrentPadding(out uint frames);

    [PreserveSig]
    int IsFormatSupported(int shareMode, WaveFormatExtensible* format, WaveFormatExtensible** closestMatch);

    [PreserveSig]
    int GetMixFormat(WaveFormatExtensible** format);

    [PreserveSig]
    int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

    [PreserveSig]
    int Start();

    [PreserveSig]
    int Stop();

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int SetEventHandle(nint eventHandle);

    [PreserveSig]
    int GetService(in Guid iid, out nint service);
}

[GeneratedComInterface, Guid("726778CD-F60A-4EDA-82DE-E47610CD78AA")]
internal unsafe partial interface IAudioClient2 : IAudioClient
{
    [PreserveSig]
    int IsOffloadCapable(int category, out int offloadCapable);

    [PreserveSig]
    int SetClientProperties(nint properties);

    [PreserveSig]
    int GetBufferSizeLimits(WaveFormatExtensible* format, int eventDriven, out long minDuration, out long maxDuration);
}

[GeneratedComInterface, Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42")]
internal unsafe partial interface IAudioClient3 : IAudioClient2
{
    [PreserveSig]
    int GetSharedModeEnginePeriod(WaveFormatExtensible* format, out uint defaultPeriod, out uint fundamentalPeriod, out uint minPeriod, out uint maxPeriod);

    [PreserveSig]
    int GetCurrentSharedModeEnginePeriod(WaveFormatExtensible** format, out uint currentPeriod);

    [PreserveSig]
    int InitializeSharedAudioStream(uint streamFlags, uint periodInFrames, WaveFormatExtensible* format, Guid* sessionGuid);
}

[GeneratedComInterface, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
internal unsafe partial interface IAudioRenderClient
{
    [PreserveSig]
    int GetBuffer(uint frames, byte** data);

    [PreserveSig]
    int ReleaseBuffer(uint frames, uint flags);
}

[GeneratedComInterface, Guid("CD63314F-3FBA-4A1B-812C-EF96358728E7")]
internal partial interface IAudioClockNative
{
    [PreserveSig]
    int GetFrequency(out ulong frequency);

    [PreserveSig]
    int GetPosition(out ulong position, out ulong qpcPosition);
}

[GeneratedComInterface, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
internal partial interface IMMNotificationClient
{
    [PreserveSig]
    int OnDeviceStateChanged(nint deviceId, uint newState);

    [PreserveSig]
    int OnDeviceAdded(nint deviceId);

    [PreserveSig]
    int OnDeviceRemoved(nint deviceId);

    [PreserveSig]
    int OnDefaultDeviceChanged(int flow, int role, nint defaultDeviceId);

    [PreserveSig]
    int OnPropertyValueChanged(nint deviceId, PropertyKey key);
}

internal static unsafe partial class NativeMethods
{
    public const uint CoinitMultithreaded = 0;
    public const uint WaitObject0 = 0;
    public const uint WaitTimeout = 0x102;
    public const uint Infinite = 0xFFFFFFFF;

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    public static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid clsid, nint outer, uint clsContext, in Guid iid, out nint instance);

    [LibraryImport("ole32.dll")]
    public static partial int PropVariantClear(ref PropVariant value);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", SetLastError = true)]
    public static partial nint CreateEvent(nint attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, nint name);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetEvent(nint handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    public static partial uint WaitForMultipleObjects(uint count, nint* handles, [MarshalAs(UnmanagedType.Bool)] bool waitAll, uint milliseconds);

    [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    [LibraryImport("avrt.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AvRevertMmThreadCharacteristics(nint handle);
}
