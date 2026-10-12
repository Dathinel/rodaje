using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;
using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.Media.Probe;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.Playback.Composition;

namespace Rodaje.Engine.Offline;

/// <summary>Qué hacer con lo que la vista previa tolera y la exportación no (D-028).</summary>
public enum MissingContentPolicy
{
    /// <summary>Falla con <see cref="ExportException"/> (<see cref="ExportErrorCode.ContentMissing"/>). Por defecto.</summary>
    Fail,

    /// <summary>El usuario eligió «continuar sin»: la LUT o la transición que falta se omite, como en la vista previa.</summary>
    ContinueWithout,
}

/// <summary>Opciones de <see cref="OfflineTimeline"/>.</summary>
public sealed record OfflineTimelineOptions
{
    /// <summary>Carpeta contra la que se resuelven las rutas relativas de los medios.</summary>
    public string? ProjectDirectory { get; init; }

    public IEffectContent? Content { get; init; }

    /// <summary>
    /// Quien rasteriza los títulos en el dispositivo de la exportación: se le piden los que vienen por delante. La
    /// composición estricta espera a los que falten; las fuentes que faltan las cuenta la exportación.
    /// </summary>
    public IGeneratedContentCache? Titles { get; init; }

    public bool AllowHardwareDecoding { get; init; } = true;

    /// <summary>Presupuesto de VRAM propio de la exportación; <c>null</c>, según la del dispositivo.</summary>
    public long? VramBudgetBytes { get; init; }

    public int MaxDecodersPerMedia { get; init; } = 4;

    /// <summary>Cuadros de la secuencia que se piden por delante; cada decodificador guarda como mucho estos (menos si no caben en la VRAM).</summary>
    public int LookaheadFrames { get; init; } = 8;

    /// <summary>Cuánto puede tardar un cuadro en estar exacto antes de dar <see cref="ExportErrorCode.DecodeFailed"/>.</summary>
    public TimeSpan FrameTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Cuánto se espera a que cargue cada LUT antes del primer cuadro; si no llega, cuenta como contenido que falta.</summary>
    public TimeSpan ContentTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public MissingContentPolicy MissingContent { get; init; } = MissingContentPolicy.Fail;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxDecodersPerMedia, nameof(MaxDecodersPerMedia));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(LookaheadFrames, nameof(LookaheadFrames));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(LookaheadFrames, 64, nameof(LookaheadFrames));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(FrameTimeout, TimeSpan.Zero, nameof(FrameTimeout));
        ArgumentOutOfRangeException.ThrowIfLessThan(ContentTimeout, TimeSpan.Zero, nameof(ContentTimeout));
        if (VramBudgetBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(VramBudgetBytes), VramBudgetBytes, "El presupuesto de VRAM debe ser positivo.");
        }
    }
}

/// <summary>
/// La secuencia cuadro a cuadro, exacta y sin reloj, para exportar: <see cref="Render"/> espera a que todas las capas
/// tengan su cuadro exacto (sin proxies, sin keyframe cercano, sin <c>SkipNonReference</c>).
/// </summary>
/// <remarks>
/// <para>Recibe una copia del proyecto (<see cref="Project.Clone"/>): la edición puede seguir mientras exporta.</para>
/// <para>
/// Se usa desde un solo hilo, el de la exportación. Los cuadros se piden en orden creciente; un salto hacia atrás es
/// válido pero cuesta un seek. La composición que devuelve es de quien la recibe y la desecha tras componerla; sus listas
/// de efectos valen hasta dos llamadas más (como en la vista previa).
/// </para>
/// <para>
/// Las capas se arman como en <c>TimelineScheduler</c> con la composición ya completa: el mismo plan, los mismos
/// efectos evaluados en <see cref="VideoClipPlan.EffectTimeAt"/>, la misma transición con el carril compañero. Cada medio
/// tiene sus decodificadores propios (hasta <see cref="OfflineTimelineOptions.MaxDecodersPerMedia"/>, cada uno en su hilo)
/// que van por delante <see cref="OfflineTimelineOptions.LookaheadFrames"/> cuadros, dentro del presupuesto de VRAM.
/// </para>
/// <para>
/// Reversa: un clip invertido tiene su lector propio, que decodifica por tramos desde el keyframe con
/// <see cref="ReverseFrameReader"/> y entrega de mayor a menor (un seek por tramo, no por cuadro).
/// </para>
/// </remarks>
public sealed class OfflineTimeline : IDisposable
{
    /// <summary>Un decodificador sin cuadros pedidos se cierra tras este tramo de la secuencia sin usarse.</summary>
    private static readonly Time IdleClose = Time.FromSeconds(2);

