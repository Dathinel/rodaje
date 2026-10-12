using System.Collections;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Playback.Composition;

namespace Rodaje.Engine.Timeline;

/// <summary>
/// Los efectos activos de un clip de video copiados del modelo al armar el plan: transformación, efectos de imagen en el
/// orden de la pila y el primer modo de mezcla activo. Se evalúan en tiempo del origen sin reservar memoria.
/// </summary>
/// <remarks>
/// Solo entran los tipos del registro con etapa de transformación, de imagen o de mezcla; los demás se conservan en el
/// proyecto pero no se aplican. Las opciones de tipo <see cref="OptionKind.Choice"/> viajan como el índice de la opción
/// en el hueco que sigue a los parámetros; la referencia de una LUT viaja como <see cref="LayerEffect.Resource"/>.
/// </remarks>
internal sealed class EffectPlan
{
    private readonly Curve[]? _transform;
    private readonly ImageEffect[] _effects;
    private Curve[]? _crop;

    private EffectPlan(Curve[]? transform, ImageEffect[] effects, BlendMode blend, StabilizationTrack? stabilization)
    {
        _transform = transform;
        _effects = effects;
        Blend = blend;
        Stabilization = stabilization;
    }

    public BlendMode Blend { get; }

    /// <summary>Las correcciones del primer «Estabilizar» activo cuyo análisis está en la caché.</summary>
    public StabilizationTrack? Stabilization { get; }

    public int EffectCount => _effects.Length;

    /// <summary>Puede dejar ver lo de abajo aunque sea opaca: recorte, máscara o croma.</summary>
    public bool MayRevealBelow => _crop is not null
        || Array.Exists(_effects, e => e.Code == MaskCode || e.Code == ChromaKeyCode || e.Code == DistortionCode);

    private static readonly int MaskCode = EffectRegistry.Find(BuiltInEffects.Mask)!.Code;
    private static readonly int ChromaKeyCode = EffectRegistry.Find(BuiltInEffects.ChromaKey)!.Code;
    private static readonly int DistortionCode = EffectRegistry.Find(BuiltInEffects.LensDistortion)!.Code;

    /// <summary>Nulo si el clip no tiene transformación, efectos de imagen, mezcla ni estabilizar: el camino rápido.</summary>
    /// <param name="effects">La pila del clip.</param>
    /// <param name="content">De dónde salen las correcciones de estabilizar; sin él, no se estabiliza.</param>
    public static EffectPlan? From(IReadOnlyList<EffectInstance> effects, IEffectContent? content = null)
    {
        ArgumentNullException.ThrowIfNull(effects);
        Curve[]? transform = null;
        var images = new List<ImageEffect>();
        BlendMode blend = BlendMode.Normal;
        bool blended = false;
        StabilizationTrack? stabilization = null;
        bool stabilized = false;
        Curve[]? crop = null;
        foreach (EffectInstance effect in effects)
        {
            if (!effect.Enabled || EffectRegistry.Find(effect.Type) is not { } definition)
            {
                continue;
            }

            if (effect.Type == BuiltInEffects.Stabilize)
            {
                if (!stabilized)
                {
                    stabilized = true;
                    stabilization = FindStabilization(definition, effect, content);
                }

                continue;
            }

            if (effect.Type == BuiltInEffects.Crop)
            {
                crop ??= Curves(definition, effect.Parameters);
                continue;
            }

            switch (definition.Stage)
            {
                case EffectStage.Transform when transform is null:
                    transform = Curves(definition, effect.Parameters);
                    break;
                case EffectStage.PerPixel or EffectStage.Spatial:
                    images.Add(ImageEffect.From(definition, effect));
                    break;
                case EffectStage.Blend when !blended:
                    // Como la transformación, manda el primer modo de mezcla activo de la pila.
                    blend = ParseBlend(effect.Options.GetValueOrDefault(BuiltInEffects.BlendOptions.Mode));
                    blended = true;
                    break;
            }
        }

        return transform is null && images.Count == 0 && blend == BlendMode.Normal && stabilization is null && crop is null
            ? null
            : new EffectPlan(transform, [.. images], blend, stabilization) { _crop = crop };
    }

