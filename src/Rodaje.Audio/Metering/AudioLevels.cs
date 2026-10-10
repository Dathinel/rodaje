namespace Rodaje.Audio.Metering;

/// <summary>Conversión entre amplitud lineal (1 = escala completa) y decibelios de escala completa (dBFS).</summary>
public static class Decibels
{
    /// <summary>Por debajo de esto se considera silencio: −120 dBFS, muy por debajo del ruido de 24 bits.</summary>
    public const double SilenceThreshold = 1e-6;

    /// <returns><see cref="double.NegativeInfinity"/> para el silencio.</returns>
    public static double FromAmplitude(double amplitude)
    {
        double magnitude = Math.Abs(amplitude);
        return magnitude < SilenceThreshold || double.IsNaN(magnitude) ? double.NegativeInfinity : 20 * Math.Log10(magnitude);
    }

    public static double ToAmplitude(double decibels) =>
        double.IsNegativeInfinity(decibels) ? 0 : Math.Pow(10, decibels / 20);
}

/// <summary>Niveles de un canal en un tramo, en amplitud lineal.</summary>
/// <param name="Peak">Mayor valor absoluto de las muestras.</param>
/// <param name="TruePeak">Pico entre muestras estimado con sobremuestreo ×4; nunca menor que <paramref name="Peak"/>.</param>
/// <param name="Rms">Raíz del valor cuadrático medio.</param>
public readonly record struct ChannelLevel(float Peak, float TruePeak, float Rms)
{
    public double PeakDecibels => Decibels.FromAmplitude(Peak);

    public double TruePeakDecibels => Decibels.FromAmplitude(TruePeak);

    public double RmsDecibels => Decibels.FromAmplitude(Rms);
}

/// <summary>Niveles estéreo de un tramo de la mezcla.</summary>
/// <param name="Left">Canal izquierdo.</param>
/// <param name="Right">Canal derecho.</param>
/// <param name="Frames">Cuadros medidos para el pico; 0 si en el tramo no sonó nada (pausa o sin datos).</param>
/// <param name="Clipped">La suma de las pistas pasó de 0 dBFS antes del limitador suave en algún punto del tramo.</param>
public readonly record struct AudioLevels(ChannelLevel Left, ChannelLevel Right, int Frames, bool Clipped)
{
    public static AudioLevels Silence => default;

    /// <summary>Junta dos lecturas consecutivas: picos máximos, el RMS más reciente y la saturación de cualquiera.</summary>
    public AudioLevels Merge(AudioLevels newer) => new(
        new ChannelLevel(MathF.Max(Left.Peak, newer.Left.Peak), MathF.Max(Left.TruePeak, newer.Left.TruePeak), newer.Frames > 0 ? newer.Left.Rms : Left.Rms),
        new ChannelLevel(MathF.Max(Right.Peak, newer.Right.Peak), MathF.Max(Right.TruePeak, newer.Right.TruePeak), newer.Frames > 0 ? newer.Right.Rms : Right.Rms),
        Frames + newer.Frames,
        Clipped || newer.Clipped);
}