    private readonly Sequence _sequence;
    private readonly TimelinePlan _plan;
    private readonly GpuDevice? _device;
    private readonly OfflineTimelineOptions _options;
    private readonly Func<string, bool> _mediaExists;
    private readonly OfflineReaders _readers;
    private readonly Lane[] _lanes;
    private readonly Time _frameDuration;
    private List<MissingContentItem>? _missing;
    private Time? _last;
    private Time _plannedUntil = Time.FromTicks(-1);
    private bool _disposed;

    internal OfflineTimeline(Project project, string sequenceId, GpuDevice? device, OfflineTimelineOptions options, IClipBackend backend,
        Func<string, bool>? mediaExists = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(sequenceId);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(backend);
        options.Validate();
        _options = options;
        _device = device;
        _sequence = CompoundOps.Flatten(project, project.GetSequence(sequenceId));
        _plan = TimelinePlan.Build(project, sequenceId, options.ProjectDirectory ?? Environment.CurrentDirectory, options.Content);
        _mediaExists = mediaExists ?? (backend is MediaClipBackend ? File.Exists : static _ => true);
        Backend = backend;
        _frameDuration = Time.FromFrames(1, _plan.FrameRate);
        _lanes = [.. _plan.Layers.Select(l => new Lane(l) { Content = options.Content })];

        long dedicated = device is { IsDisposed: false } gpu ? gpu.DedicatedVideoMemory : 0;
        long budget = new TimelineSessionOptions { VramBudgetBytes = options.VramBudgetBytes }.ResolveVramBudget(dedicated);
        var decoderOptions = new VideoDecoderOptions
        {
            Device = device,
            AllowHardwareDecoding = options.AllowHardwareDecoding,
            CompactReferencePool = true,
        };
        _readers = new OfflineReaders(backend, decoderOptions, options.MaxDecodersPerMedia, options.LookaheadFrames, budget);
    }

    /// <summary>Abre la secuencia para exportar.</summary>
    /// <param name="device">El dispositivo propio de la exportación (no el de la vista previa); sin él se decodifica en la CPU.</param>
    /// <exception cref="KeyNotFoundException">La secuencia no existe en el proyecto.</exception>
    public static OfflineTimeline Open(Project project, string sequenceId, GpuDevice? device, OfflineTimelineOptions? options = null) =>
        new(project, sequenceId, device, options ?? new OfflineTimelineOptions(), MediaClipBackend.Instance);

    public string SequenceId => _plan.SequenceId;

    public int Width => _plan.Width;

    public int Height => _plan.Height;

    public Rational FrameRate => _plan.FrameRate;

    public Time Duration => _plan.Duration;

    public long FrameCount => _plan.FrameCount;

    /// <summary>
    /// LUT y transiciones de paquete que la secuencia usa y no están; con <see cref="MissingContentPolicy.Fail"/> impiden
    /// exportar. Antes de <see cref="Prepare"/> cuenta solo las transiciones.
    /// </summary>
    public int MissingContent => _missing?.Count ?? _plan.MissingContent;

    /// <summary>Lo que falta según el último <see cref="Prepare"/>; vacío antes.</summary>
    public IReadOnlyList<MissingContentItem> MissingContentItems => _missing ?? [];

    /// <summary>
    /// Los títulos que se ven en <c>[start, end)</c>, sin repetir: la exportación resuelve sus fuentes con ellos antes del
    /// primer cuadro y aplica <see cref="MissingContentPolicy"/> a las que faltan.
    /// </summary>
    public IReadOnlyList<GeneratedContent> GeneratedContents(Time start, Time end) => _plan.GeneratedContents(start, end);

    public GpuDevice? Device => _device;

    public OfflineTimelineOptions Options => _options;

    /// <summary>Decodificadores abiertos ahora.</summary>
    public int DecodersOpen => _readers.OpenReaders;

