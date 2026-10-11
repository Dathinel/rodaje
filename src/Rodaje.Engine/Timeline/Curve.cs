using Rodaje.Core.Model;

namespace Rodaje.Engine.Timeline;

/// <summary>
/// Un parámetro animado copiado del modelo a arreglos: se evalúa sin reservar memoria. Las posiciones son enteras y su
/// unidad la decide quien la arma (ticks del origen, muestras de 48 kHz o millonésimas de una transición).
/// </summary>
internal sealed class Curve : IEquatable<Curve>
{
    private static readonly long[] NoPositions = [];
    private static readonly double[] NoValues = [];
    private static readonly Interpolation[] NoInterpolations = [];

    private readonly long[] _at;
    private readonly double[] _values;
    private readonly Interpolation[] _interpolations;
    private readonly double _constant;

    private Curve(long[] at, double[] values, Interpolation[] interpolations, double constant)
    {
        _at = at;
        _values = values;
        _interpolations = interpolations;
        _constant = constant;
    }

    public bool IsConstant => _at.Length == 0;

    /// <summary>El valor si es constante; si no, el del primer punto.</summary>
    public double First => _at.Length == 0 ? _constant : _values[0];

    public static Curve Constant(double value) => new(NoPositions, NoValues, NoInterpolations, value);

    /// <summary>
    /// Copia <paramref name="parameter"/>; si falta, vale <paramref name="fallback"/>. Los valores no finitos pasan a
    /// <paramref name="fallback"/> y todos se acotan con <paramref name="clamp"/>.
    /// </summary>
    public static Curve From(Parameter? parameter, double fallback, Func<double, double> clamp, Func<long, long> position)
    {
        if (parameter is null)
        {
            return Constant(clamp(fallback));
        }

        if (parameter.Keyframes.Count == 0)
        {
            return Constant(clamp(Finite(parameter.Value, fallback)));
        }

        int n = parameter.Keyframes.Count;
        var at = new long[n];
        var values = new double[n];
        var interpolations = new Interpolation[n];
        for (int i = 0; i < n; i++)
        {
            Keyframe key = parameter.Keyframes[i];
            at[i] = position(key.Time.Ticks);
            values[i] = clamp(Finite(key.Value, fallback));
            interpolations[i] = key.Interpolation;
        }

        return new Curve(at, values, interpolations, values[0]);
    }

    /// <summary>Puntos ya ordenados por posición.</summary>
    public static Curve FromPoints(IReadOnlyList<(long At, double Value, Interpolation Interpolation)> points)
    {
        if (points.Count == 0)
        {
            return Constant(0);
        }

        var at = new long[points.Count];
        var values = new double[points.Count];
        var interpolations = new Interpolation[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            (at[i], values[i], interpolations[i]) = points[i];
        }

        return new Curve(at, values, interpolations, values[0]);
    }

    /// <summary>Entre dos puntos manda la interpolación del de la izquierda, igual que <see cref="Parameter.Evaluate"/>.</summary>
    public double Evaluate(long position)
    {
        long[] at = _at;
        if (at.Length == 0)
        {
            return _constant;
        }

        if (position <= at[0])
        {
            return _values[0];
        }

        if (position >= at[^1])
        {
            return _values[^1];
        }

        int lo = 0;
        int hi = at.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (at[mid] > position)
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        int left = lo - 1;
        double t = (double)(position - at[left]) / (at[lo] - at[left]);
        return _values[left] + ((_values[lo] - _values[left]) * Ease(_interpolations[left], t));
    }

    public bool Equals(Curve? other) =>
        other is not null && (ReferenceEquals(this, other)
            || (_constant.Equals(other._constant) && _at.AsSpan().SequenceEqual(other._at)
                && _values.AsSpan().SequenceEqual(other._values) && _interpolations.AsSpan().SequenceEqual(other._interpolations)));

    public override bool Equals(object? obj) => Equals(obj as Curve);

    public override int GetHashCode() => HashCode.Combine(_constant, _at.Length, _at.Length > 0 ? _values[^1] : 0);

    /// <summary>Las mismas curvas cúbicas que <c>Parameter.Ease</c> del núcleo.</summary>
    internal static double Ease(Interpolation interpolation, double t)
    {
        switch (interpolation)
        {
            case Interpolation.Hold:
                return 0;
            case Interpolation.EaseIn:
                return t * t * t;
            case Interpolation.EaseOut:
            {
                double u = 1 - t;
                return 1 - (u * u * u);
            }

            case Interpolation.EaseInOut:
            {
                if (t < 0.5)
                {
                    return 4 * t * t * t;
                }

                double u = (-2 * t) + 2;
                return 1 - (u * u * u / 2);
            }

            default:
                return t;
        }
    }

    private static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;
}
