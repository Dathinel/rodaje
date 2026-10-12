using Rodaje.Analysis.Dsp;

namespace Rodaje.Analysis.Rhythm;

/// <summary>
/// Fuerza de ataque (flujo espectral) a 100 cuadros por segundo: el audio se lleva a mono y a ~16 kHz, cada 10 ms se
/// toma una ventana de Hann de ~32 ms, se agrupa la energía en bandas logarítmicas en dB y se suman las subidas
/// respecto del cuadro anterior.
/// </summary>
public sealed class OnsetEnvelope : IAudioAnalyzer
{
    private const int BandCount = 36;
    private const double FloorDb = -80;

    private readonly int _factor;
    private readonly float[] _ring;
    private readonly float[] _window;
    private readonly float[] _re;
    private readonly float[] _im;
    private readonly int[] _bandEdges;
    private readonly double[] _previous;
    private readonly double[] _current;
    private readonly Fft _fft;
    private readonly double _norm;
    private readonly List<double> _flux = [];
    private int _ringAt;
    private int _sinceFrame;
    private double _decimated;
    private int _decimatedCount;
    private bool _hasPrevious;

    public OnsetEnvelope(int sampleRate, int channels)
    {
        AnalyzerGuard.Check(sampleRate, channels);
        SampleRate = sampleRate;
        Channels = channels;
        _factor = Math.Max(1, sampleRate / 16_000);
        AnalysisRate = (double)sampleRate / _factor;
        Hop = Math.Max(1, (int)Math.Round(AnalysisRate / 100));
        int size = 256;
        while (size < AnalysisRate * 0.03)
        {
            size <<= 1;
        }

        WindowLength = size;
        _fft = new Fft(size);
        _ring = new float[size];
        _window = new float[size];
        for (int i = 0; i < size; i++)
        {
            _window[i] = (float)(0.5 - (0.5 * Math.Cos(2 * Math.PI * i / size)));
        }

        _re = new float[size];
        _im = new float[size];
        _norm = Math.Pow(size / 4.0, 2);
        _previous = new double[BandCount];
        _current = new double[BandCount];
        _bandEdges = new int[BandCount + 1];
        double low = 40, high = Math.Min(8000, AnalysisRate * 0.48);
        for (int b = 0; b <= BandCount; b++)
        {
            double f = low * Math.Pow(high / low, (double)b / BandCount);
            _bandEdges[b] = Math.Clamp((int)Math.Round(f * size / AnalysisRate), 1, size / 2);
        }

        for (int b = 1; b <= BandCount; b++)
        {
            _bandEdges[b] = Math.Max(_bandEdges[b], _bandEdges[b - 1] + 1);
        }
    }

    public int SampleRate { get; }

    public int Channels { get; }

    /// <summary>Frecuencia tras diezmar.</summary>
    public double AnalysisRate { get; }

    /// <summary>Muestras (a <see cref="AnalysisRate"/>) entre cuadros.</summary>
    public int Hop { get; }

    public int WindowLength { get; }

    public double FrameRate => AnalysisRate / Hop;

    /// <summary>Flujo de cada cuadro; el cuadro k cubre las muestras <c>[(k+1)·Hop − WindowLength, (k+1)·Hop)</c>.</summary>
    public IReadOnlyList<double> Flux => _flux;

    /// <summary>Instante (s) que representa el cuadro <paramref name="frame"/>: el centro de su ventana.</summary>
    public double FrameTime(double frame) => (((frame + 1) * Hop) - (WindowLength / 2.0)) / AnalysisRate;

    public void Push(ReadOnlySpan<float> interleaved)
    {
        AnalyzerGuard.CheckFrames(interleaved, Channels);
        int frames = interleaved.Length / Channels;
        for (int i = 0; i < frames; i++)
        {
            _decimated += AnalyzerGuard.Mono(interleaved, i, Channels);
            if (++_decimatedCount < _factor)
            {
                continue;
            }

            _ring[_ringAt] = (float)(_decimated / _factor);
            _ringAt = (_ringAt + 1) % _ring.Length;
            _decimated = 0;
            _decimatedCount = 0;
            if (++_sinceFrame == Hop)
            {
                _sinceFrame = 0;
                AnalyzeFrame();
            }
        }
    }

    private void AnalyzeFrame()
    {
        int n = _ring.Length;
        for (int i = 0; i < n; i++)
        {
            _re[i] = _ring[(_ringAt + i) % n] * _window[i];
            _im[i] = 0;
        }

        _fft.Forward(_re, _im);
        double flux = 0;
        for (int b = 0; b < BandCount; b++)
        {
            double energy = 0;
            for (int k = _bandEdges[b]; k < _bandEdges[b + 1]; k++)
            {
                energy += (_re[k] * _re[k]) + (_im[k] * _im[k]);
            }

            double db = Math.Max(FloorDb, 10 * Math.Log10((energy / _norm) + 1e-12));
            _current[b] = db;
            if (_hasPrevious && db > _previous[b])
            {
                flux += db - _previous[b];
            }
        }

        Array.Copy(_current, _previous, BandCount);
        _hasPrevious = true;
        _flux.Add(flux / BandCount);
    }
}
