using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;
using Rodaje.Playback;
using Rodaje.Playback.Composition;

namespace Rodaje.Engine.RenderCache;

/// <summary>Un medio de la secuencia para la receta: su identidad por contenido y cómo se coloca su imagen.</summary>
/// <param name="Source">Huella de contenido y formato del cuadro decodificado.</param>
/// <param name="SampleAspectRatio">Forma del píxel del medio.</param>
/// <param name="Rotation">Giro del medio en grados (cualquier valor; se normaliza como en la exportación).</param>
public sealed record RecipeMedia(RecipeSource Source, Rational SampleAspectRatio, int Rotation);

/// <summary>Opciones de <see cref="RecipeBuilder"/>: todo lo de fuera, ya leído (sin lectores ni esperas).</summary>
public sealed record RecipeBuilderOptions
{
    /// <summary>Carpeta contra la que se resuelven las rutas relativas de los medios.</summary>
    public string? ProjectDirectory { get; init; }

    /// <summary>El contenido de paquetes con el que se arma el plan (transiciones de paquete y análisis de estabilizar).</summary>
    public IEffectContent? Content { get; init; }

    /// <summary>El medio de una ruta absoluta; nulo si falta, está sin conexión o aún no se leyó.</summary>
    public required Func<string, RecipeMedia?> Media { get; init; }

    /// <summary>La identidad de un recurso pedido (LUT, máscara de imagen, imagen de transición); nula si falta o no está listo.</summary>
    public required Func<ResourceKey, string?> Resources { get; init; }

    /// <summary>La huella de las fuentes de un título; nula si falta alguna.</summary>
    public required Func<GeneratedContent, string?> Fonts { get; init; }

    /// <summary>Huella de la cadena de render y codificación (<see cref="RecipeFormat.Chain"/>).</summary>
    public string Chain { get; init; } = "";
}

/// <summary>
/// Las huellas de los cuadros de una secuencia (D-060): arma las capas con <see cref="FrameEvaluator"/>, el mismo que usa
/// la exportación, sin decodificar nada, y las reduce con <see cref="FrameRecipe"/>. Se usa desde un solo hilo.
/// </summary>
public sealed class RecipeBuilder : IEvaluatorFrames, IRecipeSources
{
    private readonly FrameEvaluator _evaluator;
    private readonly RecipeBuilderOptions _options;
    private readonly RecipeFormat _format;
    private readonly HashSet<string> _unstabilized;
    private readonly Dictionary<SourceKey, RecipeMedia?> _media = [];
    private readonly Dictionary<DecodedFrame, RecipeSource?> _frames = new(ReferenceEqualityComparer.Instance);

    private RecipeBuilder(TimelinePlan plan, RecipeBuilderOptions options)
    {
        _options = options;
        _evaluator = new FrameEvaluator(plan, RequestedResources.Instance);
        _format = new RecipeFormat(plan.FrameRate, plan.Expanded.ColorSpace, options.Chain);
        _unstabilized = Unstabilized(plan);
    }

    /// <exception cref="KeyNotFoundException">La secuencia no existe en el proyecto.</exception>
    public static RecipeBuilder Create(Project project, string sequenceId, RecipeBuilderOptions options)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(options);
        TimelinePlan plan = TimelinePlan.Build(project, sequenceId, options.ProjectDirectory ?? Environment.CurrentDirectory, options.Content);
        return new RecipeBuilder(plan, options);
    }

    public long FrameCount => _evaluator.Plan.FrameCount;

    public Rational FrameRate => _evaluator.Plan.FrameRate;

    /// <summary>La huella del cuadro <paramref name="frame"/> de la secuencia, o <c>null</c> si no tiene receta válida.</summary>
    public RenderCacheKey? KeyAt(long frame, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(frame, FrameCount);
        Time time = Time.FromFrames(frame, FrameRate);
        _evaluator.Pick(time);
        foreach (FrameEvaluator.Lane lane in _evaluator.Lanes)
        {
            // Una transición de paquete que cayó a fundido es un respaldo: no se guarda.
            if (lane.Transition is { FellBack: true })
            {
                return null;
            }
        }

        _frames.Clear();
        using CompositionFrame composition = _evaluator.Build(time, frame, this, cancellationToken);
        return Incomplete(composition.Layers) ? null : FrameRecipe.Compute(composition, _format, this);
    }

    (DecodedFrame Frame, Rational SampleAspectRatio, int Rotation) IEvaluatorFrames.Get(int ordinal, VideoClipPlan clip, bool incoming, long index,
        long sequenceFrame, CancellationToken cancellationToken)
    {
        if (!_media.TryGetValue(clip.Source, out RecipeMedia? media))
        {
            _media[clip.Source] = media = _options.Media(clip.Source.Path);
        }

        var frame = new DecodedFrame(index, Time.Zero, Time.FromTicks(1), null, 0);
        _frames[frame] = media?.Source;
        return (frame, media?.SampleAspectRatio ?? new Rational(1, 1), TimelineScheduler.NormalizeRotation(media?.Rotation ?? 0));
    }

    RecipeSource? IRecipeSources.Source(LayerFrame layer) => _frames.GetValueOrDefault(layer.Frame);

    string? IRecipeSources.Resource(ResourceKey key) => _options.Resources(key);

    string? IRecipeSources.Fonts(GeneratedContent content) => _options.Fonts(content);

    /// <summary>Los clips que piden estabilizar y cuyo análisis no está: se verían sin estabilizar, que es un respaldo.</summary>
    private static HashSet<string> Unstabilized(TimelinePlan plan)
    {
        var missing = new HashSet<string>(StringComparer.Ordinal);
        foreach (VideoLayerPlan layer in plan.Layers.Concat(plan.Layers.Select(l => l.Companion).OfType<VideoLayerPlan>()))
        {
            foreach (VideoClipPlan clip in layer.Clips)
            {
                if (clip.Effects?.Stabilization is null && plan.Expanded.FindClip(clip.ClipId) is { } found
                    && found.Clip.Effects.FirstOrDefault(e => e.Enabled && e.Type == BuiltInEffects.Stabilize) is { } stabilize
                    && stabilize.Options.GetValueOrDefault(BuiltInEffects.StabilizeOptions.Analysis) is { Length: > 0 })
                {
                    missing.Add(clip.ClipId);
                }
            }
        }

        return missing;
    }

    private bool Incomplete(IReadOnlyList<LayerFrame> layers)
    {
        foreach (LayerFrame layer in layers)
        {
            if (_unstabilized.Contains(layer.ClipId) || (layer.Nested is { } nested && Incomplete(nested))
                || (layer.Transition is { } transition && Incomplete([transition.Incoming])))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Da por cargado todo recurso pedido: así la capa lleva la referencia de lo pedido y su estado lo decide
    /// <see cref="RecipeBuilderOptions.Resources"/>, no lo que haya cargado ya la vista previa.
    /// </summary>
    private sealed class RequestedResources : IEffectResources
    {
        public static readonly RequestedResources Instance = new();

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public bool TryGetLut(ResourceKey key, out LutData? lut)
        {
            lut = null;
            return true;
        }
    }
}
