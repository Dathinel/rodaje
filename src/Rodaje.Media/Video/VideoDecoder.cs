using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using Rodaje.Core.Timing;
using Rodaje.Media.Gpu;
using Rodaje.Media.Interop;
using Rodaje.Media.Probe;
using Rodaje.Playback;

namespace Rodaje.Media.Video;

/// <summary>Decodificador de video con acceso aleatorio exacto al cuadro.</summary>
/// <remarks>
/// <para>
/// El cuadro <c>n</c> es el que, en orden de presentación, tiene la marca <c>start_time + n / tasa</c>
/// (redondeada al índice más cercano). Pedir el siguiente al último entregado, o uno poco más adelante,
/// sigue decodificando sin seek. Si no, se busca el keyframe anterior con <c>AVSEEK_FLAG_BACKWARD</c>, se
/// comprueba que su marca de presentación no pase del objetivo (el índice del contenedor puede ir por
/// DTS) y se decodifica hacia delante descartando lo anterior.
/// </para>
/// <para>
/// No es seguro entre hilos: las llamadas a <see cref="GetFrame"/> no pueden solaparse, como garantiza el
/// programador de reproducción. Los cuadros entregados son independientes del decodificador y pueden
/// liberarse en cualquier hilo y después de <see cref="Dispose"/>.
/// </para>
/// </remarks>
public sealed unsafe class VideoDecoder : IVideoFrameSource, IDisposable
{
    private const int MaxSeekAttempts = 24;
    private const int MaxEarlierSeeks = 4;
    private const int MaxConsecutiveErrors = 64;
    private const uint D3D11BindShaderResource = 0x8;

    private readonly VideoDecoderOptions _options;
    private readonly GpuDevice? _device;
    private readonly int _streamIndex;
    private readonly AVRational _timeBase;
    private readonly long _originTs;
    private readonly long _frameDurationTs;
    private readonly AVColorSpace _streamColorSpace;
    private readonly AVColorRange _streamColorRange;
    private GCHandle _self;
    private AVFormatContext* _format;
    private AVCodecContext* _codec;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private AVFrame* _held;
    private AVFrame* _pending;
    private FrameUploader? _uploader;

    private bool _hasHeld;
    private long _heldSlot;
    private bool _hasPending;
    private bool _packetWaiting;
    private bool _positionValid;
    private bool _inputEnded;
    private bool _drained;
    private long _lastIndex = -1;
    private long _minValidSlot = long.MinValue;
    private int _consecutiveErrors;
    private int _busy;
    private volatile bool _hardwareActive;
    private long _seeks;
    private long _decodedFrames;
    private long _decodeErrors;

