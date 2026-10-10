using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Timeline;

/// <summary>Qué hace un arrastre sobre un clip.</summary>
public enum DragOperation
{
    Move,
    TrimStart,
    TrimEnd,

    /// <summary>Alt+arrastrar: otra parte del origen, sin mover el clip.</summary>
    Slip,
}

/// <summary>Las opciones de la barra que cambian lo que hace un arrastre.</summary>
public sealed record DragOptions
{
    public bool Snapping { get; init; }

    /// <summary>La pista principal magnética, o <c>null</c> si está apagada.</summary>
    public string? MagneticTrackId { get; init; }

    public bool AutoRipple { get; init; }

    public RippleScope RippleScope { get; init; } = RippleScope.Track;

    /// <summary>Editar solo el clip agarrado, sin sus enlazados.</summary>
    public bool IgnoreGroups { get; init; }

    public double PixelsPerSecond { get; init; } = 60;
}

/// <summary>
/// Lo que corresponde a la posición actual del puntero: el comando (una fábrica, porque un comando ya aplicado no se
/// reutiliza), si se ejecuta ya (fundiéndose con los anteriores) o al soltar, y lo que se dibuja mientras tanto.
/// </summary>
/// <param name="Build">El comando, o <c>null</c> si no hay nada que cambiar.</param>
/// <param name="IsLive">Se ejecuta en cada paso; si no, solo al soltar.</param>
/// <param name="GhostTrackId">Pista donde se dibuja el clip arrastrado.</param>
/// <param name="SnapGuide">Instante al que se pegó, para dibujar la guía.</param>
/// <param name="InsertAt">Corte de la pista magnética donde caerá el clip.</param>
public sealed record DragPlan(Func<ICommand>? Build, bool IsLive, Time GhostStart, Time GhostEnd, string GhostTrackId, Time? SnapGuide, Time? InsertAt);

/// <summary>
/// Traduce un arrastre sobre un clip a comandos del núcleo. Recuerda el estado del clip al empezar y, en cada paso,
/// calcula el comando relativo al estado actual (que ya incluye los pasos anteriores del mismo gesto).
/// </summary>
/// <remarks>
/// En pistas libres se mueve sin pisar nada (<see cref="PlacementMode.Strict"/>) y se recorta sin correr lo demás, salvo
/// con ripple automático. En la pista magnética mover reordena y recortar corre lo que sigue; entrar o salir de ella se
/// aplica al soltar, porque cada paso intermedio alteraría clips que solo se cruzaron.
/// </remarks>
public sealed class ClipDrag
{
    private readonly string _sequenceId;
    private readonly string _clipId;
    private readonly DragOperation _operation;
    private readonly Time _grab;
    private readonly DragOptions _options;
    private readonly IIdGenerator? _ids;
    private readonly string _originTrackId;
    private readonly TrackKind _kind;
    private readonly Time _originStart;
    private readonly Time _originIn;
    private readonly Time _originOut;
    private readonly Time _minStart;
    private readonly Rational _frameRate;
    private readonly SnapPoints _snaps;
    private readonly HashSet<string> _group;

    public ClipDrag(Project project, string sequenceId, string clipId, DragOperation operation, Time grab, DragOptions options, Time? playhead, IIdGenerator? ids = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(options);
        Sequence sequence = project.GetSequence(sequenceId);
        (Track track, Clip clip) = sequence.GetClip(clipId);
        _sequenceId = sequenceId;
        _clipId = clipId;
        _operation = operation;
        _grab = grab;
        _options = options;
        _ids = ids;
        _originTrackId = track.Id;
        _kind = track.Kind;
        _originStart = clip.Start;
        _originIn = clip.SourceIn;
        _originOut = clip.SourceOut;
        _frameRate = sequence.FrameRate;
        List<(Track Track, Clip Clip)> members = options.IgnoreGroups ? [(track, clip)] : sequence.GetLinkGroup(clipId);
        _group = [.. members.Select(m => m.Clip.Id)];
        _minStart = clip.Start - members.Min(m => m.Clip.Start);
        _snaps = options.Snapping ? SnapPoints.Collect(sequence, _group, playhead) : SnapPoints.Empty;
    }

    public string ClipId => _clipId;

    public DragOperation Operation => _operation;

    /// <summary>Los clips que se editan con el agarrado (él y sus enlazados, salvo que se ignoren los grupos).</summary>
    public IReadOnlySet<string> Group => _group;

