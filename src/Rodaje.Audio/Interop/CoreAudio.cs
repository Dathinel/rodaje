using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Rodaje.Audio.Interop;

// Declaraciones propias de AudioToolbox (AudioUnit), CoreAudio (AudioObject) y CoreFoundation. Solo lo que se usa.

[StructLayout(LayoutKind.Sequential)]
internal struct AudioComponentDescription
{
    public uint ComponentType;
    public uint ComponentSubType;
    public uint ComponentManufacturer;
    public uint ComponentFlags;
    public uint ComponentFlagsMask;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioStreamBasicDescription
{
    public double SampleRate;
    public uint FormatId;
    public uint FormatFlags;
    public uint BytesPerPacket;
    public uint FramesPerPacket;
    public uint BytesPerFrame;
    public uint ChannelsPerFrame;
    public uint BitsPerChannel;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AURenderCallbackStruct
{
    public void* InputProc;
    public void* InputProcRefCon;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioObjectPropertyAddress
{
    public uint Selector;
    public uint Scope;
    public uint Element;
}

/// <summary>El principio de <c>AudioTimeStamp</c>: lo que se lee de él.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AudioTimeStampHead
{
    public double SampleTime;
    public ulong HostTime;
}

/// <summary><c>AudioBufferList</c> con un solo búfer intercalado.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AudioBufferListOne
{
    public uint NumberBuffers;
    public uint NumberChannels;
    public uint DataByteSize;
    public void* Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MachTimebaseInfo
{
    public uint Numer;
    public uint Denom;
}

[SupportedOSPlatform("macos")]
internal static unsafe partial class CoreAudio
{
    private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private const string CoreAudioFramework = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string LibSystem = "/usr/lib/libSystem.dylib";

    public const uint TypeOutput = 0x61756F75; // 'auou'
    public const uint SubTypeDefaultOutput = 0x64656620; // 'def '
    public const uint SubTypeHalOutput = 0x6168616C; // 'ahal'
    public const uint ManufacturerApple = 0x6170706C; // 'appl'

    public const uint PropertyStreamFormat = 8;
    public const uint PropertySetRenderCallback = 23;
    public const uint PropertyCurrentDevice = 2000;
    public const uint PropertyEnableIo = 2003;
    public const uint PropertySetInputCallback = 2005;

    public const uint ScopeGlobal = 0;
    public const uint ScopeInput = 1;
    public const uint ScopeOutput = 2;

    public const uint FormatLinearPcm = 0x6C70636D; // 'lpcm'
    public const uint FormatFlagsFloatPacked = 1 | 8;

    public const uint SystemObject = 1;
    public const uint SelectorDefaultOutputDevice = 0x644F7574; // 'dOut'
    public const uint SelectorDefaultInputDevice = 0x64496E20; // 'dIn '
    public const uint SelectorDevices = 0x64657623; // 'dev#'
    public const uint SelectorName = 0x6C6E616D; // 'lnam'
    public const uint SelectorDeviceUid = 0x75696420; // 'uid '
    public const uint SelectorLatency = 0x6C746E63; // 'ltnc'
    public const uint SelectorSafetyOffset = 0x73616674; // 'saft'
    public const uint SelectorBufferFrameSize = 0x6673697A; // 'fsiz'
    public const uint SelectorStreams = 0x73746D23; // 'stm#'
    public const uint ScopeObjectGlobal = 0x676C6F62; // 'glob'
    public const uint ScopeObjectOutput = 0x6F757470; // 'outp'
    public const uint ScopeObjectInput = 0x696E7074; // 'inpt'

    private const uint Utf8 = 0x08000100;

    [LibraryImport(AudioToolbox)]
    public static partial nint AudioComponentFindNext(nint component, AudioComponentDescription* description);

    [LibraryImport(AudioToolbox)]
    public static partial int AudioComponentInstanceNew(nint component, nint* instance);

    [LibraryImport(AudioToolbox)]
    public static partial int AudioComponentInstanceDispose(nint instance);

    [LibraryImport(AudioToolbox)]
    public static partial int AudioUnitSetProperty(nint unit, uint property, uint scope, uint element, void* data, uint size);

    [LibraryImport(AudioToolbox)]
    public static partial int AudioUnitGetProperty(nint unit, uint property, uint scope, uint element, void* data, uint* size);

    [LibraryImport(AudioToolbox)]
    public static partial int AudioUnitInitialize(nint unit);

    [LibraryImport(AudioToolbox)]
    public static partial int AudioUnitUninitialize(nint unit);

    [LibraryImport(AudioToolbox)]
    public static partial int AudioOutputUnitStart(nint unit);

    [LibraryImport(AudioToolbox)]
    public static partial int AudioOutputUnitStop(nint unit);

    [LibraryImport(AudioToolbox)]
    public static partial int AudioUnitRender(nint unit, uint* flags, void* timeStamp, uint bus, uint frames, AudioBufferListOne* data);

    [LibraryImport(CoreAudioFramework)]
    public static partial int AudioObjectGetPropertyData(uint objectId, AudioObjectPropertyAddress* address, uint qualifierSize, void* qualifier,
        uint* dataSize, void* data);

    [LibraryImport(CoreAudioFramework)]
    private static partial int AudioObjectSetPropertyData(uint objectId, AudioObjectPropertyAddress* address, uint qualifierSize, void* qualifier,
        uint dataSize, void* data);

    [LibraryImport(CoreAudioFramework)]
    public static partial int AudioObjectGetPropertyDataSize(uint objectId, AudioObjectPropertyAddress* address, uint qualifierSize, void* qualifier,
        uint* dataSize);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFStringGetLength(nint text);

    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFStringGetCString(nint text, byte* buffer, nint size, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint value);

    [LibraryImport(LibSystem)]
    public static partial ulong mach_absolute_time();

    [LibraryImport(LibSystem)]
    private static partial int mach_timebase_info(MachTimebaseInfo* info);

    private static readonly MachTimebaseInfo Timebase = ReadTimebase();

    /// <summary>Un instante de <c>mach_absolute_time</c> en la base de <see cref="System.Diagnostics.Stopwatch"/> (nanosegundos en macOS).</summary>
    public static long HostToStopwatch(ulong host) =>
        (long)((UInt128)host * Timebase.Numer / Timebase.Denom * (ulong)System.Diagnostics.Stopwatch.Frequency / 1_000_000_000);

    public static uint DefaultDevice(bool input)
    {
        uint device = 0;
        uint size = sizeof(uint);
        var address = new AudioObjectPropertyAddress
        {
            Selector = input ? SelectorDefaultInputDevice : SelectorDefaultOutputDevice,
            Scope = ScopeObjectGlobal,
        };
        return AudioObjectGetPropertyData(SystemObject, &address, 0, null, &size, &device) == 0 ? device : 0;
    }

    /// <summary>Cambia el dispositivo predeterminado del sistema (lo usan las pruebas, como quien lo cambia en Ajustes).</summary>
    public static int SetDefaultDevice(uint device, bool input)
    {
        var address = new AudioObjectPropertyAddress
        {
            Selector = input ? SelectorDefaultInputDevice : SelectorDefaultOutputDevice,
            Scope = ScopeObjectGlobal,
        };
        return AudioObjectSetPropertyData(SystemObject, &address, 0, null, sizeof(uint), &device);
    }

    public static uint[] Devices()
    {
        var address = new AudioObjectPropertyAddress { Selector = SelectorDevices, Scope = ScopeObjectGlobal };
        uint size = 0;
        if (AudioObjectGetPropertyDataSize(SystemObject, &address, 0, null, &size) != 0 || size == 0)
        {
            return [];
        }

        var devices = new uint[size / sizeof(uint)];
        fixed (uint* data = devices)
        {
            return AudioObjectGetPropertyData(SystemObject, &address, 0, null, &size, data) == 0 ? devices[..(int)(size / sizeof(uint))] : [];
        }
    }

    /// <summary>Si el dispositivo tiene flujos en ese sentido (salida o entrada).</summary>
    public static bool HasStreams(uint device, bool input)
    {
        var address = new AudioObjectPropertyAddress { Selector = SelectorStreams, Scope = input ? ScopeObjectInput : ScopeObjectOutput };
        uint size = 0;
        return AudioObjectGetPropertyDataSize(device, &address, 0, null, &size) == 0 && size > 0;
    }

    public static uint DeviceUInt(uint device, uint selector, uint scope)
    {
        uint value = 0;
        uint size = sizeof(uint);
        var address = new AudioObjectPropertyAddress { Selector = selector, Scope = scope };
        return AudioObjectGetPropertyData(device, &address, 0, null, &size, &value) == 0 ? value : 0;
    }

    public static string DeviceText(uint device, uint selector)
    {
        nint text = 0;
        uint size = (uint)sizeof(nint);
        var address = new AudioObjectPropertyAddress { Selector = selector, Scope = ScopeObjectGlobal };
        if (AudioObjectGetPropertyData(device, &address, 0, null, &size, &text) != 0 || text == 0)
        {
            return "";
        }

        try
        {
            int capacity = (int)((CFStringGetLength(text) * 4) + 1);
            byte* buffer = stackalloc byte[Math.Min(capacity, 1024)];
            return CFStringGetCString(text, buffer, Math.Min(capacity, 1024), Utf8) ? Marshal.PtrToStringUTF8((nint)buffer) ?? "" : "";
        }
        finally
        {
            CFRelease(text);
        }
    }

    /// <summary>Una unidad de salida de Apple sin inicializar (<paramref name="subType"/>: la predeterminada o la HAL); 0 si no hay.</summary>
    public static nint NewUnit(uint subType)
    {
        var description = new AudioComponentDescription { ComponentType = TypeOutput, ComponentSubType = subType, ComponentManufacturer = ManufacturerApple };
        nint component = AudioComponentFindNext(0, &description);
        nint unit = 0;
        return component != 0 && AudioComponentInstanceNew(component, &unit) == 0 ? unit : 0;
    }

    public static AudioStreamBasicDescription FloatFormat(int sampleRate, int channels) => new()
    {
        SampleRate = sampleRate,
        FormatId = FormatLinearPcm,
        FormatFlags = FormatFlagsFloatPacked,
        BytesPerPacket = (uint)(channels * sizeof(float)),
        FramesPerPacket = 1,
        BytesPerFrame = (uint)(channels * sizeof(float)),
        ChannelsPerFrame = (uint)channels,
        BitsPerChannel = 32,
    };

    public static void DisposeUnit(ref nint unit)
    {
        if (unit != 0)
        {
            _ = AudioOutputUnitStop(unit);
            _ = AudioUnitUninitialize(unit);
            _ = AudioComponentInstanceDispose(unit);
            unit = 0;
        }
    }

    public static string Describe(int status) => status == 0 ? "sin error" : $"OSStatus {status}";

    private static MachTimebaseInfo ReadTimebase()
    {
        MachTimebaseInfo info = default;
        return mach_timebase_info(&info) == 0 && info.Denom != 0 ? info : new MachTimebaseInfo { Numer = 1, Denom = 1 };
    }
}
