using Rodaje.Media.Video;

namespace Rodaje.Engine.Presentation;

/// <summary>
/// Lleva lo que devuelve el muestreo UNORM de un plano Y o UV a valores normalizados: <c>y = muestra *
/// LumaScale + LumaBias</c> da 0 en el negro y 1 en el blanco nominal, y la croma queda centrada en 0.
/// </summary>
/// <remarks>
/// En P010 el dato ocupa los 10 bits altos de una palabra de 16: la vista <c>R16_UNORM</c> devuelve
/// <c>código * 64 / 65535</c>, no <c>código / 1023</c>, y la escala lo corrige. El rango limitado pone el negro
/// en 16 y el blanco en 235 (64 y 940 en 10 bits); el completo usa todos los códigos.
/// </remarks>
public readonly record struct YuvRange(float LumaScale, float LumaBias, float ChromaScale, float ChromaBias)
{
    private const double P010CodesPerUnit = 65535.0 / 64.0;

    public static YuvRange LimitedNv12 { get; } = Limited(255.0, 1.0);

    public static YuvRange LimitedP010 { get; } = Limited(P010CodesPerUnit, 4.0);

    public static YuvRange FullNv12 { get; } = Full(255.0, 255);

    public static YuvRange FullP010 { get; } = Full(P010CodesPerUnit, 1023);

    public static YuvRange For(bool tenBit, bool fullRange) => (tenBit, fullRange) switch
    {
        (false, false) => LimitedNv12,
        (true, false) => LimitedP010,
        (false, true) => FullNv12,
        (true, true) => FullP010,
    };

    private static YuvRange Limited(double codesPerUnit, double step) => new(
        (float)(codesPerUnit / (219 * step)),
        (float)(-16.0 / 219.0),
        (float)(codesPerUnit / (224 * step)),
        (float)(-128.0 / 224.0));

    private static YuvRange Full(double codesPerUnit, int maxCode) => new(
        (float)(codesPerUnit / maxCode),
        0f,
        (float)(codesPerUnit / maxCode),
        (float)(-((maxCode + 1) / 2.0) / maxCode));
}

/// <summary>
/// Coeficientes de Y'CbCr normalizado a R'G'B': <c>R = Y + RedFromCr·Cr</c>, <c>G = Y − GreenFromCb·Cb −
/// GreenFromCr·Cr</c> y <c>B = Y + BlueFromCb·Cb</c>, a partir de los pesos Kr y Kb de cada norma.
/// </summary>
public readonly record struct YuvToRgb(float RedFromCr, float GreenFromCb, float GreenFromCr, float BlueFromCb)
{
    public static YuvToRgb Bt601 { get; } = FromWeights(0.299, 0.114);

    public static YuvToRgb Bt709 { get; } = FromWeights(0.2126, 0.0722);

    public static YuvToRgb Bt2020 { get; } = FromWeights(0.2627, 0.0593);

    public static YuvToRgb For(YuvMatrix matrix) => matrix switch
    {
        YuvMatrix.Bt601 => Bt601,
        YuvMatrix.Bt2020 => Bt2020,
        _ => Bt709,
    };

    public static YuvToRgb FromWeights(double kr, double kb)
    {
        double kg = 1 - kr - kb;
        return new(
            (float)(2 * (1 - kr)),
            (float)(2 * kb * (1 - kb) / kg),
            (float)(2 * kr * (1 - kr) / kg),
            (float)(2 * (1 - kb)));
    }
}