    private Time Frame => Time.FromFrames(1, _frameRate);

    private Time SnapThreshold => Time.FromSeconds(SnapPoints.ThresholdPixels / _options.PixelsPerSecond);

    /// <summary>El plan para el puntero en <paramref name="position"/>, sobre la fila <paramref name="row"/> (si hay).</summary>
    public DragPlan Update(Project project, Time position, TrackRow? row)
    {
        ArgumentNullException.ThrowIfNull(project);
        Sequence sequence = project.GetSequence(_sequenceId);
        if (sequence.FindClip(_clipId) is not { } found)
        {
            return new DragPlan(null, true, _originStart, _originStart, _originTrackId, null, null);
        }

        Time offset = position - _grab;
        return _operation switch
        {
            DragOperation.Move => PlanMove(sequence, found.Track, found.Clip, offset, row),
            DragOperation.Slip => PlanSlip(project, sequence, found.Track, found.Clip, offset),
            _ => PlanTrim(project, sequence, found.Track, found.Clip, offset),
        };
    }

    /// <summary>
    /// El lugar que tomaría en la pista magnética un clip de <paramref name="duration"/> soltado en
    /// <paramref name="ghostStart"/>: va antes del primer clip cuyo centro queda a la derecha de su centro.
    /// </summary>
    /// <param name="others">Los clips de la pista, sin el que se mueve, en orden.</param>
    /// <param name="origin">Dónde empieza la pista empaquetada.</param>
    public static (int Index, Time Boundary) MagneticSlot(IReadOnlyList<Clip> others, Time origin, Time ghostStart, Time duration)
    {
        ArgumentNullException.ThrowIfNull(others);
        long center = ghostStart.Ticks + (duration.Ticks / 2);
        Time cursor = origin;
        int index = 0;
        foreach (Clip clip in others)
        {
            if (cursor.Ticks + (clip.Duration.Ticks / 2) >= center)
            {
                break;
            }

            index++;
            cursor += clip.Duration;
        }

        return (index, cursor);
    }

    /// <summary>
    /// Una edición sobre un solo clip aunque esté enlazado (ignorar grupos): el enlace se quita mientras corre y se
    /// devuelve después, así sus compañeros no se tocan.
    /// </summary>
    internal static SequenceEditCommand Solo(string description, string sequenceId, string clipId, Action<EditContext> edit, string mergeKey, IIdGenerator? ids) =>
        new(description, sequenceId, ctx =>
        {
            Clip clip = ctx.Sequence.GetClip(clipId).Clip;
            string? link = clip.LinkId;
            clip.LinkId = null;
            try
            {
                edit(ctx);
            }
            finally
            {
                clip.LinkId = link;
            }

            ctx.EnsureValid();
        }, ids, mergeKey);

    private DragPlan PlanMove(Sequence sequence, Track track, Clip clip, Time offset, TrackRow? row)
    {
        Time duration = clip.Duration;
        Time desired = Time.Max(_originStart + offset, _minStart).SnapToFrame(_frameRate);
        Time? guide = null;
        if (_snaps.Count > 0)
        {
            (Time correction, guide) = _snaps.SnapSpan(desired, desired + duration, SnapThreshold);
            desired = Time.Max(desired + correction, _minStart);
        }

        Track target = row is { } r && r.Kind == _kind ? r.Track : track;
        string? magnetic = _options.MagneticTrackId;
        bool fromMain = magnetic is not null && _originTrackId == magnetic;
        bool toMain = magnetic is not null && target.Id == magnetic;

        if (fromMain && toMain)
        {
            List<Clip> others = target.Clips.FindAll(c => c.Id != _clipId);
            Time origin = target.Clips.Count > 0 ? target.Clips[0].Start : Time.Zero;
            (int index, Time boundary) = MagneticSlot(others, origin, desired, duration);
            int current = target.Clips.IndexOf(clip);
            Func<ICommand>? build = index == current || current < 0 ? null : () => Edits.ReorderOnMainTrack(_sequenceId, _clipId, index, _ids);
            return new DragPlan(build, true, desired, desired + duration, target.Id, null, boundary);
        }

        if (toMain)
        {
            List<Clip> others = target.Clips.FindAll(c => c.Id != _clipId);
            Time origin = others.Count > 0 ? others[0].Start : Time.Zero;
            (_, Time boundary) = MagneticSlot(others, origin, desired, duration);
            string targetId = target.Id;
            bool solo = _options.IgnoreGroups;
            RippleScope scope = _options.RippleScope;
            return new DragPlan(
                () => MagneticEdits.EnterMainTrack(_sequenceId, _clipId, targetId, boundary, scope, solo, _ids),
                false,
                desired,
                desired + duration,
                target.Id,
                null,
                boundary);
        }

        if (fromMain && track.Id == magnetic)
        {
            string targetId = target.Id;
            Time moveTo = desired;
            return new DragPlan(() => LeaveMagneticTrack(targetId, moveTo), false, desired, desired + duration, target.Id, guide, null);
        }

        if (desired == clip.Start && target == track)
        {
            return new DragPlan(null, true, desired, desired + duration, target.Id, guide, null);
        }

        string? destination = target == track ? null : target.Id;
        Time start = desired;
        Func<ICommand> move = _options.IgnoreGroups && clip.LinkId is not null
            ? () => Solo("Mover clip", _sequenceId, _clipId, ctx => TimelineEdits.Move(ctx, _clipId, start, destination, PlacementMode.Strict), $"move-solo:{_clipId}", _ids)
            : () => Edits.Move(_sequenceId, _clipId, start, destination, PlacementMode.Strict, _ids);
        return new DragPlan(move, true, desired, desired + duration, target.Id, guide, null);
    }

