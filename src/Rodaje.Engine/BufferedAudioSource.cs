using Rodaje.Audio;
using Rodaje.Core.Timing;
using Rodaje.Media.Audio;

namespace Rodaje.Engine;

/// <summary>Decodifica el audio por delante en un hilo propio para que el hilo de audio solo copie memoria.</summary>
/// <remarks>
/// <para>
/// <see cref="Read"/> y <see cref="Seek"/> llegan desde el hilo de audio y solo toman el candado el tiempo de
/// una copia. Lo que falta se devuelve corto (la salida pone silencio), pero la posición de lectura avanza
/// igual: el audio que llegue tarde se descarta en vez de sonar desplazado, así nunca se pierde la
/// sincronía con el reloj, que cuenta también el silencio.
/// </para>
/// <para>Es dueño de <see cref="IPcmSource"/>: la usa solo su hilo y la libera al terminar.</para>
/// </remarks>
internal sealed class BufferedAudioSource : IAudioSampleSource, IDisposable
{
    private const int Channels = 2;
    private const int ChunkFrames = 1024;

    private readonly object _gate = new();
    private readonly IPcmSource _pcm;
    private readonly float[] _ring;
    private readonly int _capacityFrames;
    private readonly float[] _chunk = new float[ChunkFrames * Channels];
    private readonly Thread _thread;

    // Cuadros contados desde el destino del último seek, que es _origin.
    private Time _origin;
    private long _ringStart;
    private int _ringHead;
    private int _ringCount;
    private long _readFrame;

    private int _generation;
    private Time? _pendingSeek;
    private bool _ended;
    private bool _disposed;
    private long _underruns;
    private long _skippedFrames;
    private long _errors;

    public BufferedAudioSource(IPcmSource pcm, int capacityFrames)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityFrames, ChunkFrames * 2);
        _pcm = pcm;
        _capacityFrames = capacityFrames;
        _ring = new float[capacityFrames * Channels];
        _thread = new Thread(Produce) { IsBackground = true, Name = "Rodaje audio adelantado", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Lecturas que no encontraron todo el audio listo (sin contar el final del medio).</summary>
    public long Underruns => Interlocked.Read(ref _underruns);

    /// <summary>Cuadros que llegaron tarde y se descartaron para no desincronizarse.</summary>
    public long SkippedFrames => Interlocked.Read(ref _skippedFrames);

    /// <summary>Excepciones de la decodificación; cada una deja en silencio el resto hasta el siguiente seek.</summary>
    public long Errors => Interlocked.Read(ref _errors);

    internal int BufferedFrames
    {
        get
        {
            lock (_gate)
            {
                return _ringCount;
            }
        }
    }

    internal bool IsEnded
    {
        get
        {
            lock (_gate)
            {
                return _ended;
            }
        }
    }

    public int Read(Span<float> interleaved)
    {
        int wanted = interleaved.Length / Channels;
        lock (_gate)
        {
            DropBefore(_readFrame);
            int count = _ringStart == _readFrame ? Math.Min(wanted, _ringCount) : 0;
            CopyOut(interleaved[..(count * Channels)]);
            _readFrame += wanted;
            if (count < wanted && !_ended)
            {
                _underruns++;
            }

            Monitor.Pulse(_gate);
            return count;
        }
    }

    public void Seek(Time position)
    {
        lock (_gate)
        {
            _generation++;
            _pendingSeek = position;
            _origin = position;
            _ringStart = 0;
            _ringHead = 0;
            _ringCount = 0;
            _readFrame = 0;
            _ended = false;
            Monitor.Pulse(_gate);
        }
    }

    /// <summary>
    /// Descarta lo decodificado más allá de <paramref name="keepFrames"/> cuadros por delante de la lectura y vuelve a
    /// pedirlo a la fuente desde ahí, sin mover el reloj. Sirve cuando cambia lo que la fuente produce (una edición).
    /// </summary>
    /// <remarks>Se llama desde cualquier hilo; la fuente recibe el seek en el hilo de decodificación.</remarks>
    public void Rewind(int keepFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keepFrames);
        lock (_gate)
        {
            DropBefore(_readFrame);
            if (_ringCount == 0)
            {
                _ringStart = Math.Max(_ringStart, _readFrame);
            }

            _ringCount = Math.Min(_ringCount, Math.Min(keepFrames, _capacityFrames));
            _generation++;
            _pendingSeek = _origin + Time.FromSamples(_ringStart + _ringCount, AudioDecoder.OutputSampleRate);
            _ended = false;
            Monitor.Pulse(_gate);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Monitor.PulseAll(_gate);
        }

        _thread.Join();
        _pcm.Dispose();
    }

    private void Produce()
    {
        while (true)
        {
            int generation;
            Time? seek;
            lock (_gate)
            {
                while (!_disposed && _pendingSeek is null && (_ended || _ringCount + ChunkFrames > _capacityFrames))
                {
                    Monitor.Wait(_gate);
                }

                if (_disposed)
                {
                    return;
                }

                seek = _pendingSeek;
                _pendingSeek = null;
                generation = _generation;
            }

            int frames;
#pragma warning disable CA1031 // Un medio dañado no puede tumbar el hilo: se cuenta y el audio queda en silencio.
            try
            {
                if (seek is { } target)
                {
                    _pcm.Seek(target);
                }

                frames = _pcm.Read(_chunk);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _errors);
                frames = 0;
            }
#pragma warning restore CA1031

            lock (_gate)
            {
                if (generation != _generation)
                {
                    continue;
                }

                if (frames <= 0)
                {
                    _ended = true;
                    continue;
                }

                Append(_chunk.AsSpan(0, frames * Channels));
            }
        }
    }

    private void DropBefore(long frame)
    {
        long late = Math.Min(_ringCount, frame - _ringStart);
        if (late <= 0)
        {
            return;
        }

        _ringHead = (int)((_ringHead + late) % _capacityFrames);
        _ringCount -= (int)late;
        _ringStart += late;
        _skippedFrames += late;
    }

    private void CopyOut(Span<float> destination)
    {
        int frames = destination.Length / Channels;
        int first = Math.Min(frames, _capacityFrames - _ringHead);
        _ring.AsSpan(_ringHead * Channels, first * Channels).CopyTo(destination);
        _ring.AsSpan(0, (frames - first) * Channels).CopyTo(destination[(first * Channels)..]);
        _ringHead = (_ringHead + frames) % _capacityFrames;
        _ringCount -= frames;
        _ringStart += frames;
    }

    private void Append(ReadOnlySpan<float> samples)
    {
        int frames = samples.Length / Channels;
        int tail = (_ringHead + _ringCount) % _capacityFrames;
        int first = Math.Min(frames, _capacityFrames - tail);
        samples[..(first * Channels)].CopyTo(_ring.AsSpan(tail * Channels));
        samples[(first * Channels)..].CopyTo(_ring);
        _ringCount += frames;
    }
}
