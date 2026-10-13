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
    /// Revela los cuadros 0 a <paramref name="frames"/> − 1 con <paramref name="develop"/>, hasta <paramref name="threads"/>
    /// a la vez, y llama a <paramref name="progress"/> con el número de cuadros hechos cada vez que termina uno. Relanza el
    /// primer fallo real (no la cancelación que este mismo fallo provoca en los demás).
    /// </summary>
    public static async Task RunAsync(int frames, int threads, Action<int, CancellationToken> develop,
        Func<int, CancellationToken, ValueTask> progress, CancellationToken cancellationToken)
    {
        using var failed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken ct = failed.Token;
        using var gate = new SemaphoreSlim(Math.Min(threads, frames));
        List<Task> pending = [.. Enumerable.Range(0, frames).Select(i => Task.Run(async () =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // LibRaw bloquea el hilo segundos por cuadro: en un hilo propio y no en el pool. Con hasta ProcessorCount
                // cuadros a la vez ocupaban todos los hilos mínimos del pool y, con la CPU llena, el pool no sumaba otros:
                // el bucle de ModuleHost no leía el «ping» del editor (ni salían los avances) hasta que terminaba un cuadro,
                // y el vigilante mataba un módulo sano a mitad de trabajo (CinemaDNG en la CI de Windows).
                await Task.Factory.StartNew(() => develop(i, ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }, ct))];

        // Se espera a todas las tareas, también después de un fallo: así se observan sus excepciones y ninguna suelta el
        // semáforo después de desecharlo.
        ExceptionDispatchInfo? first = null;
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
