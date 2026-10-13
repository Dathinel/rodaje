namespace Rodaje.Audio.Processing;

/// <summary>Los ajustes de un efecto de la cadena de inserciones. Comparan por valor: un cambio rehace la mezcla.</summary>
public abstract record AudioProcessorSettings
{
    /// <summary>Muestras que el efecto retrasa el audio; la mezcla le da la entrada adelantada eso mismo.</summary>
    public virtual int LatencyFrames(double sampleRate) => 0;

    internal abstract IAudioStage CreateStage(double sampleRate);
}

/// <summary>Un efecto de la cadena con el identificador de su instancia en el proyecto (para el A/B y para conservar su estado al reordenar).</summary>
public sealed record AudioInsert(string Id, AudioProcessorSettings Settings);

/// <summary>
/// Lo que se aplica a un clip, a una pista, a un bus o a la mezcla: las inserciones en el orden de la pila. El
/// sidechain externo lo escucha el primer compresor.
/// </summary>
public sealed record AudioEffectsSettings
{
    /// <summary>Ecualizador y luego compresor; cualquiera puede faltar.</summary>
    public AudioEffectsSettings(EqualizerSettings? equalizer, CompressorSettings? compressor)
        : this(Pair(equalizer, compressor))
    {
    }

    public AudioEffectsSettings(IReadOnlyList<AudioInsert> inserts)
    {
        ArgumentNullException.ThrowIfNull(inserts);
        Inserts = inserts;
    }

    public IReadOnlyList<AudioInsert> Inserts { get; }

    public bool IsEmpty => Inserts.Count == 0;

    /// <summary>El primer ecualizador paramétrico de la cadena.</summary>
    public EqualizerSettings? Equalizer => First<EqualizerSettings>();

    /// <summary>El primer compresor: el que escucha el sidechain, se mira en Dinámica y lleva los keyframes.</summary>
    public CompressorSettings? Compressor => First<CompressorSettings>();

    /// <summary>Pista cuya mezcla dispara el primer compresor (sidechain externo); nula, lo dispara el propio audio.</summary>
    public string? SidechainTrackId { get; init; }

    /// <summary>Muestras que la cadena retrasa el audio: la suma de sus inserciones.</summary>
    public int LatencyFrames(double sampleRate)
    {
        int total = 0;
        foreach (AudioInsert insert in Inserts)
        {
            total += insert.Settings.LatencyFrames(sampleRate);
        }

        return total;
    }

    public bool Equals(AudioEffectsSettings? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null || SidechainTrackId != other.SidechainTrackId || Inserts.Count != other.Inserts.Count)
        {
            return false;
        }

        for (int i = 0; i < Inserts.Count; i++)
        {
            if (!Inserts[i].Equals(other.Inserts[i]))
            {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode() => HashCode.Combine(Inserts.Count, SidechainTrackId);

    private T? First<T>()
        where T : AudioProcessorSettings
    {
        foreach (AudioInsert insert in Inserts)
        {
            if (insert.Settings is T found)
            {
                return found;
            }
        }

        return null;
    }

    private static AudioInsert[] Pair(EqualizerSettings? equalizer, CompressorSettings? compressor)
    {
        var inserts = new List<AudioInsert>(2);
        if (equalizer is not null)
        {
            inserts.Add(new AudioInsert("eq", equalizer));
        }

        if (compressor is not null)
        {
            inserts.Add(new AudioInsert("compressor", compressor));
        }

        return [.. inserts];
    }
}

/// <summary>Lo que hizo un efecto de dinámica en la última lectura, para los medidores del panel.</summary>
/// <param name="InputPeak">Pico de la entrada del efecto (después de lo anterior de la cadena), lineal.</param>
/// <param name="OutputPeak">Pico de la salida, lineal.</param>
/// <param name="ReductionDb">Mayor reducción de ganancia (compresor, puerta o de-esser), en dB (positiva).</param>
/// <param name="LimiterReductionDb">Mayor reducción del limitador, en dB.</param>
public readonly record struct DynamicsReading(float InputPeak, float OutputPeak, float ReductionDb, float LimiterReductionDb);

/// <summary>
/// Una etapa de la cadena. Todo lo que decide su salida y cabe en poco va en su estado (<see cref="SaveState"/>), que la
/// mezcla guarda al empezar cada lectura y restaura al volver atrás tras una edición. Las líneas de retardo largas no
/// se copian: se indexan por la cuenta de muestras procesadas (que sí va en el estado) y guardan lo bastante para que
/// volver a un estado de hasta <see cref="SampleRing.Retention"/> atrás las encuentre intactas.
/// </summary>
internal interface IAudioStage
{
    int Latency { get; }

    int StateLength { get; }

    DynamicsReading Reading { get; }

    void SaveState(Span<double> into);

    void LoadState(ReadOnlySpan<double> from);

    /// <summary>Cambia los ajustes sin tocar el estado (mismo tipo de efecto).</summary>
    void Configure(AudioProcessorSettings settings);

    void Reset();

    /// <summary>Procesa estéreo intercalado en el sitio; <paramref name="key"/> solo lo usa el compresor.</summary>
    void Process(Span<float> interleaved, ReadOnlySpan<float> key);
}
