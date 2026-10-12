using System.Numerics;
using Rodaje.Playback.Composition;
using Rodaje.Render;

namespace Rodaje.App.Views.Player;

/// <summary>Dónde se dibujó un título en el último cuadro: su tamaño nativo en píxeles de la secuencia y su transformación.</summary>
/// <remarks>Con <see cref="Fitted"/> es una capa de video: su imagen de <c>Width</c>×<c>Height</c> se ajusta al cuadro antes de transformarse.</remarks>
public readonly record struct TitleBox(string ClipId, LayerTransform Transform, double Width, double Height)
{
    public bool Fitted { get; init; }
}

/// <summary>Qué parte del título queda bajo el puntero.</summary>
public enum TitleGrip
{
    None,
    Body,
    TopLeft,
    TopRight,
    BottomRight,
    BottomLeft,

    /// <summary>El asa de girar, encima del borde de arriba.</summary>
    Rotate,
}

/// <summary>Dónde pone el render la imagen en el panel: esquina y unidades del panel por píxel de la secuencia.</summary>
public readonly record struct ImageFrame(double Left, double Top, double Scale)
{
    public Vector2 ToPanel(Vector2 sequence) => new((float)(Left + (sequence.X * Scale)), (float)(Top + (sequence.Y * Scale)));

    public Vector2 ToSequence(double x, double y) => new((float)((x - Left) / Scale), (float)((y - Top) / Scale));
}

/// <summary>
/// Las asas de un título en el visor, sin WinUI: la caja con la misma geometría que el render
/// (<see cref="LayerLayout.PlaceNative"/>), el acierto del puntero y lo que un arrastre hace con la transformación.
/// </summary>
public static class TitleHandles
{
    /// <summary>Radio de acierto de una esquina, en unidades del panel.</summary>
    public const double GripRadius = 8;

    public const double MinScale = 0.05;

    public const double MaxScale = 20;

    /// <summary>Cuánto sobresale el asa de girar del borde de arriba, en unidades del panel.</summary>
    public const double RotateDistance = 24;

    /// <summary>Con Mayús, el giro va de este en este paso (grados).</summary>
    public const double RotateSnap = 15;

    /// <summary>El asa de girar: por encima del centro del borde de arriba, hacia fuera de la caja (gira con ella).</summary>
    public static Vector2 RotateGrip(Vector2[] corners)
    {
        ArgumentNullException.ThrowIfNull(corners);
        Vector2 top = (corners[0] + corners[1]) / 2;
        Vector2 bottom = (corners[3] + corners[2]) / 2;
        Vector2 up = top - bottom;
        return up.LengthSquared() < 1e-6f ? top : top + (Vector2.Normalize(up) * (float)RotateDistance);
    }

    /// <summary>
    /// El giro al arrastrar el asa: el de partida más lo que giró el puntero alrededor del centro (en el sentido de las
    /// agujas del reloj, como <c>rotation</c>); con <paramref name="snap"/>, redondeado a <see cref="RotateSnap"/>.
    /// </summary>
    public static double Rotation(double startRotation, Vector2 center, Vector2 start, Vector2 now, bool snap)
    {
        Vector2 from = start - center;
        Vector2 to = now - center;
        if (from.LengthSquared() < 1e-6f || to.LengthSquared() < 1e-6f)
        {
            return startRotation;
        }

        double delta = (Math.Atan2(to.Y, to.X) - Math.Atan2(from.Y, from.X)) * 180 / Math.PI;
        delta -= 360 * Math.Round(delta / 360);
        double rotation = startRotation + delta;
        return snap ? Math.Round(rotation / RotateSnap) * RotateSnap : Math.Round(rotation, 2);
    }

    /// <summary>El rectángulo recto que encierra la caja, en unidades del panel: dónde va el cuadro de texto al escribir.</summary>
    public static (double Left, double Top, double Width, double Height) Bounds(Vector2[] corners)
    {
        ArgumentNullException.ThrowIfNull(corners);
        float left = corners.Min(c => c.X);
        float top = corners.Min(c => c.Y);
        return (left, top, corners.Max(c => c.X) - left, corners.Max(c => c.Y) - top);
    }

