using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Shell;
using Rodaje.Media.Proxies;

namespace Rodaje.App.Views.Inspector.Jobs;

/// <summary>Una fila de trabajo tal como se muestra.</summary>
internal sealed partial class JobItem(JobRow row)
{
    public string SourcePath { get; } = row.SourcePath;

    public string Title { get; } = row.Title;

    public string Status { get; } = row.Status;

    public double Percent { get; } = row.Progress * 100;

    public Visibility ActiveVisibility { get; } = row.IsActive ? Visibility.Visible : Visibility.Collapsed;

    public double ErrorOpacity { get; } = row.IsError ? 1 : 0;

    public double PlainOpacity { get; } = row.IsError ? 0 : 1;
}

/// <summary>Trabajos de proxies (del <see cref="MediaSourceResolver"/> del reproductor) con su progreso y cancelar.</summary>
/// <remarks>El reproductor crea los proxies en su hilo cuando abre la secuencia; hasta entonces se mira cada segundo.</remarks>
internal sealed partial class JobsPanel : UserControl, IEditorView
{
    private readonly ProxyJobList _jobs = new();
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private DispatcherQueueTimer? _lookup;
    private MediaSourceResolver? _resolver;

    public JobsPanel()
    {
        InitializeComponent();
    }

    /// <summary>Cambió cuántos trabajos hay en curso (para el título de la pestaña).</summary>
    public event EventHandler<int>? ActiveCountChanged;

    public ObservableCollection<JobItem> Items { get; } = [];

    public void Attach(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _lookup = _dispatcher.CreateTimer();
        _lookup.Interval = TimeSpan.FromSeconds(1);
        _lookup.Tick += (_, _) => Connect(services);
        _lookup.Start();
        Connect(services);
        Modules.Attach(services);
        Exports.Attach(services);
        Show();
        Bindings.Update();
    }

    private void Connect(EditorServices services)
    {
        if (services.Player.ProxyResolver is not { } resolver)
        {
            return;
        }

        _lookup?.Stop();
        _resolver = resolver;
        resolver.StatusChanged += OnStatusChanged;
        bool changed = false;
        foreach (ProxyStatus status in resolver.Snapshot())
        {
            changed |= _jobs.Apply(status);
        }

        if (changed)
        {
            Show();
        }
    }

    // Llega desde hilos de trabajo: se pasa al de la interfaz.
    private void OnStatusChanged(object? sender, ProxyStatusChangedEventArgs e)
    {
        ProxyStatus status = e.Status;
        _dispatcher.TryEnqueue(() =>
        {
            if (_jobs.Apply(status))
            {
                Show();
            }
        });
    }

    private void Show()
    {
        Items.Clear();
        foreach (JobRow row in _jobs.Rows)
        {
            Items.Add(new JobItem(row));
        }

        int active = _jobs.ActiveCount;
        int finished = _jobs.Rows.Count - active;
        EmptyText.Visibility = _jobs.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.IsEnabled = finished > 0;
        ProxySummary.Text = active switch
        {
            0 when _resolver is { CanGenerate: false } => "No se pueden generar: falta el ffmpeg.exe LGPL",
            0 => "Ninguno en curso",
            1 => "1 en curso",
            _ => $"{active} en curso",
        };
        ActiveCountChanged?.Invoke(this, active);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string source })
        {
            _resolver?.Cancel(source);
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (_jobs.ClearFinished())
        {
            Show();
        }
    }
}
