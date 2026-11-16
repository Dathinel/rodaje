namespace Rodaje.Analysis.Video.Interpolation;

/// <summary>
/// Cuadro 4:2:0 en planos: Y de Width×Height y UV entrelazado de (Width/2)×(Height/2) pares. Códigos sin normalizar:
/// 8 bits (NV12) en 0..255; 16 bits (P010/P016) en 0..65535 con el dato en los bits altos. Width y Height pares.
/// </summary>
public sealed class PlanarFrame
{
    public PlanarFrame(int width, int height, int bitDepth, ushort[] y, ushort[] uv)
    {
        if (width <= 0 || (width & 1) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "El ancho debe ser positivo y par.");
        }

        if (height <= 0 || (height & 1) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "El alto debe ser positivo y par.");
        }

        if (bitDepth is not (8 or 16))
        {
            throw new ArgumentOutOfRangeException(nameof(bitDepth), "La profundidad de bits debe ser 8 o 16.");
        }

        ArgumentNullException.ThrowIfNull(y);
        ArgumentNullException.ThrowIfNull(uv);

        if (y.Length != width * height)
        {
            throw new ArgumentException("La longitud de Y debe coincidir con width * height.", nameof(y));
        }

        if (uv.Length != (width / 2) * (height / 2) * 2)
        {
            throw new ArgumentException("La longitud de UV debe coincidir con (width / 2) * (height / 2) * 2.", nameof(uv));
        }

        Width = width;
        Height = height;
        BitDepth = bitDepth;
        Y = y;
        UV = uv;
    }

    public int Width { get; }

    public int Height { get; }

    public int BitDepth { get; }

    public ushort[] Y { get; }

    public ushort[] UV { get; }
}
