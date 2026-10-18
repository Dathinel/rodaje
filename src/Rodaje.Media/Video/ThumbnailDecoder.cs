using FFmpeg.AutoGen.Abstractions;
using Rodaje.Core.Timing;
using Rodaje.Media.Interop;
using Rodaje.Media.Probe;

namespace Rodaje.Media.Video;

/// <summary>Imagen BGRA de 8 bits, fila tras fila sin relleno (<c>Width * 4</c> bytes por fila), alfa opaco.</summary>
public sealed record BgraImage(int Width, int Height, byte[] Pixels)
{
    public int SizeBytes => Pixels.Length;
}

/// <summary>
/// Miniaturas de un video: el cuadro más cercano a un instante (el último que no pasa de él), reducido en la CPU a BGRA
/// del alto pedido y con la rotación de pantalla aplicada.
/// </summary>
/// <remarks>
/// <para>
/// No es exacto al cuadro a propósito: decodifica en la CPU sin filtro de desbloqueo y saltándose los cuadros que nadie
/// usa como referencia, así una miniatura de 1080p sale del keyframe anterior en pocos milisegundos y no gasta la memoria
/// de video que necesita la reproducción (D-019). Si el siguiente instante pedido queda poco más adelante, sigue
/// decodificando sin buscar: una tira de miniaturas en orden recorre el archivo una sola vez.
/// </para>
/// <para>No es seguro entre hilos: un decodificador por hilo de trabajo.</para>
/// </remarks>
public sealed unsafe class ThumbnailDecoder : IDisposable
{
    private const int MaxFramesPerRequest = 900;
    private const double ContinueWindowSeconds = 4;

    private readonly int _streamIndex;
    private readonly AVRational _timeBase;
    private readonly long _originTs;
    private readonly long _continueWindowTs;
    private readonly int _rotation;
    private AVFormatContext* _format;
    private AVCodecContext* _codec;
    private AVPacket* _packet;
    private AVFrame* _decoded;
    private AVFrame* _best;
    private AVFrame* _ahead;
    private AVFrame* _scaled;
    private SwsContext* _sws;
    private long _bestPts;
    private long _aheadPts;
    private bool _hasBest;
    private bool _hasAhead;
    private bool _positioned;
    private bool _inputEnded;
    private bool _drained;

    private ThumbnailDecoder(AVFormatContext* format, string path)
    {
        _format = format;
        try
        {
            Info = MediaProbe.Probe(format, path, scanTimestamps: false);
            Stream = Info.Video ?? throw new MediaException($"El archivo {path} no tiene un flujo de video.");
            _streamIndex = Stream.StreamIndex;
            for (int i = 0; i < format->nb_streams; i++)
            {
                format->streams[i]->discard = i == _streamIndex ? AVDiscard.AVDISCARD_DEFAULT : AVDiscard.AVDISCARD_ALL;
            }

            AVStream* stream = format->streams[_streamIndex];
            _timeBase = stream->time_base;
            _originTs = Av.FromTime(Stream.StartTime, _timeBase);
            _continueWindowTs = Av.FromTime(Time.FromSeconds(ContinueWindowSeconds), _timeBase);
            _rotation = ((Stream.Rotation % 360) + 360) % 360;
            OpenCodec(stream);
            _packet = ffmpeg.av_packet_alloc();
            _decoded = ffmpeg.av_frame_alloc();
            _best = ffmpeg.av_frame_alloc();
            _ahead = ffmpeg.av_frame_alloc();
            _scaled = ffmpeg.av_frame_alloc();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public MediaInfo Info { get; }

    public VideoStreamInfo Stream { get; }

    /// <summary>Un solo cuadro (una imagen): sirve para cualquier instante.</summary>
    public bool IsStill => Stream.FrameCount <= 1;

    /// <summary>Cuántas veces se buscó un keyframe (para medir).</summary>
    public long Seeks { get; private set; }

    /// <summary>Cuadros decodificados en total (para medir).</summary>
    public long DecodedFrames { get; private set; }

    /// <exception cref="FfmpegNotFoundException">No se pudo cargar FFmpeg.</exception>
    /// <exception cref="MediaException">El archivo no se puede abrir o no tiene video.</exception>
    public static ThumbnailDecoder Open(string path) => new(Av.OpenInput(path), path);

    /// <summary>Ancho de la miniatura de alto <paramref name="height"/>, con la proporción de pantalla del video.</summary>
    public int WidthFor(int height) => ThumbnailSize.WidthFor(Stream.DisplayWidth, Stream.DisplayHeight, height);

    /// <summary>
    /// Miniatura del cuadro que se ve en <paramref name="sourceTime"/> (tiempo del origen: 0 es el primer cuadro), de
    /// <paramref name="height"/> píxeles de alto; <c>null</c> si el archivo no tiene ningún cuadro decodificable.
    /// </summary>
    public BgraImage? Decode(Time sourceTime, int height, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_format == null, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, 2160);
        long target = _originTs + Av.FromTime(sourceTime < Time.Zero ? Time.Zero : sourceTime, _timeBase);
        if (IsStill && _hasBest)
        {
            return Convert(_best, height);
        }

        if (_positioned && _hasBest && _bestPts <= target && (!_hasAhead || _aheadPts > target) && (_hasAhead || _drained))
        {
            return Convert(_best, height);
        }

        long position = _hasAhead ? _aheadPts : _hasBest ? _bestPts : long.MinValue;
        bool continueForward = _positioned && position != long.MinValue && target >= position && target - position <= _continueWindowTs;
        if (!continueForward)
        {
            Seek(target);
        }

        PromoteAhead(target);
        int decoded = 0;
        while (!_hasAhead && !_drained && decoded < MaxFramesPerRequest)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!NextFrame())
            {
                break;
            }

            decoded++;
            long pts = _decoded->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE ? _decoded->best_effort_timestamp : _decoded->pts;
            if (pts == ffmpeg.AV_NOPTS_VALUE)
            {
                pts = _hasBest ? _bestPts + 1 : target;
            }

            if (pts <= target || IsStill)
            {
                Keep(_best, ref _hasBest, ref _bestPts, pts);
            }
            else
            {
                Keep(_ahead, ref _hasAhead, ref _aheadPts, pts);
            }
        }

