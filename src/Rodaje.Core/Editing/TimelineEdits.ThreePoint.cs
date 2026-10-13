using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

/// <summary>Qué hacer en una edición de cuatro puntos cuando el tramo del origen y el de la secuencia no duran lo mismo.</summary>
public enum FourPointFit
{
    /// <summary>No decidir: el plan lo marca para que la interfaz pregunte.</summary>
    Ask,

    /// <summary>Ajustar a la velocidad: el tramo del origen entero llena el de la secuencia.</summary>
    FitToFill,

    /// <summary>Recortar: manda la duración de la secuencia y el origen se corta desde su entrada.</summary>
    Trim,
}

/// <summary>
/// Una edición de tres o cuatro puntos: las marcas del origen y de la secuencia (las que faltan son <c>null</c>), el
/// cursor y las pistas destino (una de video, una de audio o las dos; <c>null</c> apaga ese selector de origen).
/// </summary>
public sealed record ThreePointRequest(
    string MediaId,
    Time? SourceIn,
    Time? SourceOut,
    Time? SequenceIn,
    Time? SequenceOut,
    Time Playhead,
    string? VideoTrackId,
    string? AudioTrackId,
    PlacementMode Mode,
    FourPointFit Fit = FourPointFit.Ask,
    RippleScope Scope = RippleScope.Track,
    Time? SourcePlayhead = null);

/// <summary>Las cinco ediciones desde el origen.</summary>
public enum ThreePointAction
{
    /// <summary>Inserta y corre lo que sigue.</summary>
    Insert,

    /// <summary>Tapa lo que haya, sin correr nada.</summary>
    Overwrite,

    /// <summary>
    /// Reemplaza el clip bajo el cursor (o el tramo marcado) conservando su lugar y su duración; el cuadro del cursor del
    /// origen (o su entrada) queda alineado con el cursor de la secuencia (o con el inicio del clip).
    /// </summary>
    Replace,

    /// <summary>El tramo del origen llena el de la secuencia (o el del clip bajo el cursor) cambiando la velocidad.</summary>
    FitToFill,

    /// <summary>La imagen va encima, en la primera pista de video libre sobre la destino (o en una nueva).</summary>
    Superimpose,
}

/// <summary>Lo que hará la edición.</summary>
/// <param name="SourceIn">Entrada del tramo del origen.</param>
/// <param name="SourceOut">Salida del tramo del origen.</param>
/// <param name="At">Dónde empieza en la secuencia.</param>
/// <param name="Duration">Cuánto ocupa en la secuencia.</param>
/// <param name="Rate">La velocidad si se ajusta (cuatro puntos), o <c>null</c> a 1×.</param>
/// <param name="NeedsDecision">Cuatro puntos con duraciones distintas y <see cref="FourPointFit.Ask"/>.</param>
public sealed record ThreePointPlan(Time SourceIn, Time SourceOut, Time At, Time Duration, Rational? Rate, bool NeedsDecision);

public static partial class TimelineEdits
{
    /// <summary>
    /// Las reglas clásicas: con tres marcas la cuarta se calcula (sin entrada del origen se toma su inicio, sin salida su
    /// final; sin entrada de la secuencia, el cursor; con solo la salida de la secuencia el clip termina ahí). Con las cuatro
    /// y duraciones distintas decide <see cref="ThreePointRequest.Fit"/>.
    /// </summary>
    public static ThreePointPlan PlanThreePoint(Project project, ThreePointRequest request)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(request);
        MediaRef media = project.FindMedia(request.MediaId)
            ?? throw new EditException($"No existe el medio «{request.MediaId}» en el proyecto.");
        (Time? sIn, Time? sOut, Time? qIn, Time? qOut) = (request.SourceIn, request.SourceOut, request.SequenceIn, request.SequenceOut);
        if (sIn is { } a0 && sOut is { } b0 && b0 <= a0)
        {
            throw new EditException("La salida del origen tiene que ir después de su entrada.");
        }

        if (qIn is { } c0 && qOut is { } d0 && d0 <= c0)
        {
            throw new EditException("La salida de la secuencia tiene que ir después de su entrada.");
        }