    /// <summary>Saca el clip de la pista magnética a otra y cierra el hueco que deja, con sus enlazados.</summary>
    private SequenceEditCommand LeaveMagneticTrack(string targetTrackId, Time start)
    {
        string magnetic = _options.MagneticTrackId!;
        RippleScope scope = _options.RippleScope;
        bool solo = _options.IgnoreGroups;
        return new SequenceEditCommand("Mover clip", _sequenceId, ctx =>
        {
            (Track mainTrack, Clip clip) = ctx.Sequence.GetClip(_clipId);
            Time oldStart = clip.Start;
            Time oldEnd = clip.End;
            List<(Track Track, Clip Clip)> group = solo ? [(mainTrack, clip)] : ctx.Sequence.GetLinkGroup(_clipId);
            Track main = ctx.Sequence.GetTrack(magnetic);
            List<Track> closing = [main, .. group.Skip(1).Select(g => g.Track)];
            foreach (Clip later in main.Clips.Where(c => c.Start >= oldEnd && c.LinkId is not null))
            {
                closing.AddRange(ctx.Sequence.GetLinkGroup(later.Id).Skip(1).Select(g => g.Track));
            }
            string? link = clip.LinkId;
            if (solo)
            {
                clip.LinkId = null;
            }

            try
            {
                TimelineEdits.Move(ctx, _clipId, start, targetTrackId, PlacementMode.Strict);
            }
            finally
            {
                clip.LinkId = link;
            }

            var moved = new HashSet<Clip>(group.Select(g => g.Clip));
            ctx.RippleShift(closing, oldEnd, oldStart - oldEnd, scope, moved);
            ctx.EnsureValid();
        }, _ids);
    }

