using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Packages;

namespace Rodaje.App.Views.Timeline;

public enum TransitionHitKind
{
    None,

    /// <summary>El bloque de la transición: un clic la selecciona.</summary>
    Body,

    /// <summary>La manija izquierda: arrastrarla cambia la duración, siempre centrada en el corte.</summary>
    StartHandle,

    EndHandle,

    /// <summary>El símbolo de fundido cruzado de un corte sin transición de la pista principal: un clic lo crea.</summary>
    AutoCrossfade,
}

/// <param name="From">El clip que termina en el corte.</param>
/// <param name="Transition">La transición, salvo en <see cref="TransitionHitKind.AutoCrossfade"/>.</param>
public readonly record struct TransitionHit(TransitionHitKind Kind, TrackRow? Row, Clip? From, TransitionInstance? Transition, Time Cut)
{
    public static readonly TransitionHit None = new(TransitionHitKind.None, null, null, null, Time.Zero);
}

/// <summary>Dónde se dibujan las transiciones, qué hay bajo el puntero y qué comando crea soltar una.</summary>
public static class TransitionPlanning
{
    /// <summary>
    /// Formato del arrastre de una transición desde la biblioteca: el texto es el tipo del motor (<c>rodaje.wipe</c>) o la
    /// referencia <c>paquete@versión/elemento</c> de un preajuste. Con <c>rodaje/elemento</c> solo se aceptan los tipos
    /// del motor, porque una referencia de paquete ahí puede ser un filtro.
    /// </summary>
    public const string DragFormat = "rodaje/transicion";

    public const double BlockHeight = 18;
    public const double MinBlockWidth = 12;
    public const double HandleGrab = 5;
    public const double SymbolRadius = 8;

    /// <summary>Hasta qué distancia de un corte, en píxeles, cae una transición soltada.</summary>
    public const double DropReachPixels = 28;

    /// <summary>Los cortes entre clips pegados que todavía no tienen transición.</summary>
    public static List<(Clip From, Time Cut)> OpenCuts(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var cuts = new List<(Clip, Time)>();
        for (int i = 1; i < track.Clips.Count; i++)
        {
            Clip from = track.Clips[i - 1];
            if (from.End == track.Clips[i].Start && track.TransitionFrom(from.Id) is null)
            {
                cuts.Add((from, from.End));
            }
        }

        return cuts;
    }

