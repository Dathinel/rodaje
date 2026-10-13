using Microsoft.UI.Dispatching;
using Rodaje.App.Services;

namespace Rodaje.App.Views.Inspector;

/// <summary>
/// La cola de la vista para <see cref="UiDeferral"/>, con el registro de fallos de la app. Es el único sitio de la app que
/// llama a <c>DispatcherQueue.TryEnqueue</c> (lo vigila <c>DispatcherQueueCallsTests</c>): un delegado puesto en la cola sin
/// envolver que lanza cierra la app con 0xC000027B y sin pila.
/// </summary>
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

    /// <summary>
    /// <c>TryEnqueue</c> con el registro de fallos: pone <paramref name="action"/> en la cola y devuelve si la aceptó; si no, no
    /// la hace (ver <see cref="UiDeferral.TryRunLater"/>). Para lo que llega de otro hilo.
    /// </summary>
    public static bool RunLater(this DispatcherQueue queue, Action action) => RunLater(queue, DispatcherQueuePriority.Normal, action);

    /// <inheritdoc cref="RunLater(DispatcherQueue, Action)"/>
    public static bool RunLater(this DispatcherQueue queue, DispatcherQueuePriority priority, Action action)
    {
        ArgumentNullException.ThrowIfNull(queue);
        return UiDeferral.TryRunLater(guarded => queue.TryEnqueue(priority, () => guarded()), action, Program.OnDispatcherException);
    }
}
