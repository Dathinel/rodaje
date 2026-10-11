namespace Rodaje.Engine.Timeline.Retime;

/// <summary>
/// Cambio de velocidad que mantiene el tono (WSOLA): trozos de 2 × <see cref="Hop"/> muestras del origen, con ventana de
/// Hann, se solapan a la mitad en la salida. Cada trozo se busca a ±<see cref="SearchRadius"/> muestras de donde manda el
/// mapeo, el que mejor continúa al anterior.
/// </summary>
/// <remarks>
/// <para>
/// La posición está atada al mapeo: el trozo <c>k</c> sale en la muestra <c>k · Hop</c> de la línea de tiempo y su
/// centro se busca alrededor de <c>map(k · Hop + Hop)</c>, calculado desde el ancla del clip. El error nunca pasa del
/// radio de búsqueda (no hay deriva) y una curva de velocidad sale sola.
/// </para>
/// <para>
/// La búsqueda gruesa va sobre la mezcla mono diezmada ×<see cref="Decimation"/> y se afina a la muestra. Un leve castigo
/// por alejarse del mapeo desempata entre periodos. El fundido entre trozos conserva la potencia según cuánto se parecen:
/// lineal si son iguales, de igual potencia si no tienen nada que ver.
/// </para>
/// <para>
/// Tras un salto la cadena arranca <see cref="WarmUp"/> muestras antes. Recuerda los últimos trozos: volver a mezclar lo
/// recién oído (tras una edición) repite exactamente lo mismo.
/// </para>
/// </remarks>
internal sealed class Wsola
{
    public const int SearchRadius = 256;

    public const int Decimation = 4;

    /// <summary>250 ms de arranque antes de lo pedido tras un salto.</summary>
    public const int WarmUp = TimelinePlan.AudioSampleRate / 4;

    /// <summary>Por encima de esta velocidad los trozos son de 2048 muestras.</summary>
    public const double LongWindowAbove = 4;

    private const int Refine = 3;
    private const double DistancePenalty = 0.05;

    // −60 dBFS en cada canal (la plantilla es L + R): bajo eso el trozo va donde manda el mapeo, sin buscar. Así dos
    // cadenas que empezaron en sitios distintos (un seek y la reproducción seguida) coinciden desde la primera pausa.
    private const double SilenceEnergy = 2e-3 * 2e-3;
    private const int HistorySize = 128;

    private readonly SampleMap _map;
    private readonly SourceWindow _source;
    private readonly int _hop;
    private readonly float[] _window;
    private readonly float[] _tailL;
    private readonly float[] _tailR;
    private readonly float[] _headL;
    private readonly float[] _headR;
    private readonly float[] _gainTail;
    private readonly float[] _gainHead;
    private readonly float[] _template;
    private readonly float[] _candidate;
    private readonly float[] _regionL;
    private readonly float[] _regionR;
    private readonly float[] _region;
    private readonly float[] _templateCoarse;
    private readonly float[] _regionCoarse;
    private readonly double[] _energyCoarse;
    private readonly long[] _historyK = new long[HistorySize];
    private readonly double[] _historyA = new double[HistorySize];
    private long _k = long.MinValue;
    private double _a;

    public Wsola(SampleMap map, SourceWindow source, double maxRate)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(source);
        _map = map;
        _source = source;
        _hop = maxRate > LongWindowAbove ? 1024 : 512;
        int n = 2 * _hop;
        _window = new float[n];
        for (int i = 0; i < n; i++)
        {
            _window[i] = (float)(0.5 - (0.5 * Math.Cos(2 * Math.PI * i / n)));
        }

