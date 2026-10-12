using Rodaje.Audio.Metering;

namespace Rodaje.Audio.Processing;

/// <summary>
/// Limitador de pico verdadero estéreo con anticipación: ninguna muestra ni ningún punto entre muestras (sobremuestreo
/// ×4, el mismo filtro que el medidor) pasa del techo.
/// </summary>
/// <remarks>
/// <para>
/// Para cada muestra calcula la ganancia que la deja en el techo (contando los dos tramos entre muestras que la tocan),
/// toma el mínimo de las últimas <see cref="Window"/> + 1 y lo suaviza con una media móvil de la misma longitud: así la
/// ganancia ya llegó a su valor cuando suena el pico y baja en línea recta, sin escalón. La recuperación es exponencial.
/// El audio sale con <see cref="Latency"/> muestras de retraso.
/// </para>
/// <para>Todo el estado está en el tramo que se le pasa, para guardarlo y restaurarlo con el resto de la cadena.</para>
/// </remarks>
internal sealed class TruePeakLimiter
{
    /// <summary>Muestras de anticipación: 1 ms a 48 kHz.</summary>
    public const int Window = 48;

    public const int Latency = Window + TruePeakDetector.Delay;

    private const int Channels = 2;
    private const int Taps = TruePeakDetector.WindowTaps;
    private const int Ring = Window + 1;

    // Disposición del estado.
    private const int History = 0;                          // 2 canales × 2·Taps (escrita dos veces)
    private const int Head = History + (Channels * Taps * 2);
    private const int PreviousPeak = Head + 1;
    private const int Required = PreviousPeak + 1;          // Ring ganancias pedidas
    private const int Smoothed = Required + Ring;           // Ring ganancias tras la recuperación
    private const int Sum = Smoothed + Ring;
    private const int RingPosition = Sum + 1;
    private const int Envelope = RingPosition + 1;
    private const int Held = Envelope + 1;                  // mínimo de las pedidas y dónde está
    private const int HeldAt = Held + 1;
    private const int Loud = HeldAt + 1;                    // muestras que quedan con algo fuerte en la ventana del filtro
    private const int Line = Loud + 1;                      // 2 canales × Latency
    private const int LinePosition = Line + (Channels * Latency);

    public const int StateSize = LinePosition + 1;

    private static readonly float[] Phase1 = TruePeakDetector.Phase(1).ToArray();
    private static readonly float[] Phase2 = TruePeakDetector.Phase(2).ToArray();
    private static readonly float[] Phase3 = TruePeakDetector.Phase(3).ToArray();

    private static readonly double Spread = Math.Max(Absolute(Phase1), Math.Max(Absolute(Phase2), Absolute(Phase3)));

    private double _ceiling = 1;
    private double _quiet = 1;
    private double _release;

    /// <summary>Techo en dBTP; un pequeño margen cubre que la ganancia cambia dentro del filtro de reconstrucción.</summary>
    public void Configure(double ceilingDb, double sampleRate, double releaseMs = 60)
    {
        _ceiling = Math.Pow(10, (ceilingDb - 0.05) / 20);

        // Si nada en la ventana del filtro pasa de esto, ningún punto entre muestras puede pasar del techo.
        _quiet = _ceiling / Spread;
        _release = Math.Exp(-1 / (releaseMs / 1000 * sampleRate));
    }

    /// <summary>Deja el estado como tras un silencio: ganancia 1.</summary>
    public static void Initialize(Span<double> state)
    {
        state[..StateSize].Clear();
        state.Slice(Required, Ring).Fill(1);
        state.Slice(Smoothed, Ring).Fill(1);
        state[Sum] = Ring;
        state[Envelope] = 1;
        state[Held] = 1;
    }

