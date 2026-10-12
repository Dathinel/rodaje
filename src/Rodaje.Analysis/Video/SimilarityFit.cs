using System.Numerics;

namespace Rodaje.Analysis.Video;

/// <summary>
/// Similitud que mejor lleva unos puntos a otros: RANSAC con muestras de dos pares (Fischler y Bolles, 1981) y después
/// mínimos cuadrados cerrados sobre los que coinciden (Umeyama, 1991, sin reflexión).
/// </summary>
internal static class SimilarityFit
{
    private const int Iterations = 160;

    /// <summary>
    /// Ajusta <paramref name="from"/> → <paramref name="to"/> (en coordenadas de <see cref="Similarity"/>). Devuelve cuántos
    /// pares coinciden con el resultado a menos de <paramref name="tolerance"/>; 0 si no hay dos pares útiles.
    /// </summary>
    public static int Fit(ReadOnlySpan<Vector2> from, ReadOnlySpan<Vector2> to, float tolerance, int seed, out Similarity result)
    {
        result = Similarity.Identity;
        int n = from.Length;
        if (n < 2)
        {
            return 0;
        }

        uint state = (uint)seed * 2654435761u | 1u;
        float tolerance2 = tolerance * tolerance;
        int bestCount = 0;
        Similarity best = Similarity.Identity;
        for (int it = 0; it < Iterations; it++)
        {
            int i = Next(ref state, n);
            int j = Next(ref state, n - 1);
            if (j >= i)
            {
                j++;
            }

            if (!FromTwo(from[i], from[j], to[i], to[j], out Similarity candidate))
            {
                continue;
            }

            int count = Count(from, to, candidate, tolerance2, null);
            if (count > bestCount)
            {
                bestCount = count;
                best = candidate;
                if (count == n)
                {
                    break;
                }
            }
        }

        if (bestCount < 2)
        {
            return 0;
        }

        // Dos rondas de mínimos cuadrados: con los que coinciden con la muestra y con los que coinciden con el ajuste.
        bool[] inliers = new bool[n];
        Similarity refined = best;
        for (int round = 0; round < 2; round++)
        {
            Count(from, to, refined, tolerance2, inliers);
            if (!LeastSquares(from, to, inliers, out Similarity fitted))
            {
                break;
            }

            refined = fitted;
        }

        result = refined;
        return Count(from, to, refined, tolerance2, null);
    }

    /// <summary>Xorshift de 32 bits: muestras repetibles sin <see cref="Random"/>.</summary>
    private static int Next(ref uint state, int bound)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return (int)((ulong)state * (uint)bound >> 32);
    }

    private static bool FromTwo(Vector2 p1, Vector2 p2, Vector2 q1, Vector2 q2, out Similarity s)
    {
        // Con números complejos: z = (q2 − q1) / (p2 − p1), t = q1 − z·p1.
        double px = p2.X - p1.X, py = p2.Y - p1.Y;
        double qx = q2.X - q1.X, qy = q2.Y - q1.Y;
        double d = (px * px) + (py * py);
        if (d < 1e-8)
        {
            s = Similarity.Identity;
            return false;
        }

        double a = ((qx * px) + (qy * py)) / d;
        double b = ((qy * px) - (qx * py)) / d;
        s = new Similarity(a, b, q1.X - ((a * p1.X) - (b * p1.Y)), q1.Y - ((b * p1.X) + (a * p1.Y)));
        return s.Scale is > 0.5 and < 2;
    }

    private static int Count(ReadOnlySpan<Vector2> from, ReadOnlySpan<Vector2> to, Similarity s, float tolerance2, bool[]? inliers)
    {
        int count = 0;
        for (int k = 0; k < from.Length; k++)
        {
            (double x, double y) = s.Apply(from[k].X, from[k].Y);
            double ex = x - to[k].X;
            double ey = y - to[k].Y;
            bool inside = (ex * ex) + (ey * ey) <= tolerance2;
            if (inliers is not null)
            {
                inliers[k] = inside;
            }

            count += inside ? 1 : 0;
        }

        return count;
    }

    private static bool LeastSquares(ReadOnlySpan<Vector2> from, ReadOnlySpan<Vector2> to, bool[] use, out Similarity s)
    {
        s = Similarity.Identity;
        double mpx = 0, mpy = 0, mqx = 0, mqy = 0;
        int n = 0;
        for (int k = 0; k < from.Length; k++)
        {
            if (use[k])
            {
                mpx += from[k].X;
                mpy += from[k].Y;
                mqx += to[k].X;
                mqy += to[k].Y;
                n++;
            }
        }

        if (n < 2)
        {
            return false;
        }

        mpx /= n;
        mpy /= n;
        mqx /= n;
        mqy /= n;
        double dot = 0, cross = 0, norm = 0;
        for (int k = 0; k < from.Length; k++)
        {
            if (!use[k])
            {
                continue;
            }

            double px = from[k].X - mpx, py = from[k].Y - mpy;
            double qx = to[k].X - mqx, qy = to[k].Y - mqy;
            dot += (px * qx) + (py * qy);
            cross += (px * qy) - (py * qx);
            norm += (px * px) + (py * py);
        }

        if (norm < 1e-12)
        {
            return false;
        }

        double a = dot / norm;
        double b = cross / norm;
        s = new Similarity(a, b, mqx - ((a * mpx) - (b * mpy)), mqy - ((b * mpx) + (a * mpy)));
        return s.IsFinite;
    }
}
