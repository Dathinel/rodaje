using System.Numerics;

namespace Rodaje.App.Views.Player;

/// <summary>Una guía a la que se pegó la capa: vertical (una x) u horizontal (una y), en píxeles de la secuencia.</summary>
public readonly record struct SnapLine(bool Vertical, double At, SnapKind Kind);

public enum SnapKind
{
    Center,
    Third,
    Edge,
}

/// <summary>
/// Guías de ajuste del visor, sin WinUI: al mover una capa, su centro y sus bordes (la caja que la encierra) se pegan al
/// centro, a los tercios y a los bordes del cuadro si quedan a menos de <see cref="MagnetPixels"/> de pantalla. Alt lo
/// apaga (como el ajuste de la línea de tiempo).
/// </summary>
public static class SnapGuides
{
    /// <summary>Distancia del imán en unidades del panel (independiente del zoom).</summary>
    public const double MagnetPixels = 8;

    /// <summary>Las líneas candidatas de un cuadro: centro, tercios y bordes.</summary>
    public static IReadOnlyList<SnapLine> Lines(int width, int height) =>
    [
        new(true, width / 2.0, SnapKind.Center),
        new(false, height / 2.0, SnapKind.Center),
        new(true, width / 3.0, SnapKind.Third),
        new(true, width * 2 / 3.0, SnapKind.Third),
        new(false, height / 3.0, SnapKind.Third),
        new(false, height * 2 / 3.0, SnapKind.Third),
        new(true, 0, SnapKind.Edge),
        new(true, width, SnapKind.Edge),
        new(false, 0, SnapKind.Edge),
        new(false, height, SnapKind.Edge),
    ];

    /// <summary>
    /// Cuánto corregir el desplazamiento de una caja que se mueve (centro <paramref name="center"/> y medidas, en píxeles de
    /// la secuencia) para pegarla, y a qué líneas quedó pegada (como mucho una vertical y una horizontal). El centro de la
    /// capa puede ir a cualquier línea; sus bordes, a los bordes y tercios.
    /// </summary>
    /// <param name="pixelsPerUnit">Unidades del panel por píxel de la secuencia (para medir el imán en pantalla).</param>
    public static (Vector2 Correction, IReadOnlyList<SnapLine> Hits) Snap(Vector2 center, Vector2 halfSize, int width, int height, double pixelsPerUnit)
    {
        double reach = MagnetPixels / Math.Max(pixelsPerUnit, 1e-6);
        IReadOnlyList<SnapLine> lines = Lines(width, height);
        (double dx, SnapLine? vertical) = Best(lines, true, [center.X, center.X - halfSize.X, center.X + halfSize.X], reach);
        (double dy, SnapLine? horizontal) = Best(lines, false, [center.Y, center.Y - halfSize.Y, center.Y + halfSize.Y], reach);
        var hits = new List<SnapLine>(2);
        if (vertical is { } v)
        {
            hits.Add(v);
        }

        if (horizontal is { } h)
        {
            hits.Add(h);
        }

        return (new Vector2((float)dx, (float)dy), hits);
    }

    // El primer candidato es el centro de la capa (va a cualquier línea); los otros, sus bordes (no al centro del cuadro).
    private static (double Delta, SnapLine? Line) Best(IReadOnlyList<SnapLine> lines, bool vertical, double[] candidates, double reach)
    {
        double best = reach;
        double delta = 0;
        SnapLine? hit = null;
        foreach (SnapLine line in lines)
        {
            if (line.Vertical != vertical)
            {
                continue;
            }

            for (int i = 0; i < candidates.Length; i++)
            {
                if (i > 0 && line.Kind == SnapKind.Center)
                {
                    continue;
                }

                double d = line.At - candidates[i];
                if (Math.Abs(d) < best || (Math.Abs(d) == best && hit is null && Math.Abs(d) < reach))
                {
                    best = Math.Abs(d);
                    delta = d;
                    hit = line;
                }
            }
        }

        return (delta, hit);
    }
}
