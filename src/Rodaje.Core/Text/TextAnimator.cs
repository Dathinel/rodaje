using Rodaje.Core.Effects;
using Rodaje.Core.Model;

namespace Rodaje.Core.Text;

/// <summary>
/// Pose de una unidad (línea, palabra o letra) en un instante: desplazamiento en píxeles de la referencia del estilo, escala
/// y giro alrededor de su centro, opacidad y desenfoque (radio en píxeles de la referencia).
/// </summary>
public readonly record struct UnitPose(double X, double Y, double Scale, double Rotation, double Opacity, double Blur)
{
    public static readonly UnitPose Rest = new(0, 0, 1, 0, 1, 0);
}

/// <summary>
/// Una animación por unidades (<see cref="StyleAnimation.PerUnit"/>) copiada a arreglos: evalúa la pose de cada unidad sin
/// reservar memoria, igual en la vista previa y en la exportación. Cada unidad recorre las curvas del estilo en su propia
/// ventana: la fase dura <see cref="StyleAnimation.DurationSeconds"/> y la unidad <c>k</c> arranca en
/// <c>desfase · retraso(k)</c> (de 0 a 1 según <see cref="StyleAnimation.Order"/>) y dura <c>1 − desfase</c> de la fase.
/// </summary>
public sealed class TextAnimator
{
    private const int Slots = 6;
    private static readonly string[] Names =
    [
        BuiltInEffects.TransformParameters.X,
        BuiltInEffects.TransformParameters.Y,
        BuiltInEffects.TransformParameters.Scale,
        BuiltInEffects.TransformParameters.Rotation,
        BuiltInEffects.TransformParameters.Opacity,
        StyleAnimation.Blur,
    ];

    private static readonly double[] Defaults = [0, 0, 1, 0, 1, 0];

    private readonly double[][] _at;
    private readonly double[][] _values;
    private readonly Interpolation[][] _interpolations;

    private TextAnimator(StyleAnimation animation)
    {
        Duration = animation.DurationSeconds;
        Stagger = double.IsFinite(animation.Stagger) ? Math.Clamp(animation.Stagger, 0, 1) : 0;
        Order = animation.Order;
        Unit = animation.Unit;
        _at = new double[Slots][];
        _values = new double[Slots][];
        _interpolations = new Interpolation[Slots][];
        for (int slot = 0; slot < Slots; slot++)
        {
            List<StyleKey> keys = animation.Curves.TryGetValue(Names[slot], out List<StyleKey>? found)
                ? [.. found.Where(k => double.IsFinite(k.T) && double.IsFinite(k.Value)).OrderBy(k => k.T)]
                : [];
            _at[slot] = [.. keys.Select(k => Math.Clamp(k.T, 0, Duration))];
            _values[slot] = [.. keys.Select(k => Clamp(slot, k.Value))];
            _interpolations[slot] = [.. keys.Select(k => k.Interp)];
        }

        double maxScale = 1;
        foreach (double v in _values[2])
        {
            maxScale = Math.Max(maxScale, v);
        }

        // Rebote y elástico se pasan un poco del valor de destino.
        MaxScale = maxScale * (_interpolations[2].Any(i => i is Interpolation.Elastic or Interpolation.Bezier) ? 1.4 : 1);
    }

    public double Duration { get; }

    public double Stagger { get; }

    public UnitOrder Order { get; }

    public TextUnit Unit { get; }

    /// <summary>La mayor escala que alcanza una unidad (al menos 1): la textura se rasteriza a ese tamaño.</summary>
    public double MaxScale { get; }

    /// <summary>Nulo si la animación no dura o no es por unidades.</summary>
    public static TextAnimator? From(StyleAnimation? animation) =>
        animation is { PerUnit: true, DurationSeconds: > 0 } a && double.IsFinite(a.DurationSeconds) ? new TextAnimator(a) : null;

