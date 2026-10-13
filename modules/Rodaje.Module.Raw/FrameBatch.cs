using System.Runtime.ExceptionServices;

namespace Rodaje.Module.Raw;

/// <summary>
/// Reparte los cuadros de una llamada a «revelar» entre <c>threads</c> hilos propios. Si un cuadro falla, cancela los
/// demás y espera a que terminen todos antes de devolver su error: los que siguieran revelando escribirían en los búferes
/// compartidos de una llamada ya respondida y, al soltar un semáforo ya desechado, lanzarían
/// <see cref="ObjectDisposedException"/> en tareas que nadie observa (#162).
/// </summary>
internal static class FrameBatch
{
    /// <summary>
    /// Costura para las pruebas: recibe la acción que arranca un hilo de revelado (<see cref="Thread.Start()"/>) y devuelve
    /// la que se ejecuta en su lugar, en el mismo sitio. Así la prueba ve en qué hilo corre de verdad el arranque (si se
    /// moviera al pool, su envoltorio correría allí) y puede hacerlo fallar. Es por hilo
    /// (<see cref="ThreadStaticAttribute"/>): cada prueba ve solo los arranques que hace su propio hilo, y un bucle de
    /// arranques llevado al pool no la encontraría.
    /// </summary>
    [ThreadStatic]
    internal static Func<Action, Action>? WrapWorkerStartForTests;

    /// <summary>
    /// Revela los cuadros 0 a <paramref name="frames"/> − 1 con <paramref name="develop"/>, hasta <paramref name="threads"/>
    /// a la vez, y llama a <paramref name="progress"/> con el número de cuadros hechos cada vez que termina uno. Relanza el
    /// primer fallo real (no la cancelación que este mismo fallo provoca en los demás).
    /// </summary>
    /// <remarks>
    /// Los hilos de revelado se arrancan aquí, en el hilo que llama y antes de la primera espera: en «revelar» es el hilo
    /// propio de la llamada (<c>ModuleHost</c>), nunca el pool. <see cref="Thread.Start()"/> espera a que el hilo nuevo
    /// arranque, y en la CI de Windows esa espera pasó de 10 s (tareas 235 y 260). Antes cada cuadro arrancaba su hilo
    /// desde un trabajo del pool: con <c>hilos = ProcessorCount</c>, tantos hilos del pool como su mínimo quedaban
    /// esperando arranques, el bucle de <c>ModuleHost</c> (continuaciones del pool) no leía el «ping» ni salían los
    /// avances, y el vigilante del editor mataba un módulo sano (CinemaDNG en la CI de Windows de #126 y de #246).
    /// </remarks>
    public static async Task RunAsync(int frames, int threads, Action<int, CancellationToken> develop,
        Func<int, CancellationToken, ValueTask> progress, CancellationToken cancellationToken)
    {
        using var failed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken ct = failed.Token;
        TaskCompletionSource[] results = [.. Enumerable.Range(0, frames).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))];
        int next = -1;

        // Cada hilo toma el siguiente cuadro sin revelar hasta que no quedan. Con la cancelación pedida, los que quedan se
        // dan por cancelados sin revelarlos.
        void Work()
        {
            for (int i = Interlocked.Increment(ref next); i < frames; i = Interlocked.Increment(ref next))
            {
                if (ct.IsCancellationRequested)
                {
                    results[i].TrySetCanceled(ct);
                    continue;
                }

                try
                {
                    develop(i, ct);
                    results[i].TrySetResult();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    results[i].TrySetCanceled(ct);
                }
#pragma warning disable CA1031 // El fallo de un cuadro se devuelve por su tarea y lo relanza RunAsync.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    results[i].TrySetException(ex);
                }
            }
        }

        ExceptionDispatchInfo? first = null;
        int workers = Math.Min(threads, frames);
        for (int w = 0; w < workers; w++)
        {
            try
            {
                Thread thread = new(Work) { IsBackground = true, Name = "Rodaje: revelado" };
                Action start = thread.Start;
                start = WrapWorkerStartForTests?.Invoke(start) ?? start;
                start();
            }
            catch (Exception ex) when (w > 0)
            {
                // Sin otro hilo no se puede seguir, pero los que ya arrancaron revelan (o cancelan) todos los cuadros:
                // se espera a que terminen, como tras el fallo de un cuadro.
                first = ExceptionDispatchInfo.Capture(ex);
                await failed.CancelAsync().ConfigureAwait(false);
                break;
            }
        }

        // Se espera a todos los cuadros, también después de un fallo: así se observan sus excepciones y ningún hilo sigue
        // escribiendo en los búferes de una llamada ya respondida.
        List<Task> pending = [.. results.Select(r => r.Task)];
        int done = 0;
        while (pending.Count > 0)
        {
            Task finished = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(finished);
            try
            {
                await finished.ConfigureAwait(false);
                if (first is null)
                {
                    await progress(++done, ct).ConfigureAwait(false);
                }
            }
#pragma warning disable CA1031 // El primer fallo se relanza tal cual al final; los demás son la cancelación que provoca.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                if (first is null)
                {
                    first = ExceptionDispatchInfo.Capture(ex);
                    await failed.CancelAsync().ConfigureAwait(false);
                }
            }
        }

        first?.Throw();
    }
}
