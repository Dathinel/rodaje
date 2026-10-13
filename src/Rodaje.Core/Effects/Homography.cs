using System.Numerics;

namespace Rodaje.Core.Effects;

/// <summary>
/// Homografía del plano (perspectiva): <c>(x', y') = ((a·x + b·y + c)/w, (d·x + e·y + f)/w)</c> con
/// <c>w = g·x + h·y + i</c>. Una sola matemática para el anclaje de esquinas del render, el seguimiento y la interfaz.
/// </summary>
public readonly record struct Homography(double A, double B, double C, double D, double E, double F, double G, double H, double I)
{
    public static Homography Identity { get; } = new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    public static Homography Translation(double x, double y) => new(1, 0, x, 0, 1, y, 0, 0, 1);

    /// <summary>Similitud: escala <paramref name="scale"/> y giro <paramref name="radians"/> (horario con y hacia abajo) y luego desplazamiento.</summary>
    public static Homography Similarity(double scale, double radians, double x, double y)
    {
        double cos = scale * Math.Cos(radians), sin = scale * Math.Sin(radians);
        return new(cos, -sin, x, sin, cos, y, 0, 0, 1);
    }

    /// <summary>La afín de una similitud: lleva <paramref name="from0"/>→<paramref name="to0"/> y <paramref name="from1"/>→<paramref name="to1"/>.</summary>
    public static Homography SimilarityFrom(Vector2 from0, Vector2 from1, Vector2 to0, Vector2 to1)
    {
        Vector2 a = from1 - from0, b = to1 - to0;
        double length = a.LengthSquared();
        if (!(length > 1e-12))
        {
            return Translation(to0.X - from0.X, to0.Y - from0.Y);
        }

        // b = (p + q·i)·a en complejos: p = escala·cos, q = escala·sen.
        double p = ((a.X * (double)b.X) + (a.Y * (double)b.Y)) / length;
        double q = ((a.X * (double)b.Y) - (a.Y * (double)b.X)) / length;
        return new(p, -q, to0.X - ((p * from0.X) - (q * from0.Y)), q, p, to0.Y - ((q * from0.X) + (p * from0.Y)), 0, 0, 1);
    }

    /// <summary>
    /// La que lleva las cuatro esquinas <paramref name="from"/> a <paramref name="to"/> (sistema de 8 ecuaciones,
    /// eliminación con pivote). Nula si tres esquinas están alineadas en alguno de los dos lados.
    /// </summary>
    public static Homography? FromQuads(ReadOnlySpan<Vector2> from, ReadOnlySpan<Vector2> to)
    {
        if (from.Length != 4 || to.Length != 4)
        {
            throw new ArgumentException("Hacen falta cuatro esquinas de cada lado.");
        }

        // Se normaliza cada lado (centro y escala) para que el sistema esté bien condicionado en píxeles de 4K.
        (Homography nf, Homography nfInverse) = Normalizer(from);
        (Homography nt, Homography ntInverse) = Normalizer(to);
        Span<double> m = stackalloc double[8 * 9];
        for (int k = 0; k < 4; k++)
        {
            Vector2 p = nf.Apply(from[k]);
            Vector2 q = nt.Apply(to[k]);
            int r0 = 2 * k * 9, r1 = ((2 * k) + 1) * 9;
            m[r0 + 0] = p.X; m[r0 + 1] = p.Y; m[r0 + 2] = 1; m[r0 + 6] = -p.X * q.X; m[r0 + 7] = -p.Y * q.X; m[r0 + 8] = q.X;
            m[r1 + 3] = p.X; m[r1 + 4] = p.Y; m[r1 + 5] = 1; m[r1 + 6] = -p.X * q.Y; m[r1 + 7] = -p.Y * q.Y; m[r1 + 8] = q.Y;
        }

        Span<double> h = stackalloc double[8];
        if (!Solve(m, 8, h))
        {
            return null;
        }

        var core = new Homography(h[0], h[1], h[2], h[3], h[4], h[5], h[6], h[7], 1);
        Homography result = ntInverse.After(core.After(nf));
        return result.IsFinite && !result.IsDegenerate(from) ? result.Normalized() : null;
    }

    /// <summary>Esta después de <paramref name="first"/>: <c>this(first(p))</c>.</summary>
    public Homography After(in Homography first) => new(
        (A * first.A) + (B * first.D) + (C * first.G), (A * first.B) + (B * first.E) + (C * first.H), (A * first.C) + (B * first.F) + (C * first.I),
        (D * first.A) + (E * first.D) + (F * first.G), (D * first.B) + (E * first.E) + (F * first.H), (D * first.C) + (E * first.F) + (F * first.I),
        (G * first.A) + (H * first.D) + (I * first.G), (G * first.B) + (H * first.E) + (I * first.H), (G * first.C) + (H * first.F) + (I * first.I));

    public Homography? Inverse()
    {
        double c00 = (E * I) - (F * H), c01 = (F * G) - (D * I), c02 = (D * H) - (E * G);
        double det = (A * c00) + (B * c01) + (C * c02);
        if (!(Math.Abs(det) > 1e-300) || !double.IsFinite(det))
        {
            return null;
        }

        double k = 1 / det;
        return new Homography(
            c00 * k, ((C * H) - (B * I)) * k, ((B * F) - (C * E)) * k,
            c01 * k, ((A * I) - (C * G)) * k, ((C * D) - (A * F)) * k,
            c02 * k, ((B * G) - (A * H)) * k, ((A * E) - (B * D)) * k).Normalized();
    }

    /// <summary>El punto llevado; <c>w</c> es el denominador (positivo si el punto queda del lado visible).</summary>
    public Vector2 Apply(Vector2 p) => Apply(p, out _);

    public Vector2 Apply(Vector2 p, out double w)
    {
        w = (G * p.X) + (H * p.Y) + I;
        double x = (A * p.X) + (B * p.Y) + C, y = (D * p.X) + (E * p.Y) + F;
        return new Vector2((float)(x / w), (float)(y / w));
    }

    public (double X, double Y) Apply(double x, double y)
    {
        double w = (G * x) + (H * y) + I;
        return (((A * x) + (B * y) + C) / w, ((D * x) + (E * y) + F) / w);
    }

    /// <summary>Sin perspectiva (fila de abajo 0, 0, 1).</summary>
    public bool IsAffine => Math.Abs(G) < 1e-12 && Math.Abs(H) < 1e-12;

    public bool IsFinite => double.IsFinite(A) && double.IsFinite(B) && double.IsFinite(C) && double.IsFinite(D) && double.IsFinite(E)
        && double.IsFinite(F) && double.IsFinite(G) && double.IsFinite(H) && double.IsFinite(I);

    /// <summary>Con <c>I = 1</c> (o el signo que deja <c>w</c> positivo), así dos homografías iguales se comparan término a término.</summary>
    public Homography Normalized() => Math.Abs(I) > 1e-300
        ? new Homography(A / I, B / I, C / I, D / I, E / I, F / I, G / I, H / I, 1)
        : this;

    /// <summary>Alguna esquina cae detrás del horizonte (<c>w ≤ 0</c>): el cuadrilátero se dio vuelta o se cruza.</summary>
    public bool IsDegenerate(ReadOnlySpan<Vector2> corners)
    {
        int sign = 0;
        foreach (Vector2 c in corners)
        {
            double w = (G * c.X) + (H * c.Y) + I;
            int s = Math.Sign(w);
            if (s == 0 || (sign != 0 && s != sign))
            {
                return true;
            }

            sign = s;
        }

        return false;
    }

    /// <summary>
    /// El cuadrilátero <paramref name="quad"/> (en orden, arriba izquierda, arriba derecha, abajo derecha, abajo izquierda)
    /// es convexo y no se cruza: lo que un anclaje de esquinas puede dibujar.
    /// </summary>
    public static bool IsConvex(ReadOnlySpan<Vector2> quad)
    {
        int sign = 0;
        for (int i = 0; i < quad.Length; i++)
        {
            Vector2 a = quad[i], b = quad[(i + 1) % quad.Length], c = quad[(i + 2) % quad.Length];
            double cross = ((b.X - (double)a.X) * (c.Y - (double)b.Y)) - ((b.Y - (double)a.Y) * (c.X - (double)b.X));
            int s = Math.Sign(cross);
            if (s == 0 || (sign != 0 && s != sign))
            {
                return false;
            }

            sign = s;
        }

        return true;
    }

    private static (Homography Forward, Homography Inverse) Normalizer(ReadOnlySpan<Vector2> points)
    {
        double cx = 0, cy = 0;
        foreach (Vector2 p in points)
        {
            cx += p.X;
            cy += p.Y;
        }

        cx /= points.Length;
        cy /= points.Length;
        double spread = 0;
        foreach (Vector2 p in points)
        {
            spread += Math.Sqrt(((p.X - cx) * (p.X - cx)) + ((p.Y - cy) * (p.Y - cy)));
        }

        double s = spread > 1e-12 ? Math.Sqrt(2) * points.Length / spread : 1;
        return (new Homography(s, 0, -s * cx, 0, s, -s * cy, 0, 0, 1), new Homography(1 / s, 0, cx, 0, 1 / s, cy, 0, 0, 1));
    }

    // Gauss con pivote parcial sobre la matriz aumentada n×(n+1).
    private static bool Solve(Span<double> m, int n, Span<double> x)
    {
        int w = n + 1;
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < n; r++)
            {
                if (Math.Abs(m[(r * w) + col]) > Math.Abs(m[(pivot * w) + col]))
                {
                    pivot = r;
                }
            }

            if (Math.Abs(m[(pivot * w) + col]) < 1e-12)
            {
                return false;
            }

            if (pivot != col)
            {
                for (int k = 0; k < w; k++)
                {
                    (m[(col * w) + k], m[(pivot * w) + k]) = (m[(pivot * w) + k], m[(col * w) + k]);
                }
            }

            for (int r = col + 1; r < n; r++)
            {
                double f = m[(r * w) + col] / m[(col * w) + col];
                if (f == 0)
                {
                    continue;
                }

                for (int k = col; k < w; k++)
                {
                    m[(r * w) + k] -= f * m[(col * w) + k];
                }
            }
        }

        for (int r = n - 1; r >= 0; r--)
        {
            double sum = m[(r * w) + n];
            for (int k = r + 1; k < n; k++)
            {
                sum -= m[(r * w) + k] * x[k];
            }

            x[r] = sum / m[(r * w) + r];
        }

        return true;
    }
}
