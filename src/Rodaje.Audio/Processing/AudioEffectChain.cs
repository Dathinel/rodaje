namespace Rodaje.Audio.Processing;

/// <summary>
/// Ecualizador paramétrico de cinco bandas: corte de graves (pasa altos, 0 = apagado), estante de graves, dos campanas
/// (medios y presencia) y estante de agudos. Compara por valor.
/// </summary>
public sealed record EqualizerSettings(
    double LowCutHz,
    double LowGainDb,
    double LowHz,
    double MidGainDb,
    double MidHz,
    double MidQ,
    double PresenceGainDb,
    double PresenceHz,
    double PresenceQ,
    double HighGainDb,
    double HighHz)
{
    /// <summary>No cambia nada: sin corte y con todas las ganancias en 0 dB.</summary>
    public bool IsIdentity => LowCutHz <= 0 && LowGainDb == 0 && MidGainDb == 0 && PresenceGainDb == 0 && HighGainDb == 0;

    public const int BandCount = 5;

    /// <summary>La banda <paramref name="index"/> (corte, graves, medios, presencia, agudos); nula si está apagada.</summary>
    public Biquad? Band(int index, double sampleRate) => index switch
    {
        0 when LowCutHz > 0 => Biquad.HighPass(sampleRate, LowCutHz),
        1 when LowGainDb != 0 => Biquad.LowShelf(sampleRate, LowHz, LowGainDb),
        2 when MidGainDb != 0 => Biquad.Peaking(sampleRate, MidHz, MidQ, MidGainDb),
        3 when PresenceGainDb != 0 => Biquad.Peaking(sampleRate, PresenceHz, PresenceQ, PresenceGainDb),
        4 when HighGainDb != 0 => Biquad.HighShelf(sampleRate, HighHz, HighGainDb),
        _ => null,
    };

    /// <summary>Respuesta del ecualizador entero en <paramref name="hz"/>, en dB.</summary>
    public double MagnitudeDb(double sampleRate, double hz)
    {
        double sum = 0;
        for (int i = 0; i < BandCount; i++)
        {
            sum += Band(i, sampleRate)?.MagnitudeDb(sampleRate, hz) ?? 0;
        }

        return sum;
    }
}

/// <summary>
/// Compresor de picos con rodilla suave (Giannoulis, Massberg y Reiss, JAES 2012): detecta el mayor de los dos canales
/// (estéreo enlazado), calcula la reducción en dB y la suaviza con ataque y relajación por separado.
/// </summary>
public sealed record CompressorSettings(double ThresholdDb, double Ratio, double AttackMs, double ReleaseMs, double MakeupDb, double KneeDb)
{
    /// <summary>Nivel de salida en dB para una entrada estable de <paramref name="inputDb"/>, sin la compensación.</summary>
    public double StaticCurve(double inputDb)
    {
        double over = inputDb - ThresholdDb;
        double ratio = Math.Max(1, Ratio);
        if (2 * over < -KneeDb)
        {
            return inputDb;
        }

        if (KneeDb > 0 && 2 * Math.Abs(over) <= KneeDb)
        {
            double t = over + (KneeDb / 2);
            return inputDb + ((1 / ratio) - 1) * t * t / (2 * KneeDb);
        }

        return ThresholdDb + (over / ratio);
    }
}

/// <summary>Lo que se aplica a un clip o a una pista: ecualizador y luego compresor; cualquiera puede faltar.</summary>
public sealed record AudioEffectsSettings(EqualizerSettings? Equalizer, CompressorSettings? Compressor)
{
    public bool IsEmpty => Equalizer is null && Compressor is null;
}

