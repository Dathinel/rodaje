using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Rodaje.Audio.Interop;

// Declaraciones propias de la API asíncrona de libpulse (pulse/mainloop.h, context.h, stream.h, introspect.h). Funciona
// igual contra PulseAudio y contra pipewire-pulse. Solo lo que se usa.

[StructLayout(LayoutKind.Sequential)]
internal struct PulseSampleSpec
{
    public int Format;
    public uint Rate;
    public byte Channels;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PulseBufferAttr
{
    public uint MaxLength;
    public uint TLength;
    public uint PreBuf;
    public uint MinReq;
    public uint FragSize;
}

[SupportedOSPlatform("linux")]
internal static unsafe partial class Pulse
{
    private const string Library = "libpulse.so.0";

    public const int SampleFloat32Le = 5;

    public const int ContextReady = 4;
    public const int ContextFailed = 5;
    public const int ContextTerminated = 6;
    public const int ContextNoAutospawn = 1;

    public const int StreamReady = 2;
    public const int StreamFailed = 3;
    public const int StreamTerminated = 4;

    public const int StreamStartCorked = 0x0001;
    public const int StreamInterpolateTiming = 0x0002;
    public const int StreamAutoTimingUpdate = 0x0008;
    public const int StreamAdjustLatency = 0x2000;

    public const int OperationRunning = 0;
    public const int SeekRelative = 0;

    /// <summary>«Lo que el servidor elija» en <see cref="PulseBufferAttr"/>.</summary>
    public const uint Default = uint.MaxValue;

    [LibraryImport(Library)]
    public static partial nint pa_mainloop_new();

    [LibraryImport(Library)]
    public static partial nint pa_mainloop_get_api(nint mainloop);

    [LibraryImport(Library)]
    public static partial int pa_mainloop_prepare(nint mainloop, int timeoutMilliseconds);

    [LibraryImport(Library)]
    public static partial int pa_mainloop_poll(nint mainloop);

    [LibraryImport(Library)]
    public static partial int pa_mainloop_dispatch(nint mainloop);

    [LibraryImport(Library)]
    public static partial void pa_mainloop_wakeup(nint mainloop);

    [LibraryImport(Library)]
    public static partial void pa_mainloop_free(nint mainloop);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint pa_context_new(nint api, string name);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int pa_context_connect(nint context, string? server, int flags, nint spawnApi);

    [LibraryImport(Library)]
    public static partial int pa_context_get_state(nint context);

    [LibraryImport(Library)]
    public static partial int pa_context_errno(nint context);

    [LibraryImport(Library)]
    public static partial void pa_context_disconnect(nint context);

    [LibraryImport(Library)]
    public static partial void pa_context_unref(nint context);

    [LibraryImport(Library)]
    public static partial nint pa_context_get_server_info(nint context, delegate* unmanaged<nint, nint, nint, void> callback, nint userdata);

    [LibraryImport(Library)]
    public static partial nint pa_context_get_source_info_list(nint context, delegate* unmanaged<nint, nint, int, nint, void> callback, nint userdata);

    [LibraryImport(Library)]
    public static partial nint pa_strerror(int error);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint pa_stream_new(nint context, string name, PulseSampleSpec* spec, nint channelMap);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int pa_stream_connect_playback(nint stream, string? device, PulseBufferAttr* attr, int flags, nint volume, nint syncStream);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int pa_stream_connect_record(nint stream, string? device, PulseBufferAttr* attr, int flags);

    [LibraryImport(Library)]
    public static partial int pa_stream_get_state(nint stream);

    [LibraryImport(Library)]
    public static partial PulseBufferAttr* pa_stream_get_buffer_attr(nint stream);

    [LibraryImport(Library)]
    public static partial nint pa_stream_get_device_name(nint stream);

    [LibraryImport(Library)]
    public static partial nuint pa_stream_writable_size(nint stream);

    [LibraryImport(Library)]
    public static partial nuint pa_stream_readable_size(nint stream);

    [LibraryImport(Library)]
    public static partial int pa_stream_write(nint stream, void* data, nuint bytes, nint freeCallback, long offset, int seek);

    [LibraryImport(Library)]
    public static partial int pa_stream_peek(nint stream, void** data, nuint* bytes);

