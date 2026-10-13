using System.Numerics;
using Rodaje.Analysis.Dsp;

namespace Rodaje.Analysis.Sync;

/// <summary>
/// Huella para sincronizar por forma de onda: la subida de energía (en logaritmo) por bloques de 1 ms del audio mezclado
/// a mono, más la energía de cada bloque para elegir dónde refinar. Una hora son 3,6 millones de valores; el audio no se
/// guarda.
/// </summary>
public sealed class SyncFeature : IAudioAnalyzer
{
    private readonly List<float> _energy = [];
    private double _sum;
    private int _filled;

    public SyncFeature(int sampleRate, int channels)
    {
        AnalyzerGuard.Check(sampleRate, channels);
        SampleRate = sampleRate;
        Channels = channels;
        BlockLength = Math.Max(1, sampleRate / 1000);
    }

    public int SampleRate { get; }

    public int Channels { get; }

    /// <summary>Muestras por bloque (48 a 48 kHz).</summary>
    public int BlockLength { get; }

    public long Frames { get; private set; }

    /// <summary>Energía media de cada bloque completo.</summary>
    public IReadOnlyList<float> Energy => _energy;

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
                _energy.Add((float)(_sum / BlockLength));
                _sum = 0;
                _filled = 0;
            }
        }

        Frames += frames;
    }

    /// <summary>La subida de energía por bloque, sin media: lo que se correlaciona. No depende del volumen de cada micrófono.</summary>
    internal float[] Onsets()
    {
        var onsets = new float[_energy.Count];
        double previous = Math.Log(_energy.Count > 0 ? _energy[0] + 1e-9 : 1e-9);
        double sum = 0;
        for (int i = 0; i < onsets.Length; i++)
        {
            double level = Math.Log(_energy[i] + 1e-9);
            double rise = Math.Max(0, level - previous);
            previous = level;
            onsets[i] = (float)rise;
            sum += rise;
        }

        float mean = onsets.Length == 0 ? 0 : (float)(sum / onsets.Length);
        for (int i = 0; i < onsets.Length; i++)
        {
            onsets[i] -= mean;
        }

        return onsets;
    }
}

/// <summary>Un desfase encontrado: el otro audio empieza <see cref="Lag"/> muestras después del de referencia.</summary>
/// <param name="Lag">
/// <c>otro[n] ≈ referencia[n + Lag]</c>: el instante 0 del otro cae en la muestra <see cref="Lag"/> de la referencia (negativo:
/// el otro empezó antes).
/// </param>
/// <param name="Confidence">
/// Cuánto sobresale el pico de la correlación gruesa sobre el segundo mejor, de 0 a 1; por debajo de
/// <see cref="WaveformSync.MinConfidence"/> el resultado no es fiable (audios distintos o música muy repetitiva).
/// </param>
/// <param name="Correlation">Correlación normalizada del refinado en el desfase final (de −1 a 1).</param>
public sealed record SyncResult(long Lag, double Confidence, double Correlation)
{
    public bool IsReliable => Confidence >= WaveformSync.MinConfidence;
}

/// <summary>
/// Sincroniza dos grabaciones del mismo momento por su audio, con correlación propia en dos pasos: una gruesa con la
/// huella de 1 ms (FFT, toda la grabación) y un refinado muestra a muestra sobre unos segundos con mucha señal, con
/// interpolación parabólica del pico. El error queda por debajo de una muestra cuando el sonido es el mismo.
/// </summary>
public static class WaveformSync
{
    /// <summary>Por debajo de esto el pico grueso no se distingue lo bastante del segundo mejor.</summary>
    public const double MinConfidence = 0.15;

    /// <summary>Segundos de audio que se comparan muestra a muestra.</summary>
    public const double RefineSeconds = 4;

    /// <summary>Bloques de la huella a cada lado del pico grueso que revisa el refinado.</summary>
    public const int RefineBlocks = 3;

