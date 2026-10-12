namespace Rodaje.App.Services;

/// <summary>
/// Recorte y croma entre la pestaña Filtros y el visor: qué recorte se ajusta (el visor muestra la imagen entera con sus
/// asas) y el pedido del cuentagotas, que el visor atiende con un clic sobre la imagen.
/// </summary>
public sealed partial class EditorSession
{
    private string? _editedCropId;

    /// <summary>Cambió el recorte que se ajusta, o se dejó de ajustar.</summary>
    public event EventHandler? CropEditingChanged;

    /// <summary>Se pidió el cuentagotas del croma: (clip, efecto). Lo atiende el visor.</summary>
    public event EventHandler<(string ClipId, string EffectId)>? KeyPickRequested;

    /// <summary>El recorte (<c>rodaje.crop</c>) elegido en Filtros con la pestaña a la vista; nulo si ninguno.</summary>
    public string? EditedCropId => _editedCropId;

    public void SetCropEditing(string? cropId)
    {
        VerifyAccess();
        if (_editedCropId == cropId)
        {
            return;
        }

        _editedCropId = cropId;
        CropEditingChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RequestKeyPick(string clipId, string effectId)
    {
        VerifyAccess();
        KeyPickRequested?.Invoke(this, (clipId, effectId));
    }
}
