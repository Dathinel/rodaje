namespace Rodaje.Analysis.Video;

/// <summary>Ajustes de «Estabilizar».</summary>
/// <param name="Intensity">0 no corrige; 1 suaviza la trayectoria con una gaussiana de 1 s (sigma).</param>
/// <param name="AutoCrop">Acerca lo justo para que el borde que mueve la corrección no se vea en ningún cuadro.</param>
/// <param name="MaxZoom">Tope del acercamiento automático.</param>
public sealed record StabilizeOptions(double Intensity = 0.5, bool AutoCrop = true, double MaxZoom = 1.5)
{
    /// <summary>Sigma de la gaussiana a intensidad 1, en segundos.</summary>
    public const double MaxSigmaSeconds = 1.0;
}

/// <summary>La corrección de cada cuadro y el acercamiento que ya lleva incluido.</summary>
public sealed class StabilizationResult(Similarity[] corrections, double zoom, double shakeBefore, double shakeAfter)
{
    /// <summary>Lleva una posición del cuadro original a la del cuadro estabilizado (acercamiento incluido).</summary>
    public IReadOnlyList<Similarity> Corrections { get; } = corrections;

    /// <summary>Acercamiento del recorte automático (1 sin recorte).</summary>
    public double Zoom { get; } = zoom;

    /// <summary>Temblor medido (<see cref="CameraPath.Shake"/>) de la trayectoria original, en altos de la imagen por cuadro².</summary>
    public double ShakeBefore { get; } = shakeBefore;

    /// <summary>El mismo, de la trayectoria suavizada.</summary>
    public double ShakeAfter { get; } = shakeAfter;
}

/// <summary>
/// Trayectoria de la cámara a partir del movimiento cuadro a cuadro, suavizada con una gaussiana (los extremos se
/// reflejan de forma impar, así una panorámica uniforme queda igual) y la corrección que lleva cada cuadro a ella.
/// </summary>
public static class CameraPath
{
    /// <summary>
    /// Corrección de cada cuadro. <paramref name="motions"/> trae un movimiento por cuadro (el del primero se ignora);
    /// <paramref name="aspect"/> es ancho/alto de la imagen, para el recorte automático.
    /// </summary>
    public static StabilizationResult Stabilize(IReadOnlyList<Similarity> motions, double frameRate, double aspect, StabilizeOptions options)
    {
        ArgumentNullException.ThrowIfNull(motions);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(aspect);
        int n = motions.Count;
        var path = new Similarity[n];
        double[] angle = new double[n];
        double[] logScale = new double[n];
        double[] x = new double[n];
        double[] y = new double[n];
        Similarity cumulative = Similarity.Identity;
        double a = 0, s = 0;
        for (int k = 0; k < n; k++)
        {
            if (k > 0)
            {
                Similarity m = motions[k].IsFinite ? motions[k] : Similarity.Identity;
                cumulative = m.After(cumulative);
                // El giro se suma cuadro a cuadro para no saltar en ±π.
                a += m.Angle;
                s += Math.Log(m.Scale);
            }

            path[k] = cumulative;
            angle[k] = a;
            logScale[k] = s;
            x[k] = cumulative.X;
            y[k] = cumulative.Y;
        }

        double sigma = Math.Clamp(options.Intensity, 0, 1) * StabilizeOptions.MaxSigmaSeconds * frameRate;
        var corrections = new Similarity[n];
        if (sigma < 0.5 || n < 3)
        {
            Array.Fill(corrections, Similarity.Identity);
            double shake = Shake(path, aspect);
            return new StabilizationResult(corrections, 1, shake, shake);
        }

        double[] kernel = Gaussian(sigma);
        double[] sa = Smooth(angle, kernel);
        double[] ss = Smooth(logScale, kernel);
        double[] sx = Smooth(x, kernel);
        double[] sy = Smooth(y, kernel);
        var smooth = new Similarity[n];
        for (int k = 0; k < n; k++)
        {
            smooth[k] = Similarity.FromParts(sa[k], Math.Exp(ss[k]), sx[k], sy[k]);
            corrections[k] = smooth[k].After(path[k].Inverse());
        }

        double zoom = 1;
        if (options.AutoCrop)
        {
            foreach (Similarity c in corrections)
            {
                zoom = Math.Max(zoom, ZoomToCover(c, aspect, options.MaxZoom));
            }

            for (int k = 0; k < n; k++)
            {
                corrections[k] = corrections[k].Zoomed(zoom);
            }
        }

        return new StabilizationResult(corrections, zoom, Shake(path, aspect), Shake(smooth, aspect));
    }

