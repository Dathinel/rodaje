using Rodaje.Core.Editing;
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
    private readonly Time[] _wordStarts;
    private readonly GeneratedContent[] _wordContents;

    public GeneratedClipPlan(string clipId, ClipTimeMap map, GeneratedContent content, EffectPlan? effects, TitleAnimation? animation,
        IReadOnlyList<(Time Start, GeneratedContent Content)>? words = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(content);
        ClipId = clipId;
        TimeMap = map;
        Content = content;
        Effects = effects;
        Animation = animation;
        _wordStarts = words is null ? [] : [.. words.Select(w => w.Start)];
        _wordContents = words is null ? [] : [.. words.Select(w => w.Content)];
        Contents = [content, .. _wordContents.Distinct()];
    }

    public string ClipId { get; }

    public ClipTimeMap TimeMap { get; }

    public Time Start => TimeMap.Start;

    public Time End => TimeMap.End;

    /// <summary>El título sin palabra resaltada: el de antes de la primera palabra y el de los avisos de fuentes.</summary>
    public GeneratedContent Content { get; }

    /// <summary>Todo lo que puede verse del título (con cada palabra resaltada, en karaoke), para prepararlo antes.</summary>
    public IReadOnlyList<GeneratedContent> Contents { get; }

    /// <summary>Lo que se ve en <paramref name="timeline"/>: en karaoke, el título con la palabra que suena resaltada.</summary>
    public GeneratedContent ContentAt(Time timeline)
    {
        if (_wordStarts.Length == 0)
        {
            return Content;
        }

        Time origin = TimeMap.EffectTime(timeline);
        int index = Array.BinarySearch(_wordStarts, origin);
        index = index >= 0 ? index : ~index - 1;
        while (index + 1 < _wordStarts.Length && _wordStarts[index + 1] == origin)
        {
            index++;
        }

        return index >= 0 ? _wordContents[index] : Content;
    }

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

        return new LayerFrame(trackIndex, ClipId, ContentAt(timeline), TransformAt(timeline))
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
        var content = new GeneratedContent(generator.Kind, generator.Text, style, box, sequenceWidth, sequenceHeight, scale) { ClipId = clip.Id };
        List<(Time Start, GeneratedContent Content)>? words = null;
        if (style.Highlight is not null && generator.Words is { Count: > 0 } list)
        {
            // Una textura por palabra: un subtítulo lleva pocas y se preparan por adelantado como cualquier título.
            IReadOnlyList<WordRange> ranges = SubtitleEdits.WordRanges(generator.Text, list);
            words = new List<(Time, GeneratedContent)>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                GeneratedContent shown = ranges[i].Found
                    ? new GeneratedContent(generator.Kind, generator.Text, style, box, sequenceWidth, sequenceHeight, scale, ranges[i].Start, ranges[i].Length) { ClipId = clip.Id }
                    : content;
                words.Add((list[i].Start, shown));
            }
        }

        return new GeneratedClipPlan(clip.Id, clip.TimeMap, content, effects, animation, words);
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
    private readonly long _leadTicks;
    private readonly long _durationTicks;
    private readonly double _stretch;
    private readonly double _unit;

    private TitleAnimation(Curve?[]? @in, long inTicks, Curve?[]? @out, long outTicks, long leadTicks, long durationTicks, double stretch, double unit)
    {
        _in = @in;
        _out = @out;
        _inTicks = inTicks;
        _outTicks = outTicks;
        _leadTicks = leadTicks;
        _durationTicks = durationTicks;
        _stretch = stretch;
        _unit = unit;
        MaxScale = Math.Max(1, Math.Max(MaxOf(@in), MaxOf(@out)));
    }

    /// <summary>Mayor escala de las dos animaciones (al menos 1, la de en medio).</summary>
    public double MaxScale { get; }

    /// <summary>Tramo de la entrada, ya escalado al título entero.</summary>
    public Time InDuration => Time.FromTicks(_inTicks);

    /// <summary>Tramo de la salida, ya escalado al título entero.</summary>
    public Time OutDuration => Time.FromTicks(_outTicks);

    /// <summary>
    /// Nula si el estilo no anima ningún borde activo. Un trozo de un título dividido se anima como el título entero:
    /// <see cref="GeneratorEdges.Before"/> y <see cref="GeneratorEdges.After"/> lo alargan por los dos lados.
    /// </summary>
    public static TitleAnimation? From(TextStyle style, GeneratorEdges edges, Time duration, double unit)
    {
        Curve?[]? @in = edges.In ? Curves(style.In) : null;
        Curve?[]? @out = edges.Out ? Curves(style.Out) : null;
        if ((@in is null && @out is null) || duration <= Time.Zero)
        {
            return null;
        }

        long lead = Math.Max(0, edges.Before.Ticks);
        long whole = lead + duration.Ticks + Math.Max(0, edges.After.Ticks);
        (long inTicks, long outTicks, double stretch) = TitleMotion.Fit(
            @in is null ? 0 : Time.FromSeconds(style.In!.DurationSeconds).Ticks,
            @out is null ? 0 : Time.FromSeconds(style.Out!.DurationSeconds).Ticks,
            whole);
        return new TitleAnimation(@in, inTicks, @out, outTicks, lead, whole, stretch, unit);
    }

    /// <summary>
    /// Compone el delta de la animación en <paramref name="sinceStart"/> (tiempo desde el borde izquierdo del clip) con la
    /// transformación del clip: posición y giro se suman, escala y opacidad se multiplican, y el desplazamiento del delta
    /// se escala y gira con el clip.
    /// </summary>
    public LayerTransform Apply(LayerTransform clip, Time sinceStart)
    {
        long at = sinceStart.Ticks + _leadTicks;
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

    /// <summary>Primero el delta, luego el clip: el desplazamiento del delta gira y escala con el clip (<see cref="TitleMotion.Compose"/>).</summary>
    internal static LayerTransform Compose(LayerTransform clip, double dx, double dy, double scale, double rotation, double opacity)
    {
        (double x, double y, double s, double r, double o) = TitleMotion.Compose(
            clip.X, clip.Y, clip.Scale, clip.RotationDegrees, clip.Opacity, dx, dy, scale, rotation, opacity);
        return new LayerTransform(x, y, s, r, o);
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
