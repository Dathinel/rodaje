namespace Rodaje.App.Services;

public enum NotificationSeverity
{
    Informational,
    Success,
    Warning,
    Error,
}

/// <summary>Un aviso para la barra de mensajes de la ventana, en español y sin jerga.</summary>
public sealed record EditorNotification(NotificationSeverity Severity, string Title, string Message);
