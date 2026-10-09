namespace Rodaje.Engine.Presentation;

/// <summary>
/// Lleva lo que devuelve el muestreo UNORM de un plano Y o UV a valores normalizados de rango limitado
/// (BT.709): <c>y = muestra * LumaScale + LumaBias</c> da 0 en el negro y 1 en el blanco nominal, y la
/// croma queda centrada en 0.
/// </summary>
/// <remarks>
/// En P010 el dato ocupa los 10 bits altos de una palabra de 16: la vista <c>R16_UNORM</c> devuelve
/// <c>código * 64 / 65535</c>, no <c>código / 1023</c>, y la escala lo corrige.
/// </remarks>
public readonly record struct YuvRange(float LumaScale, float LumaBias, float ChromaScale, float ChromaBias)
{
    public static YuvRange LimitedNv12 { get; } = Limited(255.0, 1.0);

    public static YuvRange LimitedP010 { get; } = Limited(65535.0 / 64.0, 4.0);

    public static YuvRange For(bool tenBit) => tenBit ? LimitedP010 : LimitedNv12;

    private static YuvRange Limited(double codesPerUnit, double step) => new(
        (float)(codesPerUnit / (219 * step)),
        (float)(-16.0 / 219.0),
        (float)(codesPerUnit / (224 * step)),
        (float)(-128.0 / 224.0));
}
