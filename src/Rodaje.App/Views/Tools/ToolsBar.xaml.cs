using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Shell;
using Rodaje.App.Views.Analysis;

namespace Rodaje.App.Views.Tools;

/// <summary>
/// La barra de herramientas rápidas (<see cref="QuickTools"/>). Cada botón lanza la misma acción que los menús por el
/// router de atajos; se rehace el estado al cambiar la selección, el proyecto o los módulos instalados.
/// </summary>
internal sealed partial class ToolsBar : UserControl, IEditorView
{
    private readonly Dictionary<string, Button> _buttons = [];
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
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(new FontIcon { Glyph = tool.Glyph, FontSize = 14 });
            content.Children.Add(label);
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

        Refresh();
    }

    /// <summary>Estado de un botón, para la autoprueba: si aplica y la ayuda que muestra.</summary>
    internal (bool Available, string ToolTip) Describe(string actionId) =>
        _buttons.TryGetValue(actionId, out Button? b) ? (b.Opacity > 0.9, ToolTipService.GetToolTip(b) as string ?? "") : (false, "");

    private QuickToolState State(string actionId)
    {
        EditorSession session = _services!.Session;
        bool noiseModule = _services.Modules?.FindByFunction(NoiseReduction.Function) is not null;
        return QuickTools.State(actionId, session.Project, session.Sequence, [.. session.SelectedClipIds], noiseModule);
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