    /// <summary>El instante del corte de la transición; <c>null</c> si su clip ya no está.</summary>
    public static Time? CutOf(Track track, TransitionInstance transition)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(transition);
        return track.Clips.Find(c => c.Id == transition.From)?.End;
    }

    /// <summary>Franja vertical del bloque, en el contenido: centrada en la fila.</summary>
    public static (double Top, double Bottom) Band(TrackRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        double height = Math.Min(BlockHeight, row.Height - 8);
        double top = row.Top + ((row.Height - height) / 2);
        return (top, top + height);
    }

    /// <summary>Bordes del bloque en x del área visible; con poco zoom se ensancha alrededor del corte para poder agarrarlo.</summary>
    public static (double Left, double Right) Span(TimelineViewport viewport, Time cut, TransitionInstance transition)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(transition);
        double left = viewport.TimeToX(cut - transition.Before);
        double right = viewport.TimeToX(cut + transition.After);
        if (right - left < MinBlockWidth)
        {
            double center = viewport.TimeToX(cut);
            return (center - (MinBlockWidth / 2), center + (MinBlockWidth / 2));
        }

        return (left, right);
    }

    /// <param name="x">X en el área visible.</param>
    /// <param name="contentY">Y en el contenido.</param>
    /// <param name="symbolTrack">La pista con los símbolos de fundido automático, o <c>null</c> si están apagados.</param>
    public static TransitionHit Hit(TrackLayout layout, TimelineViewport viewport, double x, double contentY, Track? symbolTrack)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(viewport);
        if (layout.RowStrictlyAt(contentY) is not { } row)
        {
            return TransitionHit.None;
        }

        Track track = row.Track;
        (double top, double bottom) = Band(row);
        if (contentY >= top && contentY < bottom)
        {
            foreach (TransitionInstance transition in track.Transitions)
            {
                if (CutOf(track, transition) is not { } cut)
                {
                    continue;
                }

                (double left, double right) = Span(viewport, cut, transition);
                if (x < left - 2 || x > right + 2)
                {
                    continue;
                }

                double grab = Math.Min(HandleGrab, (right - left) / 3);
                TransitionHitKind kind = x - left <= grab ? TransitionHitKind.StartHandle
                    : right - x <= grab ? TransitionHitKind.EndHandle
                    : TransitionHitKind.Body;
                return new TransitionHit(kind, row, track.Clips.Find(c => c.Id == transition.From), transition, cut);
            }
        }

        if (ReferenceEquals(track, symbolTrack))
        {
            double centerY = row.Top + (row.Height / 2);
            foreach ((Clip from, Time cut) in OpenCuts(track))
            {
                double cx = viewport.TimeToX(cut);
                if (Math.Abs(x - cx) + Math.Abs(contentY - centerY) <= SymbolRadius + 1)
                {
                    return new TransitionHit(TransitionHitKind.AutoCrossfade, row, from, null, cut);
                }
            }
        }

        return TransitionHit.None;
    }

    /// <summary>
    /// Duración al arrastrar una manija hasta <paramref name="handleTime"/>: el doble de la distancia al corte, en cuadros
    /// enteros por lado, de un cuadro como mínimo y sin pasar <see cref="TransitionOps.MaxDuration"/> (el clip más corto).
    /// </summary>
    public static Time DurationForHandle(Track track, TransitionInstance transition, Time handleTime, Rational frameRate)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(transition);
        Clip? from = track.Clips.Find(c => c.Id == transition.From);
        Clip? to = track.Clips.Find(c => c.Id == transition.To);
        if (from is null || to is null)
        {
            return transition.Duration;
        }

        Time cut = from.End;
        Time half = handleTime >= cut ? handleTime - cut : cut - handleTime;
        Time frame = Time.FromFrames(1, frameRate);
        half = Time.Max(frame, half.SnapToFrame(frameRate));
        Time limit = Time.FromTicks(TransitionOps.MaxDuration(from, to).Ticks / 2);
        if (half > limit)
        {
            half = Time.Max(frame, limit);
        }

        return half + half;
    }

    /// <summary>El corte entre clips pegados más cercano a <paramref name="at"/>, si está a menos de <paramref name="reach"/>.</summary>
    public static (Clip From, Time Cut)? NearestCut(Track track, Time at, Time reach)
    {
        ArgumentNullException.ThrowIfNull(track);
        (Clip, Time)? best = null;
        long bestDistance = long.MaxValue;
        for (int i = 1; i < track.Clips.Count; i++)
        {
            Clip from = track.Clips[i - 1];
            if (from.End != track.Clips[i].Start)
            {
                continue;
            }

            long distance = Math.Abs((from.End - at).Ticks);
            if (distance <= reach.Ticks && distance < bestDistance)
            {
                bestDistance = distance;
                best = (from, from.End);
            }
        }

        return best;
    }

    /// <summary>Si el texto arrastrado es una transición: un tipo del motor o, con <paramref name="packageAllowed"/>, una referencia de paquete.</summary>
    public static bool IsTransitionReference(string? reference, bool packageAllowed)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        if (EffectRegistry.Find(reference) is { } definition)
        {
            return definition.IsTransition && reference != BuiltInTransitions.Preset;
        }

        return packageAllowed && PackageReference.TryParse(reference, out _);
    }

    /// <summary>
    /// Soltar <paramref name="reference"/> en el corte donde termina <paramref name="from"/>: crea la transición o, si el
    /// corte ya tiene una, le cambia el tipo y conserva la duración. Las de paquete son <see cref="BuiltInTransitions.Preset"/>
    /// con la referencia en su opción. <see cref="SequenceEditCommand.CreatedIds"/>: la transición.
    /// </summary>
    public static SequenceEditCommand BuildDrop(string sequenceId, string fromClipId, string reference, IIdGenerator? ids = null)
    {
        ArgumentNullException.ThrowIfNull(sequenceId);
        ArgumentNullException.ThrowIfNull(fromClipId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        bool builtIn = EffectRegistry.Find(reference) is { IsTransition: true };
        string type = builtIn ? reference : BuiltInTransitions.Preset;
        return new SequenceEditCommand("Añadir transición", sequenceId, ctx =>
        {
            Track? track = ctx.Sequence.FindClip(fromClipId)?.Track;
            string id;
            if (track?.TransitionFrom(fromClipId) is { } existing)
            {
                TransitionEdits.SetType(ctx, existing.Id, type);
                id = existing.Id;
            }
            else
            {
                id = TransitionEdits.AddTransition(ctx, fromClipId, type);
            }

            if (!builtIn)
            {
                TransitionEdits.SetOption(ctx, id, BuiltInTransitions.PresetOptions.Preset, reference);
            }

            return [id];
        }, ids);
    }

    /// <summary>El fundido cruzado del símbolo de un corte de la pista principal.</summary>
    public static SequenceEditCommand BuildAutoCrossfade(string sequenceId, string fromClipId, IIdGenerator? ids = null) =>
        Edits.AddTransition(sequenceId, fromClipId, BuiltInTransitions.Crossfade, null, ids);
}
