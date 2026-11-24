namespace Rodaje.App.Services.Shortcuts;

public enum CaptureStatus
{
    /// <summary>Solo hay modificadores apretados: falta la tecla.</summary>
    Waiting,

    /// <summary>Esc sin modificadores: se deja todo como estaba.</summary>
    Cancelled,

    /// <summary>La combinación está reservada; se sigue esperando otra.</summary>
    Rejected,

    Accepted,
}
