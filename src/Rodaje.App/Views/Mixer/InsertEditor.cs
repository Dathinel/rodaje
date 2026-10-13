using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Rodaje.App.Services;
using Rodaje.App.Services.Audio;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Theme;
using Rodaje.App.Views.Inspector;
using Rodaje.App.Views.Inspector.Properties;
using Rodaje.Audio.Processing;
using Rodaje.Core.Commands;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Rodaje.App.Views.Mixer;

/// <summary>
/// El editor de una inserción del mezclador, en una ventanita sobre la tira: activar, A/B (solo la vista previa),
/// preajustes, su curva (ecualizadores y puerta) y todos sus parámetros. Cada cambio es un paso del historial; un
/// arrastre deja uno solo.
/// </summary>
internal sealed partial class InsertEditor : StackPanel
{
    private static readonly HashSet<string> Switches = new(StringComparer.Ordinal)
    {
        BuiltInEffects.DeEsserParameters.SplitBand, BuiltInEffects.DelayParameters.Sync, BuiltInEffects.DelayParameters.PingPong,
    };

    private static readonly string[] Divisions =
        ["Redonda", "Blanca", "Negra", "Corchea", "Semicorchea", "Blanca con puntillo", "Negra con puntillo", "Corchea con puntillo", "Negra de tresillo", "Corchea de tresillo"];

    private readonly EditorSession _session;
    private readonly string _ownerId;
    private readonly string _effectId;
    private readonly string _type;
    private readonly ToggleSwitch _enabled = new() { OnContent = "Activado", OffContent = "Desactivado", MinWidth = 0 };
    private readonly ToggleButton _ab = new() { Content = "A/B" };
    private readonly ComboBox _presets = new() { MinWidth = 180, PlaceholderText = "Preajuste" };
    private readonly InsertGraph? _graph;
    private readonly List<ParameterRow> _rows = [];
    private readonly Dictionary<string, CheckBox> _checks = new(StringComparer.Ordinal);
    private readonly ComboBox? _division;
    private string? _dragging;
    private bool _updating;

