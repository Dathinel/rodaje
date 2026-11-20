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

/// <summary>
/// Un aviso para la barra de mensajes de la ventana, en español y sin jerga, con uno o dos botones opcionales.
/// <paramref name="IsStale"/> se reevalúa en el hilo de la interfaz tras cada edición y al abrir otro proyecto: cuando
/// da cierto el aviso ya no vale, se cierra, y si vuelve a dar falso se repone (por ejemplo, al rehacer lo deshecho).
/// </summary>
public sealed record EditorNotification(NotificationSeverity Severity, string Title, string Message, NotificationAction? Action = null, NotificationAction? SecondaryAction = null, Func<bool>? IsStale = null);
