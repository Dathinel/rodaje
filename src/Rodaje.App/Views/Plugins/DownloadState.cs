using System.ComponentModel;
using Microsoft.UI.Xaml;
using Rodaje.App.Services.Packages;

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
    public void Report(double fraction) => Report(new InstallProgress(fraction, ""));

    /// <summary>Avance de la etapa en curso (el paquete o cada modelo).</summary>
    public void Report(InstallProgress progress)
    {
        Percent = Math.Clamp(progress.Fraction, 0, 1) * 100;
        string stage = progress.Stage.Length > 0 ? progress.Stage + " · " : "";
        Text = progress.Fraction >= 1 && progress.Stage is "" or "Paquete" or "Módulo" ? "Instalando…" : $"{stage}{Percent:0} %";
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
