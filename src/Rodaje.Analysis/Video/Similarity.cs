namespace Rodaje.Analysis.Video;

/// <summary>
/// Similitud del plano (giro, escala y desplazamiento) en coordenadas centradas en la imagen y medidas en altos de la
/// imagen, con y hacia abajo: <c>x' = A·x − B·y + X</c>, <c>y' = B·x + A·y + Y</c>. Un <see cref="Angle"/> positivo gira en
/// sentido horario en pantalla, como <c>rodaje.transform</c>.
/// </summary>
public readonly record struct Similarity(double A, double B, double X, double Y)
{
    public static readonly Similarity Identity = new(1, 0, 0, 0);

    /// <summary>Giro en radianes.</summary>
    public double Angle => Math.Atan2(B, A);

    public double Scale => Math.Sqrt((A * A) + (B * B));

    public static Similarity FromParts(double angle, double scale, double x, double y) =>
        new(scale * Math.Cos(angle), scale * Math.Sin(angle), x, y);

    public (double X, double Y) Apply(double x, double y) => ((A * x) - (B * y) + X, (B * x) + (A * y) + Y);

    /// <summary><c>this ∘ first</c>: primero <paramref name="first"/>, después esta.</summary>
    public Similarity After(Similarity first) => new(
        (A * first.A) - (B * first.B),
        (A * first.B) + (B * first.A),
        (A * first.X) - (B * first.Y) + X,
        (B * first.X) + (A * first.Y) + Y);

    public Similarity Inverse()
    {
        double n = (A * A) + (B * B);
        double a = A / n;
        double b = -B / n;
        return new(a, b, -((a * X) - (b * Y)), -((b * X) + (a * Y)));
    }

    /// <summary>Escala alrededor del centro: multiplica todo, desplazamiento incluido.</summary>
    public Similarity Zoomed(double zoom) => new(A * zoom, B * zoom, X * zoom, Y * zoom);

    public bool IsFinite => double.IsFinite(A) && double.IsFinite(B) && double.IsFinite(X) && double.IsFinite(Y) && ((A * A) + (B * B)) > 0;
}
