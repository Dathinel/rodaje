using System.Numerics;

namespace Rodaje.Core.Effects;

/// <summary>
/// Del cuadro gris de una máscara de imagen (la de «Quitar fondo») a una Bézier: la mancha más grande por encima del
/// umbral, su contorno exterior (seguimiento de Moore), simplificado y con manejadores suaves.
/// </summary>
public static class MaskContour
{
    /// <summary>
    /// El contorno de la mancha más grande de <paramref name="gray"/> (<paramref name="width"/> × <paramref name="height"/>
    /// bytes, fila a fila) en coordenadas de la imagen (0–1), como vértices suaves; vacío si no hay mancha de al menos
    /// <paramref name="minArea"/> píxeles.
    /// </summary>
    /// <param name="gray">El cuadro gris.</param>
    /// <param name="width">Ancho del cuadro.</param>
    /// <param name="height">Alto del cuadro.</param>
    /// <param name="threshold">De 0 a 255: lo que pasa es la forma.</param>
    /// <param name="tolerance">Simplificación, en píxeles del cuadro.</param>
    /// <param name="minArea">Área mínima de la mancha, en píxeles.</param>
    public static MaskVertex[] Trace(ReadOnlySpan<byte> gray, int width, int height, byte threshold = 128, double tolerance = 1.5, int minArea = 16)
    {
        if (width < 2 || height < 2 || gray.Length < width * height)
        {
            return [];
        }

        int[] labels = new int[width * height];
        (int seed, int area) = Largest(gray, width, height, threshold, labels);
        if (seed < 0 || area < minArea)
        {
            return [];
        }

        List<Vector2> boundary = Boundary(labels, width, height, labels[seed], seed);
        if (boundary.Count < 3)
        {
            return [];
        }

        // Se cierra el contorno simplificando desde el punto más lejano al inicio, así ninguno de los dos extremos sobra.
        int far = 0;
        float farthest = 0;
        for (int i = 1; i < boundary.Count; i++)
        {
            float d = Vector2.DistanceSquared(boundary[i], boundary[0]);
            if (d > farthest)
            {
                farthest = d;
                far = i;
            }
        }

        List<Vector2> first = MaskGeometry.Simplify(boundary[..(far + 1)], tolerance);
        List<Vector2> second = MaskGeometry.Simplify([.. boundary[far..], boundary[0]], tolerance);
        List<Vector2> points = [.. first, .. second[1..^1]];
        while (points.Count > MaskPath.MaxVertices)
        {
            tolerance *= 1.5;
            first = MaskGeometry.Simplify(boundary[..(far + 1)], tolerance);
            second = MaskGeometry.Simplify([.. boundary[far..], boundary[0]], tolerance);
            points = [.. first, .. second[1..^1]];
        }

        if (points.Count < 3)
        {
            return [];
        }

        var size = new Vector2(width, height);
        return MaskPath.Smooth([.. points.Select(p => (p + new Vector2(0.5f)) / size)], closed: true, tension: 0.8f);
    }

    // Etiqueta las manchas (vecindad de 4) con una pila y devuelve el primer píxel y el área de la mayor.
    private static (int Seed, int Area) Largest(ReadOnlySpan<byte> gray, int width, int height, byte threshold, int[] labels)
    {
        int label = 0;
        int bestSeed = -1, bestArea = 0;
        var stack = new Stack<int>();
        for (int start = 0; start < width * height; start++)
        {
            if (labels[start] != 0 || gray[start] < threshold)
            {
                continue;
            }

            label++;
            int area = 0;
            labels[start] = label;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                area++;
                int x = i % width, y = i / width;
                Visit(x > 0, i - 1, gray);
                Visit(x < width - 1, i + 1, gray);
                Visit(y > 0, i - width, gray);
                Visit(y < height - 1, i + width, gray);
            }

            if (area > bestArea)
            {
                bestArea = area;
                bestSeed = start;
            }

            void Visit(bool inside, int j, ReadOnlySpan<byte> image)
            {
                if (inside && labels[j] == 0 && image[j] >= threshold)
                {
                    labels[j] = label;
                    stack.Push(j);
                }
            }
        }

        return (bestSeed, bestArea);
    }

    // Seguimiento de Moore del borde exterior desde el primer píxel de la mancha (el de más arriba a la izquierda).
    private static List<Vector2> Boundary(int[] labels, int width, int height, int label, int seed)
    {
        ReadOnlySpan<int> dx = [1, 1, 0, -1, -1, -1, 0, 1];
        ReadOnlySpan<int> dy = [0, 1, 1, 1, 0, -1, -1, -1];
        bool Inside(int x, int y) => x >= 0 && y >= 0 && x < width && y < height && labels[(y * width) + x] == label;

        var points = new List<Vector2>();
        int sx = seed % width, sy = seed / width;
        int cx = sx, cy = sy;
        int direction = 7;
        int limit = 4 * width * height;
        do
        {
            points.Add(new Vector2(cx, cy));
            int search = (direction + (direction % 2 == 0 ? 7 : 6)) % 8;
            bool found = false;
            for (int k = 0; k < 8; k++)
            {
                int d = (search + k) % 8;
                int nx = cx + dx[d], ny = cy + dy[d];
                if (Inside(nx, ny))
                {
                    cx = nx;
                    cy = ny;
                    direction = d;
                    found = true;
                    break;
                }
            }

            if (!found || --limit <= 0)
            {
                break;
            }
        }
        while (cx != sx || cy != sy);

        return points;
    }
}
