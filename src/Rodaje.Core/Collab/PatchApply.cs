using Rodaje.Core.Model;

namespace Rodaje.Core.Collab;

/// <summary>Límites y decisiones de quien aplica un parche.</summary>
public sealed record PatchApplyOptions
{
    /// <summary>Límites de la forma del parche; <see langword="null"/> = <see cref="WireLimits.Default"/>.</summary>
    public WireLimits? Limits { get; init; }
}

/// <summary>Aplica parches a un proyecto con todas las comprobaciones.</summary>
public static class PatchApply
{
    /// <summary>
    /// Aplica <paramref name="patch"/> sobre una copia candidata: versiones, padres y <c>ModelValidator.Validate</c> del proyecto
    /// entero. Si todo pasa, <paramref name="project"/> cambia (los objetos que el parche no toca siguen siendo los mismos) y
    /// <paramref name="versions"/> avanza; si no, no cambia nada y el resultado dice por qué. Nunca lanza por un parche mal
    /// formado o hostil. Aplicar los mismos parches en el mismo orden deja siempre el mismo <see cref="SharedState.Hash"/>.
    /// </summary>
    public static PatchResult TryApply(Project project, ElementVersions versions, Patch patch, PatchApplyOptions? options = null) =>
        throw Pending.Exception();
}
