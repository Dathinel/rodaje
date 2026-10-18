using Rodaje.App.Services;
using Rodaje.App.Services.Settings;
using Rodaje.App.Services.Titles;
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

    /// <summary>Alt+arrastrar el cuerpo: otra parte del origen, sin mover el clip.</summary>
    Slip,

    /// <summary>Alt+arrastrar el borde de entrada: cambia la velocidad para que el mismo material dure lo arrastrado.</summary>
    StretchStart,

    /// <summary>Alt+arrastrar el borde de salida: como <see cref="StretchStart"/>, con el inicio quieto.</summary>
    StretchEnd,

    /// <summary>Roll: mueve el corte entre el clip y el que va pegado después; la duración total no cambia.</summary>
    Roll,

    /// <summary>Slide: mueve el clip entre sus vecinos pegados, que se recortan para seguir tocándolo.</summary>
    Slide,
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

    /// <summary>De qué pistas de video sale el audio de un medio soltado (Ajustes).</summary>
    public LinkedAudioTracks LinkedAudio { get; init; } = LinkedAudioTracks.All;
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
/// <param name="Balloon">Texto del globo junto al borde arrastrado (al estirar: velocidad y duración) y dónde va.</param>
public sealed record DragPlan(
    Func<ICommand>? Build,
    bool IsLive,
    Time GhostStart,
    Time GhostEnd,
    string GhostTrackId,
    Time? SnapGuide,
    Time? InsertAt,
    (Time At, string Text)? Balloon = null);

