using System.Numerics;
using Rodaje.Core.Effects;
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

    /// <summary>Píxeles de la secuencia por píxel local a lo largo de cada eje local (con escala por eje e inclinación).</summary>
    public float AxisScaleX => new Vector2(LocalToSequence.M11, LocalToSequence.M12).Length();

    public float AxisScaleY => new Vector2(LocalToSequence.M21, LocalToSequence.M22).Length();

    /// <summary>La mayor escala de un eje: lo que necesita un intermedio para no perder detalle (<see cref="Scale"/> si es uniforme).</summary>
    public float MaxAxisScale
    {
        get
        {
            float max = Math.Max(AxisScaleX, AxisScaleY);
            return Math.Abs(max - Scale) <= 1e-4f * Scale ? Scale : max;
        }
    }

    /// <summary>
    /// Distancia en la secuencia por unidad de distancia local a un lado vertical (x = 0 o x = ancho): |det| / |eje y|.
    /// Con escala uniforme y sin inclinación es <see cref="Scale"/>.
    /// </summary>
    public float EdgeScaleX => Math.Abs(LocalToSequence.GetDeterminant()) / Math.Max(AxisScaleY, 1e-12f);

    /// <summary>Lo mismo hacia un lado horizontal: |det| / |eje x|.</summary>
    public float EdgeScaleY => Math.Abs(LocalToSequence.GetDeterminant()) / Math.Max(AxisScaleX, 1e-12f);
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

        (displayWidth, displayHeight) = CroppedSize(displayWidth, displayHeight, transform.Crop);
        PixelRect fit = VideoLayout.Fit(sequenceWidth, sequenceHeight, displayWidth, displayHeight);
        return fit.IsEmpty ? null : Transformed(fit.X + (fit.Width / 2.0), fit.Y + (fit.Height / 2.0), fit.Width, fit.Height,
            Stabilize(transform, fit.Height));
    }

    /// <summary>Lo que queda de una imagen de <paramref name="width"/>×<paramref name="height"/> tras el recorte, al menos 1×1.</summary>
    public static (int Width, int Height) CroppedSize(int width, int height, CropRect? crop) =>
        crop is not { } c || c.IsNone
            ? (width, height)
            : (Math.Max(1, (int)Math.Round(width * c.Width)), Math.Max(1, (int)Math.Round(height * c.Height)));

    /// <summary>
    /// Compone la corrección de estabilizar (en altos de la imagen ajustada, sobre su centro) antes de la transformación:
    /// <c>S·R(u)·(s·R(θ)·q + h·t) = S·s·R(u + θ)·q + S·R(u)·h·t</c>, así sigue siendo una sola escala, giro y desplazamiento.
    /// </summary>
    internal static LayerTransform Stabilize(LayerTransform transform, double fittedHeight)
    {
        if (transform.Stabilization is not { } c || !double.IsFinite(c.A) || !double.IsFinite(c.B) || !double.IsFinite(c.X)
            || !double.IsFinite(c.Y) || !(c.Scale > 0))
        {
            return transform;
        }

        double radians = transform.RotationDegrees % 360 * Math.PI / 180;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        double tx = c.X * fittedHeight * transform.Scale;
        double ty = c.Y * fittedHeight * transform.Scale;
        return transform with
        {
            X = transform.X + (tx * cos) - (ty * sin),
            Y = transform.Y + (tx * sin) + (ty * cos),
            Scale = transform.Scale * c.Scale,
            RotationDegrees = transform.RotationDegrees + c.RotationDegrees,
            Stabilization = null,
        };
    }

    /// <summary>
    /// Una capa generada (un título): a su tamaño nativo de <paramref name="width"/>×<paramref name="height"/> píxeles de
    /// la secuencia, centrada, sin ajuste al cuadro; después la misma transformación que <see cref="Place"/>.
    /// </summary>
    public static LayerPlacement? PlaceNative(int sequenceWidth, int sequenceHeight, double width, double height, LayerTransform transform)
    {
        if (!double.IsFinite(transform.X) || !double.IsFinite(transform.Y) || !double.IsFinite(transform.RotationDegrees)
            || !(transform.Scale > 0) || !double.IsFinite(transform.Scale) || !(width > 0) || !(height > 0)
            || !double.IsFinite(width) || !double.IsFinite(height))
        {
            return null;
        }

        return Transformed(sequenceWidth / 2.0, sequenceHeight / 2.0, width, height, transform);
    }

    private static LayerPlacement? Transformed(double rectCenterX, double rectCenterY, double width, double height, LayerTransform transform)
    {
        if (transform.Shape is { } shape && !shape.IsNone)
        {
            return Shaped(rectCenterX, rectCenterY, width, height, transform, shape);
        }

        double radians = transform.RotationDegrees % 360 * Math.PI / 180;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        double s = transform.Scale;
        double halfWidth = width / 2.0;
        double halfHeight = height / 2.0;
        double centerX = rectCenterX + transform.X;
        double centerY = rectCenterY + transform.Y;
        // Con y hacia abajo, x' = x·cos − y·sin, y' = x·sin + y·cos gira en sentido horario en pantalla.
        var matrix = new Matrix3x2(
            (float)(s * cos), (float)(s * sin),
            (float)(-s * sin), (float)(s * cos),
            (float)(centerX - (s * ((halfWidth * cos) - (halfHeight * sin)))),
            (float)(centerY - (s * ((halfWidth * sin) + (halfHeight * cos)))));
        return new LayerPlacement(matrix, (float)width, (float)height, (float)s);
    }

    /// <summary>
    /// Con ancla, escala por eje, inclinación o volteo: <c>q' = C + t + a + R·K·S·(F(q) − c − a)</c> (ver
    /// <see cref="LayerShape"/>). Nulo si la capa queda aplastada (determinante casi 0) o algún valor no es finito.
    /// </summary>
    private static LayerPlacement? Shaped(double rectCenterX, double rectCenterY, double width, double height, LayerTransform transform,
        LayerShape shape)
    {
        if (!double.IsFinite(shape.AnchorX) || !double.IsFinite(shape.AnchorY) || !double.IsFinite(shape.ScaleX) || !double.IsFinite(shape.ScaleY)
            || !double.IsFinite(shape.SkewX) || !double.IsFinite(shape.SkewY))
        {
            return null;
        }

        double radians = transform.RotationDegrees % 360 * Math.PI / 180;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        double sx = transform.Scale * shape.ScaleX;
        double sy = transform.Scale * shape.ScaleY;
        double kx = Math.Tan(Math.Clamp(shape.SkewX, -89, 89) * Math.PI / 180);
        double ky = Math.Tan(Math.Clamp(shape.SkewY, -89, 89) * Math.PI / 180);

        // Columnas de A = R·K·S con K = [[1, kx], [ky, 1]] (y hacia abajo): a = A·(1, 0), b = A·(0, 1).
        double kax = sx;
        double kay = ky * sx;
        double kbx = kx * sy;
        double kby = sy;
        double ax = (kax * cos) - (kay * sin);
        double ay = (kax * sin) + (kay * cos);
        double bx = (kbx * cos) - (kby * sin);
        double by = (kbx * sin) + (kby * cos);
        double det = (ax * by) - (ay * bx);
        if (!(Math.Abs(det) > 1e-9) || !double.IsFinite(det))
        {
            return null;
        }

        double halfWidth = width / 2.0;
        double halfHeight = height / 2.0;
        double pivotX = halfWidth + shape.AnchorX;
        double pivotY = halfHeight + shape.AnchorY;
        double originX = rectCenterX + transform.X + shape.AnchorX;
        double originY = rectCenterY + transform.Y + shape.AnchorY;

        // F voltea alrededor del centro de la imagen: x → ancho − x.
        double fx = shape.FlipX ? -1 : 1;
        double fy = shape.FlipY ? -1 : 1;
        double fxOffset = shape.FlipX ? width : 0;
        double fyOffset = shape.FlipY ? height : 0;

        // q' = origen + A·(F(q) − pivote), con F(q) = (fx·x + fxOffset, fy·y + fyOffset).
        double m11 = ax * fx;
        double m12 = ay * fx;
        double m21 = bx * fy;
        double m22 = by * fy;
        double dx = fxOffset - pivotX;
        double dy = fyOffset - pivotY;
        double tx = originX + (ax * dx) + (bx * dy);
        double ty = originY + (ay * dx) + (by * dy);
        var matrix = new Matrix3x2((float)m11, (float)m12, (float)m21, (float)m22, (float)tx, (float)ty);
        return new LayerPlacement(matrix, (float)width, (float)height, (float)Math.Sqrt(Math.Abs(det)));
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
