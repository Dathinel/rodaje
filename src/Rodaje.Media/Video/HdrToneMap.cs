namespace Rodaje.Media.Video;

/// <summary>
/// HDR → SDR en la CPU para lo que no pasa por el compositor (miniaturas, tira de fotogramas, cuadros para los módulos): la
/// misma cadena que <c>RenderShaders.HdrToSdr</c> (D-052) con la referencia automática, 100 nits en PQ y 1000 de pico en HLG.
/// </summary>
public static class HdrToneMap
{
    private static readonly float[] PqTable = Table(HdrTransfer.Pq);
    private static readonly float[] HlgTable = Table(HdrTransfer.Hlg);

    /// <summary>Convierte en el sitio una imagen BGRA de 8 bits; en SDR no toca nada.</summary>
    /// <param name="bgra">Píxeles BGRA de 8 bits.</param>
    /// <param name="color">Curva y primarios con que se grabó la imagen.</param>
    /// <param name="referenceNits">Brillo que pasa a blanco SDR; 0 elige 100 nits en PQ y 1000 de pico en HLG, como el compositor sin ajuste.</param>
    public static void ToSdrInPlace(Span<byte> bgra, VideoColor color, double referenceNits = 0)
    {
        if (color.Transfer == HdrTransfer.None)
        {
            return;
        }

        bool hlg = color.Transfer == HdrTransfer.Hlg;
        float[] table = hlg ? HlgTable : PqTable;
        double reference = referenceNits > 0 ? referenceNits : hlg ? 1000 : 100;
        double white = Hable(reference / 100);
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            double b = table[bgra[i]];
            double g = table[bgra[i + 1]];
            double r = table[bgra[i + 2]];
            if (hlg)
            {
                // BT.2100: OOTF de luminancia, gamma 1,2 a 1000 nits de pico (ajustada al pico elegido).
                double gamma = Math.Max(1, 1.2 + (0.42 * Math.Log10(reference / 1000)));
                double gain = reference * Math.Pow(Math.Max((0.2627 * r) + (0.6780 * g) + (0.0593 * b), 1e-6), gamma - 1);
                r *= gain;
                g *= gain;
                b *= gain;
            }

            if (color.Bt2020Primaries)
            {
                (r, g, b) = (
                    (1.660491 * r) - (0.587641 * g) - (0.072850 * b),
                    (-0.124550 * r) + (1.132900 * g) - (0.008350 * b),
                    (-0.018151 * r) - (0.100579 * g) + (1.118730 * b));
            }

            r = Math.Max(r, 0);
            g = Math.Max(g, 0);
            b = Math.Max(b, 0);
            double peak = Math.Max(r, Math.Max(g, b));
            double scale = Hable(peak / 100) / white / Math.Max(peak, 1e-6);
            bgra[i] = Code(b * scale);
            bgra[i + 1] = Code(g * scale);
            bgra[i + 2] = Code(r * scale);
        }
    }

    private static byte Code(double light)
    {
        double x = Math.Clamp(light, 0, 1);
        double encoded = x < 0.018 ? 4.5 * x : (1.099 * Math.Pow(x, 0.45)) - 0.099;
        return (byte)Math.Clamp(Math.Round(encoded * 255), 0, 255);
    }

    private static double Hable(double x) =>
        ((x * ((0.15 * x) + 0.05)) + 0.004) / ((x * ((0.15 * x) + 0.5)) + 0.06) - (0.02 / 0.3);

    /// <summary>Código de 8 bits → luz: nits en PQ (EOTF de ST 2084); señal lineal de escena en HLG (OETF inversa, sin OOTF).</summary>
    private static float[] Table(HdrTransfer transfer)
    {
        float[] table = new float[256];
        for (int code = 0; code < 256; code++)
        {
            double x = code / 255.0;
            table[code] = (float)(transfer == HdrTransfer.Pq
                ? 10000 * Math.Pow(Math.Max(Math.Pow(x, 1 / 78.84375) - 0.8359375, 0) / Math.Max(18.8515625 - (18.6875 * Math.Pow(x, 1 / 78.84375)), 1e-6), 1 / 0.1593017578125)
                : x <= 0.5 ? x * x / 3 : (Math.Exp((x - 0.55991073) / 0.17883277) + 0.28466892) / 12);
        }

        return table;
    }
}
