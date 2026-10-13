using System.Diagnostics;

namespace Rodaje.App.Services.Crashes;

/// <summary>
/// Lanza una tarea sin esperarla y sin perder su excepción. Un <c>_ = AlgoAsync()</c> deja la excepción en una tarea que
/// nadie mira (a lo sumo, un informe cuando el recolector la encuentra, sin aviso ni momento) y un manejador
/// <c>async void</c> sin <c>try/catch</c> depende de que el contexto del hilo de la interfaz la atrape. Con
/// <see cref="Run(Task)"/>, una excepción (salvo una cancelación) llega a <see cref="Reporter"/>: informe «recuperado» en
/// <c>fallos\</c> y aviso en la ventana (lo instala <c>Program</c>). Sin WinUI, para poder probarlo.
/// </summary>
public static class UiTask
{
    private static Action<Exception, bool>? s_reporter;

    /// <summary>
    /// Quién informa una excepción (la excepción; si además se avisa en la ventana). Se llama desde cualquier hilo. Sin él
    /// (las pruebas, o antes de arrancar), la excepción solo queda en el registro de la sesión.
    /// </summary>
    public static Action<Exception, bool>? Reporter
    {
        get => Volatile.Read(ref s_reporter);
        set => Volatile.Write(ref s_reporter, value);
    }

    /// <summary>
    /// Observa <paramref name="task"/> sin bloquear a nadie: si falla, informa la excepción y avisa. Nula (de un
    /// <c>x?.AlgoAsync()</c>), no hace nada. Con una continuación y no con <c>async void</c>: no depende del contexto del
    /// hilo que la lanza ni lo retiene (xUnit, por ejemplo, espera a los <c>async void</c> pendientes de una prueba).
    /// </summary>
    public static void Run(Task? task)
    {
        if (task is null)
        {
            return;
        }

        if (task.IsCompleted)
        {
            Observe(task);
            return;
        }

        _ = task.ContinueWith(static t => Observe(t), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Como <see cref="Run(Task)"/>, para una operación que se arranca aquí (una lambda <c>async</c>).</summary>
    public static void Run(Func<Task> start)
    {
        Task task;
        try
        {
            ArgumentNullException.ThrowIfNull(start);
            task = start();
        }
#pragma warning disable CA1031 // Una excepción antes de la primera espera sigue el mismo camino que las de después.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            task = Task.FromException(ex);
        }

        Run(task);
    }

    // Leer Exception la da por observada. Cancelar no es un error (igual que con «_ = …», que la ignoraba).
    private static void Observe(Task task)
    {
        if (!task.IsFaulted || task.Exception is not { } aggregate)
        {
            return;
        }

        Exception exception = aggregate.InnerExceptions.Count == 1 ? aggregate.InnerExceptions[0] : aggregate;
        if (exception is not OperationCanceledException)
        {
            Report(exception);
        }
    }

    /// <summary>
    /// Informa una excepción atrapada (con <paramref name="notify"/> en <c>false</c> cuando quien la atrapó ya la explicó
    /// en la ventana). <c>true</c> si alguien la informó; nunca lanza.
    /// </summary>
    public static bool Report(Exception exception, bool notify = true)
    {
        if (exception is null)
        {
            return false;
        }

        if (Reporter is not { } reporter)
        {
            Trace.WriteLine("Error sin informe (no hay quien informe): " + exception);
            return false;
        }

        try
        {
            reporter(exception, notify);
            return true;
        }
#pragma warning disable CA1031 // Informar no puede causar otro error.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }
}
