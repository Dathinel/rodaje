namespace Rodaje.TestServer;

/// <summary>
/// Señal que controla cuándo se corta una respuesta con <see cref="TestResponse.CutAfterBytes"/>. Sin ella el
/// servidor corta en cuanto manda los bytes; con ella los manda, marca <see cref="Reached"/> y espera a
/// <see cref="Release"/> para cortar.
/// </summary>
/// <remarks>
/// <para>
/// Hace falta para exigir cuántos bytes llegan antes del corte. En Windows, <c>HttpListenerResponse.Abort()</c>
/// (HTTP.sys) manda un RST y el sistema descarta lo que el cliente todavía no había leído del socket, así que un
/// corte inmediato tras 70 000 bytes puede dejar al cliente con 3911. La prueba lee los bytes que espera,
/// llama a <see cref="Release"/> y solo entonces el servidor corta: el conteo es exacto en Linux y en Windows.
/// </para>
/// <para>
/// Una señal vale para una sola respuesta. Si la misma <see cref="TestResponse"/> se repite (es la última de su
/// ruta), las siguientes peticiones encuentran la señal ya soltada y se cortan sin esperar. Al cerrar el servidor
/// se cortan también las que esperan.
/// </para>
/// </remarks>
public sealed class CutSignal
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Se completa cuando el servidor ya mandó los bytes previos al corte y espera para cortar.</summary>
    public Task Reached => _reached.Task;

    /// <summary>Si ya se soltó.</summary>
    public bool IsReleased => _released.Task.IsCompleted;

    /// <summary>Deja que el servidor corte la conexión.</summary>
    public void Release() => _released.TrySetResult();

    internal async Task WaitForReleaseAsync(CancellationToken cancellationToken)
    {
        _reached.TrySetResult();
        try
        {
            await _released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // El servidor se cierra: se corta igual.
        }
    }
}
