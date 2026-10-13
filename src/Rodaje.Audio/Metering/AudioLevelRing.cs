namespace Rodaje.Audio.Metering;

/// <summary>
/// Niveles por bloques de la mezcla, cada uno con la muestra de la línea de tiempo donde empieza, para leer los del
/// instante que <em>suena</em> y no los del que se está mezclando por delante.
/// </summary>
/// <remarks>
/// <para>
/// Un solo hilo escribe (<see cref="Write"/>, el de la mezcla) y cualquiera lee (<see cref="Measure"/>) sin candados
/// ni reservas de memoria. Cada entrada lleva un número de secuencia que el escritor invalida antes de tocarla y
/// publica después; el lector descarta la entrada si el número cambió mientras la copiaba.
/// </para>
/// <para>
/// Un bloque nuevo tapa a los anteriores que cubren las mismas muestras: tras una edición, la mezcla vuelve a escribir
/// el tramo que todavía no suena, y tras un seek empieza en otro lugar. Por eso el lector recorre de lo más nuevo a lo
/// más viejo y de cada bloque toma solo lo que queda antes del comienzo de los más nuevos que ya vio.
/// </para>
/// </remarks>
public sealed class AudioLevelRing
{
    public const int DefaultCapacity = 4096;

    private readonly Entry[] _entries;
    private long _count;

    public AudioLevelRing(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 16);
        _entries = new Entry[capacity];
        for (int i = 0; i < capacity; i++)
        {
            _entries[i].Sequence = -1;
        }
    }

    public int Capacity => _entries.Length;

    /// <summary>Bloques escritos desde el principio.</summary>
    public long Count => Volatile.Read(ref _count);

    /// <summary>
    /// Solo para las pruebas: <see cref="Measure"/> y <see cref="WeightedPower"/> lo llaman con el índice de cada entrada
    /// entre leer su número de secuencia y copiar el bloque, para que el escritor pase justo ahí sin depender del reloj.
    /// </summary>
    internal Action<long>? BeforeCopy { get; set; }

    /// <summary>Publica un bloque. Solo desde el hilo escritor.</summary>
    public void Write(in LevelBlock block)
    {
        long index = _count;
        ref Entry entry = ref _entries[index % _entries.Length];
        Interlocked.Exchange(ref entry.Sequence, -1);
        entry.Block = block;
        Volatile.Write(ref entry.Sequence, index);
        Volatile.Write(ref _count, index + 1);
    }

    /// <summary>
    /// Picos de lo que suena en <c>(<paramref name="from"/>, <paramref name="to"/>]</c> y RMS de
    /// <c>(<paramref name="rmsFrom"/>, <paramref name="to"/>]</c>, en muestras de la línea de tiempo.
    /// </summary>
    /// <remarks>
    /// Un bloque cuenta para el pico si se solapa con el tramo: lo que empieza a sonar dentro de un bloque se ve en
    /// cuanto la posición entra en él. El RMS pondera cada bloque por lo que se solapa con su tramo.
    /// </remarks>
    public AudioLevels Measure(long from, long to, long rmsFrom)
    {
        if (to <= from && to <= rmsFrom)
        {
            return AudioLevels.Silence;
        }

        long lowest = Math.Min(from, rmsFrom);
        long count = Volatile.Read(ref _count);
        long oldest = Math.Max(0, count - _entries.Length);
        long covered = long.MaxValue;
        float peakL = 0, peakR = 0, trueL = 0, trueR = 0;
        double squaresL = 0, squaresR = 0;
        long rmsFrames = 0;
        int peakFrames = 0;
        bool clipped = false;

        for (long index = count - 1; index >= oldest; index--)
        {
            ref Entry entry = ref _entries[index % _entries.Length];
            long before = Volatile.Read(ref entry.Sequence);
            BeforeCopy?.Invoke(index);
            LevelBlock block = entry.Block;
            Interlocked.MemoryBarrier();
            long after = Volatile.Read(ref entry.Sequence);
            if (before != index || after != index)
            {
                // El escritor ya pasó por aquí: lo que queda es más viejo todavía y también se está pisando.
                break;
            }

            if (block.Start >= covered)
            {
                continue;
            }

            // Si un bloque más nuevo tapa su final, cuenta solo la parte que sigue visible.
            long end = Math.Min(block.Start + block.Frames, covered);
            covered = block.Start;
            if (block.Start >= to)
            {
                continue;
            }

            if (end > from && to > from)
            {
                peakL = MathF.Max(peakL, block.PeakLeft);
                peakR = MathF.Max(peakR, block.PeakRight);
                trueL = MathF.Max(trueL, block.TruePeakLeft);
                trueR = MathF.Max(trueR, block.TruePeakRight);
                peakFrames += (int)Math.Min(int.MaxValue, Math.Min(end, to) - Math.Max(block.Start, from));
                clipped |= block.Clipped;
            }

            long overlap = Math.Min(end, to) - Math.Max(block.Start, rmsFrom);
            if (overlap > 0 && block.Frames > 0)
            {
                double weight = (double)overlap / block.Frames;
                squaresL += block.SquaresLeft * weight;
                squaresR += block.SquaresRight * weight;
                rmsFrames += overlap;
            }

            if (block.Start <= lowest)
            {
                break;
            }
        }

        float rmsL = rmsFrames > 0 ? (float)Math.Sqrt(squaresL / rmsFrames) : 0;
        float rmsR = rmsFrames > 0 ? (float)Math.Sqrt(squaresR / rmsFrames) : 0;
        return new AudioLevels(
            new ChannelLevel(peakL, MathF.Max(peakL, trueL), rmsL),
            new ChannelLevel(peakR, MathF.Max(peakR, trueR), rmsR),
            peakFrames,
            clipped);
    }

    /// <summary>
    /// Potencia ponderada K media (canales sumados, BS.1770) de lo que suena en <c>(<paramref name="from"/>,
    /// <paramref name="to"/>]</c>; 0 si no hay bloques. Con <see cref="Loudness.ToLufs"/> da la sonoridad momentánea
    /// (400 ms) o corta (3 s). Mismas reglas de lectura que <see cref="Measure"/>.
    /// </summary>
    public double WeightedPower(long from, long to)
    {
        if (to <= from)
        {
            return 0;
        }

        long count = Volatile.Read(ref _count);
        long oldest = Math.Max(0, count - _entries.Length);
        long covered = long.MaxValue;
        double sum = 0;
        long frames = 0;
        for (long index = count - 1; index >= oldest; index--)
        {
            ref Entry entry = ref _entries[index % _entries.Length];
            long before = Volatile.Read(ref entry.Sequence);
            BeforeCopy?.Invoke(index);
            LevelBlock block = entry.Block;
            Interlocked.MemoryBarrier();
            if (before != index || Volatile.Read(ref entry.Sequence) != index)
            {
                break;
            }

            if (block.Start >= covered)
            {
                continue;
            }

            long end = Math.Min(block.Start + block.Frames, covered);
            covered = block.Start;
            long overlap = Math.Min(end, to) - Math.Max(block.Start, from);
            if (overlap > 0 && block.Frames > 0)
            {
                sum += block.Weighted * overlap / block.Frames;
                frames += overlap;
            }

            if (block.Start <= from)
            {
                break;
            }
        }

        return frames > 0 ? sum / frames : 0;
    }

    private struct Entry
    {
        public long Sequence;
        public LevelBlock Block;
    }
}

