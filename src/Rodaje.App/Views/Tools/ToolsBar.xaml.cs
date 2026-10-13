using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.LongActions;
using Rodaje.App.Shell;
using Rodaje.App.Theme;
using Rodaje.App.Views.Analysis;

namespace Rodaje.App.Views.Tools;

/// <summary>
/// La barra de herramientas rápidas (<see cref="QuickTools"/>). Cada botón lanza la misma acción que los menús por el
/// router de atajos; se rehace el estado al cambiar la selección, el proyecto o los módulos instalados.
/// </summary>
internal sealed partial class ToolsBar : UserControl, IEditorView
{
    private readonly Dictionary<string, Button> _buttons = [];
    private readonly Dictionary<string, (FontIcon Icon, ProgressRing Ring, TextBlock Label)> _parts = [];
    private EditorServices? _services;

    public ToolsBar()
    {
        InitializeComponent();
    }

    public void Attach(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        foreach (QuickTool tool in QuickTools.All)
        {
            var label = new TextBlock { Text = tool.Label, Style = (Style)Application.Current.Resources["BodyTextStyle"], VerticalAlignment = VerticalAlignment.Center };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var icon = new FontIcon { Glyph = tool.Glyph, FontSize = ThemeResources.Size("IconFontSize") };
            // Mientras la acción corre, el ícono pasa a un giro (con el avance si se conoce) y el botón no se vuelve a pulsar.
            var ring = new ProgressRing { Width = 14, Height = 14, MinWidth = 0, MinHeight = 0, IsActive = false, Visibility = Visibility.Collapsed, Maximum = 1 };
            content.Children.Add(icon);
            content.Children.Add(ring);
            content.Children.Add(label);
            _parts[tool.ActionId] = (icon, ring, label);
            var button = new Button { Content = content, Tag = tool.ActionId, Style = (Style)Resources["QuickToolButtonStyle"] };
            AutomationProperties.SetName(button, tool.Label);
            button.Click += OnToolClick;
            Buttons.Children.Add(button);
            _buttons[tool.ActionId] = button;
        }

        EditorSession session = services.Session;
        session.SelectionChanged += (_, _) => Refresh();
        session.ProjectEdited += (_, _) => Refresh();
        session.ProjectOpened += (_, _) => Refresh();
        services.ShortcutRegistry.Changed += (_, _) => Refresh();
        if (services.Modules is { } modules)
        {
            modules.ModulesChanged += (_, _) => DispatcherQueue.TryEnqueue(Refresh);
        }

        services.LongActions.Changed += (_, _) => DispatcherQueue.TryEnqueue(UpdateBusy);
        Scroller.SizeChanged += (_, _) => FitLabels();
        Refresh();
    }

    /// <summary>Si los nombres no caben, la barra queda solo con íconos (el nombre sigue en la ayuda) en vez de cortarse.</summary>
    private void FitLabels()
    {
        foreach ((_, _, TextBlock label) in _parts.Values)
        {
            label.Visibility = Visibility.Visible;
        }

        Buttons.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        bool compact = Buttons.DesiredSize.Width > Scroller.ActualWidth + 0.5;
        foreach ((_, _, TextBlock label) in _parts.Values)
        {
            label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>Para la autoprueba: si el botón está ocupado y qué dice.</summary>
    internal (bool Busy, string Label) Busy(string actionId) =>
        _parts.TryGetValue(actionId, out var parts) ? (parts.Ring.Visibility == Visibility.Visible, parts.Label.Text) : (false, "");

    private void UpdateBusy()
    {
        if (_services is null)
        {
            return;
        }

        IReadOnlyList<LongActionStatus> running = _services.LongActions.Running;
        foreach (QuickTool tool in QuickTools.All)
        {
            (FontIcon icon, ProgressRing ring, TextBlock label) = _parts[tool.ActionId];
            LongActionStatus? status = running.FirstOrDefault(r => r.ActionId == tool.ActionId);
            bool busy = status is not null;
            icon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
            ring.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            ring.IsActive = busy;
            ring.IsIndeterminate = status?.Progress is null;
            ring.Value = status?.Progress ?? 0;
            label.Text = status is { ProgressText.Length: > 0 } ? $"{tool.Label} · {status.ProgressText}" : tool.Label;
            _buttons[tool.ActionId].IsEnabled = !busy;
            AutomationProperties.SetItemStatus(_buttons[tool.ActionId], busy ? "En marcha " + status!.ProgressText : "");
        }
    }

    /// <summary>Estado de un botón, para la autoprueba: si aplica y la ayuda que muestra.</summary>
    internal (bool Available, string ToolTip) Describe(string actionId) =>
        _buttons.TryGetValue(actionId, out Button? b) ? (b.Opacity > 0.9, ToolTipService.GetToolTip(b) as string ?? "") : (false, "");

    private QuickToolState State(string actionId)
    {
        EditorSession session = _services!.Session;
        bool noiseModule = _services.Modules?.FindByFunction(NoiseReduction.Function) is not null;
        bool visionModule = _services.Modules?.FindByFunction(Vision.VisionTools.RemoveBackgroundFunction) is not null;
        bool subtitleModule = _services.Modules?.FindByFunction(Subtitles.AutoSubtitles.Function) is not null;
        return QuickTools.State(actionId, session.Project, session.Sequence, [.. session.SelectedClipIds], noiseModule, visionModule, subtitleModule);
    }

    private void Refresh()
    {
        if (_services is null)
        {
            return;
        }

        foreach (QuickTool tool in QuickTools.All)
        {
            Button button = _buttons[tool.ActionId];
            QuickToolState state = State(tool.ActionId);
            // Atenuado pero pulsable: un botón desactivado no muestra su ayuda, y aquí la ayuda es el porqué.
            button.Opacity = state.Available ? 1 : 0.45;
            string keys = _services.ShortcutRegistry.GetKeyText(tool.ActionId);
            string title = keys.Length == 0 ? tool.Label : $"{tool.Label} ({keys})";
            string tip = state.Available ? $"{title}: {tool.Help}" : $"{title}: {tool.Help}\n{state.Reason}";
            ToolTipService.SetToolTip(button, tip);
            AutomationProperties.SetHelpText(button, state.Reason ?? tool.Help);
        }
    }

    private void OnToolClick(object sender, RoutedEventArgs e)
    {
        if (_services is null || sender is not Button { Tag: string id })
        {
            return;
        }

        QuickTool tool = QuickTools.All.First(t => t.ActionId == id);
        QuickToolState state = State(id);
        if (!state.Available)
        {
            _services.Session.Notify(NotificationSeverity.Informational, tool.Label, state.Reason ?? "");
            return;
        }

        _services.Shortcuts.Invoke(id);
    }
}
