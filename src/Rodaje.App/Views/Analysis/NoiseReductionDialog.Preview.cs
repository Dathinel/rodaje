namespace Rodaje.App.Views.Analysis;

/// <summary>
/// Algo que un diálogo carga en segundo plano y que se suelta al cerrarlo, aunque termine de cargar después: lo que llega
/// tarde se suelta enseguida en vez de quedar sin dueño. La escucha A/B de «Reducir ruido» lo usa para su vista previa.
/// </summary>
internal sealed class DialogOwned<T>
    where T : class, IDisposable
{
    private readonly Lock _gate = new();
    private T? _value;
    private bool _closed;

    /// <summary>Lo cargado, o nulo si todavía no llegó o el diálogo se cerró.</summary>
    public T? Value
    {
        get
        {
            lock (_gate)
            {
                return _value;
            }
        }
    }

    public bool IsClosed
    {
        get
        {
            lock (_gate)
            {
                return _closed;
            }
        }
    }

    /// <summary>Guarda lo cargado (soltando lo anterior); si el diálogo ya se cerró, lo suelta y devuelve <c>false</c>.</summary>
    public bool TrySet(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        T? previous;
        bool kept;
        lock (_gate)
        {
            kept = !_closed;
            if (kept)
            {
                previous = ReferenceEquals(_value, value) ? null : _value;
                _value = value;
            }
            else
            {
                previous = value;
            }
        }

        previous?.Dispose();
        return kept;
    }

    /// <summary>El diálogo se cerró: suelta lo cargado y lo que llegue después.</summary>
    public void Close()
    {
        T? value;
        lock (_gate)
        {
            _closed = true;
            value = _value;
            _value = null;
        }

        value?.Dispose();
    }
}
