using System.Buffers.Binary;

namespace Rodaje.App.Services.Media;

/// <summary>
/// Picos de la forma de onda de un archivo: mínimo y máximo (de todos los canales) por tramo de
/// <see cref="BaseSamplesPerPeak"/> muestras, más niveles reducidos de 4 en 4 para los zooms lejanos. Mientras se
/// calcula crece por el final (<see cref="Append"/>, un solo escritor) y se puede consultar a la vez desde otro hilo: lo
/// que ya se publicó no cambia.
/// </summary>
public sealed class WaveformPeaks
{
    /// <summary>256 muestras a 48 kHz: 5,3 ms por pico, más fino de lo que una pista muestra incluso con mucho zoom.</summary>
    public const int BaseSamplesPerPeak = 256;
    public const int LevelFactor = 4;
    public const int LevelCount = 5;

    private const uint Magic = 0x4F4A4452; // "RDJO"
    private const ushort FormatVersion = 1;
    private const int HeaderBytes = 32;

    private short[] _min;
    private short[] _max;
    private int _count;
    private short[][]? _levelMin;
    private short[][]? _levelMax;
    private volatile bool _complete;

    // Acumulador del pico en curso (solo el escritor).
    private float _pendingMin = float.MaxValue;
    private float _pendingMax = float.MinValue;
    private int _pendingSamples;
    private long _samples;

    public WaveformPeaks(int sampleRate, long expectedSamples = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        SampleRate = sampleRate;
        int capacity = (int)Math.Clamp((expectedSamples / BaseSamplesPerPeak) + 2, 64, int.MaxValue / 2);
        _min = new short[capacity];
        _max = new short[capacity];
    }

    public int SampleRate { get; }

    /// <summary>Picos de base ya publicados.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Muestras (por canal) leídas.</summary>
    public long Samples => Interlocked.Read(ref _samples);

    public bool IsComplete => _complete;

    /// <summary>Suma muestras intercaladas de <paramref name="channels"/> canales.</summary>
    public void Append(ReadOnlySpan<float> interleaved, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        if (_complete)
        {
            throw new InvalidOperationException("Los picos ya se cerraron.");
        }

        int frames = interleaved.Length / channels;
        for (int f = 0; f < frames; f++)
        {
            ReadOnlySpan<float> frame = interleaved.Slice(f * channels, channels);
            foreach (float s in frame)
            {
                if (s < _pendingMin)
                {
                    _pendingMin = s;
                }

                if (s > _pendingMax)
                {
                    _pendingMax = s;
                }
            }

            if (++_pendingSamples == BaseSamplesPerPeak)
            {
                Flush();
            }
        }

        Interlocked.Add(ref _samples, frames);
    }

    /// <summary>Cierra el último pico parcial y arma los niveles reducidos.</summary>
    public void Complete()
    {
        if (_complete)
        {
            return;
        }

        if (_pendingSamples > 0)
        {
            Flush();
        }

        BuildLevels();
        _complete = true;
    }

    /// <summary>
    /// Mínimo y máximo de <paramref name="buckets"/> tramos iguales entre las muestras <paramref name="startSample"/> y
    /// <paramref name="endSample"/> (por canal). Usa el nivel más grueso que todavía da al menos un pico por tramo.
    /// </summary>
    public (float[] Min, float[] Max) Query(double startSample, double endSample, int buckets)
    {
        buckets = Math.Max(0, buckets);
        float[] min = new float[buckets];
        float[] max = new float[buckets];
        if (buckets == 0 || !(endSample > startSample))
        {
            return (min, max);
        }

        double perBucket = (endSample - startSample) / buckets;
        int level = 0;
        int levelSamples = BaseSamplesPerPeak;
        if (_complete)
        {
            while (level + 1 < LevelCount && levelSamples * LevelFactor <= perBucket)
            {
                level++;
                levelSamples *= LevelFactor;
            }
        }

        // Primero la cuenta y después los arreglos: los arreglos leídos después tienen al menos esa cantidad publicada.
        int published = Count;
        short[] lmin = level == 0 ? _min : _levelMin![level];
        short[] lmax = level == 0 ? _max : _levelMax![level];
        int available = level == 0 ? published : lmin.Length;
        for (int b = 0; b < buckets; b++)
        {
            double s0 = startSample + (b * perBucket);
            double s1 = s0 + perBucket;
            long i0 = (long)Math.Floor(s0 / levelSamples);
            long i1 = Math.Max(i0, (long)Math.Ceiling(s1 / levelSamples) - 1);
            i0 = Math.Max(0, i0);
            i1 = Math.Min(available - 1, i1);
            if (i0 > i1)
            {
                continue;
            }

            short lo = short.MaxValue;
            short hi = short.MinValue;
            for (long i = i0; i <= i1; i++)
            {
                lo = Math.Min(lo, lmin[i]);
                hi = Math.Max(hi, lmax[i]);
            }

            min[b] = lo / 32767f;
            max[b] = hi / 32767f;
        }

        return (min, max);
    }

