using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Inspector;

namespace Rodaje.App.Views.Inspector;

/// <summary>La cola de la vista para <see cref="UiDeferral.RunAfterEvent"/>, con el registro de fallos de la app.</summary>
internal static class DispatcherQueueLater
{
    /// <summary>
    /// Hace <paramref name="action"/> después del evento en curso (si la cola no la acepta, al cerrar, se descarta). Si lanza,
    /// deja informe (<c>CrashKind.DispatcherCallback</c>) y aviso en la ventana en vez de cerrar la app sin rastro.
    /// </summary>
    public static void RunAfterEvent(this DispatcherQueue queue, Action action)
    {
        ArgumentNullException.ThrowIfNull(queue);
        _ = UiDeferral.RunAfterEvent(TryEnqueue(queue), action, Program.OnDispatcherException);
    }

    /// <summary>
    /// El <c>SelectionChanged</c> de un desplegable que edita y refresca el panel: <see cref="ComboChoices.Choose{T}"/> con
    /// esta cola (anota en el evento; aplica y refresca después).
    /// </summary>
    public static void Choose<T>(this DispatcherQueue queue, ChoiceList<T> shown, int selectedIndex, bool updating, Func<T, Action?> note)
    {
        ArgumentNullException.ThrowIfNull(queue);
        _ = ComboChoices.Choose(shown, selectedIndex, updating, note, TryEnqueue(queue), Program.OnDispatcherException);
    }

    /// <summary>
    /// Repite <paramref name="refresh"/> después de que se cierre la lista de <paramref name="box"/> si un refresco no la tocó
    /// por estar abierta (<see cref="ClosedListRetry"/>).
    /// </summary>
    public static void RetryWhenClosed(this ComboBox box, ClosedListRetry retry, Action refresh)
    {
        ArgumentNullException.ThrowIfNull(box);
        ArgumentNullException.ThrowIfNull(retry);
        box.DropDownClosed += (_, _) => retry.Closed(TryEnqueue(box.DispatcherQueue), refresh, Program.OnDispatcherException);
    }

    private static Func<Action, bool> TryEnqueue(DispatcherQueue queue) => guarded => queue.TryEnqueue(() => guarded());
}
