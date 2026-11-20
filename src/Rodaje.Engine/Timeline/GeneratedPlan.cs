using Rodaje.Core.Editing;
using Rodaje.Core.Text;
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
    private readonly Time[] _spokenStarts;
    private readonly Time[] _spokenEnds;

    public GeneratedClipPlan(string clipId, ClipTimeMap map, GeneratedContent content, EffectPlan? effects, TitleAnimation? animation,
        IReadOnlyList<(Time Start, GeneratedContent Content)>? words = null, IReadOnlyList<GeneratorWord>? spoken = null)
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
        _spokenStarts = content.UsesUnits && spoken is not null ? [.. spoken.Select(w => w.Start)] : [];
        _spokenEnds = content.UsesUnits && spoken is not null ? [.. spoken.Select(w => w.End)] : [];
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

    /// <summary>Lo que el título entero dura antes de este trozo (<see cref="GeneratorEdges.Before"/>): origen de la fase del bucle.</summary>
    internal long LoopLeadTicks { get; init; }

    public TitleAnimation? Animation { get; }

    /// <summary>Posición de una capa de plantilla (D-064) en píxeles de la secuencia, sumada a la del clip.</summary>
    public double OffsetX { get; init; }

    public double OffsetY { get; init; }

    public bool Covers(Time timeline) => timeline >= Start && timeline < End;

    /// <summary>
    /// Transformación en <paramref name="timeline"/>: la animación del estilo es un delta en el espacio de la capa que se
    /// aplica antes que la transformación del clip, así se escala y gira con ella.
    /// </summary>
    public LayerTransform TransformAt(Time timeline)
    {
        LayerTransform clip = EffectPlan.Transform(Effects, TimeMap.EffectTime(timeline));
        clip = clip with { X = clip.X + OffsetX, Y = clip.Y + OffsetY };
        LayerTransform animated = Animation is { } animation ? animation.Apply(clip, timeline - Start) : clip;

        // El bucle va por fuera de la entrada y la salida (como al convertirlas en keyframes, que pasan al efecto del clip) y
        // su fase cuenta desde el inicio del título entero: un trozo cortado sigue donde iba, sin reiniciarse.
        if (Content.Style.Loop is { } loop)
        {
            UnitPose delta = TextLoops.Delta(loop, Time.FromTicks((timeline - Start).Ticks + LoopLeadTicks).ToSeconds());
            animated = TitleAnimation.Compose(animated, delta.X * Content.UnitScale, delta.Y * Content.UnitScale,
                delta.Scale, delta.Rotation, delta.Opacity);
        }

        return animated;
    }

    /// <summary>
    /// Para un título por unidades: la fase de su animación por unidades y, en karaoke, la palabra que suena (la última
    /// que empezó) con cuánto se dijo de ella. En reposo si el título se dibuja entero.
    /// </summary>
    public TextMotion MotionAt(Time timeline)
    {
        if (!Content.UsesUnits)
        {
            return TextMotion.Rest;
        }

        (TextPhase phase, double progress) = Animation?.UnitPhase(timeline - Start) ?? (TextPhase.None, 0);
        int word = -1;
        double said = 0;
        if (_spokenStarts.Length > 0)
        {
            Time origin = TimeMap.EffectTime(timeline);
            int index = Array.BinarySearch(_spokenStarts, origin);
            index = index >= 0 ? index : ~index - 1;
            while (index + 1 < _spokenStarts.Length && _spokenStarts[index + 1] == origin)
            {
                index++;
            }

            if (index >= 0)
            {
                word = index;
                long length = (_spokenEnds[index] - _spokenStarts[index]).Ticks;
                said = length <= 0 ? 1 : Math.Clamp((double)(origin - _spokenStarts[index]).Ticks / length, 0, 1);
            }
        }

        return new TextMotion(phase, progress, word, said);
    }

    /// <summary>La capa del instante, con los efectos evaluados en una lista de <paramref name="ring"/>.</summary>
    /// <param name="frameTicks">Un cuadro de la secuencia, para el obturador del desenfoque de movimiento.</param>
    public LayerFrame Layer(int trackIndex, Time timeline, EffectListRing ring, IEffectResources? resources, long frameTicks)
    {
        IReadOnlyList<LayerEffect> effects = [];
        EffectList? lists = null;
        if (Effects is { EffectCount: > 0 } plan)
        {
            lists = ring.Next();
            plan.Evaluate(TimeMap.EffectTime(timeline), lists.Reset(plan.EffectCount), resources);
            effects = lists;
        }

        IReadOnlyList<LayerTransform>? motion = MotionSampler.Sample(Effects?.Motion, new TitleMotionSource(this), timeline, frameTicks, ring, ref lists);
        return new LayerFrame(trackIndex, ClipId, ContentAt(timeline), TransformAt(timeline))
        {
            Effects = effects,
            Blend = Effects?.Blend ?? BlendMode.Normal,
            MotionSamples = motion,
            TextMotion = MotionAt(timeline),
        };
    }

    /// <summary>
    /// El plan de un clip con generador, o nulo si no se dibuja (tipo desconocido). El estilo y la caja se copian: el plan
    /// no cambia si el modelo cambia después.
    /// </summary>
    public static GeneratedClipPlan? From(Clip clip, int sequenceWidth, int sequenceHeight) =>
        clip.Generator is { Kind: GeneratorKinds.Text } generator
            ? Build(clip.Id, clip.TimeMap, clip.Duration, EffectPlan.From(clip.Effects), generator.Style, generator.Edges, generator.Box,
                generator.Text, generator.Spans, generator.Words, sequenceWidth, sequenceHeight)
            : null;

    /// <summary>
    /// Las capas de un clip de plantilla de movimiento (D-064), de abajo arriba: cada una un título con su ventana dentro del
    /// clip (<see cref="TemplateLayer.Start"/> tras el borde izquierdo hasta <see cref="TemplateLayer.End"/> antes del
    /// derecho, contando lo que el clip tenía antes de dividirse), su posición sumada a la del clip y los efectos del clip.
    /// </summary>
    public static IReadOnlyList<GeneratedClipPlan?> Layers(Clip clip, int sequenceWidth, int sequenceHeight)
    {
        if (clip.Generator is not { Kind: GeneratorKinds.Template, Template: { } template } generator)
        {
            return [];
        }

        EffectPlan? effects = EffectPlan.From(clip.Effects);
        GeneratorEdges edges = generator.Edges;
        var plans = new GeneratedClipPlan?[template.Layers.Count];
        for (int i = 0; i < plans.Length; i++)
        {
            TemplateLayer layer = template.Layers[i];
            Time before = Time.FromSeconds(layer.Start) - edges.Before;
            Time after = Time.FromSeconds(layer.End) - edges.After;
            var window = new Clip
            {
                Id = clip.Id,
                Start = clip.Start + Time.Max(Time.Zero, before),
                SourceIn = clip.SourceIn + Time.Max(Time.Zero, before),
                SourceOut = clip.SourceOut - Time.Max(Time.Zero, after),
            };
            if (window.Duration <= Time.Zero)
            {
                continue;
            }

            var layerEdges = new GeneratorEdges { In = edges.In, Out = edges.Out, Before = Time.Max(Time.Zero, -before), After = Time.Max(Time.Zero, -after) };
            double unit = GeneratedContent.UnitScaleFor(layer.Style.Reference, sequenceWidth, sequenceHeight);
            plans[i] = Build(clip.Id, window.TimeMap, window.Duration, effects, layer.Style, layerEdges, layer.Box, layer.Text, null, null,
                sequenceWidth, sequenceHeight) is { } plan
                ? new GeneratedClipPlan(plan, layer.X * unit, layer.Y * unit)
                : null;
        }

        return plans;
    }

    private GeneratedClipPlan(GeneratedClipPlan source, double offsetX, double offsetY)
        : this(source.ClipId, source.TimeMap, source.Content, source.Effects, source.Animation)
    {
        OffsetX = offsetX;
        OffsetY = offsetY;
        LoopLeadTicks = source.LoopLeadTicks;
    }

    private static GeneratedClipPlan Build(string clipId, ClipTimeMap map, Time duration, EffectPlan? effects, TextStyle? source, GeneratorEdges edges,
        TextFrame? frame, string text, List<TextSpan>? textSpans, List<GeneratorWord>? spokenWords, int sequenceWidth, int sequenceHeight)
    {
        TextStyle style = source?.Clone() ?? new TextStyle();

        // Un borde sin animación no la necesita en la textura ni en la clave de la caché.
        style.In = edges.In ? style.In : null;
        style.Out = edges.Out ? style.Out : null;
        double unit = GeneratedContent.UnitScaleFor(style.Reference, sequenceWidth, sequenceHeight);
        double box = frame switch
        {
            { WidthFraction: > 0 and <= 1 } f => f.WidthFraction.Value * sequenceWidth,
            { Width: > 0 } f => f.Width.Value * unit,
            _ => 0,
        };

        TitleAnimation? animation = TitleAnimation.From(style, edges, duration, unit);
        double unitScale = Math.Max(TextAnimator.From(style.In)?.MaxScale ?? 1, TextAnimator.From(style.Out)?.MaxScale ?? 1);
        double loopScale = style.Loop is { Kind: TextLoopKind.Pulse } loop ? 1 + 0.06 * loop.Amount : 1;
        double scale = Math.Clamp(EffectPlan.MaxScale(effects) * (animation?.MaxScale ?? 1) * unitScale * loopScale, MinRasterScale, MaxRasterScale);
        IReadOnlyList<WordRange>? ranges = style.Highlight is not null && spokenWords is { Count: > 0 } spoken
            ? SubtitleEdits.WordRanges(text, spoken)
            : null;
        var extras = new TextExtras(
            textSpans is { Count: > 0 } spans ? [.. spans] : null,
            frame?.HeightFraction is > 0 and <= 1 ? frame.HeightFraction.Value * sequenceHeight : 0,
            ranges?.Select(r => r.Found ? (r.Start, r.Length) : (-1, 0)).ToArray());
        var content = new GeneratedContent(GeneratorKinds.Text, text, style, box, sequenceWidth, sequenceHeight, scale, extras: extras) { ClipId = clipId };
        List<(Time Start, GeneratedContent Content)>? words = null;
        if (!content.UsesUnits && ranges is not null && spokenWords is { Count: > 0 } list)
        {
            // Una textura por palabra: un subtítulo lleva pocas y se preparan por adelantado como cualquier título.
            words = new List<(Time, GeneratedContent)>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                GeneratedContent shown = ranges[i].Found
                    ? new GeneratedContent(GeneratorKinds.Text, text, style, box, sequenceWidth, sequenceHeight, scale, ranges[i].Start, ranges[i].Length, extras) { ClipId = clipId }
                    : content;
                words.Add((list[i].Start, shown));
            }
        }

        return new GeneratedClipPlan(clipId, map, content, effects, animation, words, spokenWords) { LoopLeadTicks = Math.Max(0, edges.Before.Ticks) };

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
    private readonly bool _inUnits;
    private readonly bool _outUnits;

    private TitleAnimation(Curve?[]? @in, long inTicks, Curve?[]? @out, long outTicks, long leadTicks, long durationTicks, double stretch, double unit,
        bool inUnits, bool outUnits)
    {
        _inUnits = inUnits;
        _outUnits = outUnits;
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
        // Por unidades no hay delta del bloque: el rasterizador anima cada unidad con la fase que da UnitPhase.
        bool inUnits = edges.In && style.In is { PerUnit: true } && TitleMotion.Ticks(style.In) > 0;
        bool outUnits = edges.Out && style.Out is { PerUnit: true } && TitleMotion.Ticks(style.Out) > 0;
        Curve?[]? @in = edges.In && !inUnits ? Curves(style.In) : null;
        Curve?[]? @out = edges.Out && !outUnits ? Curves(style.Out) : null;
        if ((@in is null && @out is null && !inUnits && !outUnits) || duration <= Time.Zero)
        {
            return null;
        }

        long lead = Math.Max(0, edges.Before.Ticks);
        long whole = lead + duration.Ticks + Math.Max(0, edges.After.Ticks);
        (long inTicks, long outTicks, double stretch) = TitleMotion.Fit(
            @in is null && !inUnits ? 0 : Time.FromSeconds(style.In!.DurationSeconds).Ticks,
            @out is null && !outUnits ? 0 : Time.FromSeconds(style.Out!.DurationSeconds).Ticks,
            whole);
        return new TitleAnimation(@in, inTicks, @out, outTicks, lead, whole, stretch, unit, inUnits, outUnits);
    }

    /// <summary>
    /// La fase por unidades en <paramref name="sinceStart"/> y su avance de 0 a 1 (ya encogida si no cabía); en reposo
    /// fuera de una entrada o salida por unidades.
    /// </summary>
    public (TextPhase Phase, double Progress) UnitPhase(Time sinceStart)
    {
        long at = sinceStart.Ticks + _leadTicks;
        if (_inUnits && at >= 0 && at < _inTicks)
        {
            return (TextPhase.In, (double)at / _inTicks);
        }

        long outStart = _durationTicks - _outTicks;
        if (_outUnits && at >= outStart && at < _durationTicks)
        {
            return (TextPhase.Out, (double)(at - outStart) / _outTicks);
        }

        return (TextPhase.None, 0);
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
        return clip with { X = x, Y = y, Scale = s, RotationDegrees = r, Opacity = o };
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
