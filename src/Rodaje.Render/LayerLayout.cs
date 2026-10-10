using System.Numerics;
using Rodaje.Playback.Composition;

namespace Rodaje.Render;

/// <summary>
/// Dónde cae una capa: lleva un punto del espacio local (píxeles del rectángulo ajustado al cuadro, origen arriba a la
/// izquierda) a píxeles de la secuencia (origen arriba a la izquierda, y hacia abajo).
/// </summary>
/// <param name="Scale">Píxeles de la secuencia por píxel local: la escala de la transformación.</param>
public readonly record struct LayerPlacement(Matrix3x2 LocalToSequence, float LocalWidth, float LocalHeight, float Scale)
{
    public Vector2 TopLeft => Map(0, 0);

    public Vector2 TopRight => Map(LocalWidth, 0);

    public Vector2 BottomLeft => Map(0, LocalHeight);

    public Vector2 BottomRight => Map(LocalWidth, LocalHeight);

    public Vector2 Center => Map(LocalWidth / 2, LocalHeight / 2);

    public Vector2 Map(float x, float y) => Vector2.Transform(new Vector2(x, y), LocalToSequence);
}

/// <summary>Geometría de las capas: ajuste al cuadro ("contain") y después la transformación de <c>rodaje.transform</c>.</summary>
public static class LayerLayout
{
    /// <summary>
    /// Ajusta la imagen de <paramref name="displayWidth"/>×<paramref name="displayHeight"/> (SAR y rotación ya aplicados)
    /// centrada en la secuencia y le aplica <paramref name="transform"/>: escala alrededor de su centro, rotación horaria y
    /// desplazamiento del centro en píxeles de la secuencia. Devuelve <c>null</c> si no hay nada que dibujar.
    /// </summary>
    public static LayerPlacement? Place(int sequenceWidth, int sequenceHeight, int displayWidth, int displayHeight, LayerTransform transform)
    {
        if (!double.IsFinite(transform.X) || !double.IsFinite(transform.Y) || !double.IsFinite(transform.RotationDegrees)
            || !(transform.Scale > 0) || !double.IsFinite(transform.Scale))
        {
            return null;
        }

        PixelRect fit = VideoLayout.Fit(sequenceWidth, sequenceHeight, displayWidth, displayHeight);
        if (fit.IsEmpty)
        {
            return null;
        }

        double radians = transform.RotationDegrees % 360 * Math.PI / 180;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        double s = transform.Scale;
        double halfWidth = fit.Width / 2.0;
        double halfHeight = fit.Height / 2.0;
        double centerX = fit.X + halfWidth + transform.X;
        double centerY = fit.Y + halfHeight + transform.Y;
        // Con y hacia abajo, x' = x·cos − y·sin, y' = x·sin + y·cos gira en sentido horario en pantalla.
        var matrix = new Matrix3x2(
            (float)(s * cos), (float)(s * sin),
            (float)(-s * sin), (float)(s * cos),
            (float)(centerX - (s * ((halfWidth * cos) - (halfHeight * sin)))),
            (float)(centerY - (s * ((halfWidth * sin) + (halfHeight * cos)))));
        return new LayerPlacement(matrix, fit.Width, fit.Height, (float)s);
    }

    /// <summary>Coordenada de textura en un punto local, extendiendo linealmente la de las esquinas (también fuera del rectángulo).</summary>
    internal static TexCoord TexCoordAt(in QuadTexCoords corners, float x, float y, float width, float height)
    {
        float u = x / width;
        float v = y / height;
        return new TexCoord(
            corners.TopLeft.U + ((corners.TopRight.U - corners.TopLeft.U) * u) + ((corners.BottomLeft.U - corners.TopLeft.U) * v),
            corners.TopLeft.V + ((corners.TopRight.V - corners.TopLeft.V) * u) + ((corners.BottomLeft.V - corners.TopLeft.V) * v));
    }

    /// <summary>Píxeles de la secuencia a coordenadas normalizadas de Direct3D (y hacia arriba).</summary>
    internal static Vector2 ToNdc(Vector2 point, float sequenceWidth, float sequenceHeight) =>
        new((2f * point.X / sequenceWidth) - 1f, 1f - (2f * point.Y / sequenceHeight));
}