    /// <summary>Si el texto empieza (en su primera letra con dirección) en una escritura de derecha a izquierda.</summary>
    public static bool IsRightToLeft(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (char c in text)
        {
            if (c is (>= '\u0590' and <= '\u08FF') or (>= '\uFB1D' and <= '\uFDFF') or (>= '\uFE70' and <= '\uFEFC'))
            {
                return true;
            }

            if (char.IsLetter(c))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>Las cuatro esquinas en el panel (arriba izquierda, arriba derecha, abajo derecha, abajo izquierda); nulo si no se dibuja.</summary>
    public static Vector2[]? Corners(TitleBox box, int sequenceWidth, int sequenceHeight, ImageFrame frame)
    {
        if (Placement(box, sequenceWidth, sequenceHeight) is not { } placement)
        {
            return null;
        }

        return [frame.ToPanel(placement.TopLeft), frame.ToPanel(placement.TopRight), frame.ToPanel(placement.BottomRight), frame.ToPanel(placement.BottomLeft)];
    }

    /// <summary>Dónde la dibuja el render: a su tamaño (un título) o ajustada al cuadro (una capa de video).</summary>
    public static LayerPlacement? Placement(TitleBox box, int sequenceWidth, int sequenceHeight) => box.Fitted
        ? LayerLayout.Place(sequenceWidth, sequenceHeight, (int)box.Width, (int)box.Height, box.Transform)
        : LayerLayout.PlaceNative(sequenceWidth, sequenceHeight, box.Width, box.Height, box.Transform);

    /// <summary>
    /// Una esquina o el asa de girar si el puntero está a <see cref="GripRadius"/> de ella (solo con las asas a la vista);
    /// si no, el cuerpo o nada.
    /// </summary>
    public static TitleGrip Hit(Vector2[] corners, double x, double y, bool withCorners)
    {
        ArgumentNullException.ThrowIfNull(corners);
        var point = new Vector2((float)x, (float)y);
        if (withCorners)
        {
            for (int i = 0; i < 4; i++)
            {
                if (Vector2.Distance(corners[i], point) <= GripRadius)
                {
                    return TitleGrip.TopLeft + i;
                }
            }

            if (Vector2.Distance(RotateGrip(corners), point) <= GripRadius)
            {
                return TitleGrip.Rotate;
            }
        }

        return Inside(corners, point) ? TitleGrip.Body : TitleGrip.None;
    }

    /// <summary>El centro de la caja en píxeles de la secuencia: el ancla de la escala.</summary>
    public static Vector2 Center(TitleBox box, int sequenceWidth, int sequenceHeight) =>
        new((float)((sequenceWidth / 2.0) + box.Transform.X), (float)((sequenceHeight / 2.0) + box.Transform.Y));

    /// <summary>La escala al arrastrar una esquina: la de partida por lo que creció la distancia al centro, acotada.</summary>
    public static double Scale(double startScale, Vector2 center, Vector2 start, Vector2 now)
    {
        float from = Vector2.Distance(center, start);
        if (from < 1e-3f)
        {
            return startScale;
        }

        return Math.Clamp(startScale * Vector2.Distance(center, now) / from, MinScale, MaxScale);
    }

    // Convexo: el punto está del mismo lado de las cuatro aristas.
    private static bool Inside(Vector2[] corners, Vector2 point)
    {
        int sign = 0;
        for (int i = 0; i < 4; i++)
        {
            Vector2 a = corners[i];
            Vector2 b = corners[(i + 1) % 4];
            float cross = ((b.X - a.X) * (point.Y - a.Y)) - ((b.Y - a.Y) * (point.X - a.X));
            int s = Math.Sign(cross);
            if (s != 0)
            {
                if (sign != 0 && s != sign)
                {
                    return false;
                }

                sign = s;
            }
        }

        return sign != 0;
    }
}