    private static StabilizationTrack? FindStabilization(EffectDefinition definition, EffectInstance effect, IEffectContent? content)
    {
        if (content is null || effect.Options.GetValueOrDefault(BuiltInEffects.StabilizeOptions.Analysis) is not { Length: > 0 } analysis)
        {
            return null;
        }

        ParameterDefinition intensity = definition.FindParameter(BuiltInEffects.StabilizeParameters.Intensity)!;
        double value = intensity.Clamp(effect.Parameters.GetValueOrDefault(BuiltInEffects.StabilizeParameters.Intensity)?.Value
            ?? intensity.DefaultValue);
        bool crop = (effect.Options.GetValueOrDefault(BuiltInEffects.StabilizeOptions.Crop) ?? "auto") != "off";
        return value > 0 ? content.FindStabilization(analysis, value, crop) : null;
    }

    /// <summary>La transformación con la corrección de estabilizar del cuadro <paramref name="frameIndex"/> del medio, si hay.</summary>
    public static LayerTransform Stabilized(EffectPlan? plan, LayerTransform transform, long frameIndex) =>
        plan?.Stabilization is { } track ? transform with { Stabilization = track.At(frameIndex) } : transform;

    public static LayerTransform Transform(EffectPlan? plan, Time sourceTime)
    {
        long at = sourceTime.Ticks;
        LayerTransform transform = plan?._transform is { } curves
            ? new LayerTransform(curves[0].Evaluate(at), curves[1].Evaluate(at), curves[2].Evaluate(at), curves[3].Evaluate(at), curves[4].Evaluate(at))
            : LayerTransform.Identity;
        if (plan?._crop is { } crop)
        {
            CropRect rect = CropGeometry.Sanitize(new CropRect(crop[0].Evaluate(at), crop[1].Evaluate(at), crop[2].Evaluate(at), crop[3].Evaluate(at)));
            transform = rect.IsNone ? transform : transform with { Crop = rect };
        }

        return transform;
    }

    /// <summary>La mayor escala de la transformación (1 sin transformación).</summary>
    public static double MaxScale(EffectPlan? plan) => plan?._transform is { } curves ? curves[2].Max : 1;

    /// <summary>Escribe los efectos de imagen en <paramref name="destination"/>; una LUT que todavía no está lista sale sin recurso.</summary>
    public void Evaluate(Time sourceTime, Span<LayerEffect> destination, IEffectResources? resources)
    {
        for (int i = 0; i < _effects.Length; i++)
        {
            destination[i] = _effects[i].Evaluate(sourceTime.Ticks, resources);
        }
    }

    /// <summary>Índice en <see cref="EffectRegistry.BlendModes"/>, que sigue el orden de <see cref="BlendMode"/>.</summary>
    internal static BlendMode ParseBlend(string? mode)
    {
        int index = mode is null ? -1 : EffectRegistry.BlendModes.IndexOf(mode);
        return index < 0 ? BlendMode.Normal : (BlendMode)index;
    }

    /// <summary>Una curva por parámetro del registro, en el orden de <see cref="ParameterDefinition.Slot"/>.</summary>
    internal static Curve[] Curves(EffectDefinition definition, IReadOnlyDictionary<string, Parameter> parameters)
    {
        var curves = new Curve[definition.Parameters.Length];
        foreach (ParameterDefinition p in definition.Parameters)
        {
            curves[p.Slot] = Curve.From(parameters.GetValueOrDefault(p.Name), p.DefaultValue, p.Clamp, static ticks => ticks);
        }

        return curves;
    }

    /// <summary>Valores fijos de las opciones de elección: el índice de la elegida, o el de la de por defecto si no se reconoce.</summary>
    internal static void WriteChoices(EffectDefinition definition, IReadOnlyDictionary<string, string> options, ref EffectValues values)
    {
        int slot = definition.Parameters.Length;
        foreach (OptionDefinition option in definition.Options)
        {
            if (option.Kind != OptionKind.Choice)
            {
                continue;
            }

            int index = option.Choices.IndexOf(options.GetValueOrDefault(option.Name) ?? option.DefaultValue);
            values[slot++] = Math.Max(0, index < 0 ? option.Choices.IndexOf(option.DefaultValue) : index);
        }
    }