    /// <summary>
    /// Retraso de cada una de <paramref name="delays"/>.Length unidades, de 0 (la primera en arrancar) a 1 (la última). El
    /// orden «mezclado» sale de un sorteo fijo que solo depende de la cantidad.
    /// </summary>
    public static void Delays(UnitOrder order, Span<double> delays)
    {
        int n = delays.Length;
        if (n == 0)
        {
            return;
        }

        if (n == 1)
        {
            delays[0] = 0;
            return;
        }

        double last = n - 1;
        double middle = last / 2;
        switch (order)
        {
            case UnitOrder.Backward:
                for (int k = 0; k < n; k++)
                {
                    delays[k] = (last - k) / last;
                }

                break;
            case UnitOrder.Center:
            case UnitOrder.Edges:
                for (int k = 0; k < n; k++)
                {
                    double fromCenter = Math.Abs(k - middle) / middle;
                    delays[k] = order == UnitOrder.Center ? fromCenter : 1 - fromCenter;
                }

                break;
            case UnitOrder.Random:
            {
                for (int k = 0; k < n; k++)
                {
                    delays[k] = k;
                }

                // Fisher–Yates con un generador congruencial propio: el mismo orden en cualquier equipo y versión de .NET.
                ulong state = 0x9E3779B97F4A7C15UL ^ (ulong)n;
                for (int k = n - 1; k > 0; k--)
                {
                    state = (state * 6364136223846793005UL) + 1442695040888963407UL;
                    int j = (int)((state >> 33) % (ulong)(k + 1));
                    (delays[k], delays[j]) = (delays[j], delays[k]);
                }

                for (int k = 0; k < n; k++)
                {
                    delays[k] /= last;
                }

                break;
            }

            default:
                for (int k = 0; k < n; k++)
                {
                    delays[k] = k / last;
                }

                break;
        }
    }

    /// <summary>Avance propio (0 a 1) de una unidad con retraso <paramref name="delay"/> cuando la fase va por <paramref name="progress"/>.</summary>
    public double LocalProgress(double progress, double delay)
    {
        double p = double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 0;
        double start = Stagger * delay;
        double window = 1 - Stagger;
        if (window <= 1e-9)
        {
            return p >= start ? 1 : 0;
        }

        return Math.Clamp((p - start) / window, 0, 1);
    }

    /// <summary>La pose de una unidad con retraso <paramref name="delay"/> cuando la fase va por <paramref name="progress"/>.</summary>
    public UnitPose Evaluate(double progress, double delay)
    {
        double t = LocalProgress(progress, delay) * Duration;
        return new UnitPose(Value(0, t), Value(1, t), Value(2, t), Value(3, t), Math.Clamp(Value(4, t), 0, 1), Math.Max(0, Value(5, t)));
    }

    /// <summary>Las poses de todas las unidades, con sus retrasos de <see cref="Delays"/>.</summary>
    public void Evaluate(double progress, ReadOnlySpan<double> delays, Span<UnitPose> poses)
    {
        for (int k = 0; k < poses.Length && k < delays.Length; k++)
        {
            poses[k] = Evaluate(progress, delays[k]);
        }
    }

    private double Value(int slot, double t)
    {
        double[] at = _at[slot];
        if (at.Length == 0)
        {
            return Defaults[slot];
        }

        double[] values = _values[slot];
        if (t <= at[0])
        {
            return values[0];
        }

        if (t >= at[^1])
        {
            return values[^1];
        }

        int i = 1;
        while (at[i] < t)
        {
            i++;
        }

        double span = at[i] - at[i - 1];
        double u = span > 0 ? (t - at[i - 1]) / span : 1;
        return values[i - 1] + ((values[i] - values[i - 1]) * KeyframeMath.Ease(_interpolations[slot][i - 1], null, u));
    }

    private static double Clamp(int slot, double value) => slot switch
    {
        2 => Math.Clamp(value, 0, 100),
        4 => Math.Clamp(value, 0, 1),
        5 => Math.Clamp(value, 0, MaxBlur),
        _ => value,
    };

    /// <summary>Radio máximo del desenfoque por unidad, en píxeles de la referencia.</summary>
    public const double MaxBlur = 64;
}
