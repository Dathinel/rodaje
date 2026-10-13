using Rodaje.Audio.Processing;
using Rodaje.Core.Timing;

namespace Rodaje.Audio;

/// <summary>Llena el buffer del dispositivo: lee la fuente, rellena con silencio, aplica el volumen y adapta los canales.</summary>
/// <remarks>
/// Lo usa un solo hilo (el de audio). Guarda los últimos cuadros producidos para poder repetir en un
/// dispositivo nuevo lo que quedó en cola en el que se perdió. Los contadores se pueden leer desde otros hilos.
/// </remarks>
internal sealed class FrameRenderer
{
    private const int ChunkFrames = 1024;

    private readonly IAudioSampleSource _source;
    private readonly float[] _chunk = new float[ChunkFrames * 2];
    private readonly float[] _history;
    private readonly int _historyFrames;
    private readonly int _rampFrames;

    private long _written;
    private long _replayCursor = -1;
    private float _gain;
    private float _targetGain;
    private float _rampTarget = float.NaN;
    private float _rampStep;
    private long _underruns;
    private long _underrunFrames;
    private long _sourceErrors;

    public FrameRenderer(IAudioSampleSource source, int historyFrames, int rampFrames, float volume)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(historyFrames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rampFrames);
        _source = source;
        _historyFrames = historyFrames;
        _history = new float[historyFrames * 2];
        _rampFrames = rampFrames;
        _gain = _targetGain = ClampVolume(volume);
    }

    /// <summary>Cuadros producidos desde el principio, contando silencio de relleno y descartes.</summary>
    public long Written => Volatile.Read(ref _written);

    public long Underruns => Volatile.Read(ref _underruns);

    public long UnderrunFrames => Volatile.Read(ref _underrunFrames);

    public long SourceErrors => Volatile.Read(ref _sourceErrors);

    public bool IsReplaying => _replayCursor >= 0;

    public float Volume
    {
        get => Volatile.Read(ref _targetGain);
        set => Volatile.Write(ref _targetGain, ClampVolume(value));
    }

    /// <summary>Escribe <paramref name="frames"/> cuadros en <paramref name="destination"/> con <paramref name="channels"/> canales.</summary>
    /// <returns>Cuadros nuevos sacados de la fuente; los repetidos del historial no cuentan.</returns>
    public int Render(Span<float> destination, int frames, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, frames * channels, nameof(destination));

        int produced = 0;
        int done = 0;
        while (done < frames)
        {
            int n = Math.Min(frames - done, ChunkFrames);
            Span<float> chunk = _chunk.AsSpan(0, n * 2);
            if (_replayCursor >= 0)
            {
                n = (int)Math.Min(n, _written - _replayCursor);
                chunk = chunk[..(n * 2)];
                CopyFromHistory(_replayCursor, chunk);
                _replayCursor += n;
                if (_replayCursor >= _written)
                {
                    _replayCursor = -1;
                }
            }
            else
            {
                Pull(chunk, n);
                produced += n;
            }

            ApplyGain(chunk, n);
            ChannelMapper.FromStereo(chunk, destination.Slice(done * channels, n * channels), n, channels);
            done += n;
        }

        return produced;
    }

    /// <summary>Saca <paramref name="frames"/> cuadros de la fuente sin sonarlos, para que siga al reloj sin dispositivo.</summary>
    public void Discard(long frames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frames);
        _replayCursor = -1;
        while (frames > 0)
        {
            int n = (int)Math.Min(frames, ChunkFrames);
            Pull(_chunk.AsSpan(0, n * 2), n);
            frames -= n;
        }
    }

    /// <summary>Prepara la repetición de lo producido desde el cuadro global <paramref name="from"/>.</summary>
    /// <returns>Cuadro global desde el que se repetirá de verdad: el historial tiene un tamaño fijo.</returns>
    public long BeginReplay(long from)
    {
        long start = Math.Clamp(from, Math.Max(0, _written - _historyFrames), _written);
        _replayCursor = start < _written ? start : -1;
        return start;
    }

    public void CancelReplay() => _replayCursor = -1;

    public void Seek(Time position)
    {
        _replayCursor = -1;
        try
        {
            _source.Seek(position);
        }
#pragma warning disable CA1031 // Una excepción de la fuente no puede tumbar el hilo de audio: se cuenta y se sigue.
        catch (Exception)
#pragma warning restore CA1031
        {
            Interlocked.Increment(ref _sourceErrors);
        }
    }

    private void Pull(Span<float> chunk, int frames)
    {
        int read;
        try
        {
            read = _source.Read(chunk);
            if (read < 0 || read > frames)
            {
                Interlocked.Increment(ref _sourceErrors);
                read = 0;
            }
        }
#pragma warning disable CA1031 // Ver Seek.
        catch (Exception)
#pragma warning restore CA1031
        {
            Interlocked.Increment(ref _sourceErrors);
            read = 0;
        }

        // Lo que no es un número entra como silencio, como en la cadena de inserciones: ni el dispositivo ni el historial
        // que se repite al cambiar de dispositivo lo reciben, y en mono la suma de dos muestras enormes no se desborda.
        AudioSamples.Sanitize(chunk[..(read * 2)]);
        if (read < frames)
        {
            chunk[(read * 2)..].Clear();
            Interlocked.Increment(ref _underruns);
            Interlocked.Add(ref _underrunFrames, frames - read);
        }

        CopyToHistory(_written, chunk[..(frames * 2)]);
        Volatile.Write(ref _written, _written + frames);
    }

    private void CopyToHistory(long globalFrame, ReadOnlySpan<float> stereo)
    {
        int offset = (int)(globalFrame % _historyFrames) * 2;
        int first = Math.Min(stereo.Length, _history.Length - offset);
        stereo[..first].CopyTo(_history.AsSpan(offset));
        stereo[first..].CopyTo(_history);
    }

    private void CopyFromHistory(long globalFrame, Span<float> stereo)
    {
        int offset = (int)(globalFrame % _historyFrames) * 2;
        int first = Math.Min(stereo.Length, _history.Length - offset);
        _history.AsSpan(offset, first).CopyTo(stereo);
        _history.AsSpan(0, stereo.Length - first).CopyTo(stereo[first..]);
    }

    private void ApplyGain(Span<float> stereo, int frames)
    {
        float target = Volatile.Read(ref _targetGain);
        float gain = _gain;
        if (gain == target)
        {
            if (gain != 1f)
            {
                for (int i = 0; i < stereo.Length; i++)
                {
                    stereo[i] *= gain;
                }
            }

            return;
        }

        // El paso se fija al cambiar el destino, no en cada llamada: la rampa dura lo mismo y tiene la misma forma sea
        // cual sea el tamaño de los bloques que pide el dispositivo.
        if (target != _rampTarget)
        {
            _rampTarget = target;
            _rampStep = (target - gain) / _rampFrames;
        }

        float step = _rampStep;
        for (int f = 0; f < frames; f++)
        {
            gain = MathF.Abs(target - gain) <= MathF.Abs(step) ? target : gain + step;
            stereo[f * 2] *= gain;
            stereo[(f * 2) + 1] *= gain;
        }

        _gain = gain;
    }

    private static float ClampVolume(float volume) =>
        float.IsNaN(volume) ? throw new ArgumentOutOfRangeException(nameof(volume), "El volumen no puede ser NaN.") : Math.Clamp(volume, 0f, 1f);
}

/// <summary>Adapta estéreo a los canales del dispositivo.</summary>
internal static class ChannelMapper
{
    /// <remarks>
    /// Mono: promedio de L y R, para no saturar. Tres o más canales: L y R van a los dos primeros, que en
    /// las máscaras de Windows son siempre frontal izquierdo y frontal derecho; el resto queda en silencio.
    /// </remarks>
    public static void FromStereo(ReadOnlySpan<float> stereo, Span<float> destination, int frames, int channels)
    {
        switch (channels)
        {
            case 2:
                stereo[..(frames * 2)].CopyTo(destination);
                break;
            case 1:
                for (int f = 0; f < frames; f++)
                {
                    destination[f] = (stereo[f * 2] + stereo[(f * 2) + 1]) * 0.5f;
                }

                break;
            default:
                destination[..(frames * channels)].Clear();
                for (int f = 0; f < frames; f++)
                {
                    destination[f * channels] = stereo[f * 2];
                    destination[(f * channels) + 1] = stereo[(f * 2) + 1];
                }

                break;
        }
    }
}
