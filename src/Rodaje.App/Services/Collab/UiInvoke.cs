namespace Rodaje.App.Services.Collab;

/// <summary>Hacer algo en el hilo de la interfaz y esperar su resultado desde otro hilo.</summary>
public static class UiInvoke
{
    public static Task<T> Run<T>(Action<Action> post, Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(work);
        TaskCompletionSource<T> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            post(() =>
            {
                try
                {
                    done.TrySetResult(work());
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    done.TrySetException(ex);
                }
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            done.TrySetException(ex);
        }

        return done.Task;
    }
}
