namespace Rodaje.Analysis.Silence;

/// <summary>
/// Nivel RMS en dBFS por bloques fijos (5 ms por omisión) del audio mezclado a mono. Es lo único que se guarda: una
/// hora son 720 000 valores, y el detector puede volver a correr con otros ajustes sin decodificar de nuevo.
/// </summary>
public sealed class LevelEnvelope : IAudioAnalyzer
{
    /// <summary>Nivel que se usa para el silencio digital.</summary>
    public const double FloorDb = -120;

    private readonly List<double> _levels = [];
    private double _sum;
    private int _filled;

    public LevelEnvelope(int sampleRate, int channels, double blockSeconds = 0.005)
    {
        AnalyzerGuard.Check(sampleRate, channels);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSeconds, 0.001);
        SampleRate = sampleRate;
        Channels = channels;
        BlockLength = Math.Max(1, (int)Math.Round(sampleRate * blockSeconds));
    }

    public int SampleRate { get; }

    public int Channels { get; }

    /// <summary>Muestras por bloque.</summary>
    public int BlockLength { get; }

    public double BlockSeconds => (double)BlockLength / SampleRate;

    public long Frames { get; private set; }

    public double Duration => (double)Frames / SampleRate;

    /// <summary>Nivel de cada bloque; el último puede ser parcial.</summary>
    public IReadOnlyList<double> Levels => _filled == 0 ? _levels : [.. _levels, ToDb(_sum / _filled)];

    public void Push(ReadOnlySpan<float> interleaved)
    {
        AnalyzerGuard.CheckFrames(interleaved, Channels);
        int frames = interleaved.Length / Channels;
        for (int i = 0; i < frames; i++)
        {
            double s = AnalyzerGuard.Mono(interleaved, i, Channels);
            _sum += s * s;
            if (++_filled == BlockLength)
            {
                _levels.Add(ToDb(_sum / BlockLength));
                _sum = 0;
                _filled = 0;
            }
        }

        Frames += frames;
    }

    internal static double ToDb(double meanSquare) =>
        meanSquare <= 1e-12 ? FloorDb : Math.Max(FloorDb, 10 * Math.Log10(meanSquare));
}