        Time a;
        Time b;
        Time at;
        Rational? rate = null;
        bool decide = false;
        if (qIn is { } i && qOut is { } o)
        {
            Time span = o - i;
            at = i;
            if (sIn is { } x && sOut is { } y && y - x != span)
            {
                (a, b) = (x, y);
                switch (request.Fit)
                {
                    case FourPointFit.FitToFill:
                        rate = ApproximateRate((y - x).Ticks, span.Ticks);
                        break;
                    case FourPointFit.Trim:
                        b = x + span;
                        break;
                    default:
                        decide = true;
                        break;
                }
            }
            else if (sIn is null && sOut is { } y2)
            {
                (a, b) = (y2 - span, y2);
            }
            else
            {
                a = sIn ?? Time.Zero;
                b = a + span;
            }
        }
        else
        {
            a = sIn ?? Time.Zero;
            b = sOut ?? media.Duration;
            if (b <= a)
            {
                throw new EditException("La salida del origen tiene que ir después de su entrada.");
            }

            at = qOut is { } end && qIn is null ? end - (b - a) : qIn ?? request.Playhead;
        }

        if (a < Time.Zero || b > media.Duration)
        {
            throw new EditException($"No hay material suficiente en «{media.Name}» para ese tramo: falta {(a < Time.Zero ? Time.Zero - a : b - media.Duration)}.");
        }

        if (at < Time.Zero)
        {
            throw new EditException("El clip no cabe antes de la salida de la secuencia: empezaría antes del inicio.");
        }