    private DragPlan PlanTrim(Project project, Sequence sequence, Track track, Clip clip, Time offset)
    {
        bool ripple = _options.AutoRipple || (_options.MagneticTrackId is { } m && track.Id == m);
        TrimEdge edge = _operation == DragOperation.TrimStart ? TrimEdge.In : TrimEdge.Out;
        List<(Track Track, Clip Clip)> members = Members(sequence, track, clip);
        Time frame = Frame;
        Time? guide = null;
        Time delta;
        Time low = Time.FromTicks(long.MinValue / 4);
        Time high = Time.FromTicks(long.MaxValue / 4);
        foreach ((Track t, Clip c) in members)
        {
            Time mediaDuration = project.FindMedia(c.MediaId)?.Duration ?? c.SourceOut;
            if (edge == TrimEdge.In)
            {
                low = Time.Max(low, -c.SourceIn);
                high = Time.Min(high, c.Duration - frame);
                if (!ripple)
                {
                    low = Time.Max(low, -c.Start);
                    if (Previous(t, c) is { } previous)
                    {
                        low = Time.Max(low, previous.End - c.Start);
                    }
                }
            }
            else
            {
                low = Time.Max(low, frame - c.Duration);
                high = Time.Min(high, mediaDuration - c.SourceOut);
                if (!ripple && Next(t, c) is { } next)
                {
                    high = Time.Min(high, next.Start - c.End);
                }
            }
        }

        if (edge == TrimEdge.In)
        {
            Time desiredStart = (_originStart + offset).SnapToFrame(_frameRate);
            if (_snaps.Count > 0 && _snaps.Nearest(desiredStart, SnapThreshold) is { } snapped)
            {
                desiredStart = snapped;
                guide = snapped;
            }

            delta = ripple ? _originIn + (desiredStart - _originStart) - clip.SourceIn : desiredStart - clip.Start;
        }
        else
        {
            Time desiredEnd = (_originStart + (_originOut - _originIn) + offset).SnapToFrame(_frameRate);
            if (_snaps.Count > 0 && _snaps.Nearest(desiredEnd, SnapThreshold) is { } snapped)
            {
                desiredEnd = snapped;
                guide = snapped;
            }

            delta = desiredEnd - clip.End;
        }

        delta = low > high ? Time.Zero : Time.Min(Time.Max(delta, low), high);
        Time ghostStart = edge == TrimEdge.In && !ripple ? clip.Start + delta : clip.Start;
        Time ghostEnd = edge == TrimEdge.In ? clip.End - (ripple ? delta : Time.Zero) : clip.End + delta;
        if (delta == Time.Zero)
        {
            return new DragPlan(null, true, clip.Start, clip.End, track.Id, guide, null);
        }

        Time d = delta;
        RippleScope scope = _options.RippleScope;
        string key = $"{(ripple ? "rippletrim" : "trim")}-solo:{_clipId}:{(edge == TrimEdge.In ? "in" : "out")}";
        Func<ICommand> build = (_options.IgnoreGroups && clip.LinkId is not null, ripple) switch
        {
            (true, true) => () => Solo("Recorte ripple", _sequenceId, _clipId, ctx => TimelineEdits.RippleTrim(ctx, _clipId, edge, d, scope), key, _ids),
            (true, false) => () => Solo(edge == TrimEdge.In ? "Recortar inicio" : "Recortar final", _sequenceId, _clipId, ctx => TimelineEdits.Trim(ctx, _clipId, edge, d), key, _ids),
            (false, true) => () => Edits.RippleTrim(_sequenceId, _clipId, edge, d, scope, _ids),
            _ => () => Edits.Trim(_sequenceId, _clipId, edge, d, _ids),
        };
        return new DragPlan(build, true, ghostStart, ghostEnd, track.Id, guide, null);
    }

    private DragPlan PlanSlip(Project project, Sequence sequence, Track track, Clip clip, Time offset)
    {
        Time low = Time.FromTicks(long.MinValue / 4);
        Time high = Time.FromTicks(long.MaxValue / 4);
        foreach ((_, Clip c) in Members(sequence, track, clip))
        {
            Time mediaDuration = project.FindMedia(c.MediaId)?.Duration ?? c.SourceOut;
            low = Time.Max(low, -c.SourceIn);
            high = Time.Min(high, mediaDuration - c.SourceOut);
        }

        Time desiredIn = (_originIn - offset).SnapToFrame(_frameRate);
        Time delta = low > high ? Time.Zero : Time.Min(Time.Max(desiredIn - clip.SourceIn, low), high);
        if (delta == Time.Zero)
        {
            return new DragPlan(null, true, clip.Start, clip.End, track.Id, null, null);
        }

        Time d = delta;
        Func<ICommand> build = _options.IgnoreGroups && clip.LinkId is not null
            ? () => Solo("Slip", _sequenceId, _clipId, ctx => TimelineEdits.Slip(ctx, _clipId, d), $"slip-solo:{_clipId}", _ids)
            : () => Edits.Slip(_sequenceId, _clipId, d, _ids);
        return new DragPlan(build, true, clip.Start, clip.End, track.Id, null, null);
    }

    private List<(Track Track, Clip Clip)> Members(Sequence sequence, Track track, Clip clip) =>
        _options.IgnoreGroups ? [(track, clip)] : sequence.GetLinkGroup(clip.Id);

    private static Clip? Previous(Track track, Clip clip)
    {
        int i = track.Clips.IndexOf(clip);
        return i > 0 ? track.Clips[i - 1] : null;
    }

    private static Clip? Next(Track track, Clip clip)
    {
        int i = track.Clips.IndexOf(clip);
        return i >= 0 && i + 1 < track.Clips.Count ? track.Clips[i + 1] : null;
    }
}
