using Rodaje.Audio.Processing;

namespace Rodaje.Audio.Metering;

/// <summary>
/// Lo que hizo un compresor por bloques, cada uno con la muestra de la línea de tiempo donde suena, para dibujar la
/// reducción de lo que se oye y no de lo que se está mezclando por delante.
/// </summary>
/// <remarks>Mismo protocolo que <see cref="AudioLevelRing"/>: un escritor, lectores en cualquier hilo, sin candados ni reservas.</remarks>
public sealed class DynamicsMeterRing
{
    private readonly Entry[] _entries;
    private long _count;

    public DynamicsMeterRing(int capacity = 2048)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 16);
        _entries = new Entry[capacity];
        for (int i = 0; i < capacity; i++)
        {
            _entries[i].Sequence = -1;
        }
    }

    /// <summary>Publica un bloque. Solo desde el hilo escritor.</summary>
    public void Write(long start, int frames, DynamicsReading reading)
    {
        long index = _count;
        ref Entry entry = ref _entries[index % _entries.Length];
        Interlocked.Exchange(ref entry.Sequence, -1);
        entry.Start = start;
        entry.Frames = frames;
        entry.Reading = reading;
        Volatile.Write(ref entry.Sequence, index);
        Volatile.Write(ref _count, index + 1);
    }

    /// <summary>Lo mayor de cada medida en lo que suena en <c>(<paramref name="from"/>, <paramref name="to"/>]</c>; nulo si no hay bloques ahí.</summary>
    public DynamicsReading? Measure(long from, long to)
    {
        if (to <= from)
        {
            return null;
        }

        long count = Volatile.Read(ref _count);
        long oldest = Math.Max(0, count - _entries.Length);
        long covered = long.MaxValue;
        float input = 0, output = 0, reduction = 0, limiter = 0;
        bool any = false;
        for (long index = count - 1; index >= oldest; index--)
        {
            ref Entry entry = ref _entries[index % _entries.Length];
            long before = Volatile.Read(ref entry.Sequence);
            long start = entry.Start;
            int frames = entry.Frames;
            DynamicsReading reading = entry.Reading;
            Interlocked.MemoryBarrier();
            if (before != index || Volatile.Read(ref entry.Sequence) != index)
            {
                break;
            }

            if (start >= covered)
            {
                continue;
            }

            long end = Math.Min(start + frames, covered);
            covered = start;
            if (start < to && end > from)
            {
                any = true;
                input = MathF.Max(input, reading.InputPeak);
                output = MathF.Max(output, reading.OutputPeak);
                reduction = MathF.Max(reduction, reading.ReductionDb);
                limiter = MathF.Max(limiter, reading.LimiterReductionDb);
            }

            if (start <= from)
            {
                break;
            }
        }

        return any ? new DynamicsReading(input, output, reduction, limiter) : null;
    }

    private struct Entry
    {
        public long Sequence;
        public long Start;
        public int Frames;
        public DynamicsReading Reading;
    }
}
