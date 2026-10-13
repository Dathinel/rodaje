namespace Rodaje.App.Services;

/// <summary>Hacer algo en el hilo de la interfaz después del evento en curso, sin que una excepción cierre la app sin rastro.</summary>
/// <remarks>
/// Los desplegables cuya elección edita y refresca el panel aplican la edición después de su <c>SelectionChanged</c>:
/// rehacer un <c>ComboBox</c> de WinUI dentro de su propio evento cierra la app (tarea 91). Un delegado de
/// <c>DispatcherQueue.TryEnqueue</c> no pasa por <c>GuardedSynchronizationContext</c> (que solo cubre <c>Post</c> y los
/// <c>await</c>): si lanza, WinUI cierra la app con un error «stowed» (0xC000027B) y sin pila. Por eso la acción va envuelta
/// y la excepción pasa por el mismo registro de fallos (<c>CrashKind.DispatcherCallback</c>). Sin WinUI, para probarlo: la
/// vista le pasa la cola con <c>DispatcherQueueLater.RunAfterEvent</c>.
/// </remarks>
public static class UiDeferral
{
    /// <summary>
    /// Pone <paramref name="action"/> en la cola con <paramref name="tryEnqueue"/> o, si la cola no la acepta, la hace ya.
    /// Si lanza, la excepción va a <paramref name="onException"/>: si devuelve <c>true</c> (informe y aviso) queda atendida;
    /// si no (o si el manejador falla), sigue su camino como antes.
    /// </summary>
    public static void RunAfterEvent(Func<Action, bool> tryEnqueue, Action action, Func<Exception, bool> onException)
    {
        ArgumentNullException.ThrowIfNull(tryEnqueue);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(onException);
        void Guarded() => Run(action, onException);
        if (!tryEnqueue(Guarded))
        {
            Guarded();
        }
    }

    /// <summary>Hace <paramref name="action"/>; si lanza, la excepción pasa por <paramref name="onException"/> (ver <see cref="RunAfterEvent"/>).</summary>
    public static void Run(Action action, Func<Exception, bool> onException)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(onException);
        try
        {
            action();
        }
#pragma warning disable CA1031 // Se atrapa solo si el manejador la atendió (informe y aviso); si no, sigue.
        catch (Exception ex) when (Handle(onException, ex))
#pragma warning restore CA1031
        {
        }
    }

    private static bool Handle(Func<Exception, bool> onException, Exception ex)
    {
        try
        {
            return onException(ex);
        }
#pragma warning disable CA1031 // Si el manejador falla, la excepción original sigue su camino.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }
}
