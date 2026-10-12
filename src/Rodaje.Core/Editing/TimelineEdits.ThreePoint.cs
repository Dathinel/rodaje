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
    RippleScope Scope = RippleScope.Track);

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
            IEnumerable<Track> shifted = request.Scope.IncludesAllTracks() ? ctx.Sequence.AllTracks.Where(t => !t.Locked) : targets;
            foreach (Track track in shifted)
            {
                TrackOps.InsertSpace(ctx, track, map.Start, map.Duration);
            }

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
}
