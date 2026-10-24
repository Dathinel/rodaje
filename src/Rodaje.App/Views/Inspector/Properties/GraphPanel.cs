using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Shell;
using Rodaje.Core.Model;

namespace Rodaje.App.Views.Inspector.Properties;

/// <summary>Panel general que reutiliza el editor de curvas del inspector.</summary>
internal sealed partial class GraphPanel : StackPanel, IEditorView
{
    private readonly ComboBox _parameters = new() { Header = "Parámetro", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly KeyframeGraphControl _graph = new();
    private readonly TextBlock _hint = new()
    {
        Text = "Clic para seleccionar · Ctrl+clic para sumar o quitar · Arrastra un recuadro para seleccionar varios. Supr borra; Ctrl+C y Ctrl+V copian y pegan en el cursor.",
        TextWrapping = TextWrapping.Wrap,
    };
    private EditorServices? _services;
    private List<GraphParameter> _choices = [];
    private GraphParameter? _chosen;
    private bool _updating;

    public GraphPanel()
    {
        Margin = new Thickness(12);
        Spacing = 10;
        Children.Add(_parameters);
        var fit = new Button { Content = "Ver clip completo" };
        fit.Click += (_, _) => _graph.FitTime();
        Children.Add(fit);
        Children.Add(_graph);
        Children.Add(_hint);
        AutomationProperties.SetName(_parameters, "Parámetro de la gráfica");
        _parameters.SelectionChanged += (_, _) =>
        {
            if (!_updating && _parameters.SelectedIndex >= 0 && _parameters.SelectedIndex < _choices.Count)
            {
                _chosen = _choices[_parameters.SelectedIndex];
                ShowChosen();
            }
        };
        _parameters.DropDownClosed += (_, _) => Refresh();
    }

    public void Attach(EditorServices services)
    {
        _services = services;
        _graph.Attach(services, new ClipPropertyEditor(services.Session));
        _graph.Edited += (_, _) => Refresh();
        services.Session.SelectionChanged += (_, _) => Refresh();
        services.Session.ProjectOpened += (_, _) => Refresh();
        services.Session.ProjectEdited += (_, _) => Refresh();
        Refresh();
    }

    private void Refresh()
    {
        if (_services is not { } services)
        {
            return;
        }

        var choices = new List<GraphParameter>();
        foreach (string id in services.Session.SelectedClipIds)
        {
            if (services.Session.Sequence.FindClip(id) is { } found)
            {
                Clip clip = found.Clip;
                choices.AddRange(GraphParameters.For(clip, new ParameterSpec.Context(services.Session.Sequence.Width,
                    services.Session.Sequence.Height, clip.Duration.ToSeconds())));
            }
        }

        if (!_parameters.IsDropDownOpen)
        {
            _updating = true;
            try
            {
                _chosen = choices.FirstOrDefault(c => c.ClipId == _chosen?.ClipId && c.EffectId == _chosen.EffectId && c.Spec.Name == _chosen.Spec.Name)
                    ?? choices.FirstOrDefault(c => HasKeyframes(services, c))
                    ?? choices.FirstOrDefault();
                if (!choices.Select(c => c.Label).SequenceEqual(_choices.Select(c => c.Label)))
                {
                    _parameters.ItemsSource = choices.Select(c => c.Label).ToArray();
                }

                _choices = choices;
                _parameters.SelectedIndex = _chosen is null ? -1 : choices.IndexOf(_chosen);
                _parameters.IsEnabled = choices.Count > 0;
            }
            finally
            {
                _updating = false;
            }
        }

        ShowChosen();
    }

    private static bool HasKeyframes(EditorServices services, GraphParameter choice) =>
        services.Session.Sequence.FindClip(choice.ClipId)?.Clip.Effects.FirstOrDefault(e => e.Id == choice.EffectId)?.Parameters
            .TryGetValue(choice.Spec.Name, out Parameter? parameter) == true && parameter.Keyframes.Count > 0;

    private void ShowChosen()
    {
        Clip? clip = _chosen is not null ? _services?.Session.Sequence.FindClip(_chosen.ClipId)?.Clip : null;
        EffectInstance? effect = clip?.Effects.FirstOrDefault(e => e.Id == _chosen?.EffectId);
        _graph.Show(clip, effect, effect is null ? null : _chosen?.Spec);
    }
}
