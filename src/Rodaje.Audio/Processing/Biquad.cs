namespace Rodaje.Audio.Processing;

/// <summary>
/// Coeficientes de un filtro de segundo orden normalizados (<c>a0 = 1</c>), con las fórmulas del «Audio EQ Cookbook» de
/// R. Bristow-Johnson. Se procesa en forma directa transpuesta II con el estado en <c>double</c>.
/// </summary>
public readonly record struct Biquad(double B0, double B1, double B2, double A1, double A2)
{
    public static Biquad Identity { get; } = new(1, 0, 0, 0, 0);

    /// <summary>Campana: <paramref name="gainDb"/> en <paramref name="hz"/>, con ancho <paramref name="q"/>.</summary>
    public static Biquad Peaking(double sampleRate, double hz, double q, double gainDb)
    {
        double a = Math.Pow(10, gainDb / 40);
        (double cos, double alpha) = Angle(sampleRate, hz, q);
        return Normalize(1 + (alpha * a), -2 * cos, 1 - (alpha * a), 1 + (alpha / a), -2 * cos, 1 - (alpha / a));
    }

    /// <summary>Estante de graves con pendiente 1 (Q = 1/√2): <paramref name="gainDb"/> por debajo de <paramref name="hz"/>.</summary>
    public static Biquad LowShelf(double sampleRate, double hz, double gainDb)
    {
        double a = Math.Pow(10, gainDb / 40);
        (double cos, double alpha) = Angle(sampleRate, hz, Math.Sqrt(0.5));
        double k = 2 * Math.Sqrt(a) * alpha;
        return Normalize(
            a * ((a + 1) - ((a - 1) * cos) + k),
            2 * a * ((a - 1) - ((a + 1) * cos)),
            a * ((a + 1) - ((a - 1) * cos) - k),
            (a + 1) + ((a - 1) * cos) + k,
            -2 * ((a - 1) + ((a + 1) * cos)),
            (a + 1) + ((a - 1) * cos) - k);
    }

    /// <summary>Estante de agudos con pendiente 1: <paramref name="gainDb"/> por encima de <paramref name="hz"/>.</summary>
    public static Biquad HighShelf(double sampleRate, double hz, double gainDb)
    {
        double a = Math.Pow(10, gainDb / 40);
        (double cos, double alpha) = Angle(sampleRate, hz, Math.Sqrt(0.5));
        double k = 2 * Math.Sqrt(a) * alpha;
        return Normalize(
            a * ((a + 1) + ((a - 1) * cos) + k),
            -2 * a * ((a - 1) + ((a + 1) * cos)),
            a * ((a + 1) + ((a - 1) * cos) - k),
            (a + 1) - ((a - 1) * cos) + k,
            2 * ((a - 1) - ((a + 1) * cos)),
            (a + 1) - ((a - 1) * cos) - k);
    }

    /// <summary>Pasa altos de Butterworth (Q = 1/√2, −3 dB en <paramref name="hz"/>, 12 dB por octava).</summary>
    public static Biquad HighPass(double sampleRate, double hz)
    {
        (double cos, double alpha) = Angle(sampleRate, hz, Math.Sqrt(0.5));
        return Normalize((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    /// <summary>Pasa bajos de Butterworth (Q = 1/√2): dos en cascada son la mitad grave de un cruce Linkwitz-Riley de 4.º orden.</summary>
    public static Biquad LowPass(double sampleRate, double hz)
    {
        (double cos, double alpha) = Angle(sampleRate, hz, Math.Sqrt(0.5));
        return Normalize((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    /// <summary>Pasa todo de segundo orden; con Q = 1/√2 es la suma de las dos mitades de un cruce Linkwitz-Riley de 4.º orden.</summary>
    public static Biquad AllPass(double sampleRate, double hz)
    {
        (double cos, double alpha) = Angle(sampleRate, hz, Math.Sqrt(0.5));
        return Normalize(1 - alpha, -2 * cos, 1 + alpha, 1 + alpha, -2 * cos, 1 - alpha);
    }

    /// <summary>Filtra una muestra; <paramref name="z1"/> y <paramref name="z2"/> son los retardos.</summary>
    public double Tick(double x, ref double z1, ref double z2)
    {
        double y = (B0 * x) + z1;
        z1 = (B1 * x) - (A1 * y) + z2;
        z2 = (B2 * x) - (A2 * y);
        return y;
    }

    /// <summary>Ganancia del filtro en <paramref name="hz"/>, en dB, de su función de transferencia.</summary>
    public double MagnitudeDb(double sampleRate, double hz)
    {
        double w = 2 * Math.PI * hz / sampleRate;
        double c1 = Math.Cos(w), s1 = Math.Sin(w), c2 = Math.Cos(2 * w), s2 = Math.Sin(2 * w);
        double nr = B0 + (B1 * c1) + (B2 * c2), ni = -((B1 * s1) + (B2 * s2));
        double dr = 1 + (A1 * c1) + (A2 * c2), di = -((A1 * s1) + (A2 * s2));
        return 10 * Math.Log10(((nr * nr) + (ni * ni)) / ((dr * dr) + (di * di)));
    }

    /// <summary>Filtra en el sitio un canal de audio intercalado; <paramref name="state"/> son sus dos retardos.</summary>
    public void Process(Span<float> interleaved, int channel, int channels, Span<double> state)
    {
        double z1 = state[0], z2 = state[1];
        for (int i = channel; i < interleaved.Length; i += channels)
        {
            double x = interleaved[i];
            double y = (B0 * x) + z1;
            z1 = (B1 * x) - (A1 * y) + z2;
            z2 = (B2 * x) - (A2 * y);
            interleaved[i] = (float)y;
        }

        // Tras un silencio largo el estado cae a números subnormales, que la CPU procesa decenas de veces más lento.
        state[0] = Math.Abs(z1) < 1e-30 ? 0 : z1;
        state[1] = Math.Abs(z2) < 1e-30 ? 0 : z2;
    }

    private static (double Cos, double Alpha) Angle(double sampleRate, double hz, double q)
    {
        double w = 2 * Math.PI * Math.Clamp(hz, 1, (sampleRate / 2) - 1) / sampleRate;
        return (Math.Cos(w), Math.Sin(w) / (2 * Math.Max(q, 0.05)));
    }

    private static Biquad Normalize(double b0, double b1, double b2, double a0, double a1, double a2) =>
        new(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
}