    [LibraryImport(Library)]
    public static partial int pa_stream_drop(nint stream);

    [LibraryImport(Library)]
    public static partial nint pa_stream_cork(nint stream, int cork, nint callback, nint userdata);

    [LibraryImport(Library)]
    public static partial int pa_stream_get_time(nint stream, ulong* microseconds);

    [LibraryImport(Library)]
    public static partial int pa_stream_get_latency(nint stream, ulong* microseconds, int* negative);

    [LibraryImport(Library)]
    public static partial int pa_stream_disconnect(nint stream);

    [LibraryImport(Library)]
    public static partial void pa_stream_unref(nint stream);

    [LibraryImport(Library)]
    public static partial int pa_operation_get_state(nint operation);

    [LibraryImport(Library)]
    public static partial void pa_operation_unref(nint operation);

    public static string Text(nint utf8) => Marshal.PtrToStringUTF8(utf8) ?? "";

    public static string Error(nint context) => context == 0 ? "no se pudo crear la conexión" : Text(pa_strerror(pa_context_errno(context)));

    /// <summary>Una vuelta del bucle de libpulse: espera eventos como mucho <paramref name="timeoutMilliseconds"/> y los atiende.</summary>
    public static void Iterate(nint mainloop, int timeoutMilliseconds)
    {
        if (pa_mainloop_prepare(mainloop, timeoutMilliseconds) >= 0 && pa_mainloop_poll(mainloop) >= 0)
        {
            _ = pa_mainloop_dispatch(mainloop);
        }
    }

    /// <summary>Conecta un contexto nuevo (sin arrancar un servidor) y espera a que esté listo; 0 y el motivo si no.</summary>
    public static nint Connect(nint mainloop, string name, TimeSpan timeout, out string? error)
    {
        nint context = pa_context_new(pa_mainloop_get_api(mainloop), name);
        if (context == 0 || pa_context_connect(context, null, ContextNoAutospawn, 0) < 0)
        {
            error = $"No hay servidor de sonido (PulseAudio o PipeWire): {Error(context)}.";
            Release(ref context);
            return 0;
        }

        long deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        int state;
        while ((state = pa_context_get_state(context)) is not (ContextReady or ContextFailed or ContextTerminated)
            && Stopwatch.GetTimestamp() < deadline)
        {
            Iterate(mainloop, 20);
        }

        if (state != ContextReady)
        {
            error = state is ContextFailed or ContextTerminated
                ? $"No se pudo conectar con el servidor de sonido: {Error(context)}."
                : "El servidor de sonido no respondió a tiempo.";
            Release(ref context);
            return 0;
        }

        error = null;
        return context;
    }

    /// <summary>Espera a que el flujo esté listo; falso si falló o no respondió a tiempo.</summary>
    public static bool WaitReady(nint mainloop, nint stream, TimeSpan timeout)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        int state;
        while ((state = pa_stream_get_state(stream)) is not (StreamReady or StreamFailed or StreamTerminated) && Stopwatch.GetTimestamp() < deadline)
        {
            Iterate(mainloop, 10);
        }

        return state == StreamReady;
    }

    /// <summary>Espera a que el servidor confirme una operación y la suelta; falso si no llegó a tiempo.</summary>
    public static bool Await(nint mainloop, nint operation, TimeSpan timeout)
    {
        if (operation == 0)
        {
            return false;
        }

        long deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        bool done;
        while (!(done = pa_operation_get_state(operation) != OperationRunning) && Stopwatch.GetTimestamp() < deadline)
        {
            Iterate(mainloop, 10);
        }

        pa_operation_unref(operation);
        return done;
    }

    public static void Close(ref nint stream)
    {
        if (stream != 0)
        {
            _ = pa_stream_disconnect(stream);
            pa_stream_unref(stream);
            stream = 0;
        }
    }

    public static void Release(ref nint context)
    {
        if (context != 0)
        {
            pa_context_disconnect(context);
            pa_context_unref(context);
            context = 0;
        }
    }

    /// <summary>El instante de <see cref="Stopwatch"/> en unidades de 100 ns, la base de los paquetes de captura.</summary>
    public static long Now100ns() => (long)((Int128)Stopwatch.GetTimestamp() * TimeSpan.TicksPerSecond / Stopwatch.Frequency);
}
