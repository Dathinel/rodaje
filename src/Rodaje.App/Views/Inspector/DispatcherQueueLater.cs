using Microsoft.UI.Dispatching;
using Rodaje.App.Services;

namespace Rodaje.App.Views.Inspector;

/// <summary>La cola de la vista para <see cref="UiDeferral.RunAfterEvent"/>, con el registro de fallos de la app.</summary>
internal static class DispatcherQueueLater
{
    /// <summary>
    /// Hace <paramref name="action"/> después del evento en curso (o ya, si la cola no la acepta). Si lanza, deja informe
    /// (<c>CrashKind.DispatcherCallback</c>) y aviso en la ventana en vez de cerrar la app sin rastro.
    /// </summary>
    public static void RunAfterEvent(this DispatcherQueue queue, Action action)
    {
        ArgumentNullException.ThrowIfNull(queue);
        UiDeferral.RunAfterEvent(guarded => queue.TryEnqueue(() => guarded()), action, Program.OnDispatcherException);
    }
}
