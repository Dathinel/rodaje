using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

/// <summary>Ediciones de las transiciones de los cortes. Solo aceptan tipos registrados como transición.</summary>
public static class TransitionEdits
{
    /// <summary>Pone una transición en el corte donde termina <paramref name="fromClipId"/>; el clip siguiente tiene que empezar justo ahí.</summary>
    /// <remarks>La duración se acota a <see cref="TransitionOps.MaxDuration(Track, TransitionInstance, TransitionAlignment?)"/>, como la que se da con <see cref="SetDuration"/>.</remarks>
    /// <returns>Identificador de la transición nueva.</returns>
    public static string AddTransition(EditContext ctx, string fromClipId, string type, Time? duration = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(fromClipId);
        EnsureTransitionType(type);
        if (ctx.Sequence.FindClip(fromClipId) is not { } found)
        {
            throw new EditException($"El clip «{fromClipId}» ya no está en la secuencia.");
        }

        Track track = found.Track;
        EditContext.EnsureUnlocked(track);
        EnsureFitsTrack(track, type);
        int index = track.Clips.IndexOf(found.Clip);
        Clip? next = index + 1 < track.Clips.Count ? track.Clips[index + 1] : null;
        if (next is null || next.Start != found.Clip.End)
        {
            throw new EditException("Una transición va entre dos clips seguidos, sin hueco entre ellos.");
        }

        if (track.TransitionFrom(fromClipId) is not null)
        {
            throw new EditException("Ese corte ya tiene una transición.");
        }

        Time length = duration ?? EffectRegistry.DefaultTransitionDuration;
        EnsurePositive(length);
        var transition = new TransitionInstance
        {
            Id = ctx.Ids.NewId("transition"),
            Type = type,
            From = fromClipId,
            To = next.Id,
            Duration = length,
        };
        int at = track.Transitions.FindIndex(t => track.Clips.FindIndex(c => c.Id == t.From) > index);
        track.Transitions.Insert(at < 0 ? track.Transitions.Count : at, transition);
        ctx.EnsureValid();
        return transition.Id;
    }

    public static void RemoveTransition(EditContext ctx, string transitionId)
    {
        (Track track, TransitionInstance transition) = Editable(ctx, transitionId);
        track.Transitions.Remove(transition);
    }

    /// <summary>Cambia la duración; se acota a <see cref="TransitionOps.MaxDuration(Track, TransitionInstance, TransitionAlignment?)"/>.</summary>
    public static void SetDuration(EditContext ctx, string transitionId, Time duration)
    {
        EnsurePositive(duration);
        Editable(ctx, transitionId).Transition.Duration = duration;
        ctx.EnsureValid();
    }

    /// <summary>
    /// Pone la misma duración a varias transiciones, cada una acotada a lo que permiten sus clips
    /// (<see cref="TransitionOps.MaxDuration(Track, TransitionInstance, TransitionAlignment?)"/>), de izquierda a derecha.
    /// </summary>
    public static void SetDurations(EditContext ctx, IReadOnlyCollection<string> transitionIds, Time duration)
    {
        ArgumentNullException.ThrowIfNull(transitionIds);
        EnsurePositive(duration);
        var chosen = transitionIds.Select(id => Editable(ctx, id)).ToList();
        foreach (Track track in chosen.Select(c => c.Track).Distinct())
        {
            // Primero se achican todas, así cada una ve a sus vecinas ya con la duración nueva si es menor.
            foreach (TransitionInstance transition in track.Transitions.Where(t => chosen.Exists(c => c.Transition == t)))
            {
                transition.Duration = Time.Min(transition.Duration, duration);
            }

            foreach (TransitionInstance transition in track.Transitions.Where(t => chosen.Exists(c => c.Transition == t)))
            {
                transition.Duration = Time.Max(Time.FromTicks(1), Time.Min(duration, TransitionOps.MaxDuration(track, transition)));
            }
        }

        ctx.EnsureValid();
    }

