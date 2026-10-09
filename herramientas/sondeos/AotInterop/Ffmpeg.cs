#if VARIANTE_AUTOGEN
using FFmpeg.AutoGen;
#else
using FFmpeg.AutoGen.Abstractions;
#endif
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AotInterop;

internal static unsafe class Ffmpeg
{
    public enum ModoHw { Software, FfmpegCreaDispositivo, Compartido }

    private const string DllPrueba = "avcodec-63.dll";
    private const uint D3D11BindShaderResource = 0x8;

    public static void Iniciar(string? carpeta)
    {
        carpeta = Path.GetFullPath(carpeta ?? BuscarCarpeta());
        if (!File.Exists(Path.Combine(carpeta, DllPrueba)))
            throw new FileNotFoundException($"No están las DLL de FFmpeg 9 ({DllPrueba}) en {carpeta}");
        Console.WriteLine($"DLL de FFmpeg: {carpeta}");
#if VARIANTE_AUTOGEN
        ffmpeg.RootPath = carpeta;
#elif VARIANTE_ENLAZADO
        // DllImport("avcodec-63") no mira la carpeta de FFmpeg: el resolver la traduce a ruta absoluta.
        // Devolver 0 dejaría que Windows la buscara en el PATH (donde puede haber un FFmpeg GPL), así que falla.
        NativeLibrary.SetDllImportResolver(typeof(FFmpeg.AutoGen.Bindings.DynamicallyLinked.DynamicallyLinkedBindings).Assembly,
            (nombre, _, _) => NativeLibrary.Load(Path.Combine(carpeta, nombre + ".dll")));
        FFmpeg.AutoGen.Bindings.DynamicallyLinked.DynamicallyLinkedBindings.Initialize();
#else
        FFmpeg.AutoGen.Bindings.DynamicallyLoaded.DynamicallyLoadedBindings.LibrariesPath = carpeta;
        FFmpeg.AutoGen.Bindings.DynamicallyLoaded.DynamicallyLoadedBindings.Initialize();
#endif
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
    }

