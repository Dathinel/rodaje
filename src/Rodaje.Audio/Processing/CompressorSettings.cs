namespace Rodaje.Audio.Processing;

/// <summary>Qué mide el detector del compresor.</summary>
public enum LevelDetector
{
    /// <summary>El valor absoluto de cada muestra.</summary>
    Peak,

    /// <summary>La media de los cuadrados en una ventana exponencial (<see cref="CompressorSettings.RmsWindowMs"/>).</summary>
    Rms,
}

/// <summary>
/// Tres bandas con cruces Linkwitz-Riley de 4.º orden (la suma es plana): cada una se comprime aparte con el umbral
/// general más su desplazamiento, y sale con su ganancia.
/// </summary>
public sealed record MultibandSettings(
    double LowHz,
    double HighHz,
    double LowThresholdDb = 0,
    double MidThresholdDb = 0,
    double HighThresholdDb = 0,
    double LowGainDb = 0,
    double MidGainDb = 0,
    double HighGainDb = 0)
{
    public double ThresholdOffset(int band) => band switch { 0 => LowThresholdDb, 1 => MidThresholdDb, _ => HighThresholdDb };

    public double GainDb(int band) => band switch { 0 => LowGainDb, 1 => MidGainDb, _ => HighGainDb };
}

/// <summary>
/// Compresor de rodilla suave (Giannoulis, Massberg y Reiss, JAES 2012). Compara por valor: lo que no cambia el sonido
/// en vivo (escucha del sidechain, comparar A/B) va aparte, en <see cref="AudioEffectChain"/>. La relación desde
/// <see cref="InfiniteRatio"/> es ∞:1 (limitador).
/// </summary>
public sealed record CompressorSettings(double ThresholdDb, double Ratio, double AttackMs, double ReleaseMs, double MakeupDb, double KneeDb)
{
    public const double InfiniteRatio = 100;

    public const double MaxLookaheadMs = 10;

    public LevelDetector Detector { get; init; }

    public double RmsWindowMs { get; init; } = 10;

    /// <summary>Relajación que depende del programa: rápida tras un golpe corto, lenta tras una compresión sostenida.</summary>
    public bool AutoRelease { get; init; }

    /// <summary>Suma la mitad de la reducción que la curva da a 0 dBFS (sigue al umbral y la relación).</summary>
    public bool AutoMakeup { get; init; }

    /// <summary>Anticipación del detector; el audio se retrasa lo mismo y la mezcla lo compensa en la línea de tiempo.</summary>
    public double LookaheadMs { get; init; }

    /// <summary>Una sola reducción para los dos canales (la del más fuerte) o una por canal.</summary>
    public bool Linked { get; init; } = true;

    /// <summary>Mezcla en paralelo: 0 deja la señal original, 1 solo la comprimida.</summary>
    public double Mix { get; init; } = 1;

    /// <summary>Pasa altos de 12 dB por octava en el detector (0, apagado): los graves no disparan la compresión.</summary>
    public double SidechainHighPassHz { get; init; }

    public MultibandSettings? Multiband { get; init; }

    /// <summary>Techo del limitador de pico verdadero al final, en dBTP; nulo, sin limitador.</summary>
    public double? CeilingDb { get; init; }

    public int Bands => Multiband is null ? 1 : 3;

    /// <summary>Nivel de salida en dB para una entrada estable de <paramref name="inputDb"/>, sin la compensación.</summary>
    public double StaticCurve(double inputDb) => Curve(inputDb, ThresholdDb, Ratio, KneeDb);

    public static double Curve(double inputDb, double thresholdDb, double ratio, double kneeDb)
    {
        double slope = Slope(ratio);
        double over = inputDb - thresholdDb;
        if (2 * over < -kneeDb)
        {
            return inputDb;
        }

        if (kneeDb > 0 && 2 * Math.Abs(over) <= kneeDb)
        {
            double t = over + (kneeDb / 2);
            return inputDb + ((slope - 1) * t * t / (2 * kneeDb));
        }

        return thresholdDb + (over * slope);
    }

    /// <summary>Pendiente por encima del umbral: 1/relación, 0 en ∞:1.</summary>
    public static double Slope(double ratio) => ratio >= InfiniteRatio ? 0 : 1 / Math.Max(1, ratio);

    /// <summary>La compensación automática: la mitad de lo que la curva quita a 0 dBFS.</summary>
    public static double AutoMakeupDb(double thresholdDb, double ratio) => Math.Max(0, -thresholdDb * (1 - Slope(ratio)) / 2);

    public int LookaheadFrames(double sampleRate) => (int)Math.Round(Math.Clamp(LookaheadMs, 0, MaxLookaheadMs) * sampleRate / 1000);

    /// <summary>Muestras que el compresor retrasa el audio: la anticipación más la del limitador.</summary>
    public int LatencyFrames(double sampleRate) => LookaheadFrames(sampleRate) + (CeilingDb is null ? 0 : TruePeakLimiter.Latency);
}
