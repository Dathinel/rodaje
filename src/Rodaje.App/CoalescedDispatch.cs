using Microsoft.UI.Dispatching;
using Rodaje.App.Views.Inspector;

namespace Rodaje.App;

/// <summary>
/// Pasa a la interfaz un aviso que puede llegar muchas veces seguidas (cada cuadro, cada miniatura, cada archivo) sin
/// llenar la cola de DispatcherQueue: mientras hay uno pendiente, los siguientes no encolan otro. El aviso se marca como
/// atendido justo antes de correr <c>action</c>, así que lo que llega mientras corre encola uno nuevo y nada se pierde.
/// Encola con <see cref="DispatcherQueueLater.RunLater(DispatcherQueue, DispatcherQueuePriority, Action)"/>: si
/// <c>action</c> lanza, queda informe y aviso en vez de cerrar la app.
/// </summary>
internal sealed class CoalescedDispatch(DispatcherQueue queue, Action action, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal)
{
    private int _queued;

    /// <summary>Pide que <c>action</c> corra en la interfaz; desde cualquier hilo.</summary>
    public void Request()
    {
        if (Interlocked.Exchange(ref _queued, 1) != 0)
        {
            return;
        }

        if (!queue.RunLater(priority, Run))
        {
            Volatile.Write(ref _queued, 0);
        }
    }

    private void Run()
    {
        Volatile.Write(ref _queued, 0);
        action();
    }
}
