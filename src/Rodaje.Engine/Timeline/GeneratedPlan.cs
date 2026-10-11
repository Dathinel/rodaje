using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Playback.Composition;

namespace Rodaje.Engine.Timeline;

/// <summary>
/// Un clip generado (un título) listo para componer: el descriptor, armado una vez al armar el plan, sus efectos y la
/// animación de entrada y salida de su estilo. No tiene decodificador: el carril generado no pasa por la demanda.
/// </summary>
internal sealed class GeneratedClipPlan
{
    public GeneratedClipPlan(string clipId, ClipTimeMap map, GeneratedContent content, EffectPlan? effects, TitleAnimation? animation)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(content);
        ClipId = clipId;
        TimeMap = map;
        Content = content;
        Effects = effects;
        Animation = animation;
    }

    public string ClipId { get; }

    public ClipTimeMap TimeMap { get; }

    public Time Start => TimeMap.Start;

    public Time End => TimeMap.End;

    public GeneratedContent Content { get; }

    public EffectPlan? Effects { get; }

    public TitleAnimation? Animation { get; }

    public bool Covers(Time timeline) => timeline >= Start && timeline < End;

    /// <summary>
    /// Transformación en <paramref name="timeline"/>: la animación del estilo es un delta en el espacio de la capa que se
    /// aplica antes que la transformación del clip, así se escala y gira con ella.
    /// </summary>
    public LayerTransform TransformAt(Time timeline)
    {
        LayerTransform clip = EffectPlan.Transform(Effects, TimeMap.EffectTime(timeline));
        return Animation is { } animation ? animation.Apply(clip, timeline - Start) : clip;
    }

    /// <summary>La capa del instante, con los efectos evaluados en una lista de <paramref name="ring"/>.</summary>
    public LayerFrame Layer(int trackIndex, Time timeline, EffectListRing ring, IEffectResources? resources)
    {
        IReadOnlyList<LayerEffect> effects = [];
        if (Effects is { EffectCount: > 0 } plan)
        {
            EffectList list = ring.Next();
            plan.Evaluate(TimeMap.EffectTime(timeline), list.Reset(plan.EffectCount), resources);
            effects = list;
        }

        return new LayerFrame(trackIndex, ClipId, Content, TransformAt(timeline))
        {
            Effects = effects,
            Blend = Effects?.Blend ?? BlendMode.Normal,
        };
    }

    /// <summary>
    /// El plan de un clip con generador, o nulo si no se dibuja (tipo desconocido). El estilo y la caja se copian: el plan
    /// no cambia si el modelo cambia después.
    /// </summary>
    public static GeneratedClipPlan? From(Clip clip, int sequenceWidth, int sequenceHeight)
    {
        if (clip.Generator is not { Kind: GeneratorKinds.Text } generator)
        {
            return null;
        }

        TextStyle style = generator.Style?.Clone() ?? new TextStyle();
        double unit = GeneratedContent.UnitScaleFor(style.Reference, sequenceWidth, sequenceHeight);
        double box = generator.Box switch
        {
            { WidthFraction: > 0 and <= 1 } frame => frame.WidthFraction.Value * sequenceWidth,
            { Width: > 0 } frame => frame.Width.Value * unit,
            _ => 0,
        };

        EffectPlan? effects = EffectPlan.From(clip.Effects);
        TitleAnimation? animation = TitleAnimation.From(style, generator.Edges, clip.Duration, unit);
        double scale = Math.Clamp(EffectPlan.MaxScale(effects) * (animation?.MaxScale ?? 1), MinRasterScale, MaxRasterScale);
        var content = new GeneratedContent(generator.Kind, generator.Text, style, box, sequenceWidth, sequenceHeight, scale);
        return new GeneratedClipPlan(clip.Id, clip.TimeMap, content, effects, animation);
    }

    /// <summary>Por debajo, un título que siempre se ve chico se rasteriza igual a este tamaño (no ahorra lo que pierde).</summary>
    internal const double MinRasterScale = 0.25;

    /// <summary>Más que esto no se rasteriza aunque la animación crezca: el tope en bytes lo baja después si hace falta.</summary>
    internal const double MaxRasterScale = 4;
}

/// <summary>
/// Entrada y salida del estilo de un título, ancladas a los bordes del clip (D-029): <c>in</c> desde el izquierdo y
/// <c>out</c> hasta el derecho, cada una solo si su borde la tiene (<see cref="GeneratorEdges"/>). Si las dos no caben en
/// el clip se escalan por igual para sumar su duración. Fuera de ellas no hay delta.
/// </summary>
internal sealed class TitleAnimation
{
    private const int Slots = 5;
    private readonly Curve?[]? _in;
    private readonly Curve?[]? _out;
    private readonly long _inTicks;
    private readonly long _outTicks;
    private readonly long _durationTicks;
    private readonly double _stretch;
    private readonly double _unit;

    private TitleAnimation(Curve?[]? @in, long inTicks, Curve?[]? @out, long outTicks, long durationTicks, double stretch, double unit)
    {
        _in = @in;
        _out = @out;
        _inTicks = inTicks;
        _outTicks = outTicks;
        _durationTicks = durationTicks;
        _stretch = stretch;
        _unit = unit;
        MaxScale = Math.Max(1, Math.Max(MaxOf(@in), MaxOf(@out)));
    }

