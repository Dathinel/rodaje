namespace Rodaje.Analysis.Dsp;

/// <summary>Filtro biquad (forma directa transpuesta II) con los coeficientes del «Audio EQ Cookbook» de R. Bristow-Johnson.</summary>
internal sealed class Biquad
{
    private readonly double _b0, _b1, _b2, _a1, _a2;
    private double _z1, _z2;

    private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        _b0 = b0 / a0;
        _b1 = b1 / a0;
        _b2 = b2 / a0;
        _a1 = a1 / a0;
        _a2 = a2 / a0;
    }

    public static Biquad HighPass(double sampleRate, double frequency, double q = 0.7071)
    {
        (double cos, double alpha) = Prepare(sampleRate, frequency, q);
        return new((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public static Biquad LowPass(double sampleRate, double frequency, double q = 0.7071)
    {
        (double cos, double alpha) = Prepare(sampleRate, frequency, q);
        return new((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public double Process(double x)
    {
        double y = (_b0 * x) + _z1;
        _z1 = (_b1 * x) - (_a1 * y) + _z2;
        _z2 = (_b2 * x) - (_a2 * y);
        return y;
    }

    private static (double Cos, double Alpha) Prepare(double sampleRate, double frequency, double q)
    {
        double w = 2 * Math.PI * Math.Min(frequency, sampleRate * 0.45) / sampleRate;
        return (Math.Cos(w), Math.Sin(w) / (2 * q));
    }
}