/// <summary>Niveles de un bloque de la mezcla.</summary>
/// <param name="Start">Muestra de la línea de tiempo donde empieza.</param>
/// <param name="Frames">Cuadros que mide; menos que el bloque si un seek lo cortó.</param>
/// <param name="PeakLeft">Pico de muestras del canal izquierdo, lineal.</param>
/// <param name="PeakRight">Pico de muestras del canal derecho, lineal.</param>
/// <param name="TruePeakLeft">Pico entre muestras del canal izquierdo, lineal.</param>
/// <param name="TruePeakRight">Pico entre muestras del canal derecho, lineal.</param>
/// <param name="SquaresLeft">Suma de los cuadrados de las muestras (para el RMS de cualquier tramo de bloques).</param>
/// <param name="SquaresRight">Lo mismo del canal derecho.</param>
/// <param name="Clipped">La suma pasó de 0 dBFS antes del limitador en este bloque.</param>
/// <param name="Weighted">Suma de los cuadrados ponderados K de los dos canales (BS.1770), para la sonoridad.</param>
public readonly record struct LevelBlock(
    long Start,
    int Frames,
    float PeakLeft,
    float PeakRight,
    float TruePeakLeft,
    float TruePeakRight,
    double SquaresLeft,
    double SquaresRight,
    bool Clipped,
    double Weighted = 0);