    private sealed class ImageEffect(int code, Curve[] curves, EffectValues constants, ResourceKey? resource)
    {
        public static ImageEffect From(EffectDefinition definition, EffectInstance effect)
        {
            EffectValues constants = default;
            WriteChoices(definition, effect.Options, ref constants);
            ResourceKey? resource = null;
            foreach (OptionDefinition option in definition.Options)
            {
                if (option.Kind == OptionKind.PackageReference && effect.Options.GetValueOrDefault(option.Name) is { Length: > 0 } reference)
                {
                    resource = new ResourceKey(reference);
                }
            }

            if (definition.Type == BuiltInEffects.Mask)
            {
                // El trazo o el video gris viajan como recurso; el render los lee sin pasar por las LUT.
                MaskShape shape = MaskGeometry.ParseShape(effect.Options.GetValueOrDefault(BuiltInEffects.MaskOptions.Shape));
                string? key = shape switch
                {
                    MaskShape.Path => effect.Options.GetValueOrDefault(BuiltInEffects.MaskOptions.Path),
                    MaskShape.Image => effect.Options.GetValueOrDefault(BuiltInEffects.MaskOptions.Matte),
                    _ => null,
                };
                double start = double.TryParse(effect.Options.GetValueOrDefault(BuiltInEffects.MaskOptions.MatteStart),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s) && double.IsFinite(s) ? s : 0;
                return new ImageEffect(definition.Code, Curves(definition, effect.Parameters), constants, key is { Length: > 0 } ? new ResourceKey(key) : null)
                {
                    MatteStart = Time.FromSeconds(start).Ticks,
                    IsMask = true,
                };
            }

            if (definition.Type == BuiltInEffects.Curves)
            {
                // Las cuatro curvas viajan como una clave de texto; el render arma la tabla y la guarda.
                string key = ToneCurve.Key(effect.Options);
                return new ImageEffect(definition.Code, [], constants, ToneCurve.IsIdentityKey(key) ? null : new ResourceKey(key)) { KeepsResource = true };
            }

            return new ImageEffect(definition.Code, Curves(definition, effect.Parameters), constants, resource)
            {
                TimeSlot = definition.UsesTime ? definition.TimeSlot : -1,
            };
        }

        public int Code => code;

        private bool KeepsResource { get; init; }

        private bool IsMask { get; init; }

        private long MatteStart { get; init; }

        /// <summary>Hueco donde va el tiempo del origen en segundos, o −1 si el efecto no lo usa.</summary>
        private int TimeSlot { get; init; } = -1;

        public LayerEffect Evaluate(long at, IEffectResources? resources)
        {
            EffectValues values = constants;
            for (int i = 0; i < curves.Length; i++)
            {
                values[i] = (float)curves[i].Evaluate(at);
            }

            if (IsMask)
            {
                values[MaskGeometry.MatteTimeSlot] = (float)((double)(at - MatteStart) / Time.TicksPerSecond);
                return new LayerEffect(code, values, resource);
            }

            if (KeepsResource)
            {
                return new LayerEffect(code, values, resource);
            }

            if (TimeSlot >= 0)
            {
                values[TimeSlot] = (float)((double)at / Time.TicksPerSecond);
            }

            ResourceKey? ready = resource is { } key && resources is not null && resources.TryGetLut(key, out _) ? key : null;
            return new LayerEffect(code, values, ready);
        }
    }
}

/// <summary>
/// Lista de efectos evaluados que se reutiliza entre refrescos: cada carril tiene unas pocas y las va rotando, así una
/// composición vale hasta que pasan <see cref="EffectListRing.Size"/> - 1 refrescos más (la app la dibuja y la desecha
/// en el mismo refresco).
/// </summary>
internal sealed class EffectList : IReadOnlyList<LayerEffect>
{
    private LayerEffect[] _items = [];

    public int Count { get; private set; }

    public LayerEffect this[int index] => (uint)index < (uint)Count ? _items[index] : throw new ArgumentOutOfRangeException(nameof(index));

    public Span<LayerEffect> Reset(int count)
    {
        if (_items.Length < count)
        {
            _items = new LayerEffect[count];
        }

        Count = count;
        return _items.AsSpan(0, count);
    }

    public ArraySegment<LayerEffect>.Enumerator GetEnumerator() => new ArraySegment<LayerEffect>(_items, 0, Count).GetEnumerator();