    /// <summary>Pico de la VRAM comprometida por los pools de los decodificadores.</summary>
    public long PeakDecoderVramBytes => _readers.PeakReservedBytes;

    /// <summary>Decodificadores que se abrieron con la lectura por adelantado mínima porque el presupuesto no daba para más.</summary>
    public int VramOverruns => _readers.VramOverruns;

    internal IClipBackend Backend { get; }

    internal OfflineReaders Readers => _readers;

    /// <summary>
    /// Comprueba antes del primer cuadro lo que necesita <c>[start, end)</c>: que los medios existan y que estén las LUT
    /// (esperando a que carguen) y las transiciones de paquete. <see cref="Render"/> lo hace con toda la secuencia si
    /// nadie lo llamó antes.
    /// </summary>
    /// <returns>Lo que falta; con <see cref="MissingContentPolicy.ContinueWithout"/> se exporta sin ello.</returns>
    /// <exception cref="ExportException">
    /// Falta un medio (<see cref="ExportErrorCode.MediaMissing"/>) o, con <see cref="MissingContentPolicy.Fail"/>, contenido
    /// de paquetes (<see cref="ExportErrorCode.ContentMissing"/>, con la lista en el mensaje).
    /// </exception>
    public IReadOnlyList<MissingContentItem> Prepare(Time start, Time end, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (start < Time.Zero || end <= start)
        {
            throw new ArgumentOutOfRangeException(nameof(end), "El rango está vacío o empieza antes de 0.");
        }

        List<string> absent = [.. OfflineContent.Sources(_plan, start, end).Select(s => s.Path).Where(p => !_mediaExists(p))];
        if (absent.Count > 0)
        {
            throw new ExportException(ExportErrorCode.MediaMissing,
                $"Falta{(absent.Count > 1 ? "n" : "")} {absent.Count} medio{(absent.Count > 1 ? "s" : "")}: {string.Join(", ", absent.Select(Path.GetFileName))}.")
            {
                Culprit = absent[0],
            };
        }

        var missing = new List<MissingContentItem>();
        foreach ((ResourceKey key, string clipId) in OfflineContent.Luts(_sequence, _plan, start, end))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_options.Content is not { } content || !OfflineContent.WaitForLut(content, key, _options.ContentTimeout, cancellationToken))
            {
                missing.Add(new MissingContentItem(key.Reference, clipId, IsTransition: false));
            }
        }

        missing.AddRange(OfflineContent.FallenBackTransitions(_sequence, _plan, start, end));
        _missing = missing;
        if (missing.Count > 0 && _options.MissingContent == MissingContentPolicy.Fail)
        {
            throw new ExportException(ExportErrorCode.ContentMissing,
                $"Falta contenido de paquetes: {string.Join(", ", missing.Select(m => (m.IsTransition ? "transición " : "LUT ") + m.Reference))}.")
            {
                Culprit = missing[0].Reference,
            };
        }

        return missing;
    }

    /// <summary>Las capas exactas del cuadro <paramref name="frame"/> de la secuencia, de abajo arriba.</summary>
    /// <exception cref="ArgumentOutOfRangeException">El cuadro está fuera de la secuencia.</exception>
    /// <exception cref="ExportException">Un medio falta, un cuadro no se decodifica o falta contenido (según la política).</exception>
    /// <exception cref="OperationCanceledException">Se canceló mientras esperaba a un decodificador.</exception>
    public CompositionFrame Render(long frame, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(frame, FrameCount);
        cancellationToken.ThrowIfCancellationRequested();
        return Produce(Time.FromFrames(frame, FrameRate), frame, frame, cancellationToken);
    }

    /// <summary>
    /// Las capas exactas en el instante <paramref name="time"/> de la secuencia, para la conversión de tasa: la salida a
    /// otra tasa pide el instante de cada cuadro suyo (repetición o descarte). Para mezclar dos cuadros conviene
    /// <see cref="Render"/> con los dos cuadros de la secuencia.
    /// </summary>
    /// <remarks>La lectura por adelantado supone que los instantes siguen con el mismo paso que los dos últimos.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">El instante está fuera de <c>[0, Duration)</c>.</exception>
    public CompositionFrame RenderAt(Time time, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (time < Time.Zero || time >= Duration)
        {
            throw new ArgumentOutOfRangeException(nameof(time), time, "El instante está fuera de la secuencia.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Produce(time, null, time.ToFrames(FrameRate), cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _readers.Dispose();
    }

    private CompositionFrame Produce(Time time, long? gridFrame, long frame, CancellationToken cancellationToken)
    {
        if (_missing is null)
        {
            Prepare(Time.Zero, Duration, cancellationToken);
        }

        // Sin cuadro de la rejilla, el paso de los instantes se aprende de los dos últimos; el primero no lee por delante.
        Time? step = gridFrame is not null ? _frameDuration : _last is { } previous && time > previous ? time - previous : null;
        if (_last is { } last && time < last)
        {
            _readers.Reset();
            _plannedUntil = Time.FromTicks(-1);
        }

        _last = time;
        _readers.ReleaseBefore(time, IdleClose);
        foreach (Lane lane in _lanes)
        {
            lane.Pick(time);
            lane.MainSlot = lane.Main is { } main ? _readers.Request(main.Source, main.FrameAt(time), time, mustHave: true, main) : null;
            lane.IncomingSlot = lane.Incoming is { } incoming ? _readers.Request(incoming.Source, incoming.FrameAt(time), time, mustHave: true, incoming) : null;
        }

        if (step is { } known)
        {
            Prefetch(time, known, gridFrame);
        }

        PrefetchTitles(time, step ?? _frameDuration);
        return Build(time, frame, cancellationToken);
    }

    /// <summary>Pide por delante los cuadros de los próximos instantes mientras quepan en la capacidad de cada decodificador.</summary>
    private void Prefetch(Time time, Time step, long? gridFrame)
    {
        for (int k = 1; k <= _options.LookaheadFrames; k++)
        {
            Time ahead = gridFrame is { } g ? Time.FromFrames(g + k, FrameRate) : time + Time.FromTicks(step.Ticks * k);
            if (ahead >= Duration)
            {
                return;
            }

            if (ahead <= _plannedUntil)
            {
                continue;
            }

            foreach (Lane lane in _lanes)
            {
                lane.PickAhead(ahead);
                if ((lane.AheadMain is { } m && !_readers.HasRoom(m.Source, m.FrameAt(ahead), m))
                    || (lane.AheadIncoming is { } i && !_readers.HasRoom(i.Source, i.FrameAt(ahead), i)))
                {
                    return;
                }
            }

            foreach (Lane lane in _lanes)
            {
                if ((lane.AheadMain is { } m && _readers.Request(m.Source, m.FrameAt(ahead), ahead, mustHave: false, m) is null)
                    || (lane.AheadIncoming is { } i && _readers.Request(i.Source, i.FrameAt(ahead), ahead, mustHave: false, i) is null))
                {
                    return;
                }
            }

            _plannedUntil = ahead;
        }
    }

    /// <summary>Pide a quien rasteriza los títulos que se verán en los próximos cuadros de la lectura por adelantado.</summary>
    private void PrefetchTitles(Time time, Time step)
    {
        if (_options.Titles is not { } titles)
        {
            return;
        }

        Time until = time + Time.FromTicks(step.Ticks * _options.LookaheadFrames);
        foreach (Lane lane in _lanes)
        {
            IReadOnlyList<GeneratedClipPlan> generated = lane.Layer.Generated;
            for (int i = lane.Layer.FirstGeneratedEndingAfter(time); i < generated.Count && generated[i].Start <= until; i++)
            {
                foreach (GeneratedContent content in generated[i].Contents)
                {
                    titles.Prepare(content);
                }
            }
        }
    }

    private CompositionFrame Build(Time time, long frame, CancellationToken cancellationToken)
    {
        var layers = new List<LayerFrame>(_lanes.Length);
        try
        {
            foreach (Lane lane in _lanes)
            {
                if (lane.Layer.GeneratedAt(time) is { } title)
                {
                    layers.Add(title.Layer(lane.Layer.TrackIndex, time, lane.TitleEffects, lane.Content));
                    continue;
                }

                if (lane.Main is not { } main)
                {
                    continue;
                }

                DecodedFrame mainFrame = _readers.Wait(lane.MainSlot!, _options.FrameTimeout, frame, cancellationToken);
                LayerFrame? incoming = null;
                try
                {
                    if (lane.Incoming is { } entering && lane.Transition is { } transition)
                    {
                        float progress = transition.Progress(time);
                        DecodedFrame inFrame = _readers.Wait(lane.IncomingSlot!, _options.FrameTimeout, frame, cancellationToken);
                        incoming = Layer(lane, entering, inFrame, lane.IncomingEffects, lane.IncomingSlot!, time, transition, entering: true, progress, null);
                        layers.Add(Layer(lane, main, mainFrame, lane.MainEffects, lane.MainSlot!, time, transition, entering: false, progress,
                            new LayerTransition(transition.Code, progress, transition.Values, incoming)));
                    }
                    else
                    {
                        layers.Add(Layer(lane, main, mainFrame, lane.MainEffects, lane.MainSlot!, time, null, entering: false, 0, null));
                    }
                }
                catch
                {
                    mainFrame.Dispose();
                    incoming?.Dispose();
                    throw;
                }
            }

            return new CompositionFrame(time, Width, Height, layers);
        }
        catch
        {
            layers.ForEach(l => l.Dispose());
            throw;
        }
    }

    /// <summary>Como <c>TimelineScheduler.Build</c>: transformación, transición, efectos en tiempo del origen y mezcla.</summary>
    private static LayerFrame Layer(Lane lane, VideoClipPlan clip, DecodedFrame frame, EffectListRing ring, OfflineReaders.Slot slot, Time time,
        TransitionPlan? transition, bool entering, float progress, LayerTransition? withTransition)
    {
        Time source = clip.EffectTimeAt(time);
        EffectPlan? plan = clip.Effects;
        LayerTransform transform = EffectPlan.Stabilized(plan, EffectPlan.Transform(plan, source), frame.Index);
        if (transition is not null)
        {
            transform = transition.Apply(transform, entering, progress);
        }

        IReadOnlyList<LayerEffect> effects = [];
        if (plan is { EffectCount: > 0 })
        {
            EffectList list = ring.Next();
            plan.Evaluate(source, list.Reset(plan.EffectCount), lane.Content);
            effects = list;
        }

        VideoStreamInfo? stream = slot.Source.Stream;
        return new LayerFrame(lane.Layer.TrackIndex, clip.ClipId, frame, transform, stream?.SampleAspectRatio ?? new Rational(1, 1),
            TimelineScheduler.NormalizeRotation(stream?.Rotation ?? 0))
        {
            Effects = effects,
            Blend = plan?.Blend ?? BlendMode.Normal,
            Transition = withTransition,
        };
    }

    /// <summary>Una pista visible: su carril principal y el compañero, y lo que muestra cada uno en el instante pedido.</summary>
    private sealed class Lane(VideoLayerPlan layer)
    {
        public VideoLayerPlan Layer { get; } = layer;

        public IEffectResources? Content { get; init; }

        public EffectListRing MainEffects { get; } = new();

        public EffectListRing IncomingEffects { get; } = new();

        public EffectListRing TitleEffects { get; } = new();

        public VideoClipPlan? Main { get; private set; }

        public VideoClipPlan? Incoming { get; private set; }

        public TransitionPlan? Transition { get; private set; }

        public OfflineReaders.Slot? MainSlot { get; set; }

        public OfflineReaders.Slot? IncomingSlot { get; set; }

        public VideoClipPlan? AheadMain { get; private set; }

        public VideoClipPlan? AheadIncoming { get; private set; }

        public void Pick(Time time) => (Main, Incoming, Transition) = Choose(time);

        public void PickAhead(Time time) => (AheadMain, AheadIncoming, _) = Choose(time);

        /// <summary>
        /// Como <c>TimelineScheduler.Compose</c> con todas las capas listas: el clip del carril principal y, si está en su
        /// transición, el que entra por el compañero; sin principal, el que entra va solo y sin transición.
        /// </summary>
        private (VideoClipPlan?, VideoClipPlan?, TransitionPlan?) Choose(Time time)
        {
            VideoClipPlan? main = Layer.ClipAt(time);
            VideoClipPlan? incoming = Layer.Companion?.ClipAt(time);
            if (main is null)
            {
                return (incoming, null, null);
            }

            return main.Outgoing is { } transition && transition.Covers(time) && incoming is not null && incoming.ClipId == transition.ToClipId
                ? (main, incoming, transition)
                : (main, null, null);
        }
    }
}
