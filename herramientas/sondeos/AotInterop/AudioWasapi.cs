using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace AotInterop;

// WASAPI mínimo escrito a mano con [GeneratedComInterface]: los stubs COM los genera el compilador
// (ComWrappers), sin [ComImport] ni marshalling en tiempo de ejecución.
internal static unsafe partial class AudioWasapi
{
    private const uint ClsctxAll = 0x17;
    private const uint StreamflagsEventcallback = 0x00040000;
    private const uint StreamflagsAutoconvertpcm = 0x80000000;
    private const uint StreamflagsSrcDefaultQuality = 0x08000000;

    private static readonly Guid ClsidEnumerador = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidEnumerador = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IidRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    private static readonly Guid IidAudioClock = new("CD63314F-3FBA-4A1B-812C-EF96358728E7");
    private static readonly Guid SubtipoFloat = new("00000003-0000-0010-8000-00AA00389B71");

    public static void Reproducir(float[] estereo48k)
    {
        const int Hz = 48000;
        var arranque = Stopwatch.StartNew();
        _ = CoInitializeEx(0, 0); // COINIT_MULTITHREADED; S_FALSE o RPC_E_CHANGED_MODE no importan aquí
        Marshal.ThrowExceptionForHR(CoCreateInstance(ClsidEnumerador, 0, ClsctxAll, IidEnumerador, out IMMDeviceEnumerator enumerador));
        IMMDevice dispositivo = enumerador.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */);
        var cw = new StrategyBasedComWrappers();
        var cliente = Envolver<IAudioClient>(cw, dispositivo.Activate(IidAudioClient, ClsctxAll, 0));

        var formato = new WaveFormatExtensible
        {
            Etiqueta = 0xFFFE, Canales = 2, MuestrasPorSegundo = Hz, BytesPorSegundo = Hz * 8, Alineacion = 8,
            Bits = 32, Extra = 22, BitsValidos = 32, MascaraCanales = 0x3, Subtipo = SubtipoFloat,
        };
        cliente.GetDevicePeriod(out long periodoDefecto, out long periodoMinimo);
        // Shared mode con AUTOCONVERTPCM: el motor de audio remuestrea si el mezclador no está a 48 kHz.
        cliente.Initialize(0, StreamflagsEventcallback | StreamflagsAutoconvertpcm | StreamflagsSrcDefaultQuality,
            200_000, 0, &formato, null);
        uint cuadrosBuffer = cliente.GetBufferSize();
        long latencia = cliente.GetStreamLatency();
        using var evento = new AutoResetEvent(false);
        cliente.SetEventHandle(evento.SafeWaitHandle.DangerousGetHandle());
        var render = Envolver<IAudioRenderClient>(cw, cliente.GetService(IidRenderClient));
        var reloj = Envolver<IAudioClock>(cw, cliente.GetService(IidAudioClock));
        ulong frecuencia = reloj.GetFrequency();
        Console.WriteLine($"WASAPI propio: listo en {arranque.Elapsed.TotalMilliseconds:0.0} ms; buffer {cuadrosBuffer} cuadros " +
            $"({cuadrosBuffer * 1000.0 / Hz:0.0} ms), latencia del flujo {latencia / 10_000.0:0.0} ms, periodo {periodoDefecto / 10_000.0:0.00} ms " +
            $"(mín. {periodoMinimo / 10_000.0:0.00}), frecuencia del reloj {frecuencia} /s");

        int total = estereo48k.Length / 2, escritos = 0;
        Escribir(render, estereo48k, ref escritos, cuadrosBuffer);
        var registro = new RegistroReloj("WASAPI", Hz);
        var pared = Stopwatch.StartNew();
        cliente.Start();
        double desfaseQpc = 0;
        int puntosQpc = 0;
        while (true)
        {
            evento.WaitOne(200);
            uint relleno = cliente.GetCurrentPadding();
            Escribir(render, estereo48k, ref escritos, cuadrosBuffer - relleno);
            reloj.GetPosition(out ulong posicion, out ulong qpc);
            double segundos = posicion / (double)frecuencia;
            registro.Anotar(pared.Elapsed.TotalSeconds, segundos);
            // qpc viene en unidades de 100 ns y marca el instante exacto en que se muestreó la posición.
            if (segundos > 0)
            {
                desfaseQpc = Math.Max(desfaseQpc, Math.Abs(Stopwatch.GetTimestamp() * 1e7 / Stopwatch.Frequency - qpc) / 10_000.0);
                puntosQpc++;
            }
            if (segundos >= total / (double)Hz)
                break;
        }
        cliente.Stop();
        Console.WriteLine($"  edad máx. de la marca QPC de GetPosition al leerla: {desfaseQpc:0.000} ms ({puntosQpc} lecturas)");
        registro.Resumir();
    }

    private static void Escribir(IAudioRenderClient render, float[] datos, ref int escritos, uint libres)
    {
        if (libres == 0)
            return;
        byte* destino = render.GetBuffer(libres);
        int restantes = datos.Length / 2 - escritos;
        int n = (int)Math.Min(libres, (uint)Math.Max(restantes, 0));
        new ReadOnlySpan<float>(datos, escritos * 2, n * 2).CopyTo(new Span<float>(destino, n * 2));
        if (n < libres)
            new Span<float>((float*)destino + n * 2, (int)(libres - n) * 2).Clear();
        escritos += n;
        render.ReleaseBuffer(libres, 0);
    }

    private static T Envolver<T>(ComWrappers cw, nint puntero)
    {
        try
        {
            return (T)cw.GetOrCreateObjectForComInstance(puntero, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(puntero);
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reservado, uint modo);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint externo, uint contexto, in Guid iid, out IMMDeviceEnumerator objeto);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct WaveFormatExtensible
    {
        public ushort Etiqueta;
        public ushort Canales;
        public uint MuestrasPorSegundo;
        public uint BytesPorSegundo;
        public ushort Alineacion;
        public ushort Bits;
        public ushort Extra;
        public ushort BitsValidos;
        public uint MascaraCanales;
        public Guid Subtipo;
    }

    // Solo se declaran los métodos hasta el último que se usa, en el orden exacto de la vtable.
    [GeneratedComInterface, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    internal partial interface IMMDeviceEnumerator
    {
        nint EnumAudioEndpoints(int flujo, uint estados);
        IMMDevice GetDefaultAudioEndpoint(int flujo, int rol);
    }

    [GeneratedComInterface, Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    internal partial interface IMMDevice
    {
        nint Activate(in Guid iid, uint contexto, nint parametros);
    }

    [GeneratedComInterface, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    internal partial interface IAudioClient
    {
        void Initialize(int modo, uint banderas, long duracion, long periodicidad, WaveFormatExtensible* formato, Guid* sesion);
        uint GetBufferSize();
        long GetStreamLatency();
        uint GetCurrentPadding();
        [PreserveSig] int IsFormatSupported(int modo, WaveFormatExtensible* formato, WaveFormatExtensible** parecido);
        void GetMixFormat(WaveFormatExtensible** formato);
        void GetDevicePeriod(out long defecto, out long minimo);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(nint evento);
        nint GetService(in Guid iid);
    }

    [GeneratedComInterface, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
    internal partial interface IAudioRenderClient
    {
        byte* GetBuffer(uint cuadros);
        void ReleaseBuffer(uint cuadros, uint banderas);
    }

    [GeneratedComInterface, Guid("CD63314F-3FBA-4A1B-812C-EF96358728E7")]
    internal partial interface IAudioClock
    {
        ulong GetFrequency();
        void GetPosition(out ulong posicion, out ulong qpc);
    }
}
