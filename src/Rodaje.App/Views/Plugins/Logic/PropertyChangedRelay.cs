using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Rodaje.App.Views.Plugins;

/// <summary>
/// Pasa los avisos de <see cref="INotifyPropertyChanged"/> de un objeto de larga vida (la descarga de un complemento, que
/// vive en la página) a uno de corta vida (la fila, que se rehace con cada cambio de la lista), con una suscripción que se
/// suelta en <see cref="Dispose"/>. Así la fila no depende de que <c>x:Bind</c> suelte la suya al descartar la plantilla:
/// sin soltarla, cada vuelta de la lista dejaba otro manejador en la descarga, que retenía la fila y su árbol.
/// </summary>
internal sealed partial class PropertyChangedRelay : IDisposable
{
    private readonly PropertyChangedEventHandler _handler;
    private INotifyPropertyChanged? _source;

    /// <param name="source">El objeto de larga vida.</param>
    /// <param name="forward">Recibe el nombre de cada propiedad que cambió (nulo o vacío: todas).</param>
    public PropertyChangedRelay(INotifyPropertyChanged source, Action<string?> forward)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(forward);
        _source = source;
        _handler = (_, e) => forward(e.PropertyName);
        source.PropertyChanged += _handler;
    }

    /// <summary>Suelta la suscripción; después no llega ningún aviso. Se puede llamar más de una vez.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _source, null) is { } source)
        {
            source.PropertyChanged -= _handler;
        }
    }

    /// <summary>Suelta las filas de <paramref name="rows"/> y la vacía (antes de rehacerla).</summary>
    public static void Release<T>(ObservableCollection<T> rows)
        where T : IDisposable
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach (T row in rows)
        {
            row.Dispose();
        }

        rows.Clear();
    }
}