    private VideoDecoder(AVFormatContext* format, string path, VideoDecoderOptions options)
    {
        _format = format;
        _options = options;
        try
        {
            Info = MediaProbe.Probe(format, path, scanTimestamps: true);
            Stream = Info.Video ?? throw new MediaException($"El archivo {path} no tiene un flujo de video.");
            _streamIndex = Stream.StreamIndex;
            AVStream* stream = format->streams[_streamIndex];
            for (int i = 0; i < format->nb_streams; i++)
            {
                if (i != _streamIndex)
                {
                    format->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
                }
            }

            _timeBase = stream->time_base;
            _streamColorSpace = stream->codecpar->color_space;
            _streamColorRange = stream->codecpar->color_range;
            FrameRate = Stream.FrameRate;
            _originTs = Av.FromTime(Stream.StartTime, _timeBase);
            _frameDurationTs = Math.Max(1, (long)Av.RoundDiv((Int128)FrameRate.Den * _timeBase.den, (Int128)FrameRate.Num * _timeBase.num));
            _device = options.Device is { IsDisposed: false } device ? device : null;
            OpenCodec(stream);
            _packet = ffmpeg.av_packet_alloc();
            _frame = ffmpeg.av_frame_alloc();
            _held = ffmpeg.av_frame_alloc();
            _pending = ffmpeg.av_frame_alloc();
            if (_device is not null)
            {
                _uploader = new FrameUploader();
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public MediaInfo Info { get; }

    public VideoStreamInfo Stream { get; }

    public Rational FrameRate { get; }

    /// <summary>Cantidad de cuadros según el contenedor (o estimada por la duración).</summary>
    public long FrameCount => Stream.FrameCount;

    /// <summary>El último <c>get_format</c> eligió D3D11VA (puede volver a software si el hardware no admite el flujo).</summary>
    public bool UsesHardwareDecoding => _hardwareActive;

    public VideoDecoderStatistics Statistics => new(
        Interlocked.Read(ref _seeks), Interlocked.Read(ref _decodedFrames), Interlocked.Read(ref _decodeErrors));

    /// <summary>Abre el flujo de video principal del archivo.</summary>
    /// <exception cref="FfmpegNotFoundException">No se pudo cargar FFmpeg.</exception>
    /// <exception cref="MediaException">El archivo no se puede abrir o no tiene video decodificable.</exception>
    public static VideoDecoder Open(string path, VideoDecoderOptions? options = null) =>
        new(Av.OpenInput(path), path, options ?? new VideoDecoderOptions());

    public ValueTask<DecodedFrame?> GetFrameAsync(long frameIndex, CancellationToken cancellationToken)
    {
        try
        {
            return ValueTask.FromResult(GetFrame(frameIndex, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<DecodedFrame?>(cancellationToken);
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            return ValueTask.FromException<DecodedFrame?>(ex);
        }
    }

    /// <summary>Decodifica el cuadro con ese índice exacto; <c>null</c> si el medio no lo tiene.</summary>
    /// <remarks>El llamador recibe la única referencia del cuadro y debe liberarlo.</remarks>
    public DecodedFrame? GetFrame(long frameIndex, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_format == null, this);
        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _busy, 1) != 0)
        {
            throw new InvalidOperationException("Las llamadas a GetFrame del mismo decodificador no pueden solaparse.");
        }

        try
        {
            if (_positionValid && _hasHeld && frameIndex == _lastIndex)
            {
                return Wrap(_held, frameIndex);
            }

            bool forward = _positionValid && frameIndex > _lastIndex && frameIndex - _lastIndex <= _options.MaxForwardDecodeFrames;
            long backoff = 0;
            for (int attempt = 0; ; attempt++)
            {
                if (!forward && !Seek(frameIndex, backoff, cancellationToken))
                {
                    return null;
                }

                forward = false;
                if (DecodeUntil(frameIndex, cancellationToken, out DecodedFrame? frame))
                {
                    return frame;
                }

                if (attempt >= MaxEarlierSeeks)
                {
                    return null;
                }

                backoff = backoff == 0 ? _frameDurationTs * 8 : backoff * 4;
            }
        }
        catch
        {
            _positionValid = false;
            throw;
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    public void Dispose()
    {
        if (_format == null)
        {
            return;
        }

        AVFrame* frame = _frame;
        ffmpeg.av_frame_free(&frame);
        frame = _held;
        ffmpeg.av_frame_free(&frame);
        frame = _pending;
        ffmpeg.av_frame_free(&frame);
        _frame = _held = _pending = null;
        AVPacket* packet = _packet;
        ffmpeg.av_packet_free(&packet);
        _packet = null;
        AVCodecContext* codec = _codec;
        ffmpeg.avcodec_free_context(&codec);
        _codec = null;
        AVFormatContext* format = _format;
        ffmpeg.avformat_close_input(&format);
        _format = null;
        _uploader?.Dispose();
        _uploader = null;
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    // ---- Apertura ----

    private void OpenCodec(AVStream* stream)
    {
        AVCodec* codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
        if (codec == null)
        {
            throw new MediaException($"FFmpeg no tiene decodificador para {Stream.Codec}.");
        }

        _codec = ffmpeg.avcodec_alloc_context3(codec);
        Av.Check(ffmpeg.avcodec_parameters_to_context(_codec, stream->codecpar), "avcodec_parameters_to_context");
        _codec->pkt_timebase = _timeBase;
        if (_options.AllowHardwareDecoding && _device is { SupportsHardwareDecoding: true })
        {
            _self = GCHandle.Alloc(this);
            _codec->opaque = (void*)GCHandle.ToIntPtr(_self);
            _codec->hw_device_ctx = ffmpeg.av_buffer_ref(_device.HardwareDeviceContext);
            _codec->get_format = new AVCodecContext_get_format_func
            {
                Pointer = (nint)(delegate* unmanaged[Cdecl]<AVCodecContext*, AVPixelFormat*, AVPixelFormat>)&SelectPixelFormat,
            };
            _codec->thread_count = 1;
        }
        else
        {
            _codec->thread_count = Math.Max(0, _options.SoftwareThreads);
            _codec->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;
        }

        int r = ffmpeg.avcodec_open2(_codec, codec, null);
        if (r < 0)
        {
            throw new MediaException($"No se pudo abrir el decodificador de {Stream.Codec}: {Av.ErrorText(r)}.", r);
        }
    }

    // get_format como puntero a función: sin delegados ni código generado en ejecución (Native AOT).
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static AVPixelFormat SelectPixelFormat(AVCodecContext* context, AVPixelFormat* formats)
    {
#pragma warning disable CA1031 // Una excepción no puede cruzar a código nativo; NONE hace fallar la decodificación.
        try
        {
            var self = GCHandle.FromIntPtr((nint)context->opaque).Target as VideoDecoder;
            AVPixelFormat software = AVPixelFormat.AV_PIX_FMT_NONE;
            for (AVPixelFormat* p = formats; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
            {
                if (*p == AVPixelFormat.AV_PIX_FMT_D3D11 && self is not null && context->hw_device_ctx != null
                    && self.TryCreateHardwareFrames(context))
                {
                    self._hardwareActive = true;
                    return *p;
                }

                bool hardware = (ffmpeg.av_pix_fmt_desc_get(*p)->flags & (ulong)ffmpeg.AV_PIX_FMT_FLAG_HWACCEL) != 0;
                if (!hardware && software == AVPixelFormat.AV_PIX_FMT_NONE)
                {
                    software = *p;
                }
            }

            if (self is not null)
            {
                self._hardwareActive = false;
            }

            return software;
        }
        catch (Exception)
        {
            return AVPixelFormat.AV_PIX_FMT_NONE;
        }
#pragma warning restore CA1031
    }

    // Pool propio para añadir D3D11_BIND_SHADER_RESOURCE (el renderizador muestrea los planos sin copiar)
    // y texturas de sobra para los cuadros que retienen la caché y la pantalla.
    private bool TryCreateHardwareFrames(AVCodecContext* context)
    {
        AVBufferRef* frames = null;
        if (ffmpeg.avcodec_get_hw_frames_parameters(context, context->hw_device_ctx, AVPixelFormat.AV_PIX_FMT_D3D11, &frames) < 0)
        {
            return false;
        }

        var framesContext = (AVHWFramesContext*)frames->data;
        var d3d = (AVD3D11VAFramesContext*)framesContext->hwctx;
        d3d->BindFlags |= D3D11BindShaderResource;
        framesContext->initial_pool_size += Math.Max(0, _options.ExtraHardwareFrames);
        if (ffmpeg.av_hwframe_ctx_init(frames) < 0)
        {
            ffmpeg.av_buffer_unref(&frames);
            return false;
        }

        ffmpeg.av_buffer_unref(&context->hw_frames_ctx);
        context->hw_frames_ctx = frames;
        return true;
    }

    // ---- Correspondencia entre índices y marcas de tiempo ----

    private long Slot(long timestamp) =>
        (long)Av.RoundDiv((Int128)(timestamp - _originTs) * _timeBase.num * FrameRate.Num, (Int128)_timeBase.den * FrameRate.Den);

    private long TimestampOf(long frameIndex) =>
        _originTs + (long)Av.RoundDiv((Int128)frameIndex * FrameRate.Den * _timeBase.den, (Int128)FrameRate.Num * _timeBase.num);

    private long SlotOf(AVFrame* frame)
    {
        long ts = frame->pts != ffmpeg.AV_NOPTS_VALUE ? frame->pts : frame->best_effort_timestamp;
        if (ts == ffmpeg.AV_NOPTS_VALUE)
        {
            return _hasHeld ? _heldSlot + 1 : Math.Max(0, _minValidSlot);
        }

        return Slot(ts);
    }

    // ---- Seek ----

    private bool Seek(long frameIndex, long extraBackoff, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _seeks);
        long floor = _originTs - _frameDurationTs;
        long target = TimestampOf(frameIndex) - extraBackoff;
        long step = _frameDurationTs;
        for (int attempt = 0; attempt < MaxSeekAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResetDecoder();
            long ts = Math.Max(target, floor);
            int r = ffmpeg.av_seek_frame(_format, _streamIndex, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
            if (r < 0)
            {
                r = ffmpeg.avformat_seek_file(_format, _streamIndex, long.MinValue, ts, long.MaxValue, 0);
            }

            if (r < 0)
            {
                throw new MediaException($"No se pudo buscar el cuadro {frameIndex}: {Av.ErrorText(r)}.", r);
            }

            if (!ReadFirstKeyPacket(cancellationToken, out long keyTs))
            {
                if (ts <= floor)
                {
                    return false;
                }

                target = ts - step;
                step *= 2;
                continue;
            }

            long keySlot = Slot(keyTs);
            if (keySlot > frameIndex)
            {
                if (ts <= floor)
                {
                    return false;
                }

                target = Math.Min(ts, keyTs) - step;
                step *= 2;
                continue;
            }

            // Lo que sale antes del keyframe en orden de presentación (cuadros iniciales de un GOP abierto)
            // depende de referencias que no se decodificaron.
            _minValidSlot = keySlot;
            _positionValid = true;
            return true;
        }

        throw new MediaException($"No se encontró un keyframe anterior al cuadro {frameIndex} tras {MaxSeekAttempts} intentos.");
    }

    private bool ReadFirstKeyPacket(CancellationToken cancellationToken, out long keyTimestamp)
    {
        int errors = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int r = ffmpeg.av_read_frame(_format, _packet);
            if (r == ffmpeg.AVERROR_EOF)
            {
                keyTimestamp = 0;
                return false;
            }

            if (r < 0)
            {
                Interlocked.Increment(ref _decodeErrors);
                if (++errors > MaxConsecutiveErrors)
                {
                    keyTimestamp = 0;
                    return false;
                }

                continue;
            }

            long ts = _packet->pts != ffmpeg.AV_NOPTS_VALUE ? _packet->pts : _packet->dts;
            if (_packet->stream_index != _streamIndex || (_packet->flags & ffmpeg.AV_PKT_FLAG_KEY) == 0 || ts == ffmpeg.AV_NOPTS_VALUE)
            {
                ffmpeg.av_packet_unref(_packet);
                continue;
            }

            _packetWaiting = true;
            keyTimestamp = ts;
            return true;
        }
    }

    private void ResetDecoder()
    {
        ffmpeg.avcodec_flush_buffers(_codec);
        ffmpeg.av_packet_unref(_packet);
        ffmpeg.av_frame_unref(_frame);
        ffmpeg.av_frame_unref(_held);
        ffmpeg.av_frame_unref(_pending);
        _hasHeld = _hasPending = _packetWaiting = false;
        _inputEnded = _drained = false;
        _positionValid = false;
        _consecutiveErrors = 0;
        _lastIndex = -1;
        _minValidSlot = long.MinValue;
    }

    // ---- Decodificación ----

    /// <summary>Devuelve <c>false</c> si el primer cuadro decodificado ya pasa del objetivo y hay que buscar antes.</summary>
    private bool DecodeUntil(long frameIndex, CancellationToken cancellationToken, out DecodedFrame? result)
    {
        while (true)
        {
            if (!NextFrame(cancellationToken))
            {
                // Fin del flujo: el último cuadro solo cubre índices dentro de la duración (tasa variable).
                if (_hasHeld && Time.FromFrames(frameIndex, FrameRate) < Stream.Duration)
                {
                    _lastIndex = frameIndex;
                    result = Wrap(_held, frameIndex);
                    return true;
                }

                result = null;
                return true;
            }

            long slot = SlotOf(_frame);
            if (slot < _minValidSlot)
            {
                ffmpeg.av_frame_unref(_frame);
                continue;
            }

            if (slot <= frameIndex)
            {
                ffmpeg.av_frame_unref(_held);
                ffmpeg.av_frame_move_ref(_held, _frame);
                _hasHeld = true;
                _heldSlot = slot;
                if (slot == frameIndex)
                {
                    _lastIndex = frameIndex;
                    result = Wrap(_held, frameIndex);
                    return true;
                }

                continue;
            }

            if (_hasHeld)
            {
                // Hueco en las marcas (tasa variable): el índice lo cubre el último cuadro anterior.
                ffmpeg.av_frame_move_ref(_pending, _frame);
                _hasPending = true;
                _lastIndex = frameIndex;
                result = Wrap(_held, frameIndex);
                return true;
            }

            ffmpeg.av_frame_unref(_frame);
            result = null;
            return false;
        }
    }

    private bool NextFrame(CancellationToken cancellationToken)
    {
        if (_hasPending)
        {
            ffmpeg.av_frame_move_ref(_frame, _pending);
            _hasPending = false;
            return true;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_drained)
            {
                return false;
            }

            int r = ffmpeg.avcodec_receive_frame(_codec, _frame);
            if (r == 0)
            {
                Interlocked.Increment(ref _decodedFrames);
                _consecutiveErrors = 0;
                return true;
            }

            if (r == ffmpeg.AVERROR_EOF)
            {
                _drained = true;
                return false;
            }

            if (r != Av.ErrorAgain)
            {
                RecordError(r);
                continue;
            }

            if (_inputEnded)
            {
                _drained = true;
                return false;
            }

            FeedDecoder();
        }
    }

    private void FeedDecoder()
    {
        while (true)
        {
            if (!_packetWaiting)
            {
                int read = ffmpeg.av_read_frame(_format, _packet);
                if (read == ffmpeg.AVERROR_EOF)
                {
                    EndInput();
                    return;
                }

                if (read < 0)
                {
                    Interlocked.Increment(ref _decodeErrors);
                    if (++_consecutiveErrors > MaxConsecutiveErrors)
                    {
                        EndInput();
                        return;
                    }

                    continue;
                }

                if (_packet->stream_index != _streamIndex)
                {
                    ffmpeg.av_packet_unref(_packet);
                    continue;
                }
            }

            int sent = ffmpeg.avcodec_send_packet(_codec, _packet);
            if (sent == Av.ErrorAgain)
            {
                _packetWaiting = true;
                return;
            }

            _packetWaiting = false;
            ffmpeg.av_packet_unref(_packet);
            if (sent < 0)
            {
                // Paquete dañado: se descarta y se sigue con el siguiente.
                RecordError(sent);
                continue;
            }

            return;
        }
    }

    private void EndInput()
    {
        ffmpeg.av_packet_unref(_packet);
        _packetWaiting = false;
        ffmpeg.avcodec_send_packet(_codec, null);
        _inputEnded = true;
    }

    private void RecordError(int error)
    {
        Interlocked.Increment(ref _decodeErrors);
        if (++_consecutiveErrors > MaxConsecutiveErrors)
        {
            throw new MediaException(
                $"El decodificador de {Stream.Codec} falló {MaxConsecutiveErrors} veces seguidas; el último error fue: {Av.ErrorText(error)}.",
                error);
        }
    }

    // ---- Entrega ----

    private DecodedFrame Wrap(AVFrame* source, long frameIndex)
    {
        AVFrame* clone = ffmpeg.av_frame_clone(source);
        if (clone == null)
        {
            throw new MediaException("av_frame_clone no pudo reservar memoria para el cuadro.");
        }

        IDisposable payload;
        long sizeBytes;
        VideoColor color = VideoColor.Resolve(clone, _streamColorSpace, _streamColorRange);
        try
        {
            if ((AVPixelFormat)clone->format == AVPixelFormat.AV_PIX_FMT_D3D11 && _device is not null)
            {
                D3D11TexturePayload texture = D3D11TexturePayload.FromDecoderFrame(_device, clone, color);
                clone = null;
                payload = texture;
                sizeBytes = TextureBytes(texture);
            }
            else if (_device is not null && _uploader is not null && FrameUploader.CanUpload(_device, clone))
            {
                D3D11TexturePayload texture = _uploader.Upload(_device, clone, color);
                payload = texture;
                sizeBytes = TextureBytes(texture);
            }
            else
            {
                var cpu = new CpuFramePayload(clone, color);
                clone = null;
                payload = cpu;
                AVPixFmtDescriptor* desc = ffmpeg.av_pix_fmt_desc_get((AVPixelFormat)source->format);
                int bytes = desc != null && desc->comp[0].depth > 8 ? 2 : 1;
                sizeBytes = (long)cpu.Width * cpu.Height * bytes * 3 / 2;
            }
        }
        finally
        {
            if (clone != null)
            {
                ffmpeg.av_frame_free(&clone);
            }
        }

        Time pts = Time.FromFrames(frameIndex, FrameRate);
        Time next = Time.FromFrames(frameIndex + 1, FrameRate);
        return new DecodedFrame(frameIndex, pts, next - pts, payload, sizeBytes);
    }

    private static long TextureBytes(D3D11TexturePayload texture) =>
        (long)texture.CodedWidth * texture.CodedHeight * (texture.PixelFormat == FramePixelFormat.P010 ? 2 : 1) * 3 / 2;
}

/// <summary>Contadores acumulados de un <see cref="VideoDecoder"/>.</summary>
/// <param name="Seeks">Búsquedas de keyframe (las peticiones secuenciales no cuentan).</param>
/// <param name="DecodedFrames">Cuadros que salieron del decodificador, incluidos los descartados.</param>
/// <param name="DecodeErrors">Paquetes o cuadros dañados que se saltaron.</param>
public readonly record struct VideoDecoderStatistics(long Seeks, long DecodedFrames, long DecodeErrors);
