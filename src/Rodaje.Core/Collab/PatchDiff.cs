namespace Rodaje.Core.Collab;

/// <summary>Saca el parche que lleva de un estado compartido a otro.</summary>
public static class PatchDiff
{
    /// <summary>
    /// El parche que convierte <paramref name="before"/> en <paramref name="after"/>, o <see langword="null"/> si son iguales.
    /// Cada elemento lleva la versión que <paramref name="versions"/> tenga para él; las listas solo llevan su orden cuando no
    /// sale de aplicar el resto. Es determinista: el mismo par de estados da el mismo parche, con los cambios ordenados por clase y
    /// por identificador.
    /// </summary>
    public static Patch? Create(SharedState before, SharedState after, ElementVersions versions, PatchHeader header) =>
        throw Pending.Exception();
}
