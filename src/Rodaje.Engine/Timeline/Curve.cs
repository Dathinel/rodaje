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

    /// <summary>Solo si algún tramo es Bézier o tiene tangentes; si no, nulos.</summary>
    private BezierEase?[]? _eases;
    private double[]? _in;
    private double[]? _out;

    /// <summary>Acota lo que un suavizado que se pasa puede dar fuera del rango (nulo si ninguno se pasa).</summary>
    private Func<double, double>? _overshootClamp;

    private Curve(long[] at, double[] values, Interpolation[] interpolations, double constant)
    {
        _at = at;
        _values = values;
        _interpolations = interpolations;
        _constant = constant;
        Max = at.Length == 0 ? constant : values.Max();
    }

    public bool IsConstant => _at.Length == 0;

    /// <summary>El valor si es constante; si no, el del primer punto.</summary>
    public double First => _at.Length == 0 ? _constant : _values[0];

    /// <summary>El mayor valor que toma (los tramos que se pasan de sus extremos se miden en 64 puntos).</summary>
    public double Max { get; private set; }

    /// <summary>Hay algún tramo con tangentes de trayectoria (solo en X e Y de la transformación).</summary>
    public bool HasSpatial => _in is not null;

    public static Curve Constant(double value) => new(NoPositions, NoValues, NoInterpolations, value);

    /// <summary>
    /// Copia <paramref name="parameter"/>; si falta, vale <paramref name="fallback"/>. Los valores no finitos pasan a
    /// <paramref name="fallback"/> y todos se acotan con <paramref name="clamp"/>, también lo que un suavizado que se pasa
    /// dé entre dos puntos.
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
        BezierEase?[]? eases = null;
        double[]? ins = null;
        double[]? outs = null;
        bool overshoot = false;
        for (int i = 0; i < n; i++)
        {
            Keyframe key = parameter.Keyframes[i];
            at[i] = position(key.Time.Ticks);
            values[i] = clamp(Finite(key.Value, fallback));
            interpolations[i] = key.Interpolation;
            if (key.Ease is { } ease)
            {
                eases ??= new BezierEase?[n];
                eases[i] = ease.IsValid ? ease : ease.Clamped();
            }

            if (key.IsSpatial)
            {
                ins ??= Filled(n);
                outs ??= Filled(n);
                ins[i] = Finite(key.SpatialIn ?? double.NaN, double.NaN);
                outs[i] = Finite(key.SpatialOut ?? double.NaN, double.NaN);
            }

            overshoot |= i + 1 < n && KeyframeMath.MayOvershoot(key, parameter.Keyframes[i + 1]);
        }

        var curve = new Curve(at, values, interpolations, values[0])
        {
            _eases = eases,
            _in = ins,
            _out = outs,
            _overshootClamp = overshoot ? clamp : null,
        };
        if (overshoot)
        {
            curve.Max = curve.SampledMax();
        }

        return curve;
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

    /// <summary>
    /// Entre dos puntos manda la interpolación del de la izquierda, igual que <see cref="Parameter.Evaluate"/>: las mismas
    /// fórmulas de <see cref="KeyframeMath"/>.
    /// </summary>
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

        int right = Right(position);
        int left = right - 1;
        double value = KeyframeMath.Segment(_values[left], Tangent(_out, left), Tangent(_in, right), _values[right], Eased(left, right, position));
        return _overshootClamp is { } clamp ? clamp(value) : value;
    }

    /// <summary>
    /// La posición de <paramref name="x"/> e <paramref name="y"/> en <paramref name="position"/>: dentro de un tramo con
    /// trayectoria, sobre la curva a velocidad constante como <see cref="MotionPath.TryEvaluate"/>; si no, cada una por su lado.
    /// </summary>
    public static (double X, double Y) Position(Curve x, Curve y, long position)
    {
        if ((x.HasSpatial || y.HasSpatial) && x.TryPath(y, position, out double px, out double py))
        {
            return (px, py);
        }

        return (x.Evaluate(position), y.Evaluate(position));
    }

    private bool TryPath(Curve y, long position, out double px, out double py)
    {
        px = 0;
        py = 0;
        long[] at = _at;
        if (at.Length < 2 || y._at.Length < 2 || position <= at[0] || position >= at[^1])
        {
            return false;
        }

        int right = Right(position);
        int left = right - 1;
        int j = Array.IndexOf(y._at, at[left]);
        if (j < 0 || j + 1 >= y._at.Length || y._at[j + 1] != at[right])
        {
            return false;
        }

        double? xOut = Tangent(_out, left);
        double? xIn = Tangent(_in, right);
        double? yOut = Tangent(y._out, j);
        double? yIn = Tangent(y._in, j + 1);
        if (xOut is null && xIn is null && yOut is null && yIn is null)
        {
            return false;
        }

        double x0 = _values[left];
        double x1 = _values[right];
        double y0 = y._values[j];
        double y1 = y._values[j + 1];
        (px, py) = MotionPath.Along(x0, y0, MotionPath.Control(x0, xOut, x1), MotionPath.Control(y0, yOut, y1),
            MotionPath.Control(x1, xIn, x0), MotionPath.Control(y1, yIn, y0), x1, y1, Eased(left, right, position));
        return true;
    }

    private int Right(long position)
    {
        long[] at = _at;
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

        return lo;
    }

    private double Eased(int left, int right, long position)
    {
        double t = (double)(position - _at[left]) / (_at[right] - _at[left]);
        return KeyframeMath.Ease(_interpolations[left], _eases?[left], t);
    }

    private double SampledMax()
    {
        double max = _values.Max();
        for (int i = 0; i + 1 < _at.Length; i++)
        {
            for (int k = 1; k < 64; k++)
            {
                max = Math.Max(max, Evaluate(_at[i] + ((_at[i + 1] - _at[i]) * k / 64)));
            }
        }

        return max;
    }

    private static double? Tangent(double[]? tangents, int index) =>
        tangents is not null && !double.IsNaN(tangents[index]) ? tangents[index] : null;

    private static double[] Filled(int n)
    {
        var array = new double[n];
        Array.Fill(array, double.NaN);
        return array;
    }

    public bool Equals(Curve? other) =>
        other is not null && (ReferenceEquals(this, other)
            || (_constant.Equals(other._constant) && _at.AsSpan().SequenceEqual(other._at)
                && _values.AsSpan().SequenceEqual(other._values) && _interpolations.AsSpan().SequenceEqual(other._interpolations)
                && SameEases(_eases, other._eases) && Same(_in, other._in) && Same(_out, other._out)));

    public override bool Equals(object? obj) => Equals(obj as Curve);

    public override int GetHashCode() => HashCode.Combine(_constant, _at.Length, _at.Length > 0 ? _values[^1] : 0);

    private static bool SameEases(BezierEase?[]? a, BezierEase?[]? b) =>
        a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);

    private static bool Same(double[]? a, double[]? b) =>
        a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);

    private static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;
}
