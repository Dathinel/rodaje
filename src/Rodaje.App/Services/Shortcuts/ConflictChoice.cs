namespace Rodaje.App.Services.Shortcuts;

/// <summary>Qué hacer con la acción que ya tenía la combinación.</summary>
public enum ConflictChoice
{
    /// <summary>Se queda con la combinación que deja la otra acción: se intercambian.</summary>
    Swap,

    /// <summary>La pierde y queda sin esa combinación.</summary>
    Take,
}
