namespace Rodaje.App.Services.Crashes;

/// <summary>
/// Envuelve el contexto del hilo de la interfaz (el de DispatcherQueue) para que una excepción en un callback (la
/// continuación de un <c>await</c>, un <c>async void</c>) pase por <paramref name="onException"/> antes de llegar a
/// DispatcherQueue, que la convertiría en un error «stowed» de WinRT y cerraría la app sin pasar por
/// <c>Application.UnhandledException</c>. Si el manejador devuelve <c>true</c>, la excepción queda atendida; si no,
/// sigue su camino como antes.
/// </summary>
public sealed class GuardedSynchronizationContext(SynchronizationContext inner, Func<Exception, bool> onException) : SynchronizationContext
{
    private readonly SynchronizationContext _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly Func<Exception, bool> _onException = onException ?? throw new ArgumentNullException(nameof(onException));

    public override void Post(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        _inner.Post(s => Invoke(d, s), state);
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        _inner.Send(s => Invoke(d, s), state);
    }

    public override SynchronizationContext CreateCopy() => new GuardedSynchronizationContext(_inner.CreateCopy(), _onException);

    private void Invoke(SendOrPostCallback d, object? state)
    {
        SynchronizationContext? previous = Current;
        SetSynchronizationContext(this);
        try
        {
            d(state);
        }
#pragma warning disable CA1031 // Se atrapa solo si el manejador la atendió (informe y aviso); si no, sigue.
        catch (Exception ex) when (Handle(ex))
#pragma warning restore CA1031
        {
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }

    private bool Handle(Exception ex)
    {
        try
        {
            return _onException(ex);
        }
#pragma warning disable CA1031 // Si el manejador falla, la excepción original sigue su camino.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }
}
