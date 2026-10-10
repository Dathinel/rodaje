using Rodaje.Core.Timing;

namespace Rodaje.Render;

/// <summary>Metadatos del flujo que cambian la forma de la imagen en pantalla: relación de aspecto del píxel y rotación.</summary>
/// <param name="SampleAspectRatio">SAR del flujo (<c>MediaProbe</c>); 1:1 si no declara ninguna.</param>
/// <param name="Rotation">Rotación de pantalla en grados en sentido horario: 0, 90, 180 o 270.</param>
public readonly record struct SourceGeometry(Rational SampleAspectRatio, int Rotation)
{
    public static SourceGeometry Square { get; } = new(new Rational(1, 1), 0);

    /// <summary>Tamaño en pantalla de una imagen de <paramref name="width"/>×<paramref name="height"/> muestras, como lo calcula <c>MediaProbe</c>.</summary>
    public (int Width, int Height) DisplaySize(int width, int height)
    {
        Rational sar = SampleAspectRatio.Num > 0 ? SampleAspectRatio : new Rational(1, 1);
        int displayWidth = Math.Max(1, (int)Math.Round(width * sar.ToDouble()));
        return VideoLayout.NormalizeRotation(Rotation) is 90 or 270 ? (height, displayWidth) : (displayWidth, height);
    }
}