    private static string BuscarCarpeta()
    {
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, DllPrueba)))
            return AppContext.BaseDirectory;
        string? env = Environment.GetEnvironmentVariable("RODAJE_FFMPEG");
        if (!string.IsNullOrEmpty(env))
            return env;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string c = Path.Combine(d.FullName, ".entorno", "ffmpeg", "bin");
            if (File.Exists(Path.Combine(c, DllPrueba)))
                return c;
        }
        throw new DirectoryNotFoundException("No encuentro las DLL de FFmpeg: usa --ffmpeg <carpeta> o RODAJE_FFMPEG.");
    }

    public static string Version() => ffmpeg.av_version_info();

    // Costo fijo de una llamada nativa trivial según el tipo de enlace (delegado frente a DllImport).
    public static void MedirLlamadas()
    {
        const int N = 10_000_000;
        uint suma = 0;
        for (int i = 0; i < 1000; i++)
            suma += ffmpeg.avutil_version();
        var reloj = Stopwatch.StartNew();
        for (int i = 0; i < N; i++)
            suma += ffmpeg.avutil_version();
        reloj.Stop();
        Console.WriteLine($"Llamadas: {N:N0} x avutil_version() en {reloj.Elapsed.TotalMilliseconds:0} ms = " +
            $"{reloj.Elapsed.TotalMilliseconds * 1e6 / N:0.0} ns por llamada (control {suma % 10})");
    }

    internal static void Ok(int r, string que)
    {
        if (r < 0)
            throw new InvalidOperationException($"{que}: {TextoError(r)} ({r})");
    }

    internal static string TextoError(int r)
    {
        byte* buf = stackalloc byte[256];
        ffmpeg.av_strerror(r, buf, 256);
        return Marshal.PtrToStringUTF8((nint)buf) ?? r.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static string NombreFormato(AVPixelFormat f) => ffmpeg.av_get_pix_fmt_name(f) ?? f.ToString();

    // --- Dispositivos de hardware ---

    private static AVBufferRef* CrearDispositivoFfmpeg()
    {
        AVBufferRef* dev = null;
        int r = ffmpeg.av_hwdevice_ctx_create(&dev, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0);
        if (r < 0)
        {
            Console.WriteLine($"  av_hwdevice_ctx_create(D3D11VA) falló: {TextoError(r)}; se usa software");
            return null;
        }
        return dev;
    }

    // La receta para que los cuadros decodificados sean texturas de NUESTRO dispositivo.
    private static AVBufferRef* EnvolverDispositivo(Gpu gpu)
    {
        AVBufferRef* dev = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
        var hw = (AVHWDeviceContext*)dev->data;
        var d3d = (AVD3D11VADeviceContext*)hw->hwctx;
        // FFmpeg hace Release del dispositivo al liberar el contexto: le cedemos una referencia propia.
        *(nint*)&d3d->device = gpu.DispositivoConReferencia();
        // av_hwdevice_ctx_init rellena el contexto inmediato, ID3D11VideoDevice/VideoContext, el candado
        // y llama a ID3D10Multithread::SetMultithreadProtected(TRUE).
        int r = ffmpeg.av_hwdevice_ctx_init(dev);
        if (r < 0)
        {
            ffmpeg.av_buffer_unref(&dev);
            Ok(r, "av_hwdevice_ctx_init");
        }
        return dev;
    }

    // get_format como puntero a función [UnmanagedCallersOnly]: sin delegados ni código generado en ejecución.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static AVPixelFormat ElegirFormato(AVCodecContext* ctx, AVPixelFormat* formatos)
    {
        try
        {
            AVPixelFormat software = AVPixelFormat.AV_PIX_FMT_NONE;
            for (AVPixelFormat* p = formatos; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
            {
                if (*p == AVPixelFormat.AV_PIX_FMT_D3D11 && ctx->hw_device_ctx != null && CrearMarcos(ctx))
                    return *p;
                bool esHw = (ffmpeg.av_pix_fmt_desc_get(*p)->flags & (ulong)ffmpeg.AV_PIX_FMT_FLAG_HWACCEL) != 0;
                if (!esHw && software == AVPixelFormat.AV_PIX_FMT_NONE)
                    software = *p;
            }
            Console.WriteLine($"  get_format: sin D3D11, se cae a software ({NombreFormato(software)})");
            return software;
        }
        catch (Exception e)
        {
            Console.WriteLine($"  get_format falló: {e.Message}");
            return AVPixelFormat.AV_PIX_FMT_NONE;
        }
    }

    internal static AVPixelFormat UltimoFormatoSw { get; private set; } = AVPixelFormat.AV_PIX_FMT_NONE;

    // Pool de texturas propio para añadir D3D11_BIND_SHADER_RESOURCE: así el renderizador puede
    // muestrear los planos Y/UV de cada cuadro sin copiarlo.
    private static bool CrearMarcos(AVCodecContext* ctx)
    {
        AVBufferRef* marcos = null;
        if (ffmpeg.avcodec_get_hw_frames_parameters(ctx, ctx->hw_device_ctx, AVPixelFormat.AV_PIX_FMT_D3D11, &marcos) < 0)
            return false;
        var fc = (AVHWFramesContext*)marcos->data;
        var d3d = (AVD3D11VAFramesContext*)fc->hwctx;
        d3d->BindFlags |= D3D11BindShaderResource;
        fc->initial_pool_size += 8;
        if (ffmpeg.av_hwframe_ctx_init(marcos) < 0)
        {
            ffmpeg.av_buffer_unref(&marcos);
            return false;
        }
        ffmpeg.av_buffer_unref(&ctx->hw_frames_ctx);
        ctx->hw_frames_ctx = marcos;
        UltimoFormatoSw = fc->sw_format;
        return true;
    }

    // --- Decodificación de video ---

    public static void DecodificarVideo(string ruta, ModoHw modo, int cuadros)
    {
        AVBufferRef* dev = modo == ModoHw.FfmpegCreaDispositivo ? CrearDispositivoFfmpeg() : null;
        try
        {
            using var dec = new DecodificadorVideo(ruta, dev);
            Console.WriteLine($"Video [{modo}{(dev == null && modo != ModoHw.Software ? " -> software" : "")}]: " +
                $"{dec.Descripcion}");
            AVFrame* cuadro = ffmpeg.av_frame_alloc();
            try
            {
                for (int n = 0; n < cuadros && dec.Siguiente(cuadro); n++)
                {
                    var f = (AVPixelFormat)cuadro->format;
                    string extra = f == AVPixelFormat.AV_PIX_FMT_D3D11
                        ? $" textura=0x{(nint)cuadro->data[0]:X} índice={(nint)cuadro->data[1]} sw_format={NombreFormato(UltimoFormatoSw)}"
                        : "";
                    Console.WriteLine($"  cuadro {n}: pts={cuadro->pts} ({cuadro->pts * ffmpeg.av_q2d(dec.Flujo->time_base):0.000} s) " +
                        $"{cuadro->width}x{cuadro->height} formato={NombreFormato(f)}{extra}");
                    ffmpeg.av_frame_unref(cuadro);
                }
            }
            finally
            {
                ffmpeg.av_frame_free(&cuadro);
            }
        }
        finally
        {
            if (dev != null)
                ffmpeg.av_buffer_unref(&dev);
        }
    }

    public static void CompartirDispositivo(string ruta)
    {
        const int Comparar = 3;
        var referencia = new List<ushort[]>();
        using (var sw = new DecodificadorVideo(ruta, null))
        {
            AVFrame* c = ffmpeg.av_frame_alloc();
            while (referencia.Count < Comparar && sw.Siguiente(c))
            {
                referencia.Add(PlanoYSoftware(c));
                ffmpeg.av_frame_unref(c);
            }
            ffmpeg.av_frame_free(&c);
        }

        using var gpu = new Gpu();
        AVBufferRef* dev = EnvolverDispositivo(gpu);
        try
        {
            using var dec = new DecodificadorVideo(ruta, dev);
            Console.WriteLine($"Compartido: dispositivo nuestro 0x{gpu.Dispositivo:X}; {dec.Descripcion}");
            AVFrame* cuadro = ffmpeg.av_frame_alloc();
            for (int n = 0; n < Comparar && dec.Siguiente(cuadro); n++)
            {
                if ((AVPixelFormat)cuadro->format != AVPixelFormat.AV_PIX_FMT_D3D11)
                    throw new InvalidOperationException($"El cuadro salió en {NombreFormato((AVPixelFormat)cuadro->format)}, no en D3D11");
                nint textura = (nint)cuadro->data[0];
                uint indice = (uint)(nint)cuadro->data[1];
                var lectura = gpu.LeerPlanoY(textura, indice, cuadro->width, cuadro->height);
                int maxDif = 0;
                for (int k = 0; k < lectura.Y.Length; k++)
                    maxDif = Math.Max(maxDif, Math.Abs(lectura.Y[k] - referencia[n][k]));
                Console.WriteLine($"  cuadro {n}: pts={cuadro->pts} textura 0x{textura:X}[{indice}] {lectura.Descripcion}; " +
                    $"mismo dispositivo={lectura.MismoDispositivo}; Y media={lectura.Y.Average(v => (double)v):0.00}; " +
                    $"dif. máx. contra software={maxDif}");
                ffmpeg.av_frame_unref(cuadro);
            }
            ffmpeg.av_frame_free(&cuadro);
        }
        finally
        {
            ffmpeg.av_buffer_unref(&dev);
        }
    }

    // Plano Y en ushort con el mismo rango que devuelve LeerPlanoY (8 o 10 bits).
    private static ushort[] PlanoYSoftware(AVFrame* c)
    {
        var f = (AVPixelFormat)c->format;
        bool diez = f == AVPixelFormat.AV_PIX_FMT_YUV420P10LE;
        if (!diez && f != AVPixelFormat.AV_PIX_FMT_YUV420P && f != AVPixelFormat.AV_PIX_FMT_NV12)
            throw new NotSupportedException($"Formato de referencia no previsto: {NombreFormato(f)}");
        var y = new ushort[c->width * c->height];
        for (int fila = 0; fila < c->height; fila++)
        {
            byte* origen = c->data[0] + (long)fila * c->linesize[0];
            for (int x = 0; x < c->width; x++)
                y[fila * c->width + x] = diez ? ((ushort*)origen)[x] : origen[x];
        }
        return y;
    }

    // --- Medición ---

    public static void Medir(string ruta)
    {
        MedirUno(ruta, "software (hilos automáticos)", null, false);
        using var gpu = new Gpu();
        AVBufferRef* dev = EnvolverDispositivo(gpu);
        try
        {
            MedirUno(ruta, "d3d11va, dispositivo compartido, cuadros en GPU", dev, false);
            MedirUno(ruta, "d3d11va + copia a CPU (av_hwframe_transfer_data)", dev, true);
        }
        finally
        {
            ffmpeg.av_buffer_unref(&dev);
        }
    }

    private static void MedirUno(string ruta, string nombre, AVBufferRef* dev, bool copiarACpu)
    {
        using var proceso = Process.GetCurrentProcess();
        using var dec = new DecodificadorVideo(ruta, dev);
        AVFrame* cuadro = ffmpeg.av_frame_alloc();
        AVFrame* enCpu = ffmpeg.av_frame_alloc();
        int n = 0;
        TimeSpan cpu0 = proceso.TotalProcessorTime;
        var reloj = Stopwatch.StartNew();
        while (dec.Siguiente(cuadro))
        {
            if (copiarACpu)
            {
                Ok(ffmpeg.av_hwframe_transfer_data(enCpu, cuadro, 0), "av_hwframe_transfer_data");
                ffmpeg.av_frame_unref(enCpu);
            }
            ffmpeg.av_frame_unref(cuadro);
            n++;
        }
        reloj.Stop();
        proceso.Refresh();
        double cpu = (proceso.TotalProcessorTime - cpu0).TotalSeconds;
        ffmpeg.av_frame_free(&cuadro);
        ffmpeg.av_frame_free(&enCpu);
        Console.WriteLine($"Medición [{nombre}]: {dec.Descripcion}");
        Console.WriteLine($"  {n} cuadros en {reloj.Elapsed.TotalSeconds:0.000} s = {n / reloj.Elapsed.TotalSeconds:0.0} fps; " +
            $"CPU {cpu:0.00} s ({cpu / reloj.Elapsed.TotalSeconds * 100 / Environment.ProcessorCount:0.0} % del total de {Environment.ProcessorCount} hilos)");
    }

    // --- Audio ---

    public static float[] MuestrasOTono(string? ruta) => ruta is null ? Tono.Generar(48000, 2.0) : DecodificarAudio(ruta);

    public static float[] DecodificarAudio(string ruta)
    {
        AVFormatContext* fmt = null;
        Ok(ffmpeg.avformat_open_input(&fmt, ruta, null, null), "avformat_open_input");
        AVCodecContext* ctx = null;
        SwrContext* swr = null;
        AVPacket* pkt = ffmpeg.av_packet_alloc();
        AVFrame* cuadro = ffmpeg.av_frame_alloc();
        var salida = new List<float>();
        try
        {
            Ok(ffmpeg.avformat_find_stream_info(fmt, null), "avformat_find_stream_info");
            AVCodec* codec = null;
            int indice = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &codec, 0);
            Ok(indice, "av_find_best_stream(audio)");
            ctx = ffmpeg.avcodec_alloc_context3(codec);
            Ok(ffmpeg.avcodec_parameters_to_context(ctx, fmt->streams[indice]->codecpar), "avcodec_parameters_to_context");
            ctx->pkt_timebase = fmt->streams[indice]->time_base;
            Ok(ffmpeg.avcodec_open2(ctx, codec, null), "avcodec_open2(audio)");

            AVChannelLayout estereo;
            ffmpeg.av_channel_layout_default(&estereo, 2);
            Ok(ffmpeg.swr_alloc_set_opts2(&swr, &estereo, AVSampleFormat.AV_SAMPLE_FMT_FLT, 48000,
                &ctx->ch_layout, ctx->sample_fmt, ctx->sample_rate, 0, null), "swr_alloc_set_opts2");
            Ok(ffmpeg.swr_init(swr), "swr_init");
            Console.WriteLine($"Audio: {ffmpeg.avcodec_get_name(ctx->codec_id)} {ctx->sample_rate} Hz, {ctx->ch_layout.nb_channels} canales, " +
                $"{ffmpeg.av_get_sample_fmt_name(ctx->sample_fmt)} -> flt estéreo 48000 Hz");

            bool fin = false;
            while (true)
            {
                int r = ffmpeg.avcodec_receive_frame(ctx, cuadro);
                if (r == 0)
                {
                    Convertir(swr, cuadro->extended_data, cuadro->nb_samples, salida);
                    ffmpeg.av_frame_unref(cuadro);
                    continue;
                }
                if (r == ffmpeg.AVERROR_EOF)
                    break;
                if (r != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    Ok(r, "avcodec_receive_frame(audio)");
                if (fin)
                    break;
                r = ffmpeg.av_read_frame(fmt, pkt);
                if (r == ffmpeg.AVERROR_EOF)
                {
                    fin = true;
                    Ok(ffmpeg.avcodec_send_packet(ctx, null), "avcodec_send_packet(vaciar)");
                    continue;
                }
                Ok(r, "av_read_frame");
                if (pkt->stream_index == indice)
                    Ok(ffmpeg.avcodec_send_packet(ctx, pkt), "avcodec_send_packet(audio)");
                ffmpeg.av_packet_unref(pkt);
            }
            Convertir(swr, null, 0, salida);

            int cuadros = salida.Count / 2;
            int cruces = 0;
            for (int k = 2; k < salida.Count; k += 2)
                if (salida[k - 2] < 0 && salida[k] >= 0)
                    cruces++;
            Console.WriteLine($"  {cuadros} muestras por canal = {cuadros / 48000.0:0.0000} s; frecuencia estimada {cruces / (cuadros / 48000.0):0.0} Hz; " +
                $"pico {salida.Max(Math.Abs):0.000}");
            return [.. salida];
        }
        finally
        {
            ffmpeg.av_frame_free(&cuadro);
            ffmpeg.av_packet_free(&pkt);
            ffmpeg.swr_free(&swr);
            ffmpeg.avcodec_free_context(&ctx);
            ffmpeg.avformat_close_input(&fmt);
        }
    }

    // entrada null vacía el remuestreador (los cuadros que retiene por el filtro).
    private static void Convertir(SwrContext* swr, byte** entrada, int muestras, List<float> salida)
    {
        while (true)
        {
            int capacidad = ffmpeg.swr_get_out_samples(swr, muestras) + 32;
            var buffer = new float[capacidad * 2];
            int n;
            fixed (float* p = buffer)
            {
                byte* destino = (byte*)p;
                n = ffmpeg.swr_convert(swr, &destino, capacidad, entrada, muestras);
            }
            Ok(n, "swr_convert");
            for (int k = 0; k < n * 2; k++)
                salida.Add(buffer[k]);
            if (entrada != null || n == 0)
                return;
        }
    }

    // --- Decodificador de video ---

    private sealed class DecodificadorVideo : IDisposable
    {
        private AVFormatContext* fmt;
        private AVCodecContext* ctx;
        private AVPacket* pkt;
        private readonly int indice;
        private bool finEntrada;

        public DecodificadorVideo(string ruta, AVBufferRef* dispositivo)
        {
            AVFormatContext* f = null;
            Ok(ffmpeg.avformat_open_input(&f, ruta, null, null), "avformat_open_input");
            fmt = f;
            Ok(ffmpeg.avformat_find_stream_info(fmt, null), "avformat_find_stream_info");
            AVCodec* codec = null;
            indice = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
            Ok(indice, "av_find_best_stream(video)");
            ctx = ffmpeg.avcodec_alloc_context3(codec);
            Ok(ffmpeg.avcodec_parameters_to_context(ctx, Flujo->codecpar), "avcodec_parameters_to_context");
            ctx->pkt_timebase = Flujo->time_base;
            if (dispositivo != null)
            {
                ctx->hw_device_ctx = ffmpeg.av_buffer_ref(dispositivo);
                ctx->get_format = new AVCodecContext_get_format_func
                {
                    Pointer = (nint)(delegate* unmanaged[Cdecl]<AVCodecContext*, AVPixelFormat*, AVPixelFormat>)&ElegirFormato,
                };
            }
            else
            {
                ctx->thread_count = 0;
            }
            Ok(ffmpeg.avcodec_open2(ctx, codec, null), "avcodec_open2(video)");
            pkt = ffmpeg.av_packet_alloc();
            Descripcion = $"{ffmpeg.avcodec_get_name(ctx->codec_id)} {ctx->width}x{ctx->height} " +
                $"{ffmpeg.av_q2d(Flujo->avg_frame_rate):0.##} fps, formato del códec {NombreFormato(ctx->sw_pix_fmt == AVPixelFormat.AV_PIX_FMT_NONE ? ctx->pix_fmt : ctx->sw_pix_fmt)}" +
                (dispositivo != null ? ", hw_device_ctx D3D11VA" : $", hilos={ctx->thread_count}");
        }

        public string Descripcion { get; }

        public AVStream* Flujo => fmt->streams[indice];

        public bool Siguiente(AVFrame* cuadro)
        {
            while (true)
            {
                int r = ffmpeg.avcodec_receive_frame(ctx, cuadro);
                if (r == 0)
                    return true;
                if (r == ffmpeg.AVERROR_EOF)
                    return false;
                if (r != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    Ok(r, "avcodec_receive_frame");
                if (finEntrada)
                    return false;
                r = ffmpeg.av_read_frame(fmt, pkt);
                if (r == ffmpeg.AVERROR_EOF)
                {
                    finEntrada = true;
                    Ok(ffmpeg.avcodec_send_packet(ctx, null), "avcodec_send_packet(vaciar)");
                    continue;
                }
                Ok(r, "av_read_frame");
                if (pkt->stream_index == indice)
                    Ok(ffmpeg.avcodec_send_packet(ctx, pkt), "avcodec_send_packet");
                ffmpeg.av_packet_unref(pkt);
            }
        }

        public void Dispose()
        {
            AVPacket* p = pkt;
            ffmpeg.av_packet_free(&p);
            pkt = null;
            AVCodecContext* c = ctx;
            ffmpeg.avcodec_free_context(&c);
            ctx = null;
            AVFormatContext* f = fmt;
            ffmpeg.avformat_close_input(&f);
            fmt = null;
        }
    }
}