        _tailL = new float[_hop];
        _tailR = new float[_hop];
        _headL = new float[_hop];
        _headR = new float[_hop];
        _gainTail = new float[_hop];
        _gainHead = new float[_hop];
        _template = new float[_hop];
        _candidate = new float[_hop];
        int region = (2 * SearchRadius) + _hop;
        _regionL = new float[region];
        _regionR = new float[region];
        _region = new float[region];
        _templateCoarse = new float[_hop / Decimation];
        _regionCoarse = new float[region / Decimation];
        _energyCoarse = new double[(region / Decimation) + 1];
        Array.Fill(_historyK, long.MinValue);
    }

    public int Hop => _hop;

    /// <summary>Trozos calculados para arrancar tras un salto (los de <see cref="WarmUp"/>); sirve a las pruebas.</summary>
    public long WarmUpSteps { get; private set; }

    /// <summary>Llena <paramref name="interleaved"/> (estéreo) con la salida desde la muestra <paramref name="from"/> de la línea de tiempo.</summary>
    public void Render(long from, Span<float> interleaved)
    {
        int frames = interleaved.Length / 2;
        long s = from;
        int done = 0;
        while (done < frames)
        {
            long k = FloorDiv(s, _hop);
            MoveTo(k);
            int n = (int)(s - (k * _hop));
            int count = Math.Min(frames - done, _hop - n);
            for (int i = 0; i < count; i++)
            {
                int j = n + i;
                int at = (done + i) * 2;
                interleaved[at] = (_gainTail[j] * _tailL[j]) + (_gainHead[j] * _headL[j]);
                interleaved[at + 1] = (_gainTail[j] * _tailR[j]) + (_gainHead[j] * _headR[j]);
            }

            done += count;
            s += count;
        }
    }

    private void MoveTo(long k)
    {
        if (k == _k)
        {
            return;
        }

        if (k == _k + 1)
        {
            Step(k, null);
            return;
        }

        if (Remembered(k - 1) is { } previous && Remembered(k) is { } current)
        {
            _k = k - 1;
            _a = previous;
            Step(k, current);
            return;
        }

        long first = k - ((WarmUp + _hop - 1) / _hop);
        _k = first;
        _a = Nominal(first);
        Remember(first, _a);
        for (long j = first + 1; j <= k; j++)
        {
            Step(j, null);
            WarmUpSteps++;
        }
    }

    private void Step(long k, double? forced)
    {
        bool reverse = _map.Reverse;
        _source.ReadAt(_a + _hop, _tailL, _tailR, reverse);
        Mono(_tailL, _tailR, _template);
        double a = forced ?? Search(Nominal(k));
        _source.ReadAt(a, _headL, _headR, reverse);
        Mono(_headL, _headR, _candidate);
        double energyT = VectorMath.Dot(_template, _template);
        double energyH = VectorMath.Dot(_candidate, _candidate);
        double rho = energyT * energyH > 1e-20 ? VectorMath.Dot(_template, _candidate) / Math.Sqrt(energyT * energyH) : 1;
        Gains(Math.Clamp(rho, 0, 1));
        _k = k;
        _a = a;
        Remember(k, a);
    }

    /// <summary>El centro del trozo <paramref name="k"/> cae en <c>map(k · Hop + Hop)</c>.</summary>
    private long Nominal(long k) => (long)Math.Round(_map.At((k * _hop) + _hop)) - _hop;

    /// <summary>
    /// El inicio que mejor continúa la plantilla, con fracción: la correlación se afina a la muestra y su pico se
    /// interpola con una parábola. Sin la fracción cada empalme de un tono redondea la fase siempre hacia el mismo
    /// lado y el tono se desafina.
    /// </summary>
    private double Search(long nominal)
    {
        double energyT = VectorMath.Dot(_template, _template);
        if (energyT < _hop * SilenceEnergy)
        {
            return nominal;
        }

        long origin = nominal - SearchRadius;
        _source.Read(origin, _regionL, _regionR, _map.Reverse);
        Mono(_regionL, _regionR, _region);

        Decimate(_template, _templateCoarse);
        Decimate(_region, _regionCoarse);
        int coarseLength = _templateCoarse.Length;
        double energyTC = VectorMath.Dot(_templateCoarse, _templateCoarse);
        _energyCoarse[0] = 0;
        for (int i = 0; i < _regionCoarse.Length; i++)
        {
            _energyCoarse[i + 1] = _energyCoarse[i] + (_regionCoarse[i] * (double)_regionCoarse[i]);
        }

        int bestLag = SearchRadius / Decimation;
        double bestScore = double.NegativeInfinity;
        for (int lag = 0; lag <= 2 * SearchRadius / Decimation; lag++)
        {
            double energy = _energyCoarse[lag + coarseLength] - _energyCoarse[lag];
            double rho = energy * energyTC > 1e-20 ? VectorMath.Dot(_templateCoarse, _regionCoarse.AsSpan(lag)) / Math.Sqrt(energy * energyTC) : 0;
            double score = rho - Penalty((lag * Decimation) - SearchRadius);
            if (score > bestScore)
            {
                bestScore = score;
                bestLag = lag;
            }
        }

        int center = bestLag * Decimation;
        int low = Math.Max(0, center - Refine - 1);
        int high = Math.Min(2 * SearchRadius, center + Refine + 1);
        Span<double> rhos = stackalloc double[(2 * Refine) + 3];
        int best = center;
        double bestRho = double.NegativeInfinity;
        for (int offset = low; offset <= high; offset++)
        {
            ReadOnlySpan<float> candidate = _region.AsSpan(offset, _hop);
            double energy = VectorMath.Dot(candidate, candidate);
            double rho = energy > 1e-20 ? VectorMath.Dot(_template, candidate) / Math.Sqrt(energy * energyT) : 0;
            rhos[offset - low] = rho;
            if (offset >= center - Refine && offset <= center + Refine && rho > bestRho)
            {
                bestRho = rho;
                best = offset;
            }
        }

        double fraction = 0;
        if (best > low && best < high)
        {
            double before = rhos[best - 1 - low];
            double after = rhos[best + 1 - low];
            double curvature = before - (2 * bestRho) + after;
            if (curvature < 0)
            {
                fraction = Math.Clamp(0.5 * (before - after) / curvature, -0.5, 0.5);
            }
        }

        return origin + best + fraction;
    }

    private static double Penalty(int lag)
    {
        double x = (double)lag / SearchRadius;
        return DistancePenalty * x * x;
    }

    /// <summary>Fundido que conserva la potencia: <c>w · a + (1 − w) · b</c> dividido por la raíz de su potencia esperada con correlación ρ.</summary>
    private void Gains(double rho)
    {
        for (int n = 0; n < _hop; n++)
        {
            double tail = _window[n + _hop];
            double head = _window[n];
            double norm = Math.Sqrt((tail * tail) + (head * head) + (2 * rho * tail * head));
            _gainTail[n] = (float)(tail / norm);
            _gainHead[n] = (float)(head / norm);
        }
    }

    private static void Mono(ReadOnlySpan<float> left, ReadOnlySpan<float> right, Span<float> mono)
    {
        for (int i = 0; i < mono.Length; i++)
        {
            mono[i] = left[i] + right[i];
        }
    }

    private static void Decimate(ReadOnlySpan<float> full, Span<float> coarse)
    {
        for (int i = 0; i < coarse.Length; i++)
        {
            int j = i * Decimation;
            coarse[i] = full[j] + full[j + 1] + full[j + 2] + full[j + 3];
        }
    }

    private void Remember(long k, double a)
    {
        int slot = (int)(((k % HistorySize) + HistorySize) % HistorySize);
        _historyK[slot] = k;
        _historyA[slot] = a;
    }

    private double? Remembered(long k)
    {
        int slot = (int)(((k % HistorySize) + HistorySize) % HistorySize);
        return _historyK[slot] == k ? _historyA[slot] : null;
    }

    private static long FloorDiv(long a, long b) => (a / b) - ((a % b != 0 && (a < 0) != (b < 0)) ? 1 : 0);
}