    /// <summary>Limita en el sitio; devuelve la mayor reducción aplicada, en dB.</summary>
    public double Process(Span<float> interleaved, Span<double> state)
    {
        int head = (int)state[Head];
        int ring = (int)state[RingPosition];
        int line = (int)state[LinePosition];
        int heldAt = (int)state[HeldAt];
        int loud = (int)state[Loud];
        double previous = state[PreviousPeak];
        double envelope = state[Envelope];
        double held = state[Held];
        double sum = state[Sum];
        double lowest = 1;
        Span<double> history = state.Slice(History, Channels * Taps * 2);
        Span<double> required = state.Slice(Required, Ring);
        Span<double> smoothed = state.Slice(Smoothed, Ring);
        Span<double> delay = state.Slice(Line, Channels * Latency);
        ReadOnlySpan<float> p1 = Phase1, p2 = Phase2, p3 = Phase3;

        for (int i = 0; i + 1 < interleaved.Length; i += Channels)
        {
            double xl = interleaved[i], xr = interleaved[i + 1];
            history[head] = history[head + Taps] = xl;
            history[(Taps * 2) + head] = history[(Taps * 3) + head] = xr;
            head = (head + 1) % Taps;
            if (Math.Max(Math.Abs(xl), Math.Abs(xr)) > _quiet)
            {
                loud = Taps;
            }

            double peak = Math.Max(Math.Abs(history[head + Taps - 1 - TruePeakDetector.Delay]), Math.Abs(history[(Taps * 2) + head + Taps - 1 - TruePeakDetector.Delay]));
            if (loud > 0)
            {
                loud--;
                for (int c = 0; c < Channels; c++)
                {
                    ReadOnlySpan<double> w = history.Slice((c * Taps * 2) + head, Taps);
                    double a1 = 0, a2 = 0, a3 = 0;
                    for (int k = 0; k < Taps; k++)
                    {
                        double x = w[k];
                        a1 += x * p1[k];
                        a2 += x * p2[k];
                        a3 += x * p3[k];
                    }

                    peak = Math.Max(peak, Math.Max(Math.Abs(a1), Math.Max(Math.Abs(a2), Math.Abs(a3))));
                }
            }

            double both = Math.Max(peak, previous);
            previous = peak;
            double wanted = both > _ceiling ? _ceiling / both : 1;

            // Mínimo de la ventana sin recorrerla: solo hace falta buscarlo de nuevo cuando sale el que lo era.
            required[ring] = wanted;
            if (wanted <= held)
            {
                held = wanted;
                heldAt = ring;
            }
            else if (ring == heldAt)
            {
                held = 1;
                for (int k = 0; k < Ring; k++)
                {
                    if (required[k] <= held)
                    {
                        held = required[k];
                        heldAt = k;
                    }
                }
            }

            envelope = held < envelope ? held : envelope + ((held - envelope) * (1 - _release));
            sum += envelope - smoothed[ring];
            smoothed[ring] = envelope;
            if (++ring == Ring)
            {
                // La suma se rehace exacta en cada vuelta: sin deriva y siempre igual para la misma entrada.
                ring = 0;
                sum = 0;
                for (int k = 0; k < Ring; k++)
                {
                    sum += smoothed[k];
                }
            }

            double gain = sum / Ring;
            lowest = Math.Min(lowest, gain);
            double dl = delay[line], dr = delay[Latency + line];
            delay[line] = xl;
            delay[Latency + line] = xr;
            interleaved[i] = (float)(dl * gain);
            interleaved[i + 1] = (float)(dr * gain);
            if (++line == Latency)
            {
                line = 0;
            }
        }

        state[Head] = head;
        state[RingPosition] = ring;
        state[LinePosition] = line;
        state[HeldAt] = heldAt;
        state[Loud] = loud;
        state[PreviousPeak] = previous;
        state[Envelope] = envelope;
        state[Held] = held;
        state[Sum] = sum;
        return lowest >= 1 ? 0 : -20 * Math.Log10(Math.Max(lowest, 1e-9));
    }

    private static double Absolute(float[] phase) => phase.Sum(Math.Abs);
}