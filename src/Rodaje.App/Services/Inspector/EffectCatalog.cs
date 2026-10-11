using Rodaje.Core.Effects;
using Rodaje.Core.Model;

namespace Rodaje.App.Services.Inspector;

/// <summary>Un efecto que se puede añadir desde la pestaña Filtros.</summary>
/// <param name="Type">Tipo en el modelo (<see cref="EffectInstance.Type"/>).</param>
/// <param name="Name">Nombre en la interfaz.</param>
/// <param name="Kind">Pistas donde tiene sentido.</param>
/// <param name="IsRendered">El motor lo aplica (su etapa no es <see cref="EffectStage.Deferred"/>).</param>
public sealed record EffectDescriptor(string Type, string Name, TrackKind Kind, bool IsRendered);

/// <summary>Los efectos de clip del registro, con su nombre en español y si el motor los aplica. Los ocultos (<c>rodaje.speed</c>) no salen.</summary>
public static class EffectCatalog
{
    public static IReadOnlyList<EffectDescriptor> All { get; } =
    [
        .. EffectRegistry.All
            .Where(d => !d.IsTransition && !d.Hidden)
            .SelectMany(d => Kinds(d.Target).Select(kind => new EffectDescriptor(d.Type, EffectUi.Name(d.Type), kind, d.Stage != EffectStage.Deferred))),
    ];

    /// <summary>Los que se ofrecen para un clip de ese tipo de pista: solo los que el motor aplica.</summary>
    public static IReadOnlyList<EffectDescriptor> For(TrackKind kind) => [.. All.Where(e => e.Kind == kind && e.IsRendered)];

    public static EffectDescriptor? Find(string type) => All.FirstOrDefault(e => e.Type == type);

    /// <summary>Nombre para mostrar; un tipo desconocido (de un complemento que falta) se muestra tal cual.</summary>
    public static string DisplayName(string type) => EffectUi.Name(type);

    /// <summary>Si el motor aplica este efecto del clip (o lo aplicaría al activarlo).</summary>
    public static EffectSupport Support(Clip clip, EffectInstance effect, TrackKind kind)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(effect);
        if (EffectRegistry.Find(effect.Type) is not { } definition)
        {
            return EffectSupport.Unknown;
        }

        if (definition.Stage == EffectStage.Deferred || !Kinds(definition.Target).Contains(kind))
        {
            return EffectSupport.Pending;
        }

        // El motor usa la primera transformación y el primer modo de mezcla activos.
        bool shadowed = effect.Type is BuiltInEffects.Transform or BuiltInEffects.Blend
            && effect.Enabled
            && !ReferenceEquals(clip.Effects.Find(e => e.Enabled && e.Type == effect.Type), effect);
        return shadowed ? EffectSupport.Shadowed : EffectSupport.Applied;
    }

    private static IEnumerable<TrackKind> Kinds(EffectTarget target)
    {
        if (target.HasFlag(EffectTarget.Video))
        {
            yield return TrackKind.Video;
        }

        if (target.HasFlag(EffectTarget.Audio))
        {
            yield return TrackKind.Audio;
        }
    }
}

public enum EffectSupport
{
    Applied,

    /// <summary>Registrado, pero el motor todavía no lo aplica (estabilizar, o el <c>rodaje.speed</c> reservado) o no en este tipo de pista.</summary>
    Pending,

    /// <summary>Hay otro efecto igual antes en la pila y solo se aplica ese.</summary>
    Shadowed,

    /// <summary>No está en el registro: de un complemento que falta o de otro programa. Se conserva sin aplicarse.</summary>
    Unknown,
}
