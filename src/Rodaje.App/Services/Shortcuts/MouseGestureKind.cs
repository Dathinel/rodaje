namespace Rodaje.App.Services.Shortcuts;

/// <summary>Cómo se usa una acción del ratón.</summary>
public enum MouseGestureKind
{
    /// <summary>Con la rueda o su inclinación: cada paso empuja en un sentido.</summary>
    Axis,

    /// <summary>Mientras el botón está apretado y se mueve el ratón.</summary>
    Drag,
}