    IEnumerator<LayerEffect> IEnumerable<LayerEffect>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class EffectListRing
{
    public const int Size = 3;

    private readonly EffectList[] _lists = [new(), new(), new()];
    private int _next;

    public EffectList Next()
    {
        EffectList list = _lists[_next];
        _next = (_next + 1) % Size;
        return list;
    }
}

/// <summary>
/// Una transición en el corte entre dos clips de una pista, ya resuelta: su ventana en la secuencia, la mezcla del
/// registro con sus valores y, si es de paquete, las curvas que se suman a la transformación de cada lado.
/// </summary>
internal sealed class TransitionPlan
{
    /// <summary>Unidad de las posiciones de las curvas de paquete: millonésimas del avance.</summary>
    internal const double CurveScale = 1_000_000;

    private readonly Curve?[]? _outgoing;
    private readonly Curve?[]? _incoming;
    private readonly double _scaleX;
    private readonly double _scaleY;

    public TransitionPlan(string id, string fromClipId, string toClipId, Time start, Time end, int code, EffectValues values,
        TransitionCurves? curves = null, int width = 0, int height = 0, bool fellBack = false)
    {
        Id = id;
        FromClipId = fromClipId;
        ToClipId = toClipId;
        Start = start;
        End = end;
        Code = code;
        Values = values;
        FellBack = fellBack;
        if (curves is not null)
        {
            _outgoing = Resolve(curves.Outgoing);
            _incoming = Resolve(curves.Incoming);
            _scaleX = curves.ReferenceWidth > 0 && width > 0 ? (double)width / curves.ReferenceWidth : 1;
            _scaleY = curves.ReferenceHeight > 0 && height > 0 ? (double)height / curves.ReferenceHeight : 1;
        }
    }

    public string Id { get; }

    public string FromClipId { get; }

    public string ToClipId { get; }

    /// <summary>Inicio de la transición en la secuencia: el corte menos la mitad de antes.</summary>
    public Time Start { get; }

    public Time End { get; }

    public int Code { get; }

    public EffectValues Values { get; }

    /// <summary>Era de paquete y el contenido faltaba: se ve como fundido cruzado.</summary>
    public bool FellBack { get; }

    public bool Covers(Time timeline) => timeline >= Start && timeline < End;

    public float Progress(Time timeline) =>
        End <= Start ? 1f : (float)Math.Clamp((double)(timeline - Start).Ticks / (End - Start).Ticks, 0, 1);

    /// <summary>Suma a <paramref name="transform"/> las curvas del lado pedido: posiciones y giro se suman, escala y opacidad se multiplican.</summary>
    public LayerTransform Apply(LayerTransform transform, bool incoming, float progress)
    {
        if ((incoming ? _incoming : _outgoing) is not { } curves)
        {
            return transform;
        }

        long at = (long)Math.Round(progress * CurveScale);
        return transform with
        {
            X = transform.X + (curves[0] is { } x ? x.Evaluate(at) * _scaleX : 0),
            Y = transform.Y + (curves[1] is { } y ? y.Evaluate(at) * _scaleY : 0),
            Scale = transform.Scale * (curves[2]?.Evaluate(at) ?? 1),
            RotationDegrees = transform.RotationDegrees + (curves[3]?.Evaluate(at) ?? 0),
            Opacity = Math.Clamp(transform.Opacity * (curves[4]?.Evaluate(at) ?? 1), 0, 1),
        };
    }

    private static Curve?[]? Resolve(IReadOnlyList<TransitionCurve> curves)
    {
        if (curves.Count == 0 || EffectRegistry.Find(BuiltInEffects.Transform) is not { } transform)
        {
            return null;
        }

        var resolved = new Curve?[transform.Parameters.Length];
        foreach (TransitionCurve curve in curves)
        {
            if (transform.FindParameter(curve.Parameter) is not { } p)
            {
                continue;
            }

            List<(long, double, Interpolation)> points = [.. curve.Keys
                .Where(k => double.IsFinite(k.Progress) && double.IsFinite(k.Value))
                .OrderBy(k => k.Progress)
                .Select(k => ((long)Math.Round(Math.Clamp(k.Progress, 0, 1) * CurveScale), p.Clamp(k.Value), k.Interpolation))];
            if (points.Count > 0)
            {
                resolved[p.Slot] = Curve.FromPoints(points);
            }
        }

        return resolved;
    }
}
