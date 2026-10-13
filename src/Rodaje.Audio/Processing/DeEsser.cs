namespace Rodaje.Audio.Processing;

/// <summary>
/// De-esser: mide la energía por encima de <see cref="FrequencyHz"/> (pasa altos de 4.º orden) y, cuando pasa del umbral,
/// baja hasta <see cref="RangeDb"/> solo esa banda (o toda la señal si <see cref="SplitBand"/> es falso). Relación 6:1,
/// ataque de 0,5 ms y relajación de 60 ms.
/// </summary>
public sealed record DeEsserSettings(double FrequencyHz, double ThresholdDb, double RangeDb) : AudioProcessorSettings
{
    public const double Ratio = 6;

    /// <summary>Baja solo lo de encima de la frecuencia (la voz no se apaga); si no, toda la señal.</summary>
    public bool SplitBand { get; init; } = true;

    internal override IAudioStage CreateStage(double sampleRate) => new DeEsserStage(this, sampleRate);

    internal override DeEsserSettings Limited() =>
        new(Limit(FrequencyHz, 2000, 14000, 6000), Limit(ThresholdDb, -60, 0, -30), Limit(RangeDb, 0, 24, 8)) { SplitBand = SplitBand };
}

/// <summary>
/// La banda alta es <c>x − pasa bajos(x)</c> con un Butterworth de 2.º orden: la suma con la baja es la señal exacta, así
/// que sin reducción sale lo mismo que entra, bit a bit.
/// </summary>
internal sealed class DeEsserStage : IAudioStage
{
    private const double DbPerNeper = 8.685889638065035;

    // Estado: detector (dos pasa altos en cascada, 4), envolvente, pasa bajos de cada canal (4).
    private const int Detector = 0;
    private const int Envelope = 4;
    private const int Split = 5;

    private readonly double _sampleRate;
    private readonly double[] _state = new double[Split + 4];
    private readonly double _attack;
    private readonly double _release;
    private DeEsserSettings _settings = null!;
    private Biquad _highPass, _lowPass;
    private double _threshold;

    public DeEsserStage(DeEsserSettings settings, double sampleRate)
    {
        _sampleRate = sampleRate;
        _attack = Math.Exp(-1 / (0.0005 * sampleRate));
        _release = Math.Exp(-1 / (0.060 * sampleRate));
        Configure(settings);
    }

    public int Latency => 0;

    public int StateLength => _state.Length;

    public DynamicsReading Reading { get; private set; }

    public void SaveState(Span<double> into) => _state.CopyTo(into);

    public void LoadState(ReadOnlySpan<double> from) => from.CopyTo(_state);

    public void Configure(AudioProcessorSettings settings)
    {
        _settings = (DeEsserSettings)settings;
        double hz = Math.Clamp(_settings.FrequencyHz, 1000, _sampleRate * 0.45);
        _highPass = Biquad.HighPass(_sampleRate, hz);
        _lowPass = Biquad.LowPass(_sampleRate, hz);
        _threshold = Math.Pow(10, _settings.ThresholdDb / 20);
    }

    public void Reset() => Array.Clear(_state);

    public void Process(Span<float> interleaved, ReadOnlySpan<float> key)
    {
        double[] s = _state;
        double h1 = s[Detector], h2 = s[Detector + 1], h3 = s[Detector + 2], h4 = s[Detector + 3];
        double envelope = s[Envelope];
        double l1 = s[Split], l2 = s[Split + 1], r1 = s[Split + 2], r2 = s[Split + 3];
        double range = Math.Max(0, _settings.RangeDb);
        bool split = _settings.SplitBand;
        double slope = 1 - (1 / DeEsserSettings.Ratio);
        double inPeak = 0, outPeak = 0, most = 0;
        for (int i = 0; i + 1 < interleaved.Length; i += 2)
        {
            double l = interleaved[i], r = interleaved[i + 1];
            double mono = 0.5 * (l + r);
            double high = _highPass.Tick(_highPass.Tick(mono, ref h1, ref h2), ref h3, ref h4);
            double level = Math.Abs(high);
            envelope = (level > envelope ? _attack : _release) * (envelope - level) + level;
            double reduction = envelope > _threshold ? Math.Min(range, DbPerNeper * Math.Log(envelope / _threshold) * slope) : 0;
            double lowL = _lowPass.Tick(l, ref l1, ref l2);
            double lowR = _lowPass.Tick(r, ref r1, ref r2);
            if (reduction > 0)
            {
                double cut = 1 - Math.Exp(-reduction / DbPerNeper);
                interleaved[i] = (float)(split ? l - (cut * (l - lowL)) : l * (1 - cut));
                interleaved[i + 1] = (float)(split ? r - (cut * (r - lowR)) : r * (1 - cut));
            }

            inPeak = Math.Max(inPeak, Math.Max(Math.Abs(l), Math.Abs(r)));
            outPeak = Math.Max(outPeak, Math.Max(Math.Abs(interleaved[i]), Math.Abs(interleaved[i + 1])));
            most = Math.Max(most, reduction);
        }

        (s[Detector], s[Detector + 1], s[Detector + 2], s[Detector + 3]) = (h1, h2, h3, h4);
        s[Envelope] = envelope < 1e-30 ? 0 : envelope;
        (s[Split], s[Split + 1], s[Split + 2], s[Split + 3]) = (l1, l2, r1, r2);
        Reading = new DynamicsReading((float)inPeak, (float)outPeak, (float)most, 0);
    }
}