        // Si la búsqueda cayó después del instante (un GOP abierto, o antes del primer cuadro), sirve el primero que hay.
        AVFrame* source = _hasBest ? _best : _hasAhead ? _ahead : null;
        return source == null ? null : Convert(source, height);
    }

    public void Dispose()
    {
        SwsContext* sws = _sws;
        if (sws != null)
        {
            ffmpeg.sws_free_context(&sws);
            _sws = null;
        }

        foreach (nint frame in new[] { (nint)_decoded, (nint)_best, (nint)_ahead, (nint)_scaled })
        {
            if (frame != 0)
            {
                AVFrame* f = (AVFrame*)frame;
                ffmpeg.av_frame_free(&f);
            }
        }

        _decoded = _best = _ahead = _scaled = null;
        if (_packet != null)
        {
            AVPacket* packet = _packet;
            ffmpeg.av_packet_free(&packet);
            _packet = null;
        }

        if (_codec != null)
        {
            AVCodecContext* codec = _codec;
            ffmpeg.avcodec_free_context(&codec);
            _codec = null;
        }

        if (_format != null)
        {
            AVFormatContext* format = _format;
            ffmpeg.avformat_close_input(&format);
            _format = null;
        }
    }

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
        _codec->thread_count = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
        _codec->thread_type = ffmpeg.FF_THREAD_SLICE | ffmpeg.FF_THREAD_FRAME;
        _codec->skip_loop_filter = AVDiscard.AVDISCARD_ALL;
        _codec->skip_frame = AVDiscard.AVDISCARD_NONREF;
        _codec->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;
        Av.Check(ffmpeg.avcodec_open2(_codec, codec, null), "avcodec_open2");
    }

    private void Seek(long target)
    {
        Seeks++;
        ffmpeg.avcodec_flush_buffers(_codec);
        ffmpeg.av_frame_unref(_best);
        ffmpeg.av_frame_unref(_ahead);
        _hasBest = _hasAhead = false;
        _inputEnded = _drained = false;
        int r = ffmpeg.av_seek_frame(_format, _streamIndex, target, ffmpeg.AVSEEK_FLAG_BACKWARD);
        if (r < 0)
        {
            r = ffmpeg.avformat_seek_file(_format, _streamIndex, long.MinValue, target, target, 0);
        }

        if (r < 0)
        {
            ffmpeg.av_seek_frame(_format, _streamIndex, _originTs, ffmpeg.AVSEEK_FLAG_BACKWARD | ffmpeg.AVSEEK_FLAG_ANY);
        }

        _positioned = true;
    }

    /// <summary>El cuadro de más adelante que se guardó en la llamada anterior pasa a ser el mejor si ya no pasa del instante.</summary>
    private void PromoteAhead(long target)
    {
        if (_hasAhead && _aheadPts <= target)
        {
            ffmpeg.av_frame_unref(_best);
            ffmpeg.av_frame_move_ref(_best, _ahead);
            _bestPts = _aheadPts;
            _hasBest = true;
            _hasAhead = false;
        }
    }

    private void Keep(AVFrame* slot, ref bool has, ref long slotPts, long pts)
    {
        ffmpeg.av_frame_unref(slot);
        ffmpeg.av_frame_move_ref(slot, _decoded);
        has = true;
        slotPts = pts;
    }

    /// <summary>Deja el siguiente cuadro en <see cref="_decoded"/>; <c>false</c> al final del archivo.</summary>
    private bool NextFrame()
    {
        int errors = 0;
        while (true)
        {
            int r = ffmpeg.avcodec_receive_frame(_codec, _decoded);
            if (r == 0)
            {
                DecodedFrames++;
                return true;
            }

            if (r == ffmpeg.AVERROR_EOF)
            {
                _drained = true;
                return false;
            }

            if (r != Av.ErrorAgain && ++errors > 32)
            {
                return false;
            }

            if (_inputEnded)
            {
                _drained = true;
                return false;
            }

            r = ffmpeg.av_read_frame(_format, _packet);
            if (r < 0)
            {
                _inputEnded = true;
                ffmpeg.avcodec_send_packet(_codec, null);
                continue;
            }

            if (_packet->stream_index == _streamIndex)
            {
                ffmpeg.avcodec_send_packet(_codec, _packet);
            }

            ffmpeg.av_packet_unref(_packet);
        }
    }

    private BgraImage Convert(AVFrame* source, int height)
    {
        int width = WidthFor(height);
        bool sideways = _rotation is 90 or 270;
        int scaledWidth = sideways ? height : width;
        int scaledHeight = sideways ? width : height;
        if (source->colorspace == AVColorSpace.AVCOL_SPC_UNSPECIFIED)
        {
            source->colorspace = Stream.Height >= 720 ? AVColorSpace.AVCOL_SPC_BT709 : AVColorSpace.AVCOL_SPC_SMPTE170M;
        }

        if (source->color_range == AVColorRange.AVCOL_RANGE_UNSPECIFIED)
        {
            source->color_range = AVColorRange.AVCOL_RANGE_MPEG;
        }

        if (_sws == null)
        {
            _sws = ffmpeg.sws_alloc_context();
            if (_sws == null)
            {
                throw new MediaException("No se pudo crear el escalador de miniaturas.");
            }

            _sws->flags = (uint)SwsFlags.SWS_AREA;
            _sws->threads = 1;
        }

        ffmpeg.av_frame_unref(_scaled);
        _scaled->width = scaledWidth;
        _scaled->height = scaledHeight;
        _scaled->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
        _scaled->color_range = AVColorRange.AVCOL_RANGE_JPEG;
        Av.Check(ffmpeg.av_frame_get_buffer(_scaled, 0), "av_frame_get_buffer");
        Av.Check(ffmpeg.sws_scale_frame(_sws, _scaled, source), "sws_scale_frame");

        byte[] pixels = new byte[width * height * 4];
        int stride = _scaled->linesize[0];
        byte* data = _scaled->data[0];
        fixed (byte* target = pixels)
        {
            uint* dst = (uint*)target;
            for (int y = 0; y < scaledHeight; y++)
            {
                uint* row = (uint*)(data + ((long)y * stride));
                for (int x = 0; x < scaledWidth; x++)
                {
                    (int tx, int ty) = _rotation switch
                    {
                        90 => (scaledHeight - 1 - y, x),
                        180 => (scaledWidth - 1 - x, scaledHeight - 1 - y),
                        270 => (y, scaledWidth - 1 - x),
                        _ => (x, y),
                    };
                    dst[(ty * width) + tx] = IsStill ? row[x] : (row[x] | 0xFF000000u);
                }
            }
        }

        ffmpeg.av_frame_unref(_scaled);
        return new BgraImage(width, height, pixels);
    }
}

/// <summary>Tamaños de miniatura.</summary>
public static class ThumbnailSize
{
    /// <summary>Ancho con la proporción de pantalla (entre 1/4 y 4 veces el alto).</summary>
    public static int WidthFor(int displayWidth, int displayHeight, int height)
    {
        if (displayWidth <= 0 || displayHeight <= 0)
        {
            return height * 16 / 9;
        }

        double width = (double)height * displayWidth / displayHeight;
        return (int)Math.Clamp(Math.Round(width), Math.Max(1, height / 4), height * 4);
    }
}
