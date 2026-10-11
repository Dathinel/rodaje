using System.Collections.Concurrent;

namespace Rodaje.Engine.Timeline.Retime;

/// <summary>
/// Cambio de velocidad sin mantener el tono, como una cinta: cada muestra de salida se interpola del origen en
/// <c>map(s)</c> con un sinc enventanado (Kaiser) polifásico. No tiene estado: cualquier muestra sale igual se llegue
/// como se llegue, y un seek no necesita arranque.
/// </summary>
/// <remarks>
/// <para>
/// Hasta 1× el filtro tiene <see cref="BaseTaps"/> coeficientes y corta en la mitad del origen. Por encima de 1× el
/// corte baja con la velocidad (lo que pasaría de la mitad de la salida se plegaría) y el filtro se estira hasta
/// ×<see cref="MaxStretch"/>; más allá conserva el largo y solo baja el corte.
/// </para>
/// <para>
/// Cada filtro es una tabla de <see cref="Phases"/> fases que se calcula la primera vez que se usa esa velocidad
/// (cuantizada a 1/16 de octava) y se comparte entre clips.
/// </para>
/// </remarks>
internal sealed class VarispeedResampler
{
    public const int BaseTaps = 32;

    public const int Phases = 256;

    public const int MaxStretch = 4;

    /// <summary>Corte a 0,91 de la mitad: 21,8 kHz a 48 kHz.</summary>
    private const double Cutoff = 0.5 * 0.91;

    private const double Beta = 8.6;
    private const int StepsPerOctave = 16;

    private static readonly ConcurrentDictionary<int, Kernel> Kernels = new();

    /// <summary>
    /// Interpolador a 1× con corte justo en la mitad: en una fase 0 es la muestra tal cual. Lo usa WSOLA para leer
    /// sus trozos desde posiciones con fracción.
    /// </summary>
    public static Kernel Interpolator { get; } = Kernel.Create(1, 0.5);

    private readonly SampleMap _map;
    private readonly SourceWindow _source;
    private float[] _left = new float[8192];
    private float[] _right = new float[8192];

    public VarispeedResampler(SampleMap map, SourceWindow source)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(source);
        _map = map;
        _source = source;
    }

    /// <summary>Llena <paramref name="interleaved"/> (estéreo) desde la muestra <paramref name="from"/> de la línea de tiempo.</summary>
    public void Render(long from, Span<float> interleaved)
    {
        int frames = interleaved.Length / 2;
        int done = 0;
        while (done < frames)
        {
            long s = from + done;
            int count = (int)Math.Min(frames - done, SampleMap.Grid - (((s % SampleMap.Grid) + SampleMap.Grid) % SampleMap.Grid));
            double u0 = _map.At(s);
            double rate = _map.RateAt(s);
            double u1 = _map.At(s + count - 1);
            Kernel kernel = For(rate);
            int half = kernel.Taps / 2;
            long first = (long)Math.Floor(Math.Min(u0, u1)) - half + 1;
            int length = (int)((long)Math.Floor(Math.Max(u0, u1)) + half + 2 - first);
            if (_left.Length < length)
            {
                _left = new float[length * 2];
                _right = new float[length * 2];
            }

            _source.Read(first, _left.AsSpan(0, length), _right.AsSpan(0, length), _map.Reverse);
            for (int i = 0; i < count; i++)
            {
                double u = _map.At(s + i);
                long whole = (long)Math.Floor(u);
                int phase = (int)Math.Round((u - whole) * Phases);
                if (phase == Phases)
                {
                    phase = 0;
                    whole++;
                }

                int at = (int)(whole - half + 1 - first);
                (float l, float r) = VectorMath.Dot2(kernel.Row(phase), _left.AsSpan(at, kernel.Taps), _right.AsSpan(at, kernel.Taps));
                interleaved[(done + i) * 2] = l;
                interleaved[((done + i) * 2) + 1] = r;
            }

            done += count;
        }
    }

    /// <summary>El filtro para <paramref name="rate"/>, cuantizado a 1/16 de octava por encima de 1×.</summary>
    internal static Kernel For(double rate)
    {
        int step = rate <= 1 || !double.IsFinite(rate) ? 0 : (int)Math.Round(Math.Log2(rate) * StepsPerOctave);
        return Kernels.GetOrAdd(step, static s => Kernel.Create(Math.Pow(2, (double)s / StepsPerOctave), Cutoff));
    }

    internal sealed class Kernel
    {
        private readonly float[] _table;

        private Kernel(int taps, float[] table)
        {
            Taps = taps;
            _table = table;
        }

        public int Taps { get; }

        public ReadOnlySpan<float> Row(int phase) => _table.AsSpan(phase * Taps, Taps);

        /// <param name="cutoff">Corte a 1×, en ciclos por muestra; baja con la velocidad.</param>
        public static Kernel Create(double rate, double cutoff)
        {
            double stretch = Math.Clamp(rate, 1, MaxStretch);
            int taps = (int)Math.Ceiling(BaseTaps * stretch / 8) * 8;
            int half = taps / 2;
            double fc = cutoff / Math.Max(1, rate);
            var table = new float[Phases * taps];
            double norm = BesselI0(Beta);
            var row = new double[taps];
            for (int p = 0; p < Phases; p++)
            {
                double phase = (double)p / Phases;
                double sum = 0;
                for (int j = 0; j < taps; j++)
                {
                    double x = j - half + 1 - phase;
                    double t = x / half;
                    double window = Math.Abs(t) >= 1 ? 0 : BesselI0(Beta * Math.Sqrt(1 - (t * t))) / norm;
                    double arg = 2 * fc * x;
                    double sinc = Math.Abs(arg) < 1e-12 ? 1 : Math.Sin(Math.PI * arg) / (Math.PI * arg);
                    row[j] = 2 * fc * sinc * window;
                    sum += row[j];
                }

                for (int j = 0; j < taps; j++)
                {
                    table[(p * taps) + j] = (float)(row[j] / sum);
                }
            }

            return new Kernel(taps, table);
        }

        private static double BesselI0(double x)
        {
            double sum = 1;
            double term = 1;
            double half = x / 2;
            for (int k = 1; k < 64; k++)
            {
                term *= half / k;
                double add = term * term;
                sum += add;
                if (add < sum * 1e-17)
                {
                    break;
                }
            }

            return sum;
        }
    }
}
