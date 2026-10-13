namespace Rodaje.App.Tests.Support;

/// <summary>
/// Corre el cuerpo síncrono de una prueba en un hilo propio y devuelve una Task que xUnit espera sin ocupar un hilo del
/// pool. Para las pruebas que esperan en bloque (sombreadores con <c>Wait</c>, títulos con <c>ManualResetEventSlim</c>,
/// procesos con <c>WaitForExit</c>) y no se pueden pasar a <c>await</c> sin cambiar lo que comprueban: por ejemplo, las
/// de la GPU, que usan el dispositivo siempre desde el mismo hilo.
/// </summary>
internal static class OwnThread
{
    public static Task Run(Action body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                body();
                finished.TrySetResult();
            }
            catch (Exception ex)
            {
                finished.TrySetException(ex);
            }
        })
        { IsBackground = true, Name = "Hilo propio de prueba" };
        thread.Start();
        return finished.Task;
    }
}
