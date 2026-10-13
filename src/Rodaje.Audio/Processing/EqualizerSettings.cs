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
    double HighHz) : AudioProcessorSettings
{
    /// <summary>No cambia nada: sin corte y con todas las ganancias en 0 dB.</summary>
    public bool IsIdentity => LowCutHz <= 0 && LowGainDb == 0 && MidGainDb == 0 && PresenceGainDb == 0 && HighGainDb == 0;

    public const int BandCount = 5;

    internal override IAudioStage CreateStage(double sampleRate) => new EqualizerCompressorStage(this, null, sampleRate);

    internal override EqualizerSettings Limited() => new(
        Limit(LowCutHz, 0, 500, 0),
        Limit(LowGainDb, -MaxGainDb, MaxGainDb, 0),
        Limit(LowHz, 20, 1000, 120),
        Limit(MidGainDb, -MaxGainDb, MaxGainDb, 0),
        Limit(MidHz, 100, 8000, 500),
        Limit(MidQ, 0.1, 10, 1),
        Limit(PresenceGainDb, -MaxGainDb, MaxGainDb, 0),
        Limit(PresenceHz, 500, 16000, 3000),
        Limit(PresenceQ, 0.1, 10, 1),
        Limit(HighGainDb, -MaxGainDb, MaxGainDb, 0),
        Limit(HighHz, 1000, 20000, 8000));

    /// <summary>Lo más que sube o baja una banda, en dB.</summary>
    public const double MaxGainDb = 18;

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

