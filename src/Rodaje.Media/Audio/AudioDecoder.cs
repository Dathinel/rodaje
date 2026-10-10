using FFmpeg.AutoGen.Abstractions;
using Rodaje.Core.Timing;
using Rodaje.Media.Interop;
using Rodaje.Media.Probe;

namespace Rodaje.Media.Audio;

/// <summary>Decodifica el audio de un archivo a float32 estéreo intercalado a 48 kHz, con seek exacto a la muestra.</summary>
/// <remarks>
/// <para>
/// La posición 0 es el origen del medio: el primer cuadro de video si el archivo tiene video (así el audio
/// queda alineado con la numeración de <c>VideoDecoder</c>), o la primera muestra de audio si no. El
/// audio anterior al origen se descarta y, si el audio empieza después, se rellena con silencio.
/// </para>
/// <para>
/// <see cref="Seek"/> retrocede un margen de pre-roll (el códec necesita el bloque anterior para
/// reconstruir el primero), decodifica y descarta hasta la muestra pedida. No es seguro entre hilos.
/// </para>
/// <para>
/// La salida sigue a las marcas de tiempo también a mitad del archivo: si un cuadro llega con un hueco o un
/// solape respecto del anterior, el hueco suena como silencio y lo solapado se descarta.
/// </para>
/// </remarks>
public sealed unsafe class AudioDecoder : IDisposable
{
    public const int OutputSampleRate = 48_000;
    public const int OutputChannels = 2;

    private const int MaxConsecutiveErrors = 64;
    private const int MaxSeekRetries = 8;

    private readonly int _streamIndex;
    private readonly AVRational _timeBase;
    private readonly long _originTs;
    private readonly long _streamStartTs;
    private readonly long _prerollTs;
    private AVFormatContext* _format;
    private AVCodecContext* _codec;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private SwrContext* _resampler;
    private AVChannelLayout _resamplerLayout;
    private int _resamplerRate;
    private int _resamplerFormat = -1;

    private float[] _fifo = new float[OutputSampleRate * OutputChannels / 4];
    private int _fifoOffset;
    private int _fifoLength;
    private float[] _scratch = new float[8192 * OutputChannels];

    private long _position;
    private long _nextAppend;
    private bool _syncPending = true;
    private bool _seekVerify;
    private bool _frameHeld;
    private long _expectedInput;
    private int _expectedRate;
    private bool _inputEnded;
    private bool _finished;
    private int _consecutiveErrors;
    private long _seekTarget;
    private long _seekTs;
    private int _seekRetries;
    private long _decodeErrors;

