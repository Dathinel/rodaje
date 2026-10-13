using Rodaje.Audio.Processing;

namespace Rodaje.Audio.Metering;

/// <summary>Estima el pico entre muestras de un canal reconstruyendo la señal con sobremuestreo ×4.</summary>
/// <remarks>
/// <para>
/// Interpola tres puntos entre cada par de muestras con un filtro polifásico de sinc enventanado (Blackman, 16
/// coeficientes por fase, ganancia unitaria en continua). La fase 0 es la propia muestra, así que el resultado nunca
/// es menor que el pico de muestras. La salida va 8 muestras por detrás de la entrada (0,17 ms a 48 kHz).
/// </para>
/// <para>Cuesta 48 multiplicaciones por muestra y no reserva memoria después de construirse. Lo usa un solo hilo.</para>
/// </remarks>
public sealed class TruePeakDetector
{
    public const int Oversampling = 4;

    private const int Taps = 16;
    private const int HalfTaps = Taps / 2;

    private static readonly float[][] Phases = BuildPhases();

    /// <summary>Coeficientes de la fase <paramref name="phase"/> (1 a 3) para la ventana <c>x[n-15] .. x[n]</c>; los comparte el limitador.</summary>
    internal static ReadOnlySpan<float> Phase(int phase) => Phases[phase];

    internal const int WindowTaps = Taps;

    internal const int Delay = HalfTaps;

    // Las últimas 16 muestras, escritas dos veces para leerlas siempre como un tramo contiguo.
    private readonly float[] _history = new float[Taps * 2];
    private int _head;

    /// <summary>Procesa <paramref name="frames"/> muestras de <paramref name="channel"/> en un búfer intercalado de <paramref name="channels"/> canales.</summary>
    /// <returns>El mayor valor absoluto de la señal reconstruida, contando las muestras.</returns>
    public float Process(ReadOnlySpan<float> interleaved, int channel, int channels, int frames)
    {
        float max = 0;
        for (int f = 0; f < frames; f++)
        {
            float sample = AudioSamples.ForMeter(interleaved[(f * channels) + channel]);
            max = MathF.Max(max, MathF.Abs(sample));
            _history[_head] = sample;
            _history[_head + Taps] = sample;
            _head = (_head + 1) % Taps;

            ReadOnlySpan<float> window = _history.AsSpan(_head, Taps);
            for (int p = 1; p < Oversampling; p++)
            {
                float[] coefficients = Phases[p];
                float acc = 0;
                for (int k = 0; k < Taps; k++)
                {
                    acc += window[k] * coefficients[k];
                }

                max = MathF.Max(max, MathF.Abs(acc));
            }
        }

        return max;
    }

    /// <summary>Olvida la historia: la señal siguiente no continúa la anterior (por ejemplo, tras un seek).</summary>
    public void Reset()
    {
        Array.Clear(_history);
        _head = 0;
    }

    /// <summary>
    /// Coeficientes de la fase <c>p</c> para la ventana <c>x[n-15] .. x[n]</c>: interpolan el instante
    /// <c>n - 8 + p/4</c>, entre <c>x[n-8]</c> y <c>x[n-7]</c>.
    /// </summary>
    private static float[][] BuildPhases()
    {
        var phases = new float[Oversampling][];
        for (int p = 0; p < Oversampling; p++)
        {
            var coefficients = new double[Taps];
            double sum = 0;
            for (int k = 0; k < Taps; k++)
            {
                // k = 0 es la muestra más vieja (n - 15); u es la distancia del instante buscado a esa muestra.
                double u = (Taps - 1 - k) - HalfTaps + ((double)p / Oversampling);
                double value = Sinc(u) * Blackman(u);
                coefficients[k] = value;
                sum += value;
            }

            phases[p] = [.. coefficients.Select(c => (float)(c / sum))];
        }

        return phases;
    }

    private static double Sinc(double x) => Math.Abs(x) < 1e-12 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);

    private static double Blackman(double u)
    {
        double x = Math.Clamp(u / HalfTaps, -1, 1);
        return 0.42 + (0.5 * Math.Cos(Math.PI * x)) + (0.08 * Math.Cos(2 * Math.PI * x));
    }
}
