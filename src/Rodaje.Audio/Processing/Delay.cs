namespace Rodaje.Audio.Processing;

/// <summary>Figura a la que se sincroniza el delay: negra = un pulso.</summary>
public enum DelayDivision
{
    Whole,
    Half,
    Quarter,
    Eighth,
    Sixteenth,
    DottedHalf,
    DottedQuarter,
    DottedEighth,
    QuarterTriplet,
    EighthTriplet,
}

/// <summary>
/// Delay estéreo: un tiempo en ms o sincronizado al tempo (<see cref="Sync"/>), realimentación con pasa altos y pasa
/// bajos en el lazo (cada repetición más oscura y más fina), ping-pong opcional y mezcla.
/// </summary>
/// <param name="TimeMs">Tiempo sin sincronía, en ms.</param>
/// <param name="Feedback">Cuánto vuelve de cada repetición (0 a 0,98).</param>
/// <param name="Mix">0 seco, 0,5 seco y eco a tope, 1 solo el eco.</param>
public sealed record DelaySettings(double TimeMs, double Feedback, double Mix) : AudioProcessorSettings
{
    public const double MaxTimeMs = 4000;

    public bool Sync { get; init; }

    public double Tempo { get; init; } = 120;

    public DelayDivision Division { get; init; } = DelayDivision.Eighth;

    /// <summary>Cada repetición cambia de lado: la primera a la izquierda.</summary>
    public bool PingPong { get; init; }

    /// <summary>Pasa altos del lazo; 20 Hz o menos, apagado.</summary>
    public double LowCutHz { get; init; } = 20;

    /// <summary>Pasa bajos del lazo; 20 kHz o más, apagado.</summary>
    public double HighCutHz { get; init; } = 20000;

    /// <summary>Pulsos de cada figura.</summary>
    public static double Beats(DelayDivision division) => division switch
    {
        DelayDivision.Whole => 4,
        DelayDivision.Half => 2,
        DelayDivision.Quarter => 1,
        DelayDivision.Eighth => 0.5,
        DelayDivision.Sixteenth => 0.25,
        DelayDivision.DottedHalf => 3,
        DelayDivision.DottedQuarter => 1.5,
        DelayDivision.DottedEighth => 0.75,
        DelayDivision.QuarterTriplet => 2.0 / 3,
        _ => 1.0 / 3,
    };

    /// <summary>El tiempo que suena: el fijo o el de la figura al tempo, dentro de 1 ms y <see cref="MaxTimeMs"/>.</summary>
    public double EffectiveMs => Math.Clamp(Sync ? 60000 / Math.Clamp(Tempo, 20, 300) * Beats(Division) : TimeMs, 1, MaxTimeMs);

    public int DelayFrames(double sampleRate) => Math.Max(1, (int)Math.Round(EffectiveMs * sampleRate / 1000));

    internal override IAudioStage CreateStage(double sampleRate) => new DelayStage(this, sampleRate);
}

/// <summary>
/// Una línea por canal indexada por la cuenta de muestras (<see cref="SampleRing"/>); lo que entra en la línea pasa por
/// los filtros, así que la primera repetición ya sale filtrada y cada vuelta un poco más.
/// </summary>
internal sealed class DelayStage : IAudioStage
{
    // Estado: cuenta de muestras, pasa altos y pasa bajos de cada canal (2 × 2 × 2).
    private const int Count = 0;
    private const int Filters = 1;

    private readonly double _sampleRate;
    private readonly SampleRing _left;
    private readonly SampleRing _right;
    private readonly double[] _state = new double[Filters + 8];
    private readonly int _maxDelay;
    private DelaySettings _settings = null!;
    private Biquad _highPass, _lowPass;
    private bool _lowCut, _highCut;
    private int _delay;
    private long _written;

    public DelayStage(DelaySettings settings, double sampleRate)
    {
        _sampleRate = sampleRate;
        _maxDelay = (int)Math.Ceiling(DelaySettings.MaxTimeMs * sampleRate / 1000) + 1;
        _left = new SampleRing(_maxDelay);
        _right = new SampleRing(_maxDelay);
        Configure(settings);
    }

    public int Latency => 0;

    public int StateLength => _state.Length;

    public DynamicsReading Reading => default;

    public void SaveState(Span<double> into) => _state.CopyTo(into);

    public void LoadState(ReadOnlySpan<double> from)
    {
        from.CopyTo(_state);
        long count = (long)_state[Count];
        if (!_left.Holds(count, _written, _maxDelay))
        {
            Reset();
            return;
        }

        _written = count;
    }

    public void Configure(AudioProcessorSettings settings)
    {
        _settings = (DelaySettings)settings;
        _delay = _settings.DelayFrames(_sampleRate);
        _lowCut = _settings.LowCutHz > 20;
        _highCut = _settings.HighCutHz < Math.Min(20000, _sampleRate * 0.45);
        _highPass = _lowCut ? Biquad.HighPass(_sampleRate, _settings.LowCutHz) : Biquad.Identity;
        _lowPass = _highCut ? Biquad.LowPass(_sampleRate, _settings.HighCutHz) : Biquad.Identity;
    }

    public void Reset()
    {
        Array.Clear(_state);
        _left.Clear();
        _right.Clear();
        _written = 0;
    }

    public void Process(Span<float> interleaved, ReadOnlySpan<float> key)
    {
        double[] s = _state;
        long n = (long)s[Count];
        double hl1 = s[Filters], hl2 = s[Filters + 1], ll1 = s[Filters + 2], ll2 = s[Filters + 3];
        double hr1 = s[Filters + 4], hr2 = s[Filters + 5], lr1 = s[Filters + 6], lr2 = s[Filters + 7];
        double feedback = Math.Clamp(_settings.Feedback, 0, 0.98);
        double mix = Math.Clamp(_settings.Mix, 0, 1);
        double dryGain = Math.Min(1, 2 * (1 - mix));
        double wetGain = Math.Min(1, 2 * mix);
        bool pingPong = _settings.PingPong;
        int delay = _delay;
        for (int i = 0; i + 1 < interleaved.Length; i += 2, n++)
        {
            double l = interleaved[i], r = interleaved[i + 1];
            double echoL = n >= delay ? _left[n - delay] : 0;
            double echoR = n >= delay ? _right[n - delay] : 0;
            double intoL, intoR;
            if (pingPong)
            {
                intoL = (0.5 * (l + r)) + (feedback * echoR);
                intoR = feedback * echoL;
            }
            else
            {
                intoL = l + (feedback * echoL);
                intoR = r + (feedback * echoR);
            }

            if (_lowCut)
            {
                intoL = _highPass.Tick(intoL, ref hl1, ref hl2);
                intoR = _highPass.Tick(intoR, ref hr1, ref hr2);
            }

            if (_highCut)
            {
                intoL = _lowPass.Tick(intoL, ref ll1, ref ll2);
                intoR = _lowPass.Tick(intoR, ref lr1, ref lr2);
            }

            _left[n] = (float)intoL;
            _right[n] = (float)intoR;
            interleaved[i] = (float)((l * dryGain) + (echoL * wetGain));
            interleaved[i + 1] = (float)((r * dryGain) + (echoR * wetGain));
        }

        s[Count] = n;
        _written = n;
        (s[Filters], s[Filters + 1], s[Filters + 2], s[Filters + 3]) = (hl1, hl2, ll1, ll2);
        (s[Filters + 4], s[Filters + 5], s[Filters + 6], s[Filters + 7]) = (hr1, hr2, lr1, lr2);
    }
}
