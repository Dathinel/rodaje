namespace Rodaje.Audio.Processing;

/// <summary>Limitador de pico verdadero por pista: sube la entrada <see cref="InputGainDb"/> y no deja pasar del techo.</summary>
public sealed record LimiterSettings(double CeilingDb, double InputGainDb, double ReleaseMs) : AudioProcessorSettings
{
    public override int LatencyFrames(double sampleRate) => TruePeakLimiter.Latency;

    internal override IAudioStage CreateStage(double sampleRate) => new LimiterStage(this, sampleRate);

    internal override LimiterSettings Limited() => new(Limit(CeilingDb, -24, 0, -1), Limit(InputGainDb, 0, 24, 0), Limit(ReleaseMs, 5, 1000, 60));
}

/// <summary>El <see cref="TruePeakLimiter"/> de la mezcla como inserción, con ganancia de entrada y relajación propias.</summary>
internal sealed class LimiterStage : IAudioStage
{
    private readonly double _sampleRate;
    private readonly TruePeakLimiter _limiter = new();
    private readonly double[] _state = new double[TruePeakLimiter.StateSize];
    private float _gain = 1;

    public LimiterStage(LimiterSettings settings, double sampleRate)
    {
        _sampleRate = sampleRate;
        Configure(settings);
        TruePeakLimiter.Initialize(_state);
    }

    public int Latency => TruePeakLimiter.Latency;

    public int StateLength => _state.Length;

    public DynamicsReading Reading { get; private set; }

    public void SaveState(Span<double> into) => _state.CopyTo(into);

    public void LoadState(ReadOnlySpan<double> from) => from.CopyTo(_state);

    public void Configure(AudioProcessorSettings settings)
    {
        var limiter = (LimiterSettings)settings;
        _limiter.Configure(limiter.CeilingDb, _sampleRate, Math.Clamp(limiter.ReleaseMs, 1, 5000));
        _gain = (float)Math.Pow(10, limiter.InputGainDb / 20);
    }

    public void Reset() => TruePeakLimiter.Initialize(_state);

    public void Process(Span<float> interleaved, ReadOnlySpan<float> key)
    {
        float inPeak = 0;
        for (int i = 0; i < interleaved.Length; i++)
        {
            float v = interleaved[i] * _gain;
            interleaved[i] = v;
            inPeak = MathF.Max(inPeak, MathF.Abs(v));
        }

        double reduction = _limiter.Process(interleaved, _state);
        float outPeak = 0;
        for (int i = 0; i < interleaved.Length; i++)
        {
            outPeak = MathF.Max(outPeak, MathF.Abs(interleaved[i]));
        }

        Reading = new DynamicsReading(inPeak, outPeak, 0, (float)reduction);
    }
}
