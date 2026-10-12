using Rodaje.Audio.Processing;

namespace Rodaje.App.Services.Audio;

/// <summary>La curva estática del compresor tal como la dibuja el panel: de −60 a 0 dB, con la compensación.</summary>
public static class TransferCurve
{
    public const double Floor = -60;

    /// <summary>Salida en dB para una entrada estable de <paramref name="input"/> dB.</summary>
    public static double Output(double input, double threshold, double ratio, double knee, double makeup) =>
        CompressorSettings.Curve(input, threshold, ratio, knee) + makeup;

    /// <summary>Puntos cada medio dB de entrada: la rodilla más estrecha que se dibuja (1 dB) ya lleva tres.</summary>
    public static IEnumerable<(double Input, double Output)> Points(double threshold, double ratio, double knee, double makeup)
    {
        for (double input = Floor; input <= 0; input += 0.5)
        {
            yield return (input, Output(input, threshold, ratio, knee, makeup));
        }
    }
}
