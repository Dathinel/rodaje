using Rodaje.Core.Timing;
using Rodaje.Media.Proxies;
using Rodaje.Media.Video;
using Rodaje.Playback;

namespace Rodaje.Engine.Timeline;

// Estado interno: medios, decodificadores, capas y los registros que usan las demás partes.
internal sealed partial class TimelineScheduler
{
    private sealed record Job(long Index, CancellationTokenSource Epoch);

    /// <summary>Lo que un seek deja para después de soltar el candado: cancelar lo viejo y encolar lo nuevo.</summary>
    private readonly record struct Transition(List<CancellationTokenSource>? Stale, List<(IDecodeExecutor, Action)>? Posts)
    {
        public void Finish()
        {
            Stale?.ForEach(c => c.Cancel());
            if (Posts is not null)
            {
                PostAll(Posts);
            }
        }
    }

    /// <summary>Cuadros <c>[Start, End)</c> del origen que necesita un clip; <see cref="Due"/> es el que toca mostrar ya.</summary>
    /// <remarks>
    /// <see cref="Warm"/>: de una pista oculta; va detrás de todo lo visible y es lo primero que la caché descarta.
    /// <see cref="Priority"/>: más alta cuanto más arriba está la pista; una tapada por una capa opaca queda al final.
    /// Solo hacen falta los cuadros del tramo que se mostrarán; con <see cref="Approximate"/>, los keyframes que se mostrarán.
    /// </remarks>
    private readonly record struct Window(long Start, long End, bool Due, VideoClipPlan Clip, bool Warm = false, int Priority = 0,
        bool Approximate = false);

    /// <summary>Archivo que decodifica la vista previa de un medio, con el tamaño que se espera de sus cuadros.</summary>
    private sealed record Variant(string Path, bool IsProxy, int Width, int Height);

    /// <summary>Cómo dibujar los cuadros de un decodificador: forma del píxel, giro y, para un proxy, el tamaño del original.</summary>
    private readonly record struct Look(Rational SampleAspectRatio, int Rotation, int DisplayWidth, int DisplayHeight, bool IsProxy)
    {
        public static Look Square => new(new Rational(1, 1), 0, 0, 0, IsProxy: false);
    }

    private sealed record OpenRequest(MediaState Media, Cluster Cluster);

    /// <summary>Ventanas de un medio lo bastante juntas para que un solo decodificador las sirva avanzando.</summary>
    private sealed class Cluster
    {
        public Cluster(Window first, long urgency)
        {
            Start = first.Start;
            End = first.End;
            Urgency = urgency;
            Visible = !first.Warm && first.Due;
            Priority = first.Priority;
            Windows.Add(first);
        }

        public long Start { get; }

        public long End { get; private set; }

        /// <summary>Retraso de lo más urgente del tramo: 0 si hay un cuadro debido.</summary>
        public long Urgency { get; private set; }

        /// <summary>Tiene el cuadro debido de una capa visible.</summary>
        public bool Visible { get; private set; }

        /// <summary>La de la pista más alta que pide algo del tramo.</summary>
        public int Priority { get; private set; }

        public List<Window> Windows { get; } = [];

        public DecoderState? Decoder { get; set; }

        public void Add(Window window, long urgency)
        {
            Windows.Add(window);
            End = Math.Max(End, window.End);
            Urgency = Math.Min(Urgency, urgency);
            Visible |= window.Due && !window.Warm;
            Priority = Math.Max(Priority, window.Priority);
        }
    }

    private sealed class MediaState(int id, SourceKey key, Rational frameRate, int displayWidth, int displayHeight)
    {
        public int Id { get; } = id;

        public SourceKey Key { get; } = key;

        public Rational FrameRate { get; } = frameRate;

        public int DisplayWidth { get; } = displayWidth;

        public int DisplayHeight { get; } = displayHeight;

        public List<DecoderState> Decoders { get; } = [];

        public string? Failure { get; set; }

        public List<Window> Windows { get; } = [];

        /// <summary>Capas visibles que muestran el medio ahora.</summary>
        public int Layers { get; set; }

        /// <summary>La de la pista más alta que lo pide; ordena las aperturas y los proxies.</summary>
        public int Priority { get; set; } = int.MinValue;

        public HashSet<long> Requested { get; } = [];

        public HashSet<long> Unavailable { get; } = [];

        public long? IdleSince { get; set; }

        /// <summary>Archivo elegido (proxy u original); nulo hasta resolverlo por primera vez.</summary>
        public Variant? Variant { get; set; }

        public bool ResolvePending { get; set; }

        public bool Resolving { get; set; }

        public bool Assessed { get; set; }

        public string? LearnedPath { get; set; }

        public int LearnedReferences { get; set; }

        public long LearnedSurfaceBytes { get; set; }

        /// <summary>Keyframes del archivo <see cref="KeyframesPath"/>, si su decodificador los conoce.</summary>
        public IKeyframeIndex? Keyframes { get; set; }

        public string? KeyframesPath { get; set; }

