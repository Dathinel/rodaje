using System.ComponentModel;
using Microsoft.UI.Xaml;

namespace Rodaje.App.Views.Plugins;

/// <summary>
/// La descarga en curso de un complemento: avance y cancelar. Vive en la página, no en la fila, porque las filas se
/// rehacen cada vez que cambia la lista.
/// </summary>
internal sealed partial class DownloadState : INotifyPropertyChanged, IDisposable
{
    public void Dispose() => _cancel?.Dispose();

    private double _percent;
    private string _text = "";
    private bool _active;
    private CancellationTokenSource? _cancel;

    public event PropertyChangedEventHandler? PropertyChanged;

    public double Percent
    {
        get => _percent;
        private set => Set(ref _percent, value, nameof(Percent));
    }

    public string Text
    {
        get => _text;
        private set => Set(ref _text, value, nameof(Text));
    }

    public Visibility ActiveVisibility => _active ? Visibility.Visible : Visibility.Collapsed;

    public Visibility IdleVisibility => _active ? Visibility.Collapsed : Visibility.Visible;

    public bool IsActive => _active;

    public CancellationToken Start()
    {
        _cancel?.Dispose();
        _cancel = new CancellationTokenSource();
        _active = true;
        Report(0);
        Raise(nameof(ActiveVisibility));
        Raise(nameof(IdleVisibility));
        return _cancel.Token;
    }

    /// <summary>Avance de 0 a 1; llega por <see cref="Progress{T}"/>, ya en el hilo de la interfaz.</summary>
    public void Report(double fraction)
    {
        Percent = Math.Clamp(fraction, 0, 1) * 100;
        Text = fraction >= 1 ? "Instalando…" : $"{Percent:0} %";
    }

    public void Cancel() => _cancel?.Cancel();

    public void Finish()
    {
        _active = false;
        _cancel?.Dispose();
        _cancel = null;
        Raise(nameof(ActiveVisibility));
        Raise(nameof(IdleVisibility));
    }

    private void Set<T>(ref T field, T value, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            Raise(name);
        }
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
