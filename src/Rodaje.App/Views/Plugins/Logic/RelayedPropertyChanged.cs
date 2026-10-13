using System.ComponentModel;

namespace Rodaje.App.Views.Plugins;

/// <summary>
/// El evento <see cref="INotifyPropertyChanged.PropertyChanged"/> de una fila de corta vida que repite, con un prefijo, los
/// avisos de un objeto de larga vida (la descarga, que vive en la página). La suscripción a la descarga se hace con el primer
/// manejador (<see cref="Add"/>) y se suelta en <see cref="Dispose"/>, que la página llama al rehacer la lista. Es lo que hace
/// <c>PluginRow</c>; aquí, sin WinUI, para probarlo.
/// </summary>
/// <param name="source">El objeto de larga vida.</param>
/// <param name="prefix">Lo que se antepone al nombre de cada propiedad (nulo o vacío se pasa tal cual: todas).</param>
internal sealed partial class RelayedPropertyChanged(INotifyPropertyChanged source, string prefix) : IDisposable
{
    private PropertyChangedRelay? _relay;
    private PropertyChangedEventHandler? _handlers;

    /// <summary>El <c>add</c> del evento de la fila: el primero se suscribe a la descarga.</summary>
    /// <param name="sender">La fila, que es quien manda los avisos.</param>
    public void Add(object sender, PropertyChangedEventHandler? value)
    {
        _relay ??= new PropertyChangedRelay(source, name => _handlers?.Invoke(sender, new PropertyChangedEventArgs(
            string.IsNullOrEmpty(name) ? name : prefix + name)));
        _handlers += value;
    }

    /// <summary>El <c>remove</c> del evento de la fila.</summary>
    public void Remove(PropertyChangedEventHandler? value) => _handlers -= value;

    /// <summary>Suelta la suscripción a la descarga y los manejadores; después no llega ningún aviso.</summary>
    public void Dispose()
    {
        _relay?.Dispose();
        _handlers = null;
    }
}