    /// <summary>
    /// Paso grueso: el desfase en muestras (múltiplo del bloque) que mejor alinea las huellas, buscando hasta
    /// <paramref name="maxLagSeconds"/> a cada lado.
    /// </summary>
    public static (long Lag, double Confidence)? Coarse(SyncFeature reference, SyncFeature other, double maxLagSeconds)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(other);
        if (reference.SampleRate != other.SampleRate || reference.BlockLength != other.BlockLength)
        {
            throw new ArgumentException("Las dos huellas tienen que tener la misma frecuencia de muestreo.", nameof(other));
        }

        float[] a = reference.Onsets();
        float[] b = other.Onsets();
        if (a.Length < 16 || b.Length < 16)
        {
            return null;
        }

        int size = (int)BitOperations.RoundUpToPowerOf2((uint)(a.Length + b.Length));
        var fft = new Fft(size);
        var ar = new float[size];
        var ai = new float[size];
        var br = new float[size];
        var bi = new float[size];
        a.CopyTo(ar, 0);
        b.CopyTo(br, 0);
        fft.Forward(ar, ai);
        fft.Forward(br, bi);

        // c[k] = Σ b[n]·a[n+k] = IFFT(A·conj(B)); la inversa es la directa sobre el conjugado.
        for (int i = 0; i < size; i++)
        {
            float re = (ar[i] * br[i]) + (ai[i] * bi[i]);
            float im = (ai[i] * br[i]) - (ar[i] * bi[i]);
            ar[i] = re;
            ai[i] = -im;
        }

        fft.Forward(ar, ai);
        int maxLag = (int)Math.Min(Math.Max(1, maxLagSeconds * 1000), size / 2 - 1);
        int lo = Math.Max(-(b.Length - 1), -maxLag);
        int hi = Math.Min(a.Length - 1, maxLag);
        int best = 0;
        double bestValue = double.NegativeInfinity;
        for (int k = lo; k <= hi; k++)
        {
            double value = Normalized(ar[k >= 0 ? k : size + k], k, a.Length, b.Length);
            if (value > bestValue)
            {
                bestValue = value;
                best = k;
            }
        }

        double second = double.NegativeInfinity;
        for (int k = lo; k <= hi; k++)
        {
            if (Math.Abs(k - best) > 20)
            {
                second = Math.Max(second, Normalized(ar[k >= 0 ? k : size + k], k, a.Length, b.Length));
            }
        }