    public InsertEditor(EditorSession session, string ownerId, EffectInstance effect, bool bypassed, Action<bool> setBypass)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(effect);
        ArgumentNullException.ThrowIfNull(setBypass);
        _session = session;
        _ownerId = ownerId;
        _effectId = effect.Id;
        _type = effect.Type;
        Spacing = 6;
        Width = 360;
        var title = new TextBlock { Text = EffectUi.Name(_type), Style = ThemeResources.Style("HeaderTextStyle") };
        var help = new TextBlock { Text = EffectUi.Description(_type), Style = ThemeResources.Style("CaptionTextStyle"), TextWrapping = TextWrapping.Wrap, Foreground = ThemeResources.Brush("TextMutedBrush") };
        Children.Add(title);
        Children.Add(help);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_enabled);
        actions.Children.Add(_ab);
        Children.Add(actions);
        _ab.IsChecked = bypassed;
        ToolTipService.SetToolTip(_ab, "Escuchar sin este efecto, alineado. Solo en la vista previa: no cambia el proyecto ni la exportación.");
        _ab.Click += (_, _) => setBypass(_ab.IsChecked == true);
        _enabled.Toggled += (_, _) =>
        {
            if (!_updating && Effect() is { } e && e.Enabled != _enabled.IsOn)
            {
                session.Execute(Edits.SetEffectEnabled(session.SequenceId, _ownerId, _effectId, _enabled.IsOn));
            }
        };

        IReadOnlyList<InsertPreset> presets = InsertPresets.For(_type);
        if (presets.Count > 0)
        {
            foreach (InsertPreset preset in presets)
            {
                var item = new ComboBoxItem { Content = preset.Name };
                ToolTipService.SetToolTip(item, preset.Description);
                _presets.Items.Add(item);
            }

            _presets.Items.Add(new ComboBoxItem { Content = "Personalizado", IsEnabled = false });
            AutomationProperties.SetName(_presets, "Preajuste");
            _presets.SelectionChanged += (_, _) =>
            {
                if (!_updating && _presets.SelectedIndex >= 0 && _presets.SelectedIndex < presets.Count)
                {
                    // La edición refresca el editor, que vuelve a elegir en este desplegable: va después del evento (tarea 91).
                    InsertPreset chosen = presets[_presets.SelectedIndex];
                    DispatcherQueue.RunAfterEvent(() => session.Execute(Edits.ApplyInsertPreset(session.SequenceId, _ownerId, _effectId, chosen)));
                }
            };
            Children.Add(_presets);
        }

        if (InsertGraph.Supports(_type))
        {
            _graph = new InsertGraph();
            Children.Add(_graph);
        }

        var rows = new StackPanel { Spacing = 2 };
        var context = new ParameterSpec.Context(0, 0, 0);
        foreach (ParameterDefinition parameter in EffectRegistry.Find(_type)?.Parameters ?? [])
        {
            if (Switches.Contains(parameter.Name))
            {
                var check = new CheckBox { Content = EffectUi.Find(_type, parameter.Name)?.Label ?? parameter.Name, Margin = new Thickness(12, 0, 12, 0) };
                string name = parameter.Name;
                check.Click += (_, _) => SetValue(name, check.IsChecked == true ? 1 : 0, EffectUi.Find(_type, name)?.Description ?? name);
                _checks[name] = check;
                rows.Children.Add(check);
                continue;
            }

            if (_type == BuiltInEffects.Delay && parameter.Name == BuiltInEffects.DelayParameters.Division)
            {
                _division = new ComboBox { Header = "Figura", MinWidth = 200, Margin = new Thickness(12, 0, 12, 0) };
                foreach (string division in Divisions)
                {
                    _division.Items.Add(division);
                }

                _division.SelectionChanged += (_, _) =>
                {
                    if (!_updating && _division.SelectedIndex >= 0)
                    {
                        // Igual que el preajuste: la edición vuelve a elegir en este desplegable.
                        int division = _division.SelectedIndex;
                        DispatcherQueue.RunAfterEvent(() => SetValue(BuiltInEffects.DelayParameters.Division, division, "Cambiar figura del delay"));
                    }
                };
                rows.Children.Add(_division);
                continue;
            }

            if (_type == BuiltInEffects.Compressor && parameter.Name is not (BuiltInEffects.CompressorParameters.Threshold or BuiltInEffects.CompressorParameters.Ratio
                or BuiltInEffects.CompressorParameters.Attack or BuiltInEffects.CompressorParameters.Release or BuiltInEffects.CompressorParameters.Makeup
                or BuiltInEffects.CompressorParameters.Mix))
            {
                continue;
            }

            var row = new ParameterRow();
            row.Configure(ParameterSpec.For(_type, parameter.Name, context));
            row.Changing += (s, v) => Drag(((ParameterRow)s!).Spec, v);
            row.ChangeEnded += (_, _) => EndDrag();
            row.Committed += (s, v) => SetValue(((ParameterRow)s!).Spec.Name, v, ((ParameterRow)s!).Spec.Description);
            row.ResetRequested += (s, _) =>
            {
                ParameterSpec spec = ((ParameterRow)s!).Spec;
                SetValue(spec.Name, spec.DefaultValue, spec.ResetDescription);
            };
            _rows.Add(row);
            rows.Children.Add(row);
        }

        if (_type == BuiltInEffects.Compressor)
        {
            rows.Children.Add(new TextBlock
            {
                Text = "Detector, sidechain, multibanda, limitador y la curva con su reducción, en la pestaña Dinámica.",
                Style = ThemeResources.Style("CaptionTextStyle"),
                Foreground = ThemeResources.Brush("TextMutedBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12, 4, 12, 0),
            });
        }

        Children.Add(new ScrollViewer { Content = rows, MaxHeight = 420, HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        session.ProjectEdited += OnEdited;
        Unloaded += (_, _) =>
        {
            session.ProjectEdited -= OnEdited;
            EndDrag();
        };
        Refresh();
    }

    /// <summary>Lo que hizo la inserción en lo último que sonó (puerta, de-esser, limitador); el panel lo dibuja sobre la curva.</summary>
    public void ShowReading(DynamicsReading? reading) => _graph?.SetReading(reading);

    private EffectInstance? Effect() =>
        (MixerModel.Effects(_session.Sequence, Owner()) ?? []).Find(e => e.Id == _effectId);

    private MixerStrip Owner() => MixerModel.Strips(_session.Sequence, _session.Position).FirstOrDefault(s => s.OwnerId == _ownerId)
        ?? new MixerStrip(StripKind.Master, _ownerId, "");

    private void OnEdited(object? sender, ProjectEditedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (Effect() is not { } effect)
        {
            IsHitTestVisible = false;
            Opacity = 0.5;
            return;
        }

        _updating = true;
        try
        {
            _enabled.IsOn = effect.Enabled;
            IReadOnlyList<InsertPreset> presets = InsertPresets.For(_type);
            if (presets.Count > 0)
            {
                InsertPreset? match = InsertPresets.Match(effect);
                _presets.SelectedIndex = match is null ? presets.Count : presets.ToList().IndexOf(match);
            }

            foreach (ParameterRow row in _rows)
            {
                if (!row.IsChanging)
                {
                    row.Show(new ParameterState(Read(effect, row.Spec.Name), 0, null, null, null), playheadInside: true);
                }
            }

            foreach ((string name, CheckBox check) in _checks)
            {
                check.IsChecked = Read(effect, name) >= 0.5;
            }

            if (_division is not null)
            {
                _division.SelectedIndex = (int)Math.Round(Read(effect, BuiltInEffects.DelayParameters.Division));
            }

            _graph?.SetEffect(effect);
        }
        finally
        {
            _updating = false;
        }
    }

    private static double Read(EffectInstance effect, string name)
    {
        double fallback = EffectRegistry.DefaultValue(effect.Type, name);
        return effect.Parameters.TryGetValue(name, out Parameter? p) ? (p.Keyframes.Count > 0 ? p.Keyframes[0].Value : p.Value) : fallback;
    }

    private void Drag(ParameterSpec spec, double value)
    {
        if (_dragging != spec.Name)
        {
            EndDrag();
            _dragging = spec.Name;
            _session.BeginGesture(spec.Description);
        }

        _session.TryExecute(Edits.SetParameter(_session.SequenceId, _ownerId, _effectId, spec.Name, value, Time.Zero, spec.Description));
    }

    private void EndDrag()
    {
        if (_dragging is not null)
        {
            _dragging = null;
            _session.EndGesture();
        }
    }

    private void SetValue(string name, double value, string description)
    {
        if (!_updating)
        {
            EndDrag();
            _session.Execute(Edits.SetParameter(_session.SequenceId, _ownerId, _effectId, name, value, Time.Zero, description));
        }
    }
}

/// <summary>
/// La curva de una inserción: respuesta en frecuencia de los ecualizadores (20 Hz a 20 kHz, ±18 dB) o curva de
/// transferencia de la puerta (entrada contra salida, de −90 a 0 dB) con el punto de lo que suena.
/// </summary>
internal sealed partial class InsertGraph : UserControl
{
    private const double GraphWidth = 336;
    private const double GraphHeight = 140;
    private readonly Canvas _canvas = new() { Width = GraphWidth, Height = GraphHeight };
    private readonly Polyline _curve = new() { StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
    private readonly Path _grid = new() { StrokeThickness = 1 };
    private readonly Ellipse _dot = new() { Width = 8, Height = 8, Visibility = Visibility.Collapsed };
    private readonly List<TextBlock> _labels = [];
    private EffectInstance? _effect;

    public InsertGraph()
    {
        Width = GraphWidth + 2;
        Height = GraphHeight + 2;
        _grid.Stroke = ThemeResources.Brush("StrokeBrush");
        _curve.Stroke = ThemeResources.Brush("AccentBrush");
        _dot.Fill = ThemeResources.Brush("TextBrush");
        _canvas.Children.Add(_grid);
        _canvas.Children.Add(_curve);
        _canvas.Children.Add(_dot);
        _canvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, GraphWidth, GraphHeight) };
        Content = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeResources.Brush("ControlBorderBrush"),
            Background = ThemeResources.Brush("BackgroundBrush"),
            Child = _canvas,
        };
    }

    public static bool Supports(string type) => type is BuiltInEffects.Equalizer or BuiltInEffects.GraphicEqualizer or BuiltInEffects.Gate;

    public void SetEffect(EffectInstance effect)
    {
        _effect = effect;
        bool gate = effect.Type == BuiltInEffects.Gate;
        AutomationProperties.SetName(this, gate ? "Curva de la puerta" : "Respuesta en frecuencia");
        ToolTipService.SetToolTip(this, gate
            ? "Entrada (horizontal) contra salida (vertical), de −90 a 0 dB. El punto es lo que suena ahora."
            : "Lo que sube o baja cada frecuencia, de 20 Hz a 20 kHz y de −18 a +18 dB.");
        _grid.Data = gate ? GateGrid() : FrequencyGrid();
        var points = new PointCollection();
        if (gate)
        {
            GateSettings settings = MixerCurves.Gate(effect);
            for (double input = -90; input <= 0; input += 0.5)
            {
                points.Add(new Point(GateX(input), GateY(input + settings.StaticGainDb(input))));
            }
        }
        else
        {
            Func<double, double> response = MixerCurves.Response(effect);
            for (int i = 0; i <= 200; i++)
            {
                double hz = 20 * Math.Pow(1000, i / 200.0);
                points.Add(new Point(i * GraphWidth / 200, FrequencyY(response(hz))));
            }
        }

        _curve.Points = points;
        Labels(gate);
    }

    public void SetReading(DynamicsReading? reading)
    {
        if (_effect is not { Type: BuiltInEffects.Gate } || reading is not { } r || r.InputPeak <= 1e-6)
        {
            _dot.Visibility = Visibility.Collapsed;
            return;
        }

        double input = 20 * Math.Log10(r.InputPeak);
        Canvas.SetLeft(_dot, GateX(input) - 4);
        Canvas.SetTop(_dot, GateY(input - r.ReductionDb) - 4);
        _dot.Visibility = Visibility.Visible;
    }

    private static double FrequencyY(double db) => GraphHeight * (0.5 - (Math.Clamp(db, -18, 18) / 36));

    private static double GateX(double db) => GraphWidth * (Math.Clamp(db, -90, 0) + 90) / 90;

    private static double GateY(double db) => GraphHeight * (1 - ((Math.Clamp(db, -90, 0) + 90) / 90));

    private static GeometryGroup FrequencyGrid()
    {
        var group = new GeometryGroup();
        foreach (double hz in new[] { 50.0, 100, 200, 500, 1000, 2000, 5000, 10000 })
        {
            double x = GraphWidth * Math.Log10(hz / 20) / 3;
            group.Children.Add(new LineGeometry { StartPoint = new Point(x, 0), EndPoint = new Point(x, GraphHeight) });
        }

        foreach (double db in new[] { -12.0, -6, 0, 6, 12 })
        {
            group.Children.Add(new LineGeometry { StartPoint = new Point(0, FrequencyY(db)), EndPoint = new Point(GraphWidth, FrequencyY(db)) });
        }

        return group;
    }

    private static GeometryGroup GateGrid()
    {
        var group = new GeometryGroup();
        for (double db = -80; db < 0; db += 20)
        {
            group.Children.Add(new LineGeometry { StartPoint = new Point(GateX(db), 0), EndPoint = new Point(GateX(db), GraphHeight) });
            group.Children.Add(new LineGeometry { StartPoint = new Point(0, GateY(db)), EndPoint = new Point(GraphWidth, GateY(db)) });
        }

        group.Children.Add(new LineGeometry { StartPoint = new Point(0, GraphHeight), EndPoint = new Point(GraphWidth, 0) });
        return group;
    }

    private void Labels(bool gate)
    {
        foreach (TextBlock label in _labels)
        {
            _canvas.Children.Remove(label);
        }

        _labels.Clear();
        (string Text, double X, double Y)[] marks = gate
            ? [("−60", GateX(-60) + 2, GraphHeight - 14), ("−20", GateX(-20) + 2, GraphHeight - 14), ("0 dB", GraphWidth - 26, GraphHeight - 14)]
            : [("100", (GraphWidth * Math.Log10(5) / 3) + 2, GraphHeight - 14), ("1k", (GraphWidth * Math.Log10(50) / 3) + 2, GraphHeight - 14),
                ("10k", (GraphWidth * Math.Log10(500) / 3) + 2, GraphHeight - 14), ("+12", 2, FrequencyY(12) - 1), ("−12", 2, FrequencyY(-12) - 13)];
        foreach ((string text, double x, double y) in marks)
        {
            var label = new TextBlock { Text = text, FontSize = ThemeResources.Size("MicroFontSize"), Foreground = ThemeResources.Brush("TextMutedBrush") };
            Canvas.SetLeft(label, x);
            Canvas.SetTop(label, y);
            _canvas.Children.Insert(1, label);
            _labels.Add(label);
        }
    }
}
