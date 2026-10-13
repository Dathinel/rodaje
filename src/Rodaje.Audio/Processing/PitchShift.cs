namespace Rodaje.Audio.Processing;

/// <summary>Cambio de tono sin cambiar la duración, de −12 a +12 semitonos (más los cents), con mezcla en paralelo.</summary>
public sealed record PitchShiftSettings(double Semitones, double Cents, double Mix) : AudioProcessorSettings
{
    public const double MaxSemitones = 12;

    /// <summary>Cuánto sube la frecuencia: 2^(semitonos/12), entre 0,5 y 2.</summary>
    public double Ratio => Math.Clamp(Math.Pow(2, (Semitones + (Cents / 100)) / 12), 0.5, 2);

    public override int LatencyFrames(double sampleRate) => PitchShiftStage.Latency48k;

    internal override IAudioStage CreateStage(double sampleRate) => new PitchShiftStage(this);
}

/// <summary>
/// WSOLA aplicado al tono, el mismo esquema que «Mantener tono» de la velocidad: trozos de 1024 muestras con ventana de
/// Hann solapados a la mitad. Cada trozo lee el origen remuestreado a la razón del tono y se busca a ±256 muestras de
/// donde manda el tiempo, el que mejor continúa al anterior (correlación gruesa diezmada ×4 y afinada a la muestra). La
/// salida va <see cref="Latency48k"/> muestras por detrás, lo que la cadena compensa en la línea de tiempo.
/// </summary>
/// <remarks>
/// La lectura fraccionaria usa un sinc enventanado (Kaiser) de 32 coeficientes y 512 fases; al subir el tono el corte baja
/// con la razón para no plegar. A razón 1 el trozo se lee en posiciones enteras y la salida es la entrada retrasada.
/// </remarks>
internal sealed class PitchShiftStage : IAudioStage
{
    public const int Hop = 512;
    public const int Grain = 2 * Hop;
    public const int SearchRadius = 256;
    public const int Taps = 32;
    public const int Latency48k = (3 * Hop) + SearchRadius + Taps;

    private const int Phases = 512;
    private const int Decimation = 4;
    private const double Penalty = 0.05;

    // Estado: cuenta de muestras, lectura del trozo anterior, si hay anterior, y lo que falta sumar (2 canales × Grain).
    private const int Count = 0;
    private const int Previous = 1;
    private const int HasPrevious = 2;
    private const int Overlap = 3;

    private static readonly float[] Window = HannWindow();

    private readonly SampleRing _left;
    private readonly SampleRing _right;
    private readonly double[] _state = new double[Overlap + (2 * Grain)];
    private readonly float[] _table = new float[Phases * Taps];
    private readonly float[] _template = new float[(2 * Hop / Decimation) + 1];
    private readonly float[] _region = new float[((2 * SearchRadius) + (2 * Hop)) / Decimation + 2];
    private readonly float[] _fineTemplate = new float[2 * Hop];
    private readonly int _maxDelay;
    private double _ratio = double.NaN;
    private double _mix;
    private long _written;

    public PitchShiftStage(PitchShiftSettings settings)
    {
        _maxDelay = Latency48k + (2 * Grain) + SearchRadius + Taps;
        _left = new SampleRing(_maxDelay);
        _right = new SampleRing(_maxDelay);
        Configure(settings);
    }

    public int Latency => Latency48k;

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
        var pitch = (PitchShiftSettings)settings;
        _mix = Math.Clamp(pitch.Mix, 0, 1);
        double ratio = pitch.Ratio;
        if (ratio != _ratio)
        {
            _ratio = ratio;
            BuildTable(ratio > 1 ? 0.95 / ratio : 1);
        }
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
        double mix = _mix;
        for (int i = 0; i + 1 < interleaved.Length; i += 2, n++)
        {
            _left[n] = interleaved[i];
            _right[n] = interleaved[i + 1];
            long virtualTime = n - Latency48k;
            if (virtualTime >= 0 && virtualTime % Hop == 0)
            {
                AddGrain(virtualTime / Hop, n);
            }

            int slot = (int)(n % Grain);
            double wetL = s[Overlap + slot], wetR = s[Overlap + Grain + slot];
            s[Overlap + slot] = 0;
            s[Overlap + Grain + slot] = 0;
            if (mix < 1)
            {
                double dryL = virtualTime >= 0 ? _left[virtualTime] : 0;
                double dryR = virtualTime >= 0 ? _right[virtualTime] : 0;
                wetL = (dryL * (1 - mix)) + (wetL * mix);
                wetR = (dryR * (1 - mix)) + (wetR * mix);
            }

            interleaved[i] = (float)wetL;
            interleaved[i + 1] = (float)wetR;
        }

