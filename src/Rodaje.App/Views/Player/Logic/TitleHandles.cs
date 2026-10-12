using System.Numerics;
using Rodaje.Playback.Composition;
using Rodaje.Render;

namespace Rodaje.App.Views.Player;

/// <summary>Dónde se dibujó un título en el último cuadro: su tamaño nativo en píxeles de la secuencia y su transformación.</summary>
public readonly record struct TitleBox(string ClipId, LayerTransform Transform, double Width, double Height);

/// <summary>Qué parte del título queda bajo el puntero.</summary>
public enum TitleGrip
{
    None,
    Body,
    TopLeft,
    TopRight,
    BottomRight,
    BottomLeft,
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

    /// <summary>Las cuatro esquinas en el panel (arriba izquierda, arriba derecha, abajo derecha, abajo izquierda); nulo si no se dibuja.</summary>
    public static Vector2[]? Corners(TitleBox box, int sequenceWidth, int sequenceHeight, ImageFrame frame)
    {
        if (LayerLayout.PlaceNative(sequenceWidth, sequenceHeight, box.Width, box.Height, box.Transform) is not { } placement)
        {
            return null;
        }

        return [frame.ToPanel(placement.TopLeft), frame.ToPanel(placement.TopRight), frame.ToPanel(placement.BottomRight), frame.ToPanel(placement.BottomLeft)];
    }

    /// <summary>Una esquina si el puntero está a <see cref="GripRadius"/> de ella (solo con <paramref name="corners"/> a la vista); si no, el cuerpo o nada.</summary>
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
