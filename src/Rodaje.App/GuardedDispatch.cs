using Microsoft.UI.Dispatching;
using Rodaje.App.Services.Crashes;

namespace Rodaje.App;

/// <summary>
/// <c>DispatcherQueue.TryEnqueue</c> con red. Un callback de DispatcherQueue no pasa por el contexto del hilo de la interfaz
/// (<see cref="GuardedSynchronizationContext"/>) ni por <c>Application.UnhandledException</c>: si lanza, DispatcherQueue lo
/// convierte en un error «stowed» de WinRT que cierra la app. Con <see cref="TryEnqueueGuarded(DispatcherQueue, DispatcherQueueHandler)"/>
/// la excepción se informa (<see cref="UiTask.Report"/>: informe «recuperado» y aviso) y la app sigue; si todavía no hay
/// quien informe, sigue su camino como antes.
/// </summary>
internal static class GuardedDispatch
{
    public static bool TryEnqueueGuarded(this DispatcherQueue queue, DispatcherQueueHandler callback) =>
        TryEnqueueGuarded(queue, DispatcherQueuePriority.Normal, callback);

    public static bool TryEnqueueGuarded(this DispatcherQueue queue, DispatcherQueuePriority priority, DispatcherQueueHandler callback)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(callback);
        return queue.TryEnqueue(priority, () =>
        {
            try
            {
                callback();
            }
#pragma warning disable CA1031 // Se atrapa solo si quedó informada; si no, sigue su camino.
            catch (Exception ex) when (UiTask.Report(ex))
#pragma warning restore CA1031
            {
            }
        });
    }
}

/// <summary>
/// Pasa a la interfaz un aviso que puede llegar muchas veces seguidas (cada cuadro, cada miniatura, cada archivo) sin
/// llenar la cola de DispatcherQueue: mientras hay uno pendiente, los siguientes no encolan otro. El aviso se marca como
/// atendido justo antes de correr <c>action</c>, así que lo que llega mientras corre encola uno nuevo y nada se pierde.
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

        if (!queue.TryEnqueueGuarded(priority, Run))
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
