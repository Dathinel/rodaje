using Rodaje.App.Services.Tracking;

namespace Rodaje.App.Services;

/// <summary>
/// El seguimiento entre la pestaña «Seguimiento» y el visor: qué seguimiento se edita (el visor muestra sus puntos con
/// el patrón y la zona de búsqueda, y deja moverlos) y el punto elegido.
/// </summary>
public sealed partial class EditorSession
{
    private string? _editedTrackId;
    private bool _trackEditing;
    private int _trackPoint = -1;
    private TrackUse? _requestedTrackUse;
    private string? _requestedTrackTargetId;

    /// <summary>Cambió el seguimiento que se edita, el punto elegido, o se abrió o cerró la pestaña.</summary>
    public event EventHandler? TrackEditingChanged;

    /// <summary>Se pidió la pestaña «Seguimiento» (menú del clip).</summary>
    public event EventHandler? TrackPanelRequested;

    /// <summary>La pestaña está a la vista: el visor dibuja los puntos de <see cref="EditedTrackId"/>.</summary>
    public bool IsTrackEditing => _trackEditing;

    /// <summary>El seguimiento (efecto <c>rodaje.track</c>) elegido en la pestaña; nulo si ninguno.</summary>
    public string? EditedTrackId => _editedTrackId;

    /// <summary>Uso y destino solicitados al abrir Seguimiento desde una máscara.</summary>
    public TrackUse? RequestedTrackUse => _requestedTrackUse;

    public string? RequestedTrackTargetId => _requestedTrackTargetId;

    /// <summary>El punto elegido en el visor (−1 si ninguno).</summary>
    public int SelectedTrackPoint => _trackPoint;

    public void SetTrackEditing(bool active, string? trackId)
    {
        VerifyAccess();
        if (_trackEditing == active && _editedTrackId == trackId)
        {
            return;
        }

        if (_editedTrackId != trackId)
        {
            _trackPoint = -1;
        }

        _trackEditing = active;
        _editedTrackId = trackId;
        TrackEditingChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectTrackPoint(int point)
    {
        VerifyAccess();
        if (_trackPoint != point)
        {
            _trackPoint = point;
            TrackEditingChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ShowTrackPanel()
    {
        VerifyAccess();
        TrackPanelRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ShowTrackPanelForMask(string maskId)
    {
        VerifyAccess();
        ArgumentException.ThrowIfNullOrWhiteSpace(maskId);
        _requestedTrackUse = TrackUse.Mask;
        _requestedTrackTargetId = maskId;
        TrackPanelRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ClearTrackPanelRequest()
    {
        VerifyAccess();
        _requestedTrackUse = null;
        _requestedTrackTargetId = null;
    }
}
