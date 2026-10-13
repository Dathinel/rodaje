namespace Rodaje.App.Services.LongActions;

/// <summary>
/// Lo que un diálogo deja corriendo mientras está abierto (leer las voces, generar una voz, traducir una pista), sin WinUI.
/// Al cerrarlo, <see cref="CloseAsync"/> cancela lo que siga y lo espera con «Cancelando…» en la barra de avisos, como
/// Quitar fondo o Reducir ruido: un módulo puede tardar hasta 10 s en confirmar la cancelación, y sin esto el diálogo se
/// cerraba sin decir nada mientras el trabajo seguía en Trabajos.
/// </summary>
public sealed partial class DialogWork : IDisposable
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly Lock _lock = new();
    private readonly List<Task> _pending = [];

    /// <summary>El token para lo que lanza el diálogo; se cancela en <see cref="CloseAsync"/>.</summary>
    public CancellationToken Token => _cancel.Token;

    /// <summary>Anota <paramref name="work"/> para esperarla al cerrar; la devuelve tal cual.</summary>
    public T Track<T>(T work)
        where T : Task
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_lock)
        {
            _pending.RemoveAll(static t => t.IsCompleted);
            _pending.Add(work);
        }

        return work;
    }

    /// <summary>
    /// Al cerrar el diálogo: cancela lo anotado y, si algo sigue en marcha, lo espera con «Cancelando…» en la fila de
    /// <paramref name="scope"/> (la de la acción larga que abrió el diálogo) o, sin ella, en una propia con
    /// <paramref name="title"/>. No lanza por lo anotado: sus fallos los informa quien lo lanzó (<c>UiTask.Run</c>).
    /// </summary>
    public async Task CloseAsync(LongActionService actions, string title, LongActionScope? scope = null)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(title);
        Task[] pending;
        lock (_lock)
        {
            pending = [.. _pending.Where(static t => !t.IsCompleted)];
        }

        if (pending.Length == 0)
        {
            await _cancel.CancelAsync().ConfigureAwait(true);
            return;
        }

        // «Cancelando…» se muestra ya, antes de pedir la cancelación (CancelAsync vuelve en otra vuelta del hilo).
        Task settled = Task.WhenAll(pending).ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        Task waiting = actions.ShowCancellingAsync(title, settled, scope);
        await _cancel.CancelAsync().ConfigureAwait(true);
        await waiting.ConfigureAwait(true);
    }

    public void Dispose() => _cancel.Dispose();
}