    private AudioDecoder(AVFormatContext* format, string path, AudioDecoderOptions options)
    {
        _format = format;
        try
        {
            Info = MediaProbe.Probe(format, path, scanTimestamps: false);
            Stream = Info.Audio ?? throw new MediaException($"El archivo {path} no tiene un flujo de audio.");
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
            Origin = options.AlignToVideo && Info.Video is { } video ? video.StartTime : Stream.StartTime;
            _originTs = Av.FromTime(Origin, _timeBase);
            _streamStartTs = Av.FromTime(Stream.StartTime, _timeBase);
            Duration = Stream.StartTime + Stream.Duration - Origin;

            AVCodec* codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
            if (codec == null)
            {
                throw new MediaException($"FFmpeg no tiene decodificador para {Stream.Codec}.");
            }

            _codec = ffmpeg.avcodec_alloc_context3(codec);
            Av.Check(ffmpeg.avcodec_parameters_to_context(_codec, stream->codecpar), "avcodec_parameters_to_context");
            _codec->pkt_timebase = _timeBase;
            int r = ffmpeg.avcodec_open2(_codec, codec, null);
            if (r < 0)
            {
                throw new MediaException($"No se pudo abrir el decodificador de {Stream.Codec}: {Av.ErrorText(r)}.", r);
            }

            int sampleRate = Math.Max(1, stream->codecpar->sample_rate);
            long prerollSamples = Math.Max(stream->codecpar->seek_preroll, 0) + (sampleRate / 10);
            _prerollTs = Math.Max(1, Av.FromTime(Time.FromSamples(prerollSamples, sampleRate), _timeBase));
            _packet = ffmpeg.av_packet_alloc();
            _frame = ffmpeg.av_frame_alloc();
            Seek(Time.Zero);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public MediaInfo Info { get; }

    public AudioStreamInfo Stream { get; }

    /// <summary>Instante del contenedor que corresponde a la posición 0.</summary>
    public Time Origin { get; }

    /// <summary>Hasta dónde llega el audio, medido desde el origen.</summary>
    public Time Duration { get; }

    /// <summary>Posición, en cuadros de muestra a 48 kHz, de lo que devolverá el próximo <see cref="Read"/>.</summary>
    public long PositionFrames => _position;

    public Time Position => Time.FromSamples(_position, OutputSampleRate);

    /// <summary>Paquetes o cuadros dañados que se saltaron.</summary>
    public long DecodeErrors => _decodeErrors;

    /// <exception cref="FfmpegNotFoundException">No se pudo cargar FFmpeg.</exception>
    /// <exception cref="MediaException">El archivo no se puede abrir o no tiene audio decodificable.</exception>
    public static AudioDecoder Open(string path, AudioDecoderOptions? options = null) =>
        new(Av.OpenInput(path), path, options ?? new AudioDecoderOptions());

    /// <summary>Llena <paramref name="interleaved"/> (L, R, L, R…) y devuelve cuántos cuadros de muestra escribió.</summary>
    /// <remarks>Devuelve menos de los pedidos solo al final del audio, y 0 cuando ya no queda nada.</remarks>
    public int Read(Span<float> interleaved)
    {
        ObjectDisposedException.ThrowIf(_format == null, this);
        if (interleaved.Length % OutputChannels != 0)
        {
            throw new ArgumentException("El búfer debe tener un número par de muestras (estéreo intercalado).", nameof(interleaved));
        }

        int wanted = interleaved.Length / OutputChannels;
        int written = 0;
        while (written < wanted)
        {
            if (_fifoLength == 0)
            {
                if (!DecodeMore())
                {
                    break;
                }

                continue;
            }

            long fifoPosition = _nextAppend - (_fifoLength / OutputChannels);
            if (fifoPosition < _position)
            {
                int drop = (int)Math.Min(_fifoLength / OutputChannels, _position - fifoPosition);
                Consume(drop);
                continue;
            }

            int count;
            if (fifoPosition > _position)
            {
                count = (int)Math.Min(wanted - written, fifoPosition - _position);
                interleaved.Slice(written * OutputChannels, count * OutputChannels).Clear();
            }
            else
            {
                count = Math.Min(wanted - written, _fifoLength / OutputChannels);
                _fifo.AsSpan(_fifoOffset, count * OutputChannels).CopyTo(interleaved[(written * OutputChannels)..]);
                Consume(count);
            }

            written += count;
            _position += count;
        }

        return written;
    }

    /// <summary>Lleva la lectura a <paramref name="position"/>, contada desde el origen y exacta a la muestra de 48 kHz.</summary>
    public void Seek(Time position)
    {
        ObjectDisposedException.ThrowIf(_format == null, this);
        _seekTarget = Math.Max(0, position.ToSamples(OutputSampleRate));
        _seekRetries = 0;
        SeekTo(_originTs + Av.FromTime(Time.FromSamples(_seekTarget, OutputSampleRate), _timeBase) - _prerollTs);
    }

    public void Dispose()
    {
        if (_format == null)
        {
            return;
        }

        AVFrame* frame = _frame;
        ffmpeg.av_frame_free(&frame);
        _frame = null;
        AVPacket* packet = _packet;
        ffmpeg.av_packet_free(&packet);
        _packet = null;
        SwrContext* resampler = _resampler;
        ffmpeg.swr_free(&resampler);
        _resampler = null;
        fixed (AVChannelLayout* layout = &_resamplerLayout)
        {
            ffmpeg.av_channel_layout_uninit(layout);
        }

        AVCodecContext* codec = _codec;
        ffmpeg.avcodec_free_context(&codec);
        _codec = null;
        AVFormatContext* format = _format;
        ffmpeg.avformat_close_input(&format);
        _format = null;
    }

    private void SeekTo(long timestamp)
    {
        _seekTs = timestamp;
        int r = ffmpeg.av_seek_frame(_format, _streamIndex, timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD);
        if (r < 0)
        {
            r = ffmpeg.avformat_seek_file(_format, _streamIndex, long.MinValue, timestamp, long.MaxValue, 0);
        }

        if (r < 0)
        {
            throw new MediaException($"No se pudo buscar en el audio: {Av.ErrorText(r)}.", r);
        }

        ffmpeg.avcodec_flush_buffers(_codec);
        ffmpeg.av_packet_unref(_packet);
        ffmpeg.av_frame_unref(_frame);
        if (_resampler != null)
        {
            // Reiniciar descarta las muestras retenidas por el filtro del remuestreador.
            ffmpeg.swr_close(_resampler);
            Av.Check(ffmpeg.swr_init(_resampler), "swr_init");
        }

        _fifoOffset = _fifoLength = 0;
        _position = _seekTarget;
        _nextAppend = _seekTarget;
        _syncPending = true;
        _seekVerify = true;
        _frameHeld = false;
        _expectedRate = 0;
        _inputEnded = _finished = false;
        _consecutiveErrors = 0;
    }

    private void Consume(int frames)
    {
        _fifoOffset += frames * OutputChannels;
        _fifoLength -= frames * OutputChannels;
        if (_fifoLength == 0)
        {
            _fifoOffset = 0;
        }
    }

    /// <summary>Muestra de entrada (contada desde el origen) en la que empieza un cuadro con esa marca.</summary>
    /// <remarks>
    /// Si la base de tiempo es más gruesa que una muestra (Matroska usa milisegundos), la marca solo ubica el
    /// cuadro con ±media unidad; entonces se ajusta a la rejilla de cuadros del códec, que en audio es de
    /// tamaño fijo desde el inicio del flujo. Con códecs de tamaño de cuadro variable ese ajuste no aplica.
    /// </remarks>
    private long InputSampleIndex(long timestamp, int inputRate, int frameSamples)
    {
        long index = (long)Av.RoundDiv((Int128)(timestamp - _originTs) * _timeBase.num * inputRate, _timeBase.den);
        int grid = _codec->frame_size > 0 ? _codec->frame_size : frameSamples;
        if ((long)_timeBase.num * inputRate > _timeBase.den && grid > 0)
        {
            // Tras un hueco la rejilla ya no parte del inicio del flujo: solo se ajusta si el ajuste cabe en el
            // redondeo de la marca.
            long start = (long)Av.RoundDiv((Int128)(_streamStartTs - _originTs) * _timeBase.num * inputRate, _timeBase.den);
            long snapped = start + ((long)Av.RoundDiv(index - start, grid) * grid);
            if (Math.Abs(snapped - index) <= ((long)_timeBase.num * inputRate / _timeBase.den / 2) + 1)
            {
                index = snapped;
            }
        }

        return index;
    }

    /// <summary>La marca del cuadro se aparta de donde termina el anterior, o cambió la tasa.</summary>
    /// <remarks>
    /// Se toleran 5 ms o dos redondeos de la base de tiempo (lo que sea mayor): menos que un cuadro de audio, para
    /// que un salto no deje un corrimiento permanente de hasta un cuadro, y más que el baile de marcas redondeadas.
    /// </remarks>
    private bool IsDiscontinuity(long timestamp)
    {
        if (_expectedRate == 0)
        {
            return false;
        }

        if (_frame->sample_rate != _expectedRate)
        {
            return true;
        }

        long drift = InputSampleIndex(timestamp, _expectedRate, _frame->nb_samples) - _expectedInput;
        long rounding = ((long)_timeBase.num * _expectedRate / _timeBase.den) + 1;
        return Math.Abs(drift) > Math.Max(2 * rounding, _expectedRate / 200);
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }

    private void ResampleFrame(int skip)
    {
        if (skip == 0)
        {
            Resample(_frame->extended_data, _frame->nb_samples);
            return;
        }

        var format = (AVSampleFormat)_frame->format;
        int bytes = ffmpeg.av_get_bytes_per_sample(format);
        int channels = _frame->ch_layout.nb_channels;
        bool planar = ffmpeg.av_sample_fmt_is_planar(format) != 0;
        int planes = planar ? channels : 1;
        byte** shifted = stackalloc byte*[planes];
        for (int p = 0; p < planes; p++)
        {
            shifted[p] = _frame->extended_data[p] + ((long)skip * bytes * (planar ? 1 : channels));
        }

        Resample(shifted, _frame->nb_samples - skip);
    }

    /// <summary>Agrega al FIFO lo que salga del siguiente cuadro; <c>false</c> cuando ya no hay más audio.</summary>
    private bool DecodeMore()
    {
        while (!_finished)
        {
            int r = _frameHeld ? 0 : ffmpeg.avcodec_receive_frame(_codec, _frame);
            _frameHeld = false;
            if (r == 0)
            {
                _consecutiveErrors = 0;
                long ts = _frame->pts != ffmpeg.AV_NOPTS_VALUE ? _frame->pts : _frame->best_effort_timestamp;
                bool timed = ts != ffmpeg.AV_NOPTS_VALUE && _frame->sample_rate > 0;
                if (!_syncPending && timed && IsDiscontinuity(ts))
                {
                    // Hueco o solape en las marcas: lo pendiente en el remuestreador sale con su posición y el
                    // cuadro se ubica en su marca como tras un seek. Read rellena el hueco con silencio o
                    // descarta lo solapado, porque la posición de cada muestra la da el FIFO.
                    if (_resampler != null)
                    {
                        Resample(null, 0);
                        ffmpeg.swr_close(_resampler);
                        Av.Check(ffmpeg.swr_init(_resampler), "swr_init");
                    }

                    _syncPending = true;
                    _seekVerify = false;
                    if (_fifoLength > 0)
                    {
                        _frameHeld = true;
                        return true;
                    }
                }

                int skip = 0;
                if (_syncPending && timed)
                {
                    int inputRate = _frame->sample_rate;
                    long inputIndex = InputSampleIndex(ts, inputRate, _frame->nb_samples);
                    // El índice del contenedor dejó el seek después del objetivo: se busca más atrás.
                    if (_seekVerify && inputIndex * OutputSampleRate > _seekTarget * inputRate && _seekTs > _streamStartTs && _seekRetries < MaxSeekRetries)
                    {
                        _seekRetries++;
                        SeekTo(_seekTs - (_prerollTs << _seekRetries));
                        continue;
                    }

                    // El remuestreador recién reiniciado pone su primera salida sobre la primera entrada. Para que
                    // la fase coincida con la de una lectura continua desde el origen, se empieza en una muestra
                    // de entrada que cae exactamente sobre una de salida (cada 147 a 44,1 kHz, cada una a 48 kHz).
                    long period = inputRate / (long)Gcd(inputRate, OutputSampleRate);
                    skip = (int)((((-inputIndex) % period) + period) % period);
                    if (skip >= _frame->nb_samples)
                    {
                        ffmpeg.av_frame_unref(_frame);
                        continue;
                    }

                    _nextAppend = (inputIndex + skip) * OutputSampleRate / inputRate;
                    _expectedInput = inputIndex;
                    _expectedRate = inputRate;
                }
                else if (_syncPending)
                {
                    _expectedRate = 0;
                }

                _syncPending = false;
                _seekVerify = false;
                _expectedInput += _frame->nb_samples;
                ResampleFrame(skip);
                ffmpeg.av_frame_unref(_frame);
                return true;
            }

            if (r == ffmpeg.AVERROR_EOF)
            {
                if (_resampler != null)
                {
                    Resample(null, 0);
                }

                _finished = true;
                return _fifoLength > 0;
            }

            if (r != Av.ErrorAgain)
            {
                RecordError(r);
                continue;
            }

            FeedDecoder();
        }

        return false;
    }

    private void FeedDecoder()
    {
        if (_inputEnded)
        {
            return;
        }

        while (true)
        {
            int read = ffmpeg.av_read_frame(_format, _packet);
            if (read == ffmpeg.AVERROR_EOF || (read < 0 && _consecutiveErrors >= MaxConsecutiveErrors))
            {
                ffmpeg.avcodec_send_packet(_codec, null);
                _inputEnded = true;
                return;
            }

            if (read < 0)
            {
                _decodeErrors++;
                _consecutiveErrors++;
                continue;
            }

            if (_packet->stream_index != _streamIndex)
            {
                ffmpeg.av_packet_unref(_packet);
                continue;
            }

            int sent = ffmpeg.avcodec_send_packet(_codec, _packet);
            ffmpeg.av_packet_unref(_packet);
            if (sent < 0 && sent != Av.ErrorAgain)
            {
                RecordError(sent);
                continue;
            }

            return;
        }
    }

    private void RecordError(int error)
    {
        _decodeErrors++;
        if (++_consecutiveErrors > MaxConsecutiveErrors)
        {
            throw new MediaException(
                $"El decodificador de {Stream.Codec} falló {MaxConsecutiveErrors} veces seguidas; el último error fue: {Av.ErrorText(error)}.",
                error);
        }
    }

    private void ConfigureResampler(AVFrame* frame)
    {
        AVChannelLayout input = frame->ch_layout;
        bool sameLayout;
        fixed (AVChannelLayout* current = &_resamplerLayout)
        {
            sameLayout = _resampler != null && ffmpeg.av_channel_layout_compare(current, &input) == 0;
        }

        if (sameLayout && frame->sample_rate == _resamplerRate && frame->format == _resamplerFormat)
        {
            return;
        }

        AVChannelLayout source;
        if (input.order == AVChannelOrder.AV_CHANNEL_ORDER_UNSPEC)
        {
            ffmpeg.av_channel_layout_default(&source, input.nb_channels);
        }
        else
        {
            Av.Check(ffmpeg.av_channel_layout_copy(&source, &input), "av_channel_layout_copy");
        }

        AVChannelLayout stereo;
        ffmpeg.av_channel_layout_default(&stereo, OutputChannels);
        SwrContext* resampler = _resampler;
        ffmpeg.swr_free(&resampler);
        int r = ffmpeg.swr_alloc_set_opts2(&resampler, &stereo, AVSampleFormat.AV_SAMPLE_FMT_FLT, OutputSampleRate,
            &source, (AVSampleFormat)frame->format, frame->sample_rate, 0, null);
        ffmpeg.av_channel_layout_uninit(&source);
        _resampler = resampler;
        Av.Check(r, "swr_alloc_set_opts2");
        Av.Check(ffmpeg.swr_init(_resampler), "swr_init");
        fixed (AVChannelLayout* current = &_resamplerLayout)
        {
            ffmpeg.av_channel_layout_uninit(current);
            Av.Check(ffmpeg.av_channel_layout_copy(current, &input), "av_channel_layout_copy");
        }

        _resamplerRate = frame->sample_rate;
        _resamplerFormat = frame->format;
    }

    /// <summary>Convierte y agrega al FIFO; con <paramref name="input"/> nulo vacía lo retenido por el filtro.</summary>
    private void Resample(byte** input, int samples)
    {
        if (input != null)
        {
            ConfigureResampler(_frame);
        }

        while (true)
        {
            int capacity = ffmpeg.swr_get_out_samples(_resampler, samples) + 64;
            if (_scratch.Length < capacity * OutputChannels)
            {
                _scratch = new float[capacity * OutputChannels];
            }

            int produced;
            fixed (float* scratch = _scratch)
            {
                byte* output = (byte*)scratch;
                produced = Av.Check(ffmpeg.swr_convert(_resampler, &output, capacity, input, samples), "swr_convert");
            }

            Append(_scratch.AsSpan(0, produced * OutputChannels));
            if (input != null || produced == 0)
            {
                return;
            }
        }
    }

    private void Append(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        if (_fifoOffset + _fifoLength + samples.Length > _fifo.Length)
        {
            if (_fifoLength + samples.Length > _fifo.Length)
            {
                var larger = new float[Math.Max(_fifo.Length * 2, _fifoLength + samples.Length)];
                _fifo.AsSpan(_fifoOffset, _fifoLength).CopyTo(larger);
                _fifo = larger;
            }
            else
            {
                _fifo.AsSpan(_fifoOffset, _fifoLength).CopyTo(_fifo);
            }

            _fifoOffset = 0;
        }

        samples.CopyTo(_fifo.AsSpan(_fifoOffset + _fifoLength));
        _fifoLength += samples.Length;
        _nextAppend += samples.Length / OutputChannels;
    }
}

/// <summary>Opciones de <see cref="AudioDecoder"/>.</summary>
public sealed record AudioDecoderOptions
{
    /// <summary>Si el archivo tiene video, la posición 0 es su primer cuadro (y no la primera muestra de audio).</summary>
    public bool AlignToVideo { get; init; } = true;
}
