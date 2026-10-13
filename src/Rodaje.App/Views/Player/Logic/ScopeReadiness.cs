using System.Runtime.CompilerServices;

namespace Rodaje.App.Views.Player;

/// <summary>
/// Lo que pasa cuando terminan de compilar los shaders de los monitores, sin WinUI. El motor lo registra al crear el
/// <c>ScopeRenderer</c>: si la compilación falla, los monitores quedan vacíos y el error se informa una vez (no una por cada
/// cuadro que pidió medir mientras compilaba, ni ninguna si ya había fallado antes de la primera medición); si termina bien,
/// se despierta al motor para medir el cuadro en pausa.
/// </summary>
public static class ScopeReadiness
{
    // Una continuación por tarea: si se vuelve a pedir, la misma (no se suelta mientras viva la tarea).
    private static readonly ConditionalWeakTable<Task, Task> s_watching = new();

    /// <summary>
    /// La continuación de <paramref name="ready"/>, con la tarea terminada o no: <paramref name="report"/> con el fallo
    /// (desenvuelto si es uno solo) o <paramref name="wake"/> si no falló. Pedida otra vez para la misma tarea, devuelve la
    /// primera continuación y no registra otra (las acciones nuevas se ignoran).
    /// </summary>
    public static Task Watch(Task ready, Action<Exception> report, Action wake)
    {
        ArgumentNullException.ThrowIfNull(ready);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(wake);
        return s_watching.GetValue(ready, r => r.ContinueWith(t =>
        {
            if (t.Exception is { } failure)
            {
                // Ready es una continuación de la compilación: el error de verdad viene envuelto dos veces.
                AggregateException flat = failure.Flatten();
                report(flat.InnerExceptions.Count == 1 ? flat.InnerExceptions[0] : flat);
                return;
            }

            wake();
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default));
    }
}