        Time duration = rate is null ? b - a : request.SequenceOut!.Value - at;
        return new ThreePointPlan(a, b, at, duration, rate, decide);
    }

    /// <summary>Inserta o sobrescribe según <see cref="PlanThreePoint"/> en las pistas destino, enlazando video y audio.</summary>
    /// <returns>Los clips creados, el de video primero.</returns>
    public static IReadOnlyList<string> ThreePointEdit(EditContext ctx, ThreePointRequest request)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Mode == PlacementMode.Strict)
        {
            throw new ArgumentException("Una edición de tres puntos inserta o sobrescribe.", nameof(request));
        }

        ThreePointPlan plan = PlanThreePoint(ctx.Project, request);
        if (plan.NeedsDecision)
        {
            throw new EditException("El tramo del origen y el de la secuencia no duran lo mismo: elige ajustar la velocidad o recortar.");
        }

        MediaRef media = ctx.Project.GetMedia(request.MediaId);
        var targets = new List<Track>();
        foreach ((string? id, bool has) in new[] { (request.VideoTrackId, media.HasVideo), (request.AudioTrackId, media.HasAudio) })
        {
            if (id is not null && has)
            {
                Track track = ctx.Sequence.FindTrack(id) ?? throw new EditException($"No existe la pista «{id}» en la secuencia.");
                PlacementEnsureStream(media, track);
                EditContext.EnsureUnlocked(track);
                targets.Add(track);
            }
        }

        if (targets.Count == 0)
        {
            throw new EditException($"No hay pista destino para «{media.Name}»: activa el video o el audio del origen.");
        }

        ClipSpeed? speed = plan.Rate is { } rate ? new ClipSpeed { Rate = rate } : null;
        ClipTimeMap map = Fitted(plan, speed, media.Duration);
        if (request.Mode == PlacementMode.Insert)
        {
            TrackOps.InsertSpaceLinked(ctx, request.Scope.IncludesAllTracks() ? [.. ctx.Sequence.AllTracks.Where(t => !t.Locked)] : targets, map.Start, map.Duration);

            if (request.Scope == RippleScope.AllTracksAndMarkers)
            {
                EditContext.ShiftMarkers(ctx.Sequence, map.Start, map.Duration);
            }
        }
        else
        {
            foreach (Track track in targets)
            {
                TrackOps.ClearRange(ctx, track, map.Start, map.End);
            }
        }

        string? linkId = targets.Count > 1 ? ctx.Ids.NewId("link") : null;
        var created = new List<string>();
        foreach (Track track in targets)
        {
            Clip clip = PlacementNewClip(ctx, media, plan.SourceIn, plan.SourceOut, plan.At);
            clip.Speed = speed?.Clone();
            clip.LinkId = linkId;
            map.ApplyTo(clip);
            TrackOps.AddClipSorted(track, clip);
            created.Add(clip.Id);
        }

        ctx.EnsureValid();
        return created;
    }

    /// <summary>El mapeo del clip; ajustado a la velocidad, la duración pedida si el material alcanza.</summary>
    private static ClipTimeMap Fitted(ThreePointPlan plan, ClipSpeed? speed, Time material)
    {
        ClipTimeMap fit = ClipTimeMap.Fit(plan.At, plan.SourceIn, plan.SourceOut, speed);
        if (speed is null || fit.Duration == plan.Duration)
        {
            return fit;
        }

        ClipTimeMap exact = ClipTimeMap.Create(plan.At, plan.Duration, plan.SourceIn, speed);
        return exact.SourceIn >= Time.Zero && exact.SourceOut <= material ? exact : fit;
    }

    /// <summary>
    /// Una de las cinco ediciones desde el origen (<see cref="ThreePointAction"/>). Insertar y sobrescribir son
    /// <see cref="ThreePointEdit(EditContext, ThreePointRequest)"/>; las demás calculan sus marcas y sobrescriben.
    /// </summary>
    /// <returns>Los clips creados, el de video primero.</returns>
    public static IReadOnlyList<string> ThreePointEdit(EditContext ctx, ThreePointRequest request, ThreePointAction action)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(request);
        MediaRef media = ctx.Project.FindMedia(request.MediaId)
            ?? throw new EditException($"No existe el medio «{request.MediaId}» en el proyecto.");
        switch (action)
        {
            case ThreePointAction.Insert:
                return ThreePointEdit(ctx, request with { Mode = PlacementMode.Insert });
            case ThreePointAction.Overwrite:
                return ThreePointEdit(ctx, request with { Mode = PlacementMode.Overwrite });
            case ThreePointAction.FitToFill:
            {
                (Time start, Time end) = TargetSpan(ctx, request, media)
                    ?? throw new EditException("Ajustar a rellenar necesita la entrada y la salida de la secuencia, o un clip bajo el cursor en la pista destino.");
                return ThreePointEdit(ctx, request with
                {
                    SourceIn = request.SourceIn ?? Time.Zero,
                    SourceOut = request.SourceOut ?? media.Duration,
                    SequenceIn = start,
                    SequenceOut = end,
                    Mode = PlacementMode.Overwrite,
                    Fit = FourPointFit.FitToFill,
                });
            }

            case ThreePointAction.Replace:
            {
                (Time start, Time end) = TargetSpan(ctx, request, media)
                    ?? throw new EditException("No hay un clip bajo el cursor en la pista destino para reemplazar, ni un tramo marcado en la secuencia.");
                Time playhead = Time.Min(Time.Max(request.Playhead, start), end);
                Time sourceIn = request.SourceIn is { } marked
                    ? marked
                    : (request.SourcePlayhead ?? Time.Zero) - (playhead - start);
                return ThreePointEdit(ctx, request with
                {
                    SourceIn = sourceIn,
                    SourceOut = sourceIn + (end - start),
                    SequenceIn = start,
                    SequenceOut = end,
                    Mode = PlacementMode.Overwrite,
                    Fit = FourPointFit.Trim,
                });
            }

            case ThreePointAction.Superimpose:
            {
                if (!media.HasVideo)
                {
                    throw new EditException($"«{media.Name}» no tiene imagen: superponer pone la imagen encima de lo que hay.");
                }

                ThreePointPlan plan = PlanThreePoint(ctx.Project, request with { Mode = PlacementMode.Overwrite });
                if (plan.NeedsDecision)
                {
                    throw new EditException("El tramo del origen y el de la secuencia no duran lo mismo: elige ajustar la velocidad o recortar.");
                }

                Track baseTrack = request.VideoTrackId is { } id
                    ? ctx.Sequence.FindTrack(id) ?? throw new EditException($"No existe la pista «{id}» en la secuencia.")
                    : ctx.Sequence.VideoTracks.Count > 0 ? ctx.Sequence.VideoTracks[0] : throw new EditException("La secuencia no tiene pistas de video.");
                int from = ctx.Sequence.VideoTracks.IndexOf(baseTrack) + 1;
                Track? free = ctx.Sequence.VideoTracks.Skip(from)
                    .FirstOrDefault(t => !t.Locked && t.Role is null && TrackOps.FindOverlap(t, plan.At, plan.At + plan.Duration) is null);
                string target = free?.Id
                    ?? AddTrack(ctx, TrackKind.Video, ctx.Sequence.VideoTracks.Count, $"Video {ctx.Sequence.VideoTracks.Count + 1}");
                return ThreePointEdit(ctx, request with { VideoTrackId = target, AudioTrackId = null, Mode = PlacementMode.Overwrite });
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
    }

    /// <summary>El tramo marcado de la secuencia, o el del clip bajo el cursor en la pista destino (la de video primero).</summary>
    private static (Time Start, Time End)? TargetSpan(EditContext ctx, ThreePointRequest request, MediaRef media)
    {
        if (request.SequenceIn is { } i && request.SequenceOut is { } o && o > i)
        {
            return (i, o);
        }

        Time at = request.SequenceIn ?? request.Playhead;
        foreach ((string? id, bool has) in new[] { (request.VideoTrackId, media.HasVideo), (request.AudioTrackId, media.HasAudio) })
        {
            if (id is not null && has && ctx.Sequence.FindTrack(id)?.ClipAt(at) is { } clip)
            {
                return (clip.Start, clip.End);
            }
        }

        return null;
    }
}
