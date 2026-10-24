using Rodaje.App.Services.Effects;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services.Inspector;

/// <summary>
/// La curva de velocidad del clip vista como un parámetro más de la gráfica: cada punto de la rampa es un keyframe lineal
/// (instante de la línea de tiempo, factor de velocidad) y toda edición vuelve a <see cref="TimelineEdits.SetCurve"/>, el
/// mismo camino que el inspector de velocidad.
/// </summary>
public static class SpeedGraph
{
    public const string EffectId = "clip-speed";
    public const string Name = "speed";

    public static ParameterSpec Spec { get; } = new(EffectId, Name, "Velocidad", "Cambiar velocidad",
        RetimeMath.MinHundredths / 100.0, RetimeMath.MaxHundredths / 100.0, 0.01, 1, 1, 2, "×", true)
    {
        ModelMinimum = RetimeMath.MinHundredths / 100.0,
        ModelMaximum = RetimeMath.MaxHundredths / 100.0,
    };

    public static bool Has(Clip? clip) => clip is { Speed: { Freeze: false, Curve: not null } };

    /// <summary>
    /// El clip visto a 1× (la gráfica va en tiempo de línea, como el inspector de velocidad) y la rampa como efecto sintético;
    /// nulo sin rampa editable.
    /// </summary>
    public static (Clip Clip, EffectInstance Effect)? View(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!Has(clip))
        {
            return null;
        }

        Clip proxy = Proxy(clip);
        (double from, double to) = SpeedCurveMath.Window(clip);
        // shortcut: en un trozo de un clip dividido solo se ven y se editan los puntos de su ventana; el resto se conserva tal cual.
        var parameter = new Parameter
        {
            Value = 1,
            Keyframes = clip.Speed!.Curve!.Points.Where(p => p.At >= from && p.At <= to)
                .Select(p => new Keyframe(KeyTime(proxy, clip, p.At), p.Hundredths / 100.0)).ToList(),
        };
        return (proxy, new EffectInstance { Id = EffectId, Type = EffectId, Parameters = new() { [Name] = parameter } });
    }

    /// <summary>El instante (keyframe del clip a 1×) del punto <paramref name="at"/> de la rampa.</summary>
    public static Time KeyTime(Clip clip, double at)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return KeyTime(Proxy(clip), clip, at);
    }

    /// <summary>La rampa del clip con los puntos de su ventana sustituidos por <paramref name="keys"/>; el primero y el último punto no se mueven de los extremos.</summary>
    public static SpeedCurve ToCurve(Clip clip, IEnumerable<Keyframe> keys)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(keys);
        SpeedCurve curve = clip.Speed!.Curve!;
        (double from, double to) = SpeedCurveMath.Window(clip);
        Clip proxy = Proxy(clip);
        List<SpeedPoint> points =
        [
            .. curve.Points.Where(p => p.At < from || p.At > to),
            .. keys.Select(k => new SpeedPoint(
                Math.Clamp(SpeedCurveMath.AtOfClipTime(clip, EffectCommands.TimelineOfKeyframe(proxy, k.Time) - clip.Start), 0, SpeedCurve.AtScale),
                Math.Clamp((int)Math.Round(k.Value * 100), RetimeMath.MinHundredths, RetimeMath.MaxHundredths))),
        ];
        points.Sort((a, b) => a.At.CompareTo(b.At));
        if (points.Count > 0)
        {
            points[0] = points[0] with { At = 0 };
            points[^1] = points[^1] with { At = SpeedCurve.AtScale };
        }

        return new SpeedCurve { Span = curve.Span, Points = points };
    }

    /// <summary>Aplica <paramref name="change"/> a los keyframes de la rampa y la guarda como lo hace el inspector de velocidad.</summary>
    public static void Edit(EditContext ctx, string clipId, Func<List<Keyframe>, IEnumerable<Keyframe>> change, RippleScope scope)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(change);
        Clip clip = ctx.Sequence.GetClip(clipId).Clip;
        if (View(clip) is not { } view)
        {
            throw new EditException("El clip no tiene curva de velocidad.");
        }

        TimelineEdits.SetCurve(ctx, clipId, ToCurve(clip, change(view.Effect.Parameters[Name].Keyframes)), scope);
    }

    // Un clip igual pero a 1× y con material para toda su duración: solo sirve de sistema de coordenadas.
    private static Clip Proxy(Clip clip)
    {
        Clip proxy = clip.Clone();
        proxy.Speed = new ClipSpeed();
        proxy.SourceOut = proxy.SourceIn + clip.Duration;
        return proxy;
    }

    private static Time KeyTime(Clip proxy, Clip clip, double at) =>
        EffectCommands.KeyframeTime(proxy, clip.Start + SpeedCurveMath.ClipTimeOf(clip, at));
}
