using System.Globalization;
using System.Numerics;
using System.Text;

namespace Rodaje.Core.Effects;

/// <summary>
/// Una curva de ajuste de <see cref="Model.BuiltInEffects.Curves"/> que vale 0,5 donde no cambia nada: tono contra tono,
/// tono contra saturación (periódicas: el tono 1 es el 0) y luminancia contra saturación. Los puntos se unen con la misma
/// cúbica de Hermite con pendientes de Fritsch–Carlson que <see cref="ToneCurve"/>; una periódica repite sus puntos a cada
/// lado y una que no lo es queda plana fuera de ellos. Sin puntos es 0,5 en todo.
/// </summary>
public sealed class HueCurve
{
    public const double Neutral = 0.5;

    private readonly Vector2[] _points;
    private readonly Vector2[] _extended;
    private readonly double[] _slopes;

    private HueCurve(Vector2[] points, bool periodic)
    {
        _points = points;
        IsPeriodic = periodic;
        // Dos copias a cada lado: los tramos que cruzan el corte del tono tienen pendientes de punto interior en los dos
        // extremos, iguales a las de su copia del otro lado.
        _extended = periodic && points.Length >= 2
            ?
            [
                new Vector2(points[^2].X - 1, points[^2].Y), new Vector2(points[^1].X - 1, points[^1].Y), .. points,
                new Vector2(points[0].X + 1, points[0].Y), new Vector2(points[1].X + 1, points[1].Y),
            ]
            : points;
        _slopes = ToneCurve.Slopes(_extended);
    }

    public bool IsPeriodic { get; }

    public IReadOnlyList<Vector2> Points => _points;

    /// <summary>No cambia nada: sin puntos o con todos en 0,5.</summary>
    public bool IsNeutral => _points.All(p => Math.Abs(p.Y - Neutral) < 1e-6);

    public static HueCurve Flat(bool periodic) => new([], periodic);

    /// <summary>Puntos recortados a 0–1, ordenados, sin x repetidas (gana el último); en una periódica, x = 1 es x = 0.</summary>
    public static HueCurve From(IEnumerable<Vector2> points, bool periodic)
    {
        ArgumentNullException.ThrowIfNull(points);
        var sorted = new SortedList<float, float>();
        foreach (Vector2 p in points)
        {
            if (float.IsFinite(p.X) && float.IsFinite(p.Y) && sorted.Count < ToneCurve.MaxPoints)
            {
                float x = Math.Clamp(p.X, 0, 1);
                sorted[periodic && x >= 1 ? 0 : x] = Math.Clamp(p.Y, 0, 1);
            }
        }

        return new HueCurve([.. sorted.Select(kv => new Vector2(kv.Key, kv.Value))], periodic);
    }

    /// <summary>Lee <c>x,y;x,y;…</c>; un texto mal formado es neutro.</summary>
    public static HueCurve Parse(string? text, bool periodic)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Flat(periodic);
        }

        var points = new List<Vector2>();
        foreach (Range part in text.AsSpan().Split(';'))
        {
            ReadOnlySpan<char> pair = text.AsSpan()[part];
            int comma = pair.IndexOf(',');
            if (points.Count == ToneCurve.MaxPoints || comma < 0
                || !float.TryParse(pair[..comma], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                || !float.TryParse(pair[(comma + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
            {
                return Flat(periodic);
            }

            points.Add(new Vector2(x, y));
        }

        return From(points, periodic);
    }

    /// <summary>Los puntos con cuatro decimales; vacío si la curva es neutra.</summary>
    public string Format()
    {
        if (IsNeutral)
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
        if (_points.Length == 0)
        {
            return Neutral;
        }

        if (_points.Length == 1)
        {
            return _points[0].Y;
        }

        if (IsPeriodic)
        {
            x -= Math.Floor(x);
        }

        Vector2[] p = _extended;
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
}
