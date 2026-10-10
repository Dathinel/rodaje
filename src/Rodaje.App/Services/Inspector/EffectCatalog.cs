using Rodaje.Core.Model;

namespace Rodaje.App.Services.Inspector;

/// <summary>Un efecto que se puede añadir desde la pestaña Filtros.</summary>
/// <param name="Type">Tipo en el modelo (<see cref="EffectInstance.Type"/>).</param>
/// <param name="Name">Nombre en la interfaz.</param>
/// <param name="Kind">Pistas donde tiene sentido.</param>
/// <param name="IsRendered">El motor ya lo aplica; si no, la interfaz avisa que llega en la fase 5.</param>
public sealed record EffectDescriptor(string Type, string Name, TrackKind Kind, bool IsRendered);

/// <summary>Los efectos incorporados, con su nombre en español y si el motor ya los aplica.</summary>
public static class EffectCatalog
{
    public static IReadOnlyList<EffectDescriptor> All { get; } =
    [
        new(BuiltInEffects.Transform, "Transformar", TrackKind.Video, true),
        new(BuiltInEffects.Color, "Color y LUT", TrackKind.Video, false),
        new(BuiltInEffects.Blur, "Desenfoque", TrackKind.Video, false),
        new(BuiltInEffects.Sharpen, "Nitidez", TrackKind.Video, false),
        new(BuiltInEffects.Vignette, "Viñeta", TrackKind.Video, false),
        new(BuiltInEffects.Speed, "Velocidad", TrackKind.Video, false),
        new(BuiltInEffects.Stabilize, "Estabilizar", TrackKind.Video, false),
        new(BuiltInEffects.Audio, "Volumen y fundidos", TrackKind.Audio, false),
    ];

    /// <summary>Los que se ofrecen para un clip de ese tipo de pista.</summary>
    public static IReadOnlyList<EffectDescriptor> For(TrackKind kind) => [.. All.Where(e => e.Kind == kind)];

    public static EffectDescriptor? Find(string type) => All.FirstOrDefault(e => e.Type == type);

    /// <summary>Nombre para mostrar; un tipo desconocido (de un complemento que falta) se muestra tal cual.</summary>
    public static string DisplayName(string type) => Find(type)?.Name ?? type;

    /// <summary>Si el motor aplica este efecto del clip (o lo aplicaría al activarlo).</summary>
    public static EffectSupport Support(Clip clip, EffectInstance effect, TrackKind kind)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(effect);
        if (Find(effect.Type) is not { IsRendered: true } descriptor || descriptor.Kind != kind)
        {
            return EffectSupport.Pending;
        }

        // El motor usa solo la primera transformación activa.
        bool shadowed = effect.Type == BuiltInEffects.Transform
            && effect.Enabled
            && !ReferenceEquals(clip.Effects.Find(e => e.Enabled && e.Type == BuiltInEffects.Transform), effect);
        return shadowed ? EffectSupport.Shadowed : EffectSupport.Applied;
    }
}

public enum EffectSupport
{
    Applied,

    /// <summary>El motor todavía no lo dibuja; llega en la fase 5.</summary>
    Pending,

    /// <summary>Hay otro efecto igual antes en la pila y solo se aplica ese.</summary>
    Shadowed,
}