/// <summary>
/// Ecualizador y compresor de un clip o de una pista sobre estéreo intercalado. El mismo código suena en la vista previa y
/// se escribe en la exportación, así que la salida es la misma muestra a muestra para la misma entrada y el mismo estado.
/// </summary>
/// <remarks>
/// Todo el estado (retardos de los filtros y la envolvente del compresor) está en <see cref="State"/>, para guardarlo y
/// restaurarlo cuando la mezcla vuelve atrás tras una edición. <see cref="Configure"/> cambia los coeficientes sin tocar
/// el estado: mover un control mientras suena no reinicia los filtros.
/// </remarks>
public sealed class AudioEffectChain
{
    private const int Channels = 2;
    private const int StateLength = (EqualizerSettings.BandCount * Channels * 2) + 1;

    private readonly double _sampleRate;
    private readonly Biquad?[] _bands = new Biquad?[EqualizerSettings.BandCount];
    private CompressorSettings? _compressor;
    private double _attack;
    private double _release;
    private double _makeup;
    private float _makeupGain = 1;
    private double _kneeStart;

    public AudioEffectChain(AudioEffectsSettings settings, double sampleRate = 48_000)
    {
        _sampleRate = sampleRate;
        Configure(settings);
    }

    public AudioEffectsSettings Settings { get; private set; } = null!;

    /// <summary>Retardos de las bandas (dos por canal) y, al final, la reducción suavizada del compresor en dB.</summary>
    public double[] State { get; } = new double[StateLength];

    public static int StateSize => StateLength;

    public void Configure(AudioEffectsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Equals(Settings))
        {
            return;
        }

        Settings = settings;
        for (int b = 0; b < _bands.Length; b++)
        {
            _bands[b] = settings.Equalizer?.Band(b, _sampleRate);
            if (_bands[b] is null)
            {
                Array.Clear(State, b * Channels * 2, Channels * 2);
            }
        }

        if (settings.Compressor is null)
        {
            State[^1] = 0;
        }

        _compressor = settings.Compressor;
        if (_compressor is { } c)
        {
            _attack = Coefficient(c.AttackMs);
            _release = Coefficient(c.ReleaseMs);
            _makeup = c.MakeupDb;
            _makeupGain = (float)Math.Pow(10, c.MakeupDb / 20);
            _kneeStart = Math.Max(1e-9, Math.Pow(10, (c.ThresholdDb - (c.KneeDb / 2)) / 20));
        }
    }

    public void Reset() => Array.Clear(State);

    public void Process(Span<float> interleaved)
    {
        for (int b = 0; b < _bands.Length; b++)
        {
            for (int c = 0; _bands[b] is not null && c < Channels; c++)
            {
                _bands[b]!.Value.Process(interleaved, c, Channels, State.AsSpan(((b * Channels) + c) * 2, 2));
            }
        }

        if (_compressor is { } settings)
        {
            Compress(interleaved, settings);
        }
    }

    private void Compress(Span<float> interleaved, CompressorSettings settings)
    {
        const double DbPerNeper = 8.685889638065035;
        double envelope = State[^1];
        for (int i = 0; i + 1 < interleaved.Length; i += Channels)
        {
            double peak = Math.Max(Math.Abs(interleaved[i]), Math.Abs(interleaved[i + 1]));
            double reduction = 0;

            // Por debajo de la rodilla la curva es la identidad: sin logaritmo.
            if (peak > _kneeStart)
            {
                double level = DbPerNeper * Math.Log(peak);
                reduction = level - settings.StaticCurve(level);
            }

            double k = reduction > envelope ? _attack : _release;
            envelope = (k * envelope) + ((1 - k) * reduction);
            if (envelope < 1e-9)
            {
                envelope = 0;
            }

            float gain = envelope == 0 ? _makeupGain : (float)Math.Exp((_makeup - envelope) / DbPerNeper);
            interleaved[i] *= gain;
            interleaved[i + 1] *= gain;
        }

        State[^1] = envelope;
    }

    private double Coefficient(double milliseconds) => Math.Exp(-1 / (Math.Max(0.01, milliseconds) / 1000 * _sampleRate));
}
