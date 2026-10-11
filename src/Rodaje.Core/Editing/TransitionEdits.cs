using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

/// <summary>Ediciones de las transiciones de los cortes. Solo aceptan tipos registrados como transición.</summary>
public static class TransitionEdits
{
    /// <summary>Pone una transición en el corte donde termina <paramref name="fromClipId"/>; el clip siguiente tiene que empezar justo ahí.</summary>
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

    public static void SetDuration(EditContext ctx, string transitionId, Time duration)
    {
        EnsurePositive(duration);
        Editable(ctx, transitionId).Transition.Duration = duration;
        ctx.EnsureValid();
    }

    /// <summary>Cambia el tipo; los parámetros y opciones del anterior se descartan.</summary>
    public static void SetType(EditContext ctx, string transitionId, string type)
    {
        EnsureTransitionType(type);
        TransitionInstance transition = Editable(ctx, transitionId).Transition;
        if (transition.Type == type)
        {
            return;
        }

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

    private static void EnsurePositive(Time duration)
    {
        if (duration <= Time.Zero)
        {
            throw new EditException("La transición necesita una duración mayor que cero.");
        }
    }
}