    /// <summary>Muestras por pico de un nivel (0 es el de base).</summary>
    public static int SamplesPerPeak(int level) => BaseSamplesPerPeak << (2 * Math.Clamp(level, 0, LevelCount - 1));

    /// <summary>Valor absoluto máximo de cada pico de un nivel, entre 0 y 1. Solo con los picos completos.</summary>
    public float[] Envelope(int level)
    {
        if (!_complete)
        {
            throw new InvalidOperationException("Los picos todavía se están calculando.");
        }

        level = Math.Clamp(level, 0, LevelCount - 1);
        short[] lmin = _levelMin![level];
        short[] lmax = _levelMax![level];
        int count = level == 0 ? Count : lmin.Length;
        float[] envelope = new float[count];
        for (int i = 0; i < count; i++)
        {
            envelope[i] = Math.Min(1f, Math.Max(-lmin[i], (int)lmax[i]) / 32767f);
        }

        return envelope;
    }

    public byte[] Serialize()
    {
        if (!_complete)
        {
            throw new InvalidOperationException("Solo se guardan picos completos.");
        }

        int count = Count;
        byte[] data = new byte[HeaderBytes + (count * 4)];
        BinaryPrimitives.WriteUInt32LittleEndian(data, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), BaseSamplesPerPeak);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16), Samples);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24), count);
        for (int i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(HeaderBytes + (i * 4)), _min[i]);
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(HeaderBytes + (i * 4) + 2), _max[i]);
        }

        return data;
    }

    /// <summary>Lee lo que escribió <see cref="Serialize"/>; <c>null</c> si está dañado o es de otra versión.</summary>
    public static WaveformPeaks? Deserialize(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderBytes || BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic
            || BinaryPrimitives.ReadUInt16LittleEndian(data[4..]) != FormatVersion
            || BinaryPrimitives.ReadInt32LittleEndian(data[12..]) != BaseSamplesPerPeak)
        {
            return null;
        }

        int rate = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
        long samples = BinaryPrimitives.ReadInt64LittleEndian(data[16..]);
        int count = BinaryPrimitives.ReadInt32LittleEndian(data[24..]);
        if (rate <= 0 || count < 0 || samples < 0 || data.Length != HeaderBytes + ((long)count * 4))
        {
            return null;
        }

        var peaks = new WaveformPeaks(rate, (long)count * BaseSamplesPerPeak);
        for (int i = 0; i < count; i++)
        {
            peaks._min[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(HeaderBytes + (i * 4))..]);
            peaks._max[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(HeaderBytes + (i * 4) + 2)..]);
        }

        peaks._count = count;
        peaks._samples = samples;
        peaks.BuildLevels();
        peaks._complete = true;
        return peaks;
    }

    private static short ToShort(float v) => (short)Math.Round(Math.Clamp(v, -1f, 1f) * 32767f);

    private void Flush()
    {
        int index = _count;
        if (index == _min.Length)
        {
            // Crece en copias nuevas: quien esté leyendo sigue con las anteriores, que no cambian.
            int size = checked(_min.Length * 2);
            short[] min = new short[size];
            short[] max = new short[size];
            Array.Copy(_min, min, index);
            Array.Copy(_max, max, index);
            _min = min;
            _max = max;
        }

        _min[index] = ToShort(_pendingMin);
        _max[index] = ToShort(_pendingMax);
        Volatile.Write(ref _count, index + 1);
        _pendingMin = float.MaxValue;
        _pendingMax = float.MinValue;
        _pendingSamples = 0;
    }

    private void BuildLevels()
    {
        int count = Count;
        var levelMin = new short[LevelCount][];
        var levelMax = new short[LevelCount][];
        levelMin[0] = _min;
        levelMax[0] = _max;
        int previous = count;
        for (int level = 1; level < LevelCount; level++)
        {
            int size = (previous + LevelFactor - 1) / LevelFactor;
            short[] lmin = new short[size];
            short[] lmax = new short[size];
            short[] pmin = levelMin[level - 1];
            short[] pmax = levelMax[level - 1];
            for (int i = 0; i < size; i++)
            {
                short lo = short.MaxValue;
                short hi = short.MinValue;
                for (int j = i * LevelFactor; j < Math.Min(previous, (i + 1) * LevelFactor); j++)
                {
                    lo = Math.Min(lo, pmin[j]);
                    hi = Math.Max(hi, pmax[j]);
                }

                lmin[i] = lo;
                lmax[i] = hi;
            }

            levelMin[level] = lmin;
            levelMax[level] = lmax;
            previous = size;
        }

        _levelMin = levelMin;
        _levelMax = levelMax;
    }
}