        /// <summary>Los keyframes del archivo que se decodifica ahora; nulo si no se conocen.</summary>
        public IKeyframeIndex? CurrentKeyframes => Keyframes is not null && KeyframesPath == Variant?.Path ? Keyframes : null;

        public bool InWindows(long index) => Windows.Exists(w => index >= w.Start && index < w.End);
    }

    private sealed class DecoderState(int id, MediaState media)
    {
        public int Id { get; } = id;

        public MediaState Media { get; } = media;

        public Action Pump { get; set; } = () => { };

        public IClipVideo? Decoder { get; set; }

        public IDecodeExecutor? Executor { get; set; }

        public Variant? Variant { get; set; }

        public Look Look { get; set; } = Look.Square;

        public bool Opening { get; set; }

        public bool Closed { get; set; }

        /// <summary>Su archivo ya no es el elegido para el medio: sirve hasta que el nuevo entregue y se cierra.</summary>
        public bool Retiring { get; set; }

        public List<Window> Assigned { get; } = [];

        /// <summary>Último cuadro pedido: de dónde sigue sin buscar keyframe.</summary>
        public long Position { get; set; }

        public long Delivered { get; set; }

        /// <summary>Cuadros que puede guardar en la caché.</summary>
        public int CacheLimit { get; set; } = MinCacheFrames;

        public long OpenedAt { get; init; }

        /// <summary>VRAM de su pool: estimada al abrir, exacta desde que se crea.</summary>
        public long Reserved { get; set; }

        public bool PoolKnown { get; set; }

        /// <summary>Su pool no cabía en el presupuesto y se abrió igual porque una capa visible no tenía otro.</summary>
        public bool Overrun { get; set; }

        // Sin temporizadores un CTS no retiene recursos; el reemplazado no se libera porque la
        // decodificación en curso todavía puede consultar su token.
        public CancellationTokenSource Epoch { get; set; } = new();

        public Job? InFlight { get; set; }

        public bool PumpPosted { get; set; }

        public long? IdleSince { get; set; }
    }

    private sealed class LayerState(string trackId)
    {
        public string TrackId { get; } = trackId;

        /// <summary>Carril compañero de una pista: los clips que entran en sus transiciones.</summary>
        public bool IsCompanion { get; } = trackId.EndsWith(VideoLayerPlan.CompanionSuffix, StringComparison.Ordinal);

        /// <summary>Listas de efectos evaluados que se rotan entre refrescos.</summary>
        public EffectListRing Effects { get; } = new();

        public DecodedFrame? Current { get; set; }

        public VideoClipPlan? CurrentClip { get; set; }

        public MediaState? CurrentMedia { get; set; }

        public Look CurrentLook { get; set; } = Look.Square;

        /// <summary>Archivo del que salió el cuadro en pantalla.</summary>
        public Variant? CurrentVariant { get; set; }

        /// <summary>Clip que cubría el instante en el último refresco.</summary>
        public VideoClipPlan? ActiveClip { get; set; }

        public long LowerBound { get; set; }

        public bool CountDrops { get; set; }

        /// <summary>Tras un seek o al aparecer la capa: el próximo cuadro debe ser el exacto y no cuenta como corte.</summary>
        public bool Resync { get; set; }

        /// <summary>El corte llegó sin su primer cuadro listo; los refrescos hasta tenerlo cuentan como tarde en el corte.</summary>
        public bool AwaitingCut { get; set; }

        /// <summary>Desde cuándo está oculta la pista; nulo mientras se ve.</summary>
        public long? HiddenSince { get; set; }

        /// <summary>Motivo por el que el medio del clip activo no abre; la capa queda vacía.</summary>
        public string? Failure { get; set; }

        public long Shown { get; set; }

        public long Dropped { get; set; }

        public long Late { get; set; }

        public long Cuts { get; set; }

        public long PreRollHits { get; set; }

        public long PreRollMisses { get; set; }

        public long LateAtCuts { get; set; }

        /// <summary>Refrescos que mostraron un keyframe cercano en lugar del cuadro debido (reproducción de 16× a 100×).</summary>
        public long Approximated { get; set; }

        public bool IsShowing(MediaState media, long index) => CurrentMedia == media && Current?.Index == index;

        public void Release(List<DecodedFrame> released)
        {
            if (Current is not null)
            {
                released.Add(Current);
            }

            Current = null;
            CurrentClip = null;
            CurrentMedia = null;
            CurrentLook = Look.Square;
            CurrentVariant = null;
        }

        public void ResetStats() =>
            (Shown, Dropped, Late, Cuts, PreRollHits, PreRollMisses, LateAtCuts, Approximated) = (0, 0, 0, 0, 0, 0, 0, 0);

        public LayerStats Snapshot(int trackIndex) =>
            new(trackIndex, TrackId, Shown, Dropped, Late, Cuts, PreRollHits, PreRollMisses, LateAtCuts,
                Current is not null && CurrentLook.IsProxy, Failure, Approximated);
    }
}
