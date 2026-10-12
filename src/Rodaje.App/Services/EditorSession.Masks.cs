namespace Rodaje.App.Services;

/// <summary>
/// Las máscaras entre la pestaña «Máscaras» y el visor: cuál se edita (el visor le dibuja las asas) y el pedido de dibujar un
/// trazo libre, que el visor recoge con el ratón y vuelve en una máscara nueva.
/// </summary>
public sealed partial class EditorSession
{
    private string? _editedMaskId;
    private bool _maskEditing;

    /// <summary>Cambió la máscara que se edita, o se abrió o cerró su pestaña.</summary>
    public event EventHandler? MaskEditingChanged;

    /// <summary>Se pidió dibujar un trazo libre sobre el clip; lo atiende el visor.</summary>
    public event EventHandler<string>? MaskDrawRequested;

    /// <summary>Se pidió la pestaña «Máscaras» (menú del clip).</summary>
    public event EventHandler? MaskPanelRequested;

    /// <summary>La pestaña de máscaras está a la vista: el visor muestra las asas de <see cref="EditedMaskId"/>.</summary>
    public bool IsMaskEditing => _maskEditing;

    /// <summary>La máscara (efecto <c>rodaje.mask</c>) elegida en la pestaña; nula si ninguna.</summary>
    public string? EditedMaskId => _editedMaskId;

    public void SetMaskEditing(bool active, string? maskId)
    {
        VerifyAccess();
        if (_maskEditing == active && _editedMaskId == maskId)
        {
            return;
        }

        _maskEditing = active;
        _editedMaskId = maskId;
        MaskEditingChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RequestMaskDrawing(string clipId)
    {
        VerifyAccess();
        MaskDrawRequested?.Invoke(this, clipId);
    }

    public void ShowMaskPanel()
    {
        VerifyAccess();
        MaskPanelRequested?.Invoke(this, EventArgs.Empty);
    }
}