    /// <summary>Centrada, al final del clip que sale o al inicio del que entra; si ya no cabe, se acorta lo justo.</summary>
    public static void SetAlignment(EditContext ctx, string transitionId, TransitionAlignment alignment)
    {
        if (!Enum.IsDefined(alignment))
        {
            throw new EditException("Alineación desconocida.");
        }

        (Track track, TransitionInstance transition) = Editable(ctx, transitionId);
        Time max = TransitionOps.MaxDuration(track, transition, alignment);
        if (max <= Time.Zero)
        {
            throw new EditException("Con esa alineación la transición no cabe: la otra transición del clip ocupa todo.");
        }

        transition.Alignment = alignment;
        transition.Duration = Time.Min(transition.Duration, max);
        ctx.EnsureValid();
    }

    /// <summary>
    /// Curva del avance (nula o <see cref="Interpolation.Linear"/>, lineal); <paramref name="ease"/> solo cuenta con
    /// <see cref="Interpolation.Bezier"/> y se acota a sus límites.
    /// </summary>
    public static void SetCurve(EditContext ctx, string transitionId, Interpolation? curve, BezierEase? ease = null)
    {
        if (curve is { } c && !Enum.IsDefined(c))
        {
            throw new EditException("Curva desconocida.");
        }

        TransitionInstance transition = Editable(ctx, transitionId).Transition;
        transition.Curve = curve == Interpolation.Linear ? null : curve;
        transition.Ease = curve == Interpolation.Bezier ? (ease ?? BezierEase.Smooth).Clamped() : null;
    }

    /// <summary>Cambia el tipo; los parámetros y opciones del anterior se descartan.</summary>
    public static void SetType(EditContext ctx, string transitionId, string type)
    {
        EnsureTransitionType(type);
        (Track track, TransitionInstance transition) = Editable(ctx, transitionId);
        if (transition.Type == type)
        {
            return;
        }

        EnsureFitsTrack(track, type);

        transition.Type = type;
        transition.Parameters.Clear();
        transition.Options.Clear();
    }

    public static void SetParameter(EditContext ctx, string transitionId, string name, double value)
    {
        TransitionInstance transition = Editable(ctx, transitionId).Transition;
        ParameterDefinition definition = EffectRegistry.Find(transition.Type)?.FindParameter(name)
            ?? throw new EditException($"La transición no tiene el parámetro «{name}».");
        if (!double.IsFinite(value))
        {
            throw new EditException($"«{name}» necesita un número.");
        }

        transition.Parameters[name] = definition.Clamp(value);
    }

    /// <summary>Fija una opción; un valor vacío la quita.</summary>
    public static void SetOption(EditContext ctx, string transitionId, string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        TransitionInstance transition = Editable(ctx, transitionId).Transition;
        OptionDefinition definition = EffectRegistry.Find(transition.Type)?.FindOption(name)
            ?? throw new EditException($"La transición no tiene la opción «{name}».");
        if (value.Length == 0)
        {
            transition.Options.Remove(name);
        }
        else if (definition.Accepts(value))
        {
            transition.Options[name] = value;
        }
        else
        {
            throw new EditException($"«{value}» no es un valor de «{name}».");
        }
    }

    private static (Track Track, TransitionInstance Transition) Editable(EditContext ctx, string transitionId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(transitionId);
        foreach (Track track in ctx.Sequence.AllTracks)
        {
            if (track.FindTransition(transitionId) is { } transition)
            {
                EditContext.EnsureUnlocked(track);
                return (track, transition);
            }
        }

        throw new EditException($"La transición «{transitionId}» ya no está en la secuencia.");
    }

    private static void EnsureTransitionType(string type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        if (EffectRegistry.Find(type) is not { IsTransition: true })
        {
            throw new EditException($"«{type}» no es una transición.");
        }
    }

    private static void EnsureFitsTrack(Track track, string type)
    {
        if (BuiltInTransitions.IsAudio(type) && track.Kind != TrackKind.Audio)
        {
            throw new EditException("Las transiciones de audio van en una pista de audio.");
        }
    }

    private static void EnsurePositive(Time duration)
    {
        if (duration <= Time.Zero)
        {
            throw new EditException("La transición necesita una duración mayor que cero.");
        }
    }
}
