using Microsoft.UI.Dispatching;

namespace Rodaje.App.Views.Inspector;

/// <summary>Hacer algo después del evento en curso, con respaldo si la cola no lo acepta.</summary>
/// <remarks>
/// Lo usan los desplegables cuya elección edita y refresca el panel: rehacer un <c>ComboBox</c> dentro de su propio
/// <c>SelectionChanged</c> cierra la app desde WinUI (tarea 91), así que la edición va después del evento.
/// </remarks>
internal static class DispatcherQueueLater
{
    /// <summary>Hace <paramref name="action"/> después del evento en curso (o ya, si la cola de la vista no la acepta).</summary>
    public static void Later(this DispatcherQueue queue, DispatcherQueueHandler action)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(action);
        if (!queue.TryEnqueue(action))
        {
            action();
        }
    }
}
