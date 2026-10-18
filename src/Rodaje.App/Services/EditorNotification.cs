namespace Rodaje.App.Services;

public enum NotificationSeverity
{
    Informational,
    Success,
    Warning,
    Error,
}

/// <summary>El botón de un aviso («Deshacer», «Abrir») y lo que hace; el aviso se cierra antes de ejecutarlo.</summary>
public sealed record NotificationAction(string Text, Action Run);

/// <summary>Un aviso para la barra de mensajes de la ventana, en español y sin jerga, con uno o dos botones opcionales.</summary>
public sealed record EditorNotification(NotificationSeverity Severity, string Title, string Message, NotificationAction? Action = null, NotificationAction? SecondaryAction = null);