    /// <summary>
    /// Temblor de una trayectoria: la raíz de la media de la segunda diferencia, cuadro a cuadro, de dónde caen el centro y
    /// las cuatro esquinas. Una panorámica uniforme da 0; el temblor de la mano, mucho.
    /// </summary>
    public static double Shake(IReadOnlyList<Similarity> path, double aspect)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Count < 3)
        {
            return 0;
        }

        double hw = aspect / 2;
        ReadOnlySpan<(double X, double Y)> points = [(0, 0), (-hw, -0.5), (hw, -0.5), (-hw, 0.5), (hw, 0.5)];
        double sum = 0;
        int count = 0;
        for (int k = 1; k < path.Count - 1; k++)
        {
            foreach ((double px, double py) in points)
            {
                (double ax, double ay) = path[k - 1].Apply(px, py);
                (double bx, double by) = path[k].Apply(px, py);
                (double cx, double cy) = path[k + 1].Apply(px, py);
                double dx = ax - (2 * bx) + cx;
                double dy = ay - (2 * by) + cy;
                sum += (dx * dx) + (dy * dy);
                count++;
            }
        }

        return Math.Sqrt(sum / count);
    }

    /// <summary>
    /// El menor acercamiento (hasta <paramref name="maxZoom"/>) con que el cuadro corregido cubre todo el rectángulo: cada
    /// esquina del rectángulo, llevada atrás por la corrección, tiene que caer dentro de la imagen original.
    /// </summary>
    internal static double ZoomToCover(Similarity correction, double aspect, double maxZoom)
    {
        // Con acercamiento z la esquina c sale de K⁻¹(c / z) = P0 + u·D, con u = 1/z: se busca el mayor u ≤ 1 que la deja adentro.
        Similarity inverse = correction.Inverse();
        double hw = aspect / 2;
        (double p0x, double p0y) = inverse.Apply(0, 0);
        if (Math.Abs(p0x) >= hw || Math.Abs(p0y) >= 0.5)
        {
            return maxZoom;
        }

        double u = 1;
        ReadOnlySpan<(double X, double Y)> corners = [(-hw, -0.5), (hw, -0.5), (-hw, 0.5), (hw, 0.5)];
        foreach ((double cx, double cy) in corners)
        {
            (double ex, double ey) = inverse.Apply(cx, cy);
            double dx = ex - p0x;
            double dy = ey - p0y;
            u = Math.Min(u, Limit(p0x, dx, hw));
            u = Math.Min(u, Limit(p0y, dy, 0.5));
        }

        return Math.Clamp(1 / Math.Max(u, 1e-9), 1, maxZoom);

        static double Limit(double start, double delta, double bound) =>
            delta > 0 ? (bound - start) / delta : delta < 0 ? (-bound - start) / delta : double.PositiveInfinity;
    }

    private static double[] Gaussian(double sigma)
    {
        int radius = (int)Math.Ceiling(3 * sigma);
        double[] kernel = new double[(2 * radius) + 1];
        double sum = 0;
        for (int i = -radius; i <= radius; i++)
        {
            double v = Math.Exp(-(i * i) / (2 * sigma * sigma));
            kernel[i + radius] = v;
            sum += v;
        }

        for (int i = 0; i < kernel.Length; i++)
        {
            kernel[i] /= sum;
        }

        return kernel;
    }

    /// <summary>Convolución con reflexión impar en los extremos (2·x₀ − x₋ᵢ), que conserva las rectas.</summary>
    private static double[] Smooth(double[] values, double[] kernel)
    {
        int n = values.Length;
        int radius = kernel.Length / 2;
        double[] output = new double[n];
        for (int k = 0; k < n; k++)
        {
            double sum = 0;
            for (int i = -radius; i <= radius; i++)
            {
                sum += kernel[i + radius] * At(values, k + i);
            }

            output[k] = sum;
        }

        return output;

        static double At(double[] v, int i)
        {
            int n = v.Length;
            if (i < 0)
            {
                int m = Math.Min(-i, n - 1);
                return (2 * v[0]) - v[m];
            }

            if (i >= n)
            {
                int m = Math.Max(n - 1 - (i - (n - 1)), 0);
                return (2 * v[n - 1]) - v[m];
            }

            return v[i];
        }
    }
}
