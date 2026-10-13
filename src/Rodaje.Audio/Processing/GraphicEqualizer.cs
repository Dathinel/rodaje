namespace Rodaje.Audio.Processing;

/// <summary>
/// Ecualizador gráfico de 10 bandas en las octavas ISO (31,5 Hz a 16 kHz): una campana de una octava de ancho (Q √2) por
/// banda, de −12 a +12 dB, y la ganancia de salida.
/// </summary>
public sealed record GraphicEqualizerSettings(
    double Band31,
    double Band63,
    double Band125,
    double Band250,
    double Band500,
    double Band1k,
    double Band2k,
    double Band4k,
    double Band8k,
    double Band16k) : AudioProcessorSettings
{
    public const int BandCount = 10;

    /// <summary>Ancho de una octava.</summary>
    public static readonly double Q = Math.Sqrt(2);

    /// <summary>Frecuencias centrales ISO 266 de cada banda.</summary>
    public static IReadOnlyList<double> Frequencies { get; } = [31.5, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    public double OutputDb { get; init; }

    public double Gain(int band) => band switch
    {
        0 => Band31,
        1 => Band63,
        2 => Band125,
        3 => Band250,
        4 => Band500,
        5 => Band1k,
        6 => Band2k,
        7 => Band4k,
        8 => Band8k,
        _ => Band16k,
    };

    /// <summary>La campana de la banda; nula si está en 0 dB.</summary>
    public Biquad? Band(int band, double sampleRate)
    {
        double gain = Gain(band);
        double hz = Frequencies[band];
        return gain == 0 || hz >= sampleRate * 0.49 ? null : Biquad.Peaking(sampleRate, hz, Q, gain);
    }

    /// <summary>Respuesta del ecualizador entero en <paramref name="hz"/>, en dB (con la salida).</summary>
    public double MagnitudeDb(double sampleRate, double hz)
    {
        double sum = OutputDb;
        for (int b = 0; b < BandCount; b++)
        {
            sum += Band(b, sampleRate)?.MagnitudeDb(sampleRate, hz) ?? 0;
        }

        return sum;
    }

    internal override IAudioStage CreateStage(double sampleRate) => new GraphicEqualizerStage(this, sampleRate);

    /// <summary>Lo más que sube o baja una banda o la salida, en dB.</summary>
    public const double MaxGainDb = 12;

    internal override GraphicEqualizerSettings Limited() =>
        new(G(Band31), G(Band63), G(Band125), G(Band250), G(Band500), G(Band1k), G(Band2k), G(Band4k), G(Band8k), G(Band16k)) { OutputDb = G(OutputDb) };

    private static double G(double db) => Limit(db, -MaxGainDb, MaxGainDb, 0);
}

internal sealed class GraphicEqualizerStage : IAudioStage
{
    private const int Channels = 2;

    private readonly double _sampleRate;
    private readonly Biquad?[] _bands = new Biquad?[GraphicEqualizerSettings.BandCount];
    private readonly double[] _state = new double[GraphicEqualizerSettings.BandCount * Channels * 2];
    private float _output = 1;

    public GraphicEqualizerStage(GraphicEqualizerSettings settings, double sampleRate)
    {
        _sampleRate = sampleRate;
        Configure(settings);
    }

    public int Latency => 0;

    public int StateLength => _state.Length;

    public DynamicsReading Reading => default;

    public void SaveState(Span<double> into) => _state.CopyTo(into);

    public void LoadState(ReadOnlySpan<double> from) => from.CopyTo(_state);

    public void Configure(AudioProcessorSettings settings)
    {
        var eq = (GraphicEqualizerSettings)settings;
        for (int b = 0; b < _bands.Length; b++)
        {
            _bands[b] = eq.Band(b, _sampleRate);
            if (_bands[b] is null)
            {
                Array.Clear(_state, b * Channels * 2, Channels * 2);
            }
        }

        _output = (float)Math.Pow(10, eq.OutputDb / 20);
    }

    public void Reset() => Array.Clear(_state);

    public void Process(Span<float> interleaved, ReadOnlySpan<float> key)
    {
        for (int b = 0; b < _bands.Length; b++)
        {
            for (int c = 0; _bands[b] is not null && c < Channels; c++)
            {
                _bands[b]!.Value.Process(interleaved, c, Channels, _state.AsSpan(((b * Channels) + c) * 2, 2));
            }
        }

        if (_output != 1)
        {
            for (int i = 0; i < interleaved.Length; i++)
            {
                interleaved[i] *= _output;
            }
        }
    }
}