        s[Count] = n;
        _written = n;
    }

    /// <summary>
    /// El trozo <paramref name="k"/> (tiempo virtual <c>k·Hop</c> a <c>k·Hop + Grain</c>) sale desde la muestra
    /// <paramref name="now"/> de la salida, la última que ya entró.
    /// </summary>
    private void AddGrain(long k, long now)
    {
        double[] s = _state;
        double p = _ratio;
        long ideal = (long)Math.Round((k * Hop) + Hop - (p * Hop));
        double read = ideal;
        if (s[HasPrevious] != 0)
        {
            read = Search(s[Previous] + (p * Hop), ideal, (int)Math.Round(p * Hop));
        }

        s[Previous] = read;
        s[HasPrevious] = 1;
        bool integer = p == 1 && read == Math.Floor(read);
        for (int j = 0; j < Grain; j++)
        {
            double position = read + (j * p);
            float w = Window[j];
            double l, r;
            if (integer)
            {
                long at = (long)read + j;
                l = at >= 0 ? _left[at] : 0;
                r = at >= 0 ? _right[at] : 0;
            }
            else
            {
                (l, r) = Interpolate(position);
            }

            int slot = (int)((now + j) % Grain);
            s[Overlap + slot] += w * l;
            s[Overlap + Grain + slot] += w * r;
        }
    }

    /// <summary>
    /// La lectura del trozo entre <c>ideal ± SearchRadius</c> que mejor se parece a <c>natural</c> (donde seguiría el
    /// anterior): correlación normalizada sobre la mezcla mono diezmada ×4, afinada a la muestra, con un leve castigo por
    /// alejarse de lo ideal, y con la fracción del pico (parábola): sin ella cada empalme redondearía la fase hacia el
    /// mismo lado y el tono se desafinaría. Si lo de antes es silencio o no está, la ideal.
    /// </summary>
    private double Search(double continuation, long ideal, int length)
    {
        length = Math.Clamp(length, Decimation, 2 * Hop);
        long low = ideal - SearchRadius;
        if (Math.Abs(continuation - ideal) < 1e-9 || continuation < 0 || low < 0)
        {
            return ideal;
        }

        // La plantilla se toma en muestras enteras: lo que se redondea se le devuelve a la lectura elegida.
        long natural = (long)Math.Round(continuation);
        double rounding = continuation - natural;

        int coarse = length / Decimation;
        double templateEnergy = 0;
        for (int i = 0; i < coarse; i++)
        {
            float sum = 0;
            for (int q = 0; q < Decimation; q++)
            {
                long at = natural + (i * Decimation) + q;
                sum += _left[at] + _right[at];
            }

            _template[i] = sum;
            templateEnergy += sum * sum;
        }

        if (templateEnergy < 1e-10 * coarse)
        {
            return ideal;
        }

        int regionLength = ((2 * SearchRadius) / Decimation) + coarse;
        for (int i = 0; i < regionLength; i++)
        {
            float sum = 0;
            for (int q = 0; q < Decimation; q++)
            {
                long at = low + (i * Decimation) + q;
                sum += _left[at] + _right[at];
            }

            _region[i] = sum;
        }

        int bestOffset = SearchRadius / Decimation;
        double best = double.NegativeInfinity;
        for (int o = 0; o <= (2 * SearchRadius) / Decimation; o++)
        {
            double dot = 0, energy = 0;
            for (int i = 0; i < coarse; i++)
            {
                float c = _region[o + i];
                dot += _template[i] * c;
                energy += c * c;
            }

            double score = Score(dot, energy, templateEnergy, Math.Abs((o * Decimation) - SearchRadius));
            if (score > best)
            {
                best = score;
                bestOffset = o;
            }
        }

        double fineEnergy = 0;
        for (int i = 0; i < length; i++)
        {
            long at = natural + i;
            float v = _left[at] + _right[at];
            _fineTemplate[i] = v;
            fineEnergy += v * v;
        }

        long center = low + (bestOffset * Decimation);
        long first = Math.Max(low, center - Decimation);
        long last = Math.Min(ideal + SearchRadius, center + Decimation);
        long chosen = center;
        best = double.NegativeInfinity;
        for (long candidate = first; candidate <= last; candidate++)
        {
            double score = FineScore(candidate, length, fineEnergy, ideal);
            if (score > best)
            {
                best = score;
                chosen = candidate;
            }
        }

        // La fracción del pico con una parábola por los tres puntos alrededor del mejor.
        if (chosen > first && chosen < last)
        {
            double before = FineScore(chosen - 1, length, fineEnergy, ideal);
            double after = FineScore(chosen + 1, length, fineEnergy, ideal);
            double curvature = before - (2 * best) + after;
            if (curvature < 0)
            {
                return chosen + Math.Clamp(0.5 * (before - after) / curvature, -0.5, 0.5) + rounding;
            }
        }

        return chosen + rounding;
    }

    private double FineScore(long candidate, int length, double templateEnergy, long ideal)
    {
        double dot = 0, energy = 0;
        for (int i = 0; i < length; i++)
        {
            long at = candidate + i;
            float c = _left[at] + _right[at];
            dot += _fineTemplate[i] * c;
            energy += c * c;
        }

        return Score(dot, energy, templateEnergy, Math.Abs(candidate - ideal));
    }

    private static double Score(double dot, double energy, double templateEnergy, long distance) =>
        (energy <= 1e-20 ? 0 : dot / Math.Sqrt(energy * templateEnergy)) - (Penalty * distance / SearchRadius);

    private (double Left, double Right) Interpolate(double position)
    {
        long whole = (long)Math.Floor(position);
        int phase = (int)((position - whole) * Phases);
        if (phase >= Phases)
        {
            phase = Phases - 1;
        }

        long first = whole - ((Taps / 2) - 1);
        double l = 0, r = 0;
        int row = phase * Taps;
        for (int t = 0; t < Taps; t++)
        {
            long at = first + t;
            if (at < 0)
            {
                continue;
            }

            float c = _table[row + t];
            l += c * _left[at];
            r += c * _right[at];
        }

        return (l, r);
    }

    /// <summary>Sinc enventanado con Kaiser (β 8,6) de corte <paramref name="cutoff"/> (1 = Nyquist), normalizado a ganancia 1.</summary>
    private void BuildTable(double cutoff)
    {
        const double beta = 8.6;
        double denominator = Bessel0(beta);
        for (int phase = 0; phase < Phases; phase++)
        {
            double fraction = (double)phase / Phases;
            double sum = 0;
            for (int t = 0; t < Taps; t++)
            {
                double x = t - ((Taps / 2) - 1) - fraction;
                double sinc = x == 0 ? 1 : Math.Sin(Math.PI * cutoff * x) / (Math.PI * cutoff * x);
                double ratio = x / (Taps / 2.0);
                double window = Math.Abs(ratio) >= 1 ? 0 : Bessel0(beta * Math.Sqrt(1 - (ratio * ratio))) / denominator;
                double c = cutoff * sinc * window;
                _table[(phase * Taps) + t] = (float)c;
                sum += c;
            }

            for (int t = 0; t < Taps; t++)
            {
                _table[(phase * Taps) + t] = (float)(_table[(phase * Taps) + t] / sum);
            }
        }
    }

    private static double Bessel0(double x)
    {
        double sum = 1, term = 1;
        for (int k = 1; k < 40; k++)
        {
            term *= (x / (2 * k)) * (x / (2 * k));
            sum += term;
        }

        return sum;
    }

    private static float[] HannWindow()
    {
        float[] window = new float[Grain];
        for (int j = 0; j < Grain; j++)
        {
            window[j] = (float)(0.5 - (0.5 * Math.Cos(2 * Math.PI * j / Grain)));
        }

        return window;
    }
}
