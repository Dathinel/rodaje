using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Rodaje.App.Services;
using Rodaje.App.Services.Audio;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Shell;
using Rodaje.App.Views.Inspector.Properties;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;
using K = Rodaje.Core.Model.BuiltInEffects.CompressorParameters;

namespace Rodaje.App.Views.Inspector.Dynamics;

/// <summary>
/// La pestaña «Dinámica»: el compresor de un clip de audio, de una pista o de la mezcla, con su curva, la reducción en vivo,
/// la escucha (A/B y sidechain), los preajustes y todos sus controles. Cada cambio es un comando deshacible; umbral,
/// relación y mezcla admiten keyframes.
/// </summary>
internal sealed partial class DynamicsPanel : UserControl, IEditorView
{
    private static readonly string[] Main = [K.Threshold, K.Ratio, K.Attack, K.Release, K.Knee, K.Makeup, K.Mix];
    private static readonly string[] Detection = [K.RmsWindow, K.SidechainHighPass, K.Lookahead];
    private static readonly string[] Bands = [K.LowCrossover, K.HighCrossover, K.LowThreshold, K.MidThreshold, K.HighThreshold, K.LowGain, K.MidGain, K.HighGain];
    private static readonly string[] Limiter = [K.Ceiling];

    private readonly List<ParameterRow> _rows = [];
    // Lo que muestran ahora los desplegables (ver ComboBoxChoices): Refresh corre en cada cuadro al reproducir y dentro de
    // los SelectionChanged; rehacerlos ahí o con la lista abierta cerraba la app desde WinUI.
    private ChoiceList<DynamicsOwner> _targets = new();
    private ChoiceList<string?> _sidechains = new();
    private ChoiceList<int> _presets = new();
    private readonly ComboBox _targetBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 200 };
    private readonly ComboBox _presetBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 200 };
    private readonly ToggleSwitch _enabled = new() { OnContent = "Activado", OffContent = "Desactivado", MinWidth = 0 };
    private readonly Button _add = new() { Content = "Añadir compresor" };
    private readonly Button _remove = new() { Content = "Quitar" };
    private readonly DynamicsGraph _graph = new();
    private readonly ToggleButton _bypass = new() { Content = "A/B: sin compresor" };
    private readonly ToggleButton _listen = new() { Content = "Escuchar sidechain" };
    private readonly CheckBox _autoRelease = new() { Content = "Relajación automática (según el programa)" };
    private readonly CheckBox _autoMakeup = new() { Content = "Compensación automática" };
    private readonly ComboBox _detector = new() { MinWidth = 160 };
    private readonly CheckBox _linked = new() { Content = "Estéreo enlazado" };
    private readonly ComboBox _sidechain = new() { MinWidth = 200 };
    private readonly ToggleSwitch _multiband = new() { OnContent = "Tres bandas", OffContent = "Una banda", MinWidth = 0 };
    private readonly ToggleSwitch _limiter = new() { OnContent = "Limitador de pico verdadero", OffContent = "Sin limitador", MinWidth = 0 };
    private readonly StackPanel _editor = new() { Spacing = 2 };
    private readonly StackPanel _bandRows = new();
    private readonly StackPanel _limiterRows = new();
    private readonly TextBlock _empty = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 8, 12, 8) };
    private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(12, 0, 12, 6) };
    private EditorServices? _services;
    private DynamicsEditor? _editor2;
    private DispatcherQueueTimer? _meter;
    private ParameterRow? _dragRow;
    private DynamicsOwner? _chosen;
    private string? _lastClip;
    private Time _shownPosition;
    private bool _updating;

    public DynamicsPanel()
    {
        var body = new StackPanel { Spacing = 6, Padding = new Thickness(0, 8, 0, 16) };
        var target = new StackPanel { Spacing = 6, Margin = new Thickness(12, 0, 12, 0) };
        target.Children.Add(Labeled("Compresor de", _targetBox));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_enabled);
        actions.Children.Add(_add);
        actions.Children.Add(_remove);
        target.Children.Add(actions);
        target.Children.Add(Labeled("Preajuste", _presetBox));
        body.Children.Add(target);
        body.Children.Add(_empty);

        var listen = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 0, 12, 0) };
        listen.Children.Add(_bypass);
        listen.Children.Add(_listen);
        ToolTipService.SetToolTip(_bypass, "Compara con la señal sin comprimir (alineada). Solo en la vista previa: no cambia el proyecto.");
        ToolTipService.SetToolTip(_listen, "Escucha lo que oye el detector (la llave, con su pasa altos). Solo en la vista previa.");
        _editor.Children.Add(_graph);
        _editor.Children.Add(listen);
        _editor.Children.Add(_note);
        _editor.Children.Add(Section("dinamica-compresion", "Compresión", Rows(Main), _autoRelease, _autoMakeup));
        _editor.Children.Add(Section("dinamica-detector", "Detector y sidechain", Labeled("Detector", _detector), _linked, Rows(Detection), Labeled("Sidechain", _sidechain)));
        _editor.Children.Add(Section("dinamica-bandas", "Multibanda", _multiband, _bandRows));
        _editor.Children.Add(Section("dinamica-limitador", "Limitador", _limiter, _limiterRows));
        body.Children.Add(_editor);
        Content = new ScrollViewer { Content = body, HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        _bandRows.Children.Add(Rows(Bands));
        _limiterRows.Children.Add(Rows(Limiter));

        _detector.Items.Add("Pico");
        _detector.Items.Add("RMS");
        _targetBox.SelectionChanged += (_, _) => OnTargetChosen();
        _presetBox.SelectionChanged += (_, _) => OnPresetChosen();
        _enabled.Toggled += (_, _) => Run(e => e.SetEnabled(_enabled.IsOn));
        _add.Click += (_, _) => Run(e => e.Add());
        _remove.Click += (_, _) => Run(e => e.Remove());
        _bypass.Click += (_, _) => UpdateMonitor();
        _listen.Click += (_, _) => UpdateMonitor();
        _autoRelease.Click += (_, _) => Toggle(K.AutoRelease, _autoRelease.IsChecked == true, "Cambiar relajación automática");
        _autoMakeup.Click += (_, _) => Toggle(K.AutoMakeup, _autoMakeup.IsChecked == true, "Cambiar compensación automática");
        _linked.Click += (_, _) => Toggle(K.StereoLink, _linked.IsChecked == true, "Cambiar enlace estéreo");
        _detector.SelectionChanged += (_, _) => Toggle(K.Detector, _detector.SelectedIndex == 1, "Cambiar detector");
        _multiband.Toggled += (_, _) => Set(K.Bands, _multiband.IsOn ? 3 : 1, "Cambiar bandas");
        _limiter.Toggled += (_, _) => Toggle(K.Limiter, _limiter.IsOn, "Cambiar limitador");
        _sidechain.SelectionChanged += (_, _) =>
        {
            if (!_updating && _sidechains.TryGet(_sidechain.SelectedIndex, out string? key))
            {
                // Puede quitar «Una pista que ya no está» y rehacer este desplegable: va después del evento, y solo al
                // compresor que se mostraba al elegir.
                LaterForChosen(e => e.SetSidechain(key));
            }
        };
        IsVisibleChanged();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => IsVisibleChanged());
    }

    public void Attach(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        _editor2 = new DynamicsEditor(services.Session);
        EditorSession session = services.Session;
        foreach (InspectorSection section in _editor.Children.OfType<InspectorSection>())
        {
            section.Remember(services.Settings);
        }

        session.SelectionChanged += (_, _) => Refresh();
        session.ProjectEdited += (_, _) => Refresh();
        session.ProjectOpened += (_, _) =>
        {
            _chosen = null;
            Refresh();
        };
        session.TransportChanged += (_, _) =>
        {
            if (session.Position != _shownPosition && Visibility == Visibility.Visible)
            {
                Refresh();
            }
        };
        session.DynamicsPanelRequested += (_, owner) =>
        {
            _chosen = owner;
            Refresh();
        };
        _meter = DispatcherQueue.CreateTimer();
        _meter.Interval = TimeSpan.FromMilliseconds(33);
        _meter.Tick += (_, _) => UpdateMeter();
        Refresh();
        IsVisibleChanged();
    }

    private static StackPanel Labeled(string label, FrameworkElement control)
    {
        var panel = new StackPanel { Spacing = 2, Tag = "labeled" };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextMutedBrush"] });
        panel.Children.Add(control);
        AutomationProperties.SetName(control, label);
        return panel;
    }

    private static InspectorSection Section(string key, string title, params UIElement[] children)
    {
        var content = new StackPanel { Spacing = 4, Padding = new Thickness(0, 0, 0, 6) };
        foreach (UIElement child in children)
        {
            if (child is CheckBox or ToggleSwitch or StackPanel { Tag: "labeled" })
            {
                ((FrameworkElement)child).Margin = new Thickness(12, 0, 12, 0);
            }

            content.Children.Add(child);
        }

        return new InspectorSection { Key = key, Title = title, Body = content };
    }

    private StackPanel Rows(string[] names)
    {
        var panel = new StackPanel();
        var context = new ParameterSpec.Context(0, 0, 0);
        foreach (string name in names)
        {
            var row = new ParameterRow();
            row.Configure(ParameterSpec.For(BuiltInEffects.Compressor, name, context));
            row.Changing += OnRowChanging;
            row.ChangeEnded += OnRowChangeEnded;
            row.Committed += (s, v) => Run(e => e.SetValue(((ParameterRow)s!).Spec.Name, v, ((ParameterRow)s!).Spec.Description));
            row.KeyframeToggled += (s, _) => Run(e => e.ToggleKeyframe(((ParameterRow)s!).Spec.Name));
            row.NavigateRequested += (s, d) => Run(e => e.GoToKeyframe(((ParameterRow)s!).Spec.Name, d));
            row.InterpolationChosen += (s, i) => Run(e => e.SetInterpolation(((ParameterRow)s!).Spec.Name, i));
            row.ResetRequested += async (s, _) => await ResetAsync((ParameterRow)s!);
            row.KeyframesClearRequested += async (s, _) => await ClearKeyframesAsync((ParameterRow)s!);
            _rows.Add(row);
            panel.Children.Add(row);
        }

        return panel;
    }

    /// <summary>Restablecer; con keyframes antes pregunta (D-024).</summary>
    private async Task ResetAsync(ParameterRow row)
    {
        if (_editor2 is not { } editor)
        {
            return;
        }

        _dragRow = null;
        int keyframes = editor.Read(row.Spec.Name).KeyframeCount;
        if (keyframes == 0 || await KeyframeDialog.ForReset(XamlRoot, row.Spec.Label)(keyframes))
        {
            Run(e => e.Reset([row.Spec.Name], row.Spec.ResetDescription));
        }
    }

    private async Task ClearKeyframesAsync(ParameterRow row)
    {
        if (_editor2 is not { } editor || editor.Read(row.Spec.Name).KeyframeCount is var count && count == 0)
        {
            return;
        }

        _dragRow = null;
        string label = row.Spec.Label;
        if (await KeyframeDialog.ForClear(XamlRoot, label)(count))
        {
            Run(e => e.ClearKeyframes(row.Spec.Name, "Quitar keyframes de " + char.ToLowerInvariant(label[0]) + label[1..]));
        }
    }

    private void OnRowChanging(object? sender, double value)
    {
        if (sender is not ParameterRow row || _editor2 is not { } editor)
        {
            return;
        }

        if (!ReferenceEquals(_dragRow, row))
        {
            _dragRow = row;
            editor.BeginChange(row.Spec.Name, row.Spec.Description);
        }

        editor.Change(value);
    }

    private void OnRowChangeEnded(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _dragRow))
        {
            _dragRow = null;
            _editor2?.EndChange();
            Refresh();
        }
    }

    private void Run(Func<DynamicsEditor, bool> action)
    {
        if (!_updating && _editor2 is { Owner: not null } editor)
        {
            action(editor);
            Refresh();
        }
    }

    private void Toggle(string name, bool on, string description) => Set(name, on ? 1 : 0, description);

    private void Set(string name, double value, string description) => Run(e => e.Read(name).Value != value && e.SetValue(name, value, description));

    /// <summary>Lo que hay para elegir: el clip de audio seleccionado, cada pista de audio y la mezcla.</summary>
    private void Refresh()
    {
        if (_services is null || _editor2 is not { } editor)
        {
            return;
        }

        EditorSession session = _services.Session;
        Sequence sequence = session.Sequence;
        _shownPosition = session.Position;
        string? clip = session.SelectedClipIds.FirstOrDefault(id => sequence.FindClip(id) is { Track.Kind: TrackKind.Audio });
        if (clip is null && session.SelectedClipIds.FirstOrDefault(id => sequence.FindClip(id) is { Track.Kind: TrackKind.Video }) is { } video)
        {
            clip = sequence.GetLinkGroup(video).Find(g => g.Track.Kind == TrackKind.Audio).Clip?.Id;
        }

        // Al elegir otro clip de audio, el panel pasa a él; si no, sigue con lo que se eligió.
        if (clip is not null && clip != _lastClip)
        {
            _chosen = new DynamicsOwner(DynamicsOwnerKind.Clip, clip);
        }

        _lastClip = clip;
        (ChoiceList<DynamicsOwner> targets, _chosen) = DynamicsChoices.Targets(session.Project, sequence, clip, _chosen);
        editor.Owner = _chosen;
        _updating = true;
        try
        {
            _targets = ComboBoxChoices.Show(_targetBox, _targets, targets, targets.IndexOf(_chosen));
            ShowEditor(editor, sequence);
        }
        finally
        {
            _updating = false;
        }

        UpdateMonitor();
    }

    private void ShowEditor(DynamicsEditor editor, Sequence sequence)
    {
        EffectInstance? compressor = editor.Compressor();
        bool has = compressor is not null;
        _add.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        _remove.Visibility = _enabled.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        _editor.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        _empty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        _empty.Text = "Sin compresor. Elige un preajuste o «Añadir compresor».";
        _enabled.IsOn = compressor?.Enabled ?? false;

        AudioPreset? preset = editor.MatchingPreset(out bool hasEffects);
        // Valor de cada elemento: −1 «Sin compresor», el índice del preajuste o −2 «Personalizado».
        (ChoiceList<int> presets, int presetIndex) = DynamicsChoices.Presets(has, preset);
        _presets = ComboBoxChoices.Show(_presetBox, _presets, presets, presetIndex, i =>
        {
            if (presets.Values[i] == -1)
            {
                return presets.Labels[i];
            }

            var item = new ComboBoxItem { Content = presets.Labels[i], IsEnabled = presets.Values[i] >= 0 };
            if (presets.Values[i] >= 0)
            {
                ToolTipService.SetToolTip(item, AudioPresets.All[presets.Values[i]].Description);
            }

            return item;
        });
        if (!has)
        {
            return;
        }

        bool inside = editor.KeyframeTime().Inside;
        int keyframes = 0;
        foreach (ParameterRow row in _rows)
        {
            if (_dragRow is null || !ReferenceEquals(row, _dragRow))
            {
                ParameterState state = editor.Read(row.Spec.Name);
                keyframes += state.KeyframeCount;
                row.Show(state, inside);
            }
        }

        _autoRelease.IsChecked = editor.Read(K.AutoRelease).Value >= 0.5;
        _autoMakeup.IsChecked = editor.Read(K.AutoMakeup).Value >= 0.5;
        _linked.IsChecked = editor.Read(K.StereoLink).Value >= 0.5;
        _detector.SelectedIndex = editor.Read(K.Detector).Value >= 0.5 ? 1 : 0;
        _multiband.IsOn = editor.Read(K.Bands).Value >= 2;
        _bandRows.Visibility = _multiband.IsOn ? Visibility.Visible : Visibility.Collapsed;
        _limiter.IsOn = editor.Read(K.Limiter).Value >= 0.5;
        _limiterRows.Visibility = _limiter.IsOn ? Visibility.Visible : Visibility.Collapsed;

        (ChoiceList<string?> sidechains, int sidechain) = DynamicsChoices.Sidechains(editor.SidechainCandidates(), editor.Sidechain);
        _sidechains = ComboBoxChoices.Show(_sidechain, _sidechains, sidechains, sidechain);
        _sidechain.IsEnabled = editor.Owner?.Kind is DynamicsOwnerKind.Clip or DynamicsOwnerKind.Track;
        _listen.IsEnabled = true;

        (double threshold, double ratio, double knee, double makeup) = editor.Curve();
        _graph.SetCurve(threshold, ratio, knee, makeup);
        _note.Text = Note(editor, sequence, compressor!, keyframes);
        _note.Visibility = _note.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Note(DynamicsEditor editor, Sequence sequence, EffectInstance compressor, int keyframes)
    {
        var parts = new List<string>();
        if (!compressor.Enabled)
        {
            parts.Add("Está desactivado: los cambios se guardan pero no se oyen.");
        }

        if (editor.Owner?.Kind == DynamicsOwnerKind.Clip && !editor.KeyframeTime().Inside && keyframes > 0)
        {
            parts.Add("El cursor está fuera del clip: lleva el cursor dentro para ajustar sus keyframes.");
        }

        if (editor.Sidechain is { Length: > 0 } key && sequence.FindTrack(key) is { Muted: true })
        {
            parts.Add("La pista del sidechain está silenciada: el compresor escucha su propio audio.");
        }

        return string.Join(" ", parts);
    }

    private void OnTargetChosen()
    {
        if (_updating || !_targets.TryGet(_targetBox.SelectedIndex, out DynamicsOwner chosen) || chosen == _chosen)
        {
            return;
        }

        _chosen = chosen;
        _bypass.IsChecked = false;
        _listen.IsChecked = false;
        // El refresco va después del evento: tocar el desplegable dentro de su propio SelectionChanged lo rompe.
        DispatcherQueue.Later(Refresh);
    }

    /// <summary>
    /// Hace <paramref name="action"/> después del evento en curso, solo si el panel sigue con el mismo dueño y el mismo clip
    /// que al elegir: si antes del despacho cambió la selección o el compresor elegido, la elección no se aplica a otro.
    /// </summary>
    private void LaterForChosen(Func<DynamicsEditor, bool> action)
    {
        if (_chosen is not { } owner)
        {
            return;
        }

        var picked = new DeferredChoice<DynamicsOwner>(owner, _lastClip);
        DispatcherQueue.Later(() =>
        {
            if (picked.StillApplies(_chosen, _lastClip) && _editor2?.Owner == picked.Owner)
            {
                Run(action);
            }
            else
            {
                Refresh();
            }
        });
    }

    private void OnPresetChosen()
    {
        if (_updating || !_presets.TryGet(_presetBox.SelectedIndex, out int index) || index == -2)
        {
            return;
        }

        AudioPreset? preset = index < 0 ? null : AudioPresets.All[index];
        LaterForChosen(e => preset is null ? e.Remove() : e.ApplyPreset(preset));
    }

    /// <summary>Con la pestaña a la vista, la mezcla publica el medidor del compresor elegido; la escucha solo dura mientras se ve.</summary>
    private void UpdateMonitor()
    {
        if (_services is null)
        {
            return;
        }

        bool visible = Visibility == Visibility.Visible && _editor2?.Compressor() is not null;
        if (!visible)
        {
            _bypass.IsChecked = false;
            _listen.IsChecked = false;
        }

        _services.Player.MonitorDynamics(visible && _chosen is { } owner
            ? new DynamicsMonitor(owner.MonitorKey, _listen.IsChecked == true, _bypass.IsChecked == true)
            : null);
    }

    private void IsVisibleChanged()
    {
        if (_meter is null)
        {
            return;
        }

        if (Visibility == Visibility.Visible)
        {
            _meter.Start();
            Refresh();
        }
        else
        {
            _meter.Stop();
            _dragRow = null;
            _editor2?.EndChange();
            UpdateMonitor();
        }
    }

    private void UpdateMeter()
    {
        if (_services is null || _editor2 is not { } editor || editor.Compressor() is null)
        {
            return;
        }

        _graph.SetReading(_services.Player.TakeDynamics(), editor.Curve());
    }
}