        double confidence = bestValue <= 0 ? 0 : double.IsNegativeInfinity(second) ? 1 : Math.Clamp(1 - (Math.Max(0, second) / bestValue), 0, 1);
        return ((long)best * reference.BlockLength, confidence);
    }

    /// <summary>
    /// Dónde refinar: la ventana de <paramref name="seconds"/> del otro audio con más energía entre lo que se solapa con
    /// la referencia según el desfase grueso, dejando el margen del refinado.
    /// </summary>
    /// <returns>Inicio y largo en muestras del otro audio, o <c>null</c> si casi no se solapan.</returns>
    public static (long Start, long Length)? RefineWindow(SyncFeature reference, SyncFeature other, long coarseLag, double seconds = RefineSeconds)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(other);
        int block = other.BlockLength;
        long margin = (long)(RefineBlocks + 1) * block;

        // n del otro con n + lag dentro de la referencia, con margen a los lados.
        long first = Math.Max(0, margin - coarseLag);
        long last = Math.Min(other.Frames, reference.Frames - coarseLag - margin);
        if (last - first < block * 50)
        {
            return null;
        }

        long length = Math.Min(last - first, (long)(seconds * other.SampleRate));
        int windowBlocks = (int)(length / block);
        int firstBlock = (int)((first + block - 1) / block);
        int lastBlock = (int)Math.Min(other.Energy.Count, last / block);
        if (lastBlock - firstBlock < windowBlocks)
        {
            return (first, length);
        }

        double sum = 0;
        for (int i = firstBlock; i < firstBlock + windowBlocks; i++)
        {
            sum += other.Energy[i];
        }

        double bestSum = sum;
        int bestStart = firstBlock;
        for (int i = firstBlock + windowBlocks; i < lastBlock; i++)
        {
            sum += other.Energy[i] - other.Energy[i - windowBlocks];
            if (sum > bestSum)
            {
                bestSum = sum;
                bestStart = i - windowBlocks + 1;
            }
        }

        return ((long)bestStart * block, length);
    }

    /// <summary>
    /// Paso fino: la correlación normalizada muestra a muestra entre <paramref name="other"/> (que empieza en la muestra
    /// <paramref name="otherStart"/> del otro audio) y <paramref name="reference"/> (que empieza en
    /// <paramref name="referenceStart"/> de la referencia), para los desfases a <paramref name="radius"/> muestras de
    /// <paramref name="coarseLag"/>. El pico se afina con una parábola y se redondea a la muestra.
    /// </summary>
    public static (long Lag, double Correlation) Refine(
        ReadOnlySpan<float> reference,
        long referenceStart,
        ReadOnlySpan<float> other,
        long otherStart,
        long coarseLag,
        int radius)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(radius);
        int count = (2 * radius) + 1;
        var scores = new double[count];
        double otherEnergy = Dot(other, other);
        for (int i = 0; i < count; i++)
        {
            long lag = coarseLag - radius + i;
            long offset = otherStart + lag - referenceStart;
            if (offset < 0 || offset + other.Length > reference.Length)
            {
                scores[i] = double.NegativeInfinity;
                continue;
            }

            ReadOnlySpan<float> slice = reference.Slice((int)offset, other.Length);
            double energy = Dot(slice, slice);
            scores[i] = energy <= 0 || otherEnergy <= 0 ? 0 : Dot(other, slice) / Math.Sqrt(energy * otherEnergy);
        }

        int best = 0;
        for (int i = 1; i < count; i++)
        {
            if (scores[i] > scores[best])
            {
                best = i;
            }
        }

        double shift = 0;
        if (best > 0 && best < count - 1 && double.IsFinite(scores[best - 1]) && double.IsFinite(scores[best + 1]))
        {
            double l = scores[best - 1];
            double c = scores[best];
            double r = scores[best + 1];
            double den = l - (2 * c) + r;
            shift = den < 0 ? Math.Clamp(0.5 * (l - r) / den, -0.5, 0.5) : 0;
        }

        return (coarseLag - radius + best + (long)Math.Round(shift, MidpointRounding.AwayFromZero), scores[best]);
    }

    /// <summary>Los dos pasos sobre audios ya en memoria (mono, misma frecuencia).</summary>
    public static SyncResult? FindOffset(ReadOnlySpan<float> reference, ReadOnlySpan<float> other, int sampleRate, double maxLagSeconds = 60)
    {
        var a = new SyncFeature(sampleRate, 1);
        var b = new SyncFeature(sampleRate, 1);
        a.Push(reference);
        b.Push(other);
        if (Coarse(a, b, maxLagSeconds) is not { } coarse || RefineWindow(a, b, coarse.Lag) is not { } window)
        {
            return null;
        }

        int radius = RefineBlocks * a.BlockLength;
        (long lag, double correlation) = Refine(reference, 0, other.Slice((int)window.Start, (int)window.Length), window.Start, coarse.Lag, radius);
        return new SyncResult(lag, coarse.Confidence, correlation);
    }

    /// <summary>La correlación de un desfase dividida por la raíz del solape, para no premiar los desfases que solapan más.</summary>
    private static double Normalized(float value, int lag, int lengthA, int lengthB)
    {
        int overlap = Math.Min(lengthA, lag + lengthB) - Math.Max(0, lag);
        return overlap <= 0 ? double.NegativeInfinity : value / Math.Sqrt(overlap);
    }

    private static double Dot(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
    {
        double sum = 0;
        int i = 0;
        if (Vector.IsHardwareAccelerated && x.Length >= Vector<float>.Count)
        {
            var acc = Vector<double>.Zero;
            int width = Vector<float>.Count;
            for (; i <= x.Length - width; i += width)
            {
                Vector.Widen(new Vector<float>(x.Slice(i, width)) * new Vector<float>(y.Slice(i, width)), out Vector<double> low, out Vector<double> high);
                acc += low + high;
            }

            sum = Vector.Sum(acc);
        }

        for (; i < x.Length; i++)
        {
            sum += (double)x[i] * y[i];
        }

        return sum;
    }
}
