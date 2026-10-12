using Rodaje.App.Services.Audio;

namespace Rodaje.App.Services;

/// <summary>Abrir la pestaña «Dinámica» con el compresor de un clip, una pista o la mezcla.</summary>
public sealed partial class EditorSession
{
    /// <summary>Se pidió el compresor de <see cref="DynamicsOwner"/> (menú de la pista, de los medidores o Filtros).</summary>
    public event EventHandler<DynamicsOwner>? DynamicsPanelRequested;

    public void ShowDynamics(DynamicsOwner owner)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(owner);
        DynamicsPanelRequested?.Invoke(this, owner);
    }
}
