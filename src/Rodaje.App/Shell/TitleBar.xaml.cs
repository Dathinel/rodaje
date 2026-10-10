using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Views.Inspector;
using Rodaje.Engine.Timeline;

namespace Rodaje.App.Shell;

/// <summary>Barra de título: los menús lanzan acciones por su identificador (Tag), así muestran la tecla vigente.</summary>
internal sealed partial class TitleBar : UserControl
{
    private EditorServices? _services;

    public TitleBar()
    {
        InitializeComponent();
    }

    /// <summary>Se pidió una pestaña del panel derecho desde el menú Ver.</summary>
    public event EventHandler<InspectorView.Tab>? InspectorTabRequested;

    /// <summary>Se eligió Salir.</summary>
    public event EventHandler? ExitRequested;

    public EditorSession? Session { get; private set; }

    public void Attach(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        Session = services.Session;
        Session.TransportChanged += (_, _) => UpdateStats();
        Session.HistoryChanged += (_, _) => UpdateUndoText();
        services.ShortcutRegistry.Changed += (_, _) => UpdateAccelerators();
        UpdateAccelerators();
        UpdateUndoText();
        UpdateChecks();
        UpdateStats();
        Bindings.Update();
    }

    private void UpdateStats()
    {
        if (_services is null)
        {
            return;
        }

        string gpu = _services.Player.DeviceDescription;
        long dropped = _services.Session.Transport.FramesDropped;
        string frames = dropped == 0 ? "sin cuadros perdidos" : string.Create(CultureInfo.InvariantCulture, $"{dropped} cuadros perdidos");
        StatsText.Text = gpu.Length == 0 ? "" : $"{gpu} · {frames}";
    }

    private void UpdateUndoText()
    {
        if (Session is null)
        {
            return;
        }

        UndoItem.Text = Session.UndoDescription is { } undo ? $"Deshacer «{undo}»" : "Deshacer";
        RedoItem.Text = Session.RedoDescription is { } redo ? $"Rehacer «{redo}»" : "Rehacer";
        UndoItem.IsEnabled = Session.CanUndo;
        RedoItem.IsEnabled = Session.CanRedo;
    }

    private void UpdateAccelerators()
    {
        if (_services is null)
        {
            return;
        }

        foreach (MenuBarItem top in Menu.Items)
        {
            foreach (MenuFlyoutItemBase item in top.Items)
            {
                if (item is MenuFlyoutItem { Tag: string id } entry && !id.Contains(':', StringComparison.Ordinal) && _services.ShortcutRegistry.Find(id) is not null)
                {
                    entry.KeyboardAcceleratorTextOverride = _services.ShortcutRegistry.GetKeyText(id);
                }
            }
        }

        UpdateChecks();
    }

    private void UpdateChecks()
    {
        if (_services is null)
        {
            return;
        }

        ShortcutPreset preset = _services.ShortcutRegistry.Preset;
        PresetRodaje.IsChecked = preset == ShortcutPreset.Rodaje;
        PresetCapCut.IsChecked = preset == ShortcutPreset.CapCut;
        PresetVegas.IsChecked = preset == ShortcutPreset.Vegas;
        ProxyUsage proxies = _services.Settings.Current.ProxyUsage;
        ProxyAuto.IsChecked = proxies == ProxyUsage.Auto;
        ProxyAlways.IsChecked = proxies == ProxyUsage.Always;
        ProxyNever.IsChecked = proxies == ProxyUsage.Never;
    }

    private void OnActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id } && _services is not null && !_services.Shortcuts.Invoke(id))
        {
            _services.Session.Notify(NotificationSeverity.Informational, "Todavía no", "Esta acción llega con su vista.");
        }
    }

    private void OnInspectorClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag.AsSpan("inspector:".Length), out InspectorView.Tab tab))
        {
            InspectorTabRequested?.Invoke(this, tab);
        }
    }

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && _services is not null && Enum.TryParse(tag, out ShortcutPreset preset))
        {
            _services.ShortcutRegistry.Preset = preset;
            _services.Settings.Current.ShortcutPreset = preset;
        }
    }

    private void OnProxyClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && _services is not null && Enum.TryParse(tag, out ProxyUsage usage))
        {
            _services.Settings.Current.ProxyUsage = usage;
            _services.Player.SetProxyUsage(usage);
        }
    }

    private void OnExportClick(object sender, RoutedEventArgs e) => _services?.Shortcuts.Invoke(ActionIds.Export);

    private void OnExitClick(object sender, RoutedEventArgs e) => ExitRequested?.Invoke(this, EventArgs.Empty);
}
