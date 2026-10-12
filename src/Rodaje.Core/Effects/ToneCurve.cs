using System.Globalization;
using System.Numerics;
using System.Text;
using Rodaje.Core.Model;
using O = Rodaje.Core.Model.BuiltInEffects.CurvesOptions;

namespace Rodaje.Core.Effects;

/// <summary>
/// Una curva de <see cref="BuiltInEffects.Curves"/>: puntos de 0 a 1 unidos con una cúbica de Hermite con pendientes de
/// Fritsch–Carlson, que no se pasa de los puntos (si los puntos suben, la curva sube). Antes del primer punto y después del
/// último es plana. Sin puntos, o con uno solo, es la identidad.
/// </summary>
public sealed class ToneCurve
{
    public const int MaxPoints = 16;

    /// <summary>Entradas de la tabla que sube el render: con interpolación lineal el error frente a la curva es menor que 1/2000.</summary>
    public const int TableSize = 1024;

    public static readonly ToneCurve Identity = new([]);

    private readonly Vector2[] _points;
    private readonly double[] _slopes;

    private ToneCurve(Vector2[] points)
    {
        _points = points;
        _slopes = Slopes(points);
    }

    /// <summary>Los puntos, ordenados por x y sin x repetidas.</summary>
    public IReadOnlyList<Vector2> Points => _points;

    public bool IsIdentity => _points.Length < 2
        || (_points.All(p => Math.Abs(p.X - p.Y) < 1e-6) && _points[0].X <= 1e-6 && _points[^1].X >= 1 - 1e-6);

    /// <summary>Una curva de esos puntos: se recortan a 0–1, se ordenan y de dos con la misma x queda el último.</summary>
    public static ToneCurve From(IEnumerable<Vector2> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var sorted = new SortedList<float, float>();
        foreach (Vector2 p in points)
        {
            if (float.IsFinite(p.X) && float.IsFinite(p.Y) && sorted.Count < MaxPoints)
            {
                sorted[Math.Clamp(p.X, 0, 1)] = Math.Clamp(p.Y, 0, 1);
            }
        }

        return new ToneCurve([.. sorted.Select(kv => new Vector2(kv.Key, kv.Value))]);
    }

    /// <summary>Lee <c>x,y;x,y;…</c>; un texto mal formado es la identidad.</summary>
    public static ToneCurve Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Identity;
        }

        var points = new List<Vector2>();
        foreach (Range part in text.AsSpan().Split(';'))
        {
            ReadOnlySpan<char> pair = text.AsSpan()[part];
            int comma = pair.IndexOf(',');
            if (points.Count == MaxPoints || comma < 0
                || !float.TryParse(pair[..comma], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                || !float.TryParse(pair[(comma + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
            {
                return Identity;
            }

            points.Add(new Vector2(x, y));
        }

        return From(points);
    }

    /// <summary>Los puntos como texto con cuatro decimales; vacío si la curva es la identidad.</summary>
    public string Format()
    {
        if (IsIdentity)
        {
            return "";
        }

        var text = new StringBuilder(_points.Length * 14);
        foreach (Vector2 p in _points)
        {
            if (text.Length > 0)
            {
                text.Append(';');
            }

            text.Append(Math.Round(p.X, 4).ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(Math.Round(p.Y, 4).ToString(CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    public double Evaluate(double x)
    {
        Vector2[] p = _points;
        if (p.Length < 2)
        {
            return Math.Clamp(x, 0, 1);
        }

        if (!(x > p[0].X))
        {
            return p[0].Y;
        }

        if (x >= p[^1].X)
        {
            return p[^1].Y;
        }

        int k = 0;
        while (x > p[k + 1].X)
        {
            k++;
        }

        double h = p[k + 1].X - p[k].X;
        double t = (x - p[k].X) / h;
        double t2 = t * t, t3 = t2 * t;
        double value = (((2 * t3) - (3 * t2) + 1) * p[k].Y) + ((t3 - (2 * t2) + t) * h * _slopes[k])
            + (((-2 * t3) + (3 * t2)) * p[k + 1].Y) + ((t3 - t2) * h * _slopes[k + 1]);
        return Math.Clamp(value, 0, 1);
    }

    /// <summary>Clave del efecto para el motor: las cuatro curvas, en el orden general, rojo, verde, azul, separadas por <c>|</c>.</summary>
    public static string Key(IReadOnlyDictionary<string, string> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return string.Join('|', options.GetValueOrDefault(O.Master) ?? "", options.GetValueOrDefault(O.Red) ?? "",
            options.GetValueOrDefault(O.Green) ?? "", options.GetValueOrDefault(O.Blue) ?? "");
    }

    /// <summary>
    /// La tabla de <see cref="TableSize"/> entradas RGB (entrada i = i / (N − 1)) de una clave de <see cref="Key"/>: cada
    /// canal pasa por su curva y luego por la general.
    /// </summary>
    public static float[] Table(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        string[] parts = key.Split('|');
        ToneCurve Part(int i) => i < parts.Length ? Parse(parts[i]) : Identity;
        ToneCurve master = Part(0);
        ToneCurve[] channels = [Part(1), Part(2), Part(3)];
        float[] table = new float[TableSize * 3];
        for (int i = 0; i < TableSize; i++)
        {
            double x = (double)i / (TableSize - 1);
            for (int c = 0; c < 3; c++)
            {
                table[(i * 3) + c] = (float)master.Evaluate(channels[c].Evaluate(x));
            }
        }

        return table;
    }

    /// <summary>Si la clave no cambia nada (las cuatro curvas son la identidad).</summary>
    public static bool IsIdentityKey(string? key) =>
        key is null || key.Split('|').All(part => Parse(part).IsIdentity);

    private static double[] Slopes(Vector2[] p)
    {
        int n = p.Length;
        double[] m = new double[n];
        if (n < 2)
        {
            return m;
        }

        double[] d = new double[n - 1];
        for (int k = 0; k < n - 1; k++)
        {
            d[k] = (p[k + 1].Y - p[k].Y) / (p[k + 1].X - p[k].X);
        }

        m[0] = d[0];
        m[n - 1] = d[n - 2];
        for (int k = 1; k < n - 1; k++)
        {
            m[k] = d[k - 1] * d[k] <= 0 ? 0 : (d[k - 1] + d[k]) / 2;
        }

        // Fritsch–Carlson: si alfa² + beta² pasa de 9, se achican las dos pendientes para no salirse.
        for (int k = 0; k < n - 1; k++)
        {
            if (d[k] == 0)
            {
                m[k] = 0;
                m[k + 1] = 0;
                continue;
            }

            double a = m[k] / d[k], b = m[k + 1] / d[k];
            double s = (a * a) + (b * b);
            if (s > 9)
            {
                double tau = 3 / Math.Sqrt(s);
                m[k] = tau * a * d[k];
                m[k + 1] = tau * b * d[k];
            }
        }

        return m;
    }
}
