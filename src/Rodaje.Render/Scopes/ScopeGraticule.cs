namespace Rodaje.Render.Scopes;

/// <summary>Un objetivo del vectorscopio: el color de una barra y dónde cae en la cuadrícula de 256 × 256.</summary>
public readonly record struct VectorTarget(string Label, double X, double Y);

/// <summary>
/// La retícula de los monitores, con las mismas cuentas que los shaders (BT.709, R'G'B' de 0 a 1): dónde caen los
/// objetivos de las barras al 75 % y al 100 %, la línea de piel y las líneas de nivel de la forma de onda.
/// </summary>
public static class ScopeGraticule
{
    /// <summary>Ángulo de la línea de piel en el vectorscopio (desde +Cb, en sentido antihorario).</summary>
    public const double SkinLineDegrees = 123;

    /// <summary>Niveles marcados en la forma de onda, el desfile y el histograma, en porcentaje.</summary>
    public static IReadOnlyList<int> Levels { get; } = [0, 25, 50, 75, 100];

    /// <summary>Celda del vectorscopio (0–256, x a la derecha, y hacia abajo) de un color R'G'B'.</summary>
    public static (double X, double Y) Vector(double r, double g, double b)
    {
        double y = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
        double cb = (b - y) / 1.8556;
        double cr = (r - y) / 1.5748;
        return (128 + (cb * ScopeRenderer.VectorScale), 128 - (cr * ScopeRenderer.VectorScale));
    }

    /// <summary>Los seis objetivos de las barras de color a esa intensidad (0,75 para las barras al 75 %).</summary>
    public static IReadOnlyList<VectorTarget> Targets(double level) =>
    [
        Target("R", level, 0, 0),
        Target("Mg", level, 0, level),
        Target("B", 0, 0, level),
        Target("Cy", 0, level, level),
        Target("G", 0, level, 0),
        Target("Yl", level, level, 0),
    ];

    /// <summary>Un punto de la línea de piel a esa distancia del centro, en celdas.</summary>
    public static (double X, double Y) SkinLine(double radius)
    {
        double a = SkinLineDegrees * Math.PI / 180;
        return (128 + (Math.Cos(a) * radius), 128 - (Math.Sin(a) * radius));
    }

    /// <summary>Fila de la imagen del monitor (0 arriba, <see cref="ScopeRenderer.ImageHeight"/> abajo) de un nivel en porcentaje.</summary>
    public static double Row(double percent) => (255 - (Math.Clamp(percent, 0, 100) * 2.55)) + 0.5;

    private static VectorTarget Target(string label, double r, double g, double b)
    {
        (double x, double y) = Vector(r, g, b);
        return new VectorTarget(label, x, y);
    }
}