/// <summary>
/// Traduce un arrastre sobre un clip a comandos del núcleo. Recuerda el estado del clip al empezar y, en cada paso,
/// calcula el comando relativo al estado actual (que ya incluye los pasos anteriores del mismo gesto); estirar una
/// curva es la excepción: cada paso sale del estado del inicio del gesto (<see cref="FromGestureStart"/>).
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
    private readonly Time _originDuration;
    private readonly Time _originIn;
    private readonly double _slipRate;
    private readonly Time _minStart;
    private readonly Rational _frameRate;
    private readonly SnapPoints _snaps;
    private readonly HashSet<string> _group;

    /// <summary>
    /// Se mueve con otros clips que no son sus enlazados (su grupo o la selección): todos con el mismo desplazamiento.
    /// </summary>
    private readonly bool _together;

    // Estirar una curva se calcula desde el estado del inicio del gesto: encadenar pasos de un cuadro redondearía los
    // puntos a centésimas una y otra vez.
    private readonly SequenceSnapshot? _stretchStart;
    private readonly Time _originSource;
    private readonly (int Low, int High) _originCurveRange;

    // Si la selección de la línea de tiempo incluye al clip, moverlo arrastra a toda.
    public ClipDrag(Project project, string sequenceId, string clipId, DragOperation operation, Time grab, DragOptions options, Time? playhead, IIdGenerator? ids = null,
        IReadOnlyCollection<string>? selection = null)
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
        _originDuration = clip.Duration;
        _originIn = clip.SourceIn;
        _slipRate = SlipRate(clip);
        _frameRate = sequence.FrameRate;
        bool multi = operation == DragOperation.Move && selection is { Count: > 1 } && selection.Contains(clipId);
        List<(Track Track, Clip Clip)> members = options.IgnoreGroups
            ? multi ? [.. selection!.Select(sequence.FindClip).OfType<(Track Track, Clip Clip)>()] : [(track, clip)]
            : operation == DragOperation.Move && (multi || clip.GroupId is not null) ? [.. TimelineEdits.ExpandWithGroups(sequence, multi ? selection! : [clipId])]
            : sequence.GetLinkGroup(clipId);
        HashSet<string> linked = [.. sequence.GetLinkGroup(clipId).Select(m => m.Clip.Id)];
        _together = operation == DragOperation.Move && members.Exists(m => !linked.Contains(m.Clip.Id));
        _group = [.. members.Select(m => m.Clip.Id)];
        _minStart = clip.Start - members.Min(m => m.Clip.Start);
        _snaps = options.Snapping ? SnapPoints.Collect(sequence, _group, playhead) : SnapPoints.Empty;
        _originSource = clip.SourceDuration;
        if (operation is DragOperation.StretchStart or DragOperation.StretchEnd && clip.Speed is { Freeze: false, Curve: { Points.Count: > 0 } curve })
        {
            _stretchStart = SequenceSnapshot.Capture(sequence);
            _originCurveRange = (curve.Points.Min(p => p.Hundredths), curve.Points.Max(p => p.Hundredths));
        }
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
            DragOperation.StretchStart or DragOperation.StretchEnd => PlanStretch(project, sequence, found.Track, found.Clip, offset),
            DragOperation.Roll => PlanRoll(project, sequence, found.Track, found.Clip, offset),
            DragOperation.Slide => PlanSlide(project, sequence, found.Track, found.Clip, offset),
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

        if (_together)
        {
            return PlanTogether(sequence, track, clip, desired, guide, row);
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

    /// <summary>
    /// Mover varios: el mismo desplazamiento para todos, con el ajuste del agarrado. Cambian de pista si caben todos (si
    /// no, solo en el tiempo); si alguno está en la pista principal magnética, los de ella se reordenan como un bloque.
    /// </summary>
    private DragPlan PlanTogether(Sequence sequence, Track track, Clip clip, Time desired, Time? guide, TrackRow? row)
    {
        Time duration = clip.Duration;
        Time delta = desired - clip.Start;
        bool alone = _options.IgnoreGroups;
        if (_options.MagneticTrackId is { } magnetic && sequence.FindTrack(magnetic) is { } main && main.Clips.Exists(c => _group.Contains(c.Id)))
        {
            List<Clip> block = main.Clips.FindAll(c => _group.Contains(c.Id));
            List<Clip> others = main.Clips.FindAll(c => !_group.Contains(c.Id));
            Clip anchor = track == main ? clip : block[0];
            Time origin = main.Clips[0].Start;
            (int index, Time boundary) = MagneticSlot(others, origin, anchor.Start + delta, anchor.Duration);
            List<Clip> order = [.. others.Take(index), .. block, .. others.Skip(index)];
            Func<ICommand>? reorder = order.SequenceEqual(main.Clips)
                ? null
                : () => Edits.MoveClipsOnMainTrack(_sequenceId, _group, magnetic, index, alone, _ids);
            return new DragPlan(reorder, true, desired, desired + duration, track.Id, null, boundary);
        }

        int offset = row is { } r && r.Kind == _kind && r.Track != track ? KindIndex(sequence, r.Track) - KindIndex(sequence, track) : 0;
        if (delta == Time.Zero && offset == 0)
        {
            return new DragPlan(null, true, desired, desired + duration, track.Id, guide, null);
        }

        string ghostTrack = offset == 0 ? track.Id : row!.Track.Id;
        return new DragPlan(() => Edits.MoveClips(_sequenceId, _group, delta, _kind, offset, alone, _ids), true, desired, desired + duration, ghostTrack, guide, null);
    }

    private static int KindIndex(Sequence sequence, Track track) =>
        (track.Kind == TrackKind.Video ? sequence.VideoTracks : sequence.AudioTracks).IndexOf(track);

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
        TrimEdge edge = _operation is DragOperation.TrimStart or DragOperation.StretchStart ? TrimEdge.In : TrimEdge.Out;
        List<(Track Track, Clip Clip)> members = Members(sequence, track, clip);
        Time frame = Frame;
        Time? guide = null;
        Time delta;
        Time low = Time.FromTicks(long.MinValue / 4);
        Time high = Time.FromTicks(long.MaxValue / 4);
        foreach ((Track t, Clip c) in members)
        {
            if (edge == TrimEdge.In)
            {
                low = Time.Max(low, -Room(project, c, TrimEdge.In));
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
                high = Time.Min(high, Room(project, c, TrimEdge.Out));
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

            // Con ripple el inicio queda quieto y el clip se acorta: lo que falta es lo pedido menos lo ya recortado.
            delta = ripple ? desiredStart - _originStart - (_originDuration - clip.Duration) : desiredStart - clip.Start;
        }
        else
        {
            Time desiredEnd = (_originStart + _originDuration + offset).SnapToFrame(_frameRate);
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

    /// <summary>
    /// Estirar: la duración nueva sale del borde (pegado al cuadro y a los puntos de ajuste) y la velocidad, de cuánto
    /// origen tiene el clip; la velocidad queda entre 0,1× y 100×. Como todo cambio de velocidad, va con magnetismo en
    /// cualquier pista: el inicio queda y lo que sigue se une, con el ámbito de ripple de la sesión. Un congelado no tiene
    /// velocidad: estirarlo es recortarlo. Va siempre con los enlazados, que comparten velocidad.
    /// </summary>
    private DragPlan PlanStretch(Project project, Sequence sequence, Track track, Clip clip, Time offset)
    {
        if (Blocked(sequence, track, clip) is { } blocked)
        {
            return blocked;
        }

        if (clip.Speed is { Freeze: true })
        {
            return PlanTrim(project, sequence, track, clip, offset);
        }

        TrimEdge edge = _operation == DragOperation.StretchStart ? TrimEdge.In : TrimEdge.Out;
        bool curve = clip.Speed is { Curve: not null };
        Time span = curve ? _originSource : clip.SourceDuration;
        if ((curve && _stretchStart is null) || span <= Time.Zero)
        {
            return new DragPlan(null, true, clip.Start, clip.End, track.Id, null, null);
        }

        Time frame = Frame;
        long minTicks;
        long maxTicks;
        if (curve)
        {
            // Cada punto se divide por lo que se estira: ninguno puede salir de 0,1×–10×.
            Int128 origin = _originDuration.Ticks;
            minTicks = (long)Int128.Max(frame.Ticks, ((origin * _originCurveRange.High) + RetimeMath.MaxHundredths - 1) / RetimeMath.MaxHundredths);
            maxTicks = (long)Int128.Min(origin * _originCurveRange.Low / RetimeMath.MinHundredths, RetimeMath.MaxExtent);
        }
        else
        {
            minTicks = Math.Max(frame.Ticks, (span.Ticks + 99) / 100);
            maxTicks = Math.Min(span.Ticks * 10, RetimeMath.MaxExtent);
        }

        Time? guide = null;
        long duration;
        if (edge == TrimEdge.Out)
        {
            Time desiredEnd = (_originStart + _originDuration + offset).SnapToFrame(_frameRate);
            if (_snaps.Count > 0 && _snaps.Nearest(desiredEnd, SnapThreshold) is { } snapped)
            {
                desiredEnd = snapped;
                guide = snapped;
            }

            duration = (desiredEnd - clip.Start).Ticks;
        }
        else
        {
            Time desiredStart = (_originStart + offset).SnapToFrame(_frameRate);
            if (_snaps.Count > 0 && _snaps.Nearest(desiredStart, SnapThreshold) is { } snapped)
            {
                desiredStart = snapped;
                guide = snapped;
            }

            duration = (_originDuration - (desiredStart - _originStart)).Ticks;
        }

        if (minTicks > maxTicks)
        {
            return new DragPlan(null, true, clip.Start, clip.End, track.Id, guide, null);
        }

        Time d = Time.FromTicks(Math.Clamp(duration, minTicks, maxTicks));
        Time ghostStart = clip.Start;
        string text = $"{SpeedBadge.Format((double)span.Ticks / d.Ticks)} · {Rodaje.Engine.Presentation.Timecode.Format(d)}";
        var balloon = (ghostStart + d, text);
        if (d == clip.Duration)
        {
            return new DragPlan(null, true, ghostStart, ghostStart + d, track.Id, guide, null, balloon);
        }

        RippleScope scope = _options.RippleScope;
        Func<ICommand> build;
        if (_stretchStart is { } start && curve)
        {
            // Desde el inicio del gesto: el borde se mueve respecto del clip de entonces y el paso vuelve antes a ese estado.
            Time fromStart = edge == TrimEdge.Out ? d - _originDuration : _originDuration - d;
            build = () => new FromGestureStart(_sequenceId, start,
                fromStart == Time.Zero ? null : Edits.RateStretch(_sequenceId, _clipId, edge, fromStart, scope, _ids));
        }
        else
        {
            // El núcleo pide cuánto se mueve el borde respecto del clip de ahora (los pasos del arrastre se encadenan).
            Time delta = edge == TrimEdge.Out ? d - clip.Duration : clip.Duration - d;
            build = () => Edits.RateStretch(_sequenceId, _clipId, edge, delta, scope, _ids);
        }

        return new DragPlan(
            build,
            true,
            ghostStart,
            ghostStart + d,
            track.Id,
            guide,
            null,
            balloon);
    }

    private DragPlan PlanSlip(Project project, Sequence sequence, Track track, Clip clip, Time offset)
    {
        if (Blocked(sequence, track, clip) is { } blocked)
        {
            return blocked;
        }

        Time low = Time.FromTicks(long.MinValue / 4);
        Time high = Time.FromTicks(long.MaxValue / 4);
        foreach ((_, Clip c) in Members(sequence, track, clip))
        {
            Time mediaDuration = c.IsCompound ? CompoundOps.SourceLimit(project, c) : project.FindMedia(c.MediaId)?.Duration ?? c.SourceOut;
            low = Time.Max(low, -c.SourceIn);
            high = Time.Min(high, mediaDuration - c.SourceOut);
        }

        Time desiredIn = (_originIn - Time.FromTicks((long)Math.Round(offset.Ticks * _slipRate))).SnapToFrame(_frameRate);
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

    /// <summary>
    /// Roll del corte de salida del clip: el corte va al cuadro (y a los puntos de ajuste) y no pasa del cuadro que le
    /// queda a cada lado ni del material de los dos clips, en él y en sus enlazados. Sin vecino pegado es recortar la salida.
    /// </summary>
    private DragPlan PlanRoll(Project project, Sequence sequence, Track track, Clip clip, Time offset)
    {
        Time frame = Frame;
        Time low = Time.FromTicks(long.MinValue / 4);
        Time high = Time.FromTicks(long.MaxValue / 4);
        foreach ((Track t, Clip c) in Members(sequence, track, clip))
        {
            low = Time.Max(low, frame - c.Duration);
            high = Time.Min(high, Room(project, c, TrimEdge.Out));
            if (Next(t, c) is { } next && next.Start == c.End)
            {
                low = Time.Max(low, -Room(project, next, TrimEdge.In));
                high = Time.Min(high, next.Duration - frame);
            }
            else if (Next(t, c) is { } gap)
            {
                high = Time.Min(high, gap.Start - c.End);
            }
        }

        Time desiredCut = (_originStart + _originDuration + offset).SnapToFrame(_frameRate);
        Time? guide = null;
        if (_snaps.Count > 0 && _snaps.Nearest(desiredCut, SnapThreshold) is { } snapped)
        {
            desiredCut = snapped;
            guide = snapped;
        }

        Time delta = low > high ? Time.Zero : Time.Min(Time.Max(desiredCut - clip.End, low), high);
        (Time, string) balloon = (clip.End + delta, ClipLabels.FrameShift(clip.End + delta - (_originStart + _originDuration), _frameRate));
        if (delta == Time.Zero)
        {
            return new DragPlan(null, true, clip.Start, clip.End, track.Id, guide, null, balloon);
        }

        Time d = delta;
        Func<ICommand> build = _options.IgnoreGroups && clip.LinkId is not null
            ? () => Solo("Roll", _sequenceId, _clipId, ctx => TimelineEdits.Roll(ctx, _clipId, d), $"roll-solo:{_clipId}", _ids)
            : () => Edits.Roll(_sequenceId, _clipId, d, _ids);
        return new DragPlan(build, true, clip.Start, clip.End + delta, track.Id, guide, null, balloon);
    }

    /// <summary>
    /// Slide: el clip corre entre sus vecinos pegados, que se recortan; sin vecino, hasta el hueco que tenga. Pegado al
    /// cuadro y a los puntos de ajuste, en él y en sus enlazados.
    /// </summary>
    private DragPlan PlanSlide(Project project, Sequence sequence, Track track, Clip clip, Time offset)
    {
        Time frame = Frame;
        Time low = Time.FromTicks(long.MinValue / 4);
        Time high = Time.FromTicks(long.MaxValue / 4);
        foreach ((Track t, Clip c) in Members(sequence, track, clip))
        {
            Clip? previous = Previous(t, c);
            if (previous is not null && previous.End == c.Start)
            {
                low = Time.Max(low, frame - previous.Duration);
                high = Time.Min(high, Room(project, previous, TrimEdge.Out));
            }
            else
            {
                low = Time.Max(low, (previous?.End ?? Time.Zero) - c.Start);
            }

            Clip? next = Next(t, c);
            if (next is not null && next.Start == c.End)
            {
                low = Time.Max(low, -Room(project, next, TrimEdge.In));
                high = Time.Min(high, next.Duration - frame);
            }
            else if (next is not null)
            {
                high = Time.Min(high, next.Start - c.End);
            }
        }

        Time duration = clip.Duration;
        Time desired = (_originStart + offset).SnapToFrame(_frameRate);
        Time? guide = null;
        if (_snaps.Count > 0)
        {
            (Time correction, guide) = _snaps.SnapSpan(desired, desired + duration, SnapThreshold);
            desired += correction;
        }

        Time delta = low > high ? Time.Zero : Time.Min(Time.Max(desired - clip.Start, low), high);
        (Time, string) balloon = (clip.Start + delta, ClipLabels.FrameShift(clip.Start + delta - _originStart, _frameRate));
        if (delta == Time.Zero)
        {
            return new DragPlan(null, true, clip.Start, clip.End, track.Id, guide, null, balloon);
        }

        Time d = delta;
        Func<ICommand> build = _options.IgnoreGroups && clip.LinkId is not null
            ? () => Solo("Slide", _sequenceId, _clipId, ctx => TimelineEdits.Slide(ctx, _clipId, d), $"slide-solo:{_clipId}", _ids)
            : () => Edits.Slide(_sequenceId, _clipId, d, _ids);
        return new DragPlan(build, true, clip.Start + delta, clip.End + delta, track.Id, guide, null, balloon);
    }

    private static Time Room(Project project, Clip clip, TrimEdge edge) =>
        project.FindMedia(clip.MediaId)?.IsStillImage == true ? GeneratorRoom : Room(clip, edge, MediaDuration(project, clip));

    private static Time MediaDuration(Project project, Clip clip) =>
        clip.IsCompound ? CompoundOps.SourceLimit(project, clip) : project.FindMedia(clip.MediaId)?.Duration ?? clip.SourceOut;

    /// <summary>
    /// Slip y Alt + borde no se aplican a un título (no tiene material ni velocidad): el gesto queda quieto y el globo lo
    /// explica.
    /// </summary>
    private DragPlan? Blocked(Sequence sequence, Track track, Clip clip)
    {
        bool title = _options.IgnoreGroups
            ? clip.Generator is not null
            : sequence.GetLinkGroup(clip.Id).Exists(m => m.Clip.Generator is not null);
        return title
            ? new DragPlan(null, true, clip.Start, clip.End, track.Id, null, null, (clip.Start, ClipLabels.NoMaterial))
            : null;
    }

    /// <summary>Margen de un título: su origen virtual no tiene principio ni final (lo acota la extensión del núcleo).</summary>
    public static readonly Time GeneratorRoom = Time.FromTicks(RetimeMath.MaxExtent);

    /// <summary>
    /// Cuánto puede crecer un borde en la línea de tiempo antes de pedir origen fuera del medio, con la velocidad del borde.
    /// Un congelado no gasta origen: su tope es el del mapeo.
    /// </summary>
    public static Time Room(Clip clip, TrimEdge edge, Time mediaDuration)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.Generator is not null)
        {
            return GeneratorRoom;
        }

        if (clip.Speed is null)
        {
            return edge == TrimEdge.In ? clip.SourceIn : mediaDuration - clip.SourceOut;
        }

        ClipTimeMap map = clip.TimeMap;
        long cap = Math.Max(0, RetimeMath.MaxExtent - Math.Abs(map.Offset.Ticks) - map.Duration.Ticks);
        bool Fits(long delta)
        {
            Time source = edge == TrimEdge.In
                ? map.ExtendedSource(map.Start - Time.FromTicks(delta))
                : map.ExtendedSource(map.End - Time.FromTicks(1) + Time.FromTicks(delta));
            return source >= Time.Zero && source < mediaDuration;
        }

        // El origen avanza siempre hacia el mismo lado, así que «cabe» es monótono: búsqueda binaria.
        long lowOk = 0;
        long high = cap;
        while (lowOk < high)
        {
            long mid = lowOk + ((high - lowOk + 1) / 2);
            if (Fits(mid))
            {
                lowOk = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return Time.FromTicks(lowOk);
    }

    /// <summary>Cuánto origen se corre por cada instante que se mueve el puntero en un slip: la velocidad media del clip.</summary>
    private static double SlipRate(Clip clip)
    {
        if (clip.Speed is not { } speed || speed.Freeze)
        {
            return 1;
        }

        double rate = (double)clip.SourceDuration.Ticks / clip.Duration.Ticks;
        return speed.Reverse ? -rate : rate;
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