    /// <summary>Mayor escala de las dos animaciones (al menos 1, la de en medio).</summary>
    public double MaxScale { get; }

    /// <summary>Tramo de la entrada, ya escalado al clip.</summary>
    public Time InDuration => Time.FromTicks(_inTicks);

    /// <summary>Tramo de la salida, ya escalado al clip.</summary>
    public Time OutDuration => Time.FromTicks(_outTicks);

    /// <summary>Nula si el estilo no anima ningún borde activo.</summary>
    public static TitleAnimation? From(TextStyle style, GeneratorEdges edges, Time duration, double unit)
    {
        Curve?[]? @in = edges.In ? Curves(style.In) : null;
        Curve?[]? @out = edges.Out ? Curves(style.Out) : null;
        if ((@in is null && @out is null) || duration <= Time.Zero)
        {
            return null;
        }

        long inTicks = @in is null ? 0 : Time.FromSeconds(style.In!.DurationSeconds).Ticks;
        long outTicks = @out is null ? 0 : Time.FromSeconds(style.Out!.DurationSeconds).Ticks;
        long total = inTicks + outTicks;
        double stretch = 1;
        if (total > duration.Ticks)
        {
            // Las dos se encogen por igual y conservan su forma: suman exactamente el clip, sin solaparse.
            stretch = (double)duration.Ticks / total;
            inTicks = Math.Min(duration.Ticks, (long)Math.Round(inTicks * stretch));
            outTicks = @out is null ? 0 : duration.Ticks - inTicks;
        }

        return new TitleAnimation(@in, inTicks, @out, outTicks, duration.Ticks, stretch, unit);
    }

    /// <summary>
    /// Compone el delta de la animación en <paramref name="sinceStart"/> (tiempo desde el borde izquierdo del clip) con la
    /// transformación del clip: posición y giro se suman, escala y opacidad se multiplican, y el desplazamiento del delta
    /// se escala y gira con el clip.
    /// </summary>
    public LayerTransform Apply(LayerTransform clip, Time sinceStart)
    {
        long at = sinceStart.Ticks;
        Curve?[]? curves;
        long position;
        if (_in is not null && at >= 0 && at < _inTicks)
        {
            curves = _in;
            position = at;
        }
        else if (_out is not null && at >= _durationTicks - _outTicks && at < _durationTicks)
        {
            curves = _out;
            position = at - (_durationTicks - _outTicks);
        }
        else
        {
            return clip;
        }

        // Las curvas están en el tiempo del estilo; si se encogieron para caber, se recorren más rápido.
        long t = _stretch < 1 ? (long)Math.Round(position / _stretch) : position;
        double dx = (curves[0]?.Evaluate(t) ?? 0) * _unit;
        double dy = (curves[1]?.Evaluate(t) ?? 0) * _unit;
        double scale = curves[2]?.Evaluate(t) ?? 1;
        double rotation = curves[3]?.Evaluate(t) ?? 0;
        double opacity = curves[4]?.Evaluate(t) ?? 1;
        return Compose(clip, dx, dy, scale, rotation, opacity);
    }

    /// <summary>Primero el delta, luego el clip: el desplazamiento del delta gira y escala con el clip.</summary>
    internal static LayerTransform Compose(LayerTransform clip, double dx, double dy, double scale, double rotation, double opacity)
    {
        double radians = clip.RotationDegrees * Math.PI / 180;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        return new LayerTransform(
            clip.X + (clip.Scale * ((dx * cos) - (dy * sin))),
            clip.Y + (clip.Scale * ((dx * sin) + (dy * cos))),
            clip.Scale * scale,
            clip.RotationDegrees + rotation,
            Math.Clamp(clip.Opacity * opacity, 0, 1));
    }

    private static Curve?[]? Curves(StyleAnimation? animation)
    {
        if (animation is null || !(animation.DurationSeconds > 0) || !double.IsFinite(animation.DurationSeconds)
            || EffectRegistry.Find(BuiltInEffects.Transform) is not { } transform)
        {
            return null;
        }

        var curves = new Curve?[Slots];
        bool any = false;
        foreach ((string name, List<StyleKey> keys) in animation.Curves)
        {
            if (transform.FindParameter(name) is not { Slot: < Slots } p)
            {
                continue;
            }

            List<(long, double, Interpolation)> points = [.. keys
                .Where(k => double.IsFinite(k.T) && double.IsFinite(k.Value))
                .OrderBy(k => k.T)
                .Select(k => (Time.FromSeconds(Math.Clamp(k.T, 0, animation.DurationSeconds)).Ticks, p.Clamp(k.Value), k.Interp))];
            if (points.Count > 0)
            {
                curves[p.Slot] = Curve.FromPoints(points);
                any = true;
            }
        }

        return any ? curves : null;
    }

    private static double MaxOf(Curve?[]? curves) => curves?[2]?.Max ?? 1;
}
