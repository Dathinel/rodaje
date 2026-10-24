using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Theme;
using Rodaje.App.Services.Audio;
using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Titles;
using Rodaje.App.Shell;
using Microsoft.UI.Xaml.Controls.Primitives;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Inspector.Properties;

/// <summary>Propiedades del clip seleccionado: transformación animable con keyframes, modo de mezcla y audio.</summary>
internal sealed partial class PropertiesPanel : UserControl, IEditorView
{
    private const string DefaultPresetName = "Mi transición";

    private readonly ParameterRow[] _videoRows;
    private readonly ParameterRow[] _audioRows;
    private EditorServices? _services;
    private ClipPropertyEditor? _editor;
    private ParameterRow? _dragRow;
    private string? _videoClipId;
    private string? _audioClipId;
    private IReadOnlyList<string> _videoClipIds = [];
    private IReadOnlyList<string> _audioClipIds = [];
    private string? _emptyText;
    private (int Width, int Height) _transformRange;
    private double _audioRange = -1;
    private Time _shownPosition;
    private bool _updatingBlend;
    private bool _updatingPreset;
    private bool _updatingTransform;
    private string? _graphName;

    /// <summary>Los parámetros que muestra ahora el desplegable de la gráfica, en su orden (ver <see cref="ComboBoxChoices"/>).</summary>
    private ChoiceList<string> _graphChoices = new();
    private readonly ClosedListRetry _graphRetry = new();
    private readonly List<(Button Button, string Id)> _presetTiles = [];

    public PropertiesPanel()
    {
        InitializeComponent();
        GraphParameterBox.RetryWhenClosed(_graphRetry, Refresh);
        _videoRows = [XRow, YRow, ScaleRow, ScaleXRow, ScaleYRow, RotationRow, OpacityRow, AnchorXRow, AnchorYRow, SkewXRow, SkewYRow, ShutterRow, PhaseRow,
            SamplesRow, HdrRow];
        BuildPresetTiles();
        _audioRows = [GainRow, FadeInRow, FadeOutRow];
        foreach ((_, string name) in EffectUi.BlendModes)
        {
            BlendBox.Items.Add(name);
        }

        foreach ((double lufs, string name) in LoudnessNormalizer.Targets)
        {
            var item = new MenuFlyoutItem { Text = name };
            item.Click += (_, _) => UiTask.Run(NormalizeAsync(lufs));
            NormalizeMenu.Items.Add(item);
        }

        NormalizeMenu.Items.Add(new MenuFlyoutSeparator());
        var custom = new MenuFlyoutItem { Text = "Otro objetivo…" };
        custom.Click += (_, _) => UiTask.Run(async () =>
        {
            if (await AskTargetAsync(XamlRoot, "Normalizar el clip") is { } lufs)
            {
                await NormalizeAsync(lufs);
            }
        });
        NormalizeMenu.Items.Add(custom);

        PresetBox.Items.Add("Sin ecualizador ni compresor");
        foreach (AudioPreset preset in AudioPresets.All)
        {
            var item = new ComboBoxItem { Content = preset.Name, Tag = preset };
            ToolTipService.SetToolTip(item, preset.Description);
            PresetBox.Items.Add(item);
        }

        PresetBox.Items.Add(new ComboBoxItem { Content = "Personalizado (en Filtros)", IsEnabled = false });
    }

    /// <summary>Pide un objetivo en LUFS; nulo si se cancela o no es un número entre −60 y 0.</summary>
    internal static async Task<double?> AskTargetAsync(XamlRoot root, string title)
    {
        if (DialogGate.IsOpen)
        {
            return null;
        }

        var box = new TextBox { Text = "−18", Header = "Objetivo en LUFS (de −60 a 0)" };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = box,
            PrimaryButtonText = "Normalizar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ElementTheme.Dark,
        };
        return await DialogGate.ShowAsync(dialog) == ContentDialogResult.Primary ? LoudnessNormalizer.ParseTarget(box.Text) : null;
    }

    private async Task NormalizeAsync(double lufs)
    {
        if (_services is not { } services || _audioClipId is not { } current)
        {
            return;
        }

        IReadOnlyList<string> clips = LoudnessNormalizer.AudioClips(services.Session);
        NormalizeButton.IsEnabled = false;
        NormalizeButton.Content = "Midiendo…";
        try
        {
            await LoudnessNormalizer.NormalizeClipsAsync(services.Session, clips.Count > 0 ? clips : [current], lufs);
        }
        finally
        {
            NormalizeButton.Content = "Normalizar";
            NormalizeButton.IsEnabled = true;
        }
    }

    private IEnumerable<ParameterRow> PlacementRows => [XRow, YRow, ScaleRow, ScaleXRow, ScaleYRow, RotationRow];

    private IEnumerable<ParameterRow> ShapeRows => [AnchorXRow, AnchorYRow, SkewXRow, SkewYRow];

    private IEnumerable<ParameterRow> MotionRows => [ShutterRow, PhaseRow, SamplesRow];

    /// <summary>Una baldosa por animación de fábrica, con su miniatura: fantasmas de la capa en cuatro instantes y el recorrido del centro.</summary>
    private void BuildPresetTiles()
    {
        const int Columns = 4;
        IReadOnlyList<MotionPreset> presets = MotionPresets.All;
        for (int c = 0; c < Columns; c++)
        {
            PresetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (int r = 0; r < (presets.Count + Columns - 1) / Columns; r++)
        {
            PresetGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (int i = 0; i < presets.Count; i++)
        {
            MotionPreset preset = presets[i];
            var label = new TextBlock
            {
                Text = preset.Name,
                FontSize = ThemeResources.Size("CaptionFontSize"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var content = new StackPanel { Spacing = 3 };
            content.Children.Add(MotionPresetThumbnail.Create(preset.Id));
            content.Children.Add(label);
            var button = new Button
            {
                Content = content,
                Padding = new Thickness(2, 5, 2, 3),
                MinWidth = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, "Animación: " + preset.Name);
            ToolTipService.SetToolTip(button, preset.Name + ". " + preset.Description);
            string id = preset.Id;
            button.Click += (_, _) => ApplyPreset(id);
            Grid.SetColumn(button, i % Columns);
            Grid.SetRow(button, i / Columns);
            PresetGrid.Children.Add(button);
            _presetTiles.Add((button, id));
        }
    }

    /// <summary>Para la autoprueba: con <paramref name="animation"/>, pliega posición y opacidad y abre desenfoque, animaciones y gráfica; si no, al revés.</summary>
    /// <remarks>Con <paramref name="graphOnly"/>, además pliega el desenfoque y las animaciones para que la gráfica quede a la vista.</remarks>
    internal void ShowAnimationForSelfTest(bool animation, bool graphOnly = false)
    {
        PlacementSection.IsExpanded = !animation;
        OpacitySection.IsExpanded = !animation;
        AudioSection.IsExpanded = !animation;
        MotionBlurSection.IsExpanded = animation && !graphOnly;
        PresetsSection.IsExpanded = animation && !graphOnly;
        GraphSection.IsExpanded = animation;
        Body.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void ApplyPreset(string id)
    {
        if (_services is { } services && _videoClipId is { } clipId)
        {
            FinishChanges(_videoRows);
            services.Session.Execute(MotionPresetCommands.Apply(services.Session.SequenceId, clipId, id));
        }
    }

    public void Attach(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        _editor = new ClipPropertyEditor(services.Session);
        foreach (ParameterRow row in _videoRows.Concat(_audioRows))
        {
            row.Changing += OnRowChanging;
            row.ChangeEnded += OnRowChangeEnded;
            row.Committed += OnRowCommitted;
            row.KeyframeToggled += OnRowKeyframeToggled;
            row.NavigateRequested += OnRowNavigate;
            row.InterpolationChosen += OnRowInterpolation;
            row.ResetRequested += OnRowReset;
            row.KeyframesClearRequested += OnRowClearKeyframes;
        }

        PlacementSection.Remember(services.Settings);
        ShapeSection.Remember(services.Settings);
        MotionBlurSection.Remember(services.Settings);
        PresetsSection.Remember(services.Settings);
        GraphSection.Remember(services.Settings);
        Graph.Attach(services, _editor);
        Graph.Edited += (_, _) => Refresh();
        OpacitySection.Remember(services.Settings);
        AudioSection.Remember(services.Settings);

        EditorSession session = services.Session;
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => session.SetTransformEditing(Visibility == Visibility.Visible));
        session.SetTransformEditing(Visibility == Visibility.Visible);
        session.SelectionChanged += (_, _) => Refresh();
        session.ProjectEdited += (_, _) => Refresh();
        session.ProjectOpened += (_, _) => Refresh();
        session.TransportChanged += (_, _) =>
        {
            if (session.Position != _shownPosition && Body.Visibility == Visibility.Visible)
            {
                Refresh();
            }
        };
        Refresh();
    }

    private void ShowSequence(Sequence sequence)
    {
        SequenceTitle.Text = $"Secuencia «{sequence.Name}»";
        IReadOnlyList<(string Label, string Value)> rows = SequenceSummary.Describe(sequence);
        if (SequenceRows.Children.Count != rows.Count * 2)
        {
            SequenceRows.Children.Clear();
            SequenceRows.RowDefinitions.Clear();
            for (int i = 0; i < rows.Count; i++)
            {
                SequenceRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = new TextBlock { Style = (Style)Application.Current.Resources["LabelTextStyle"] };
                var value = new TextBlock { Style = (Style)Application.Current.Resources["BodyTextStyle"], TextWrapping = TextWrapping.Wrap };
                Grid.SetRow(label, i);
                Grid.SetRow(value, i);
                Grid.SetColumn(value, 1);
                SequenceRows.Children.Add(label);
                SequenceRows.Children.Add(value);
            }
        }

        for (int i = 0; i < rows.Count; i++)
        {
            ((TextBlock)SequenceRows.Children[2 * i]).Text = rows[i].Label;
            ((TextBlock)SequenceRows.Children[(2 * i) + 1]).Text = rows[i].Value;
        }
    }

    private void OnSequenceFormatClick(object sender, RoutedEventArgs e) => _services?.Shortcuts.Invoke(Player.PlayerActionIds.ProjectFormat);

    private void Refresh()
    {
        if (_editor is not { } editor)
        {
            return;
        }

        EditorSession session = editor.Session;
        (IReadOnlyList<Clip> videos, IReadOnlyList<Clip> audios) = editor.Targets();
        Clip? video = videos.Count > 0 ? videos[0] : null;
        Clip? audio = audios.Count > 0 ? audios[0] : null;
        List<string> videoIds = [.. videos.Select(c => c.Id)];
        List<string> audioIds = [.. audios.Select(c => c.Id)];
        if (!videoIds.SequenceEqual(_videoClipIds))
        {
            FinishChanges(_videoRows);
        }

        if (!audioIds.SequenceEqual(_audioClipIds))
        {
            FinishChanges(_audioRows);
        }

        _videoClipId = video?.Id;
        _audioClipId = audio?.Id;
        _videoClipIds = videoIds;
        _audioClipIds = audioIds;
        _shownPosition = session.Position;
        int selected = session.SelectedClipIds.Count(id => session.Sequence.FindClip(id) is not null);
        bool any = video is not null || audio is not null;
        _emptyText ??= EmptyText.Text;
        EmptyText.Text = !any && selected > 1 ? "Los clips seleccionados no tienen propiedades en común." : _emptyText;
        EmptyText.Visibility = any || selected == 0 ? Visibility.Collapsed : Visibility.Visible;
        SequenceInfo.Visibility = !any && selected == 0 ? Visibility.Visible : Visibility.Collapsed;
        Body.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (!any)
        {
            if (selected == 0)
            {
                ShowSequence(session.Sequence);
            }

            return;
        }

        Clip titled = (video ?? audio)!;
        ClipTitle.Text = selected > 1
            ? selected.ToString(CultureInfo.InvariantCulture) + " clips seleccionados"
            : ClipLabels.Name(session.Project, titled);
        ToolTipService.SetToolTip(ClipTitle, ClipTitle.Text);
        Visibility videoVisible = video is null ? Visibility.Collapsed : Visibility.Visible;
        PlacementSection.Visibility = videoVisible;
        ShapeSection.Visibility = videoVisible;
        MotionBlurSection.Visibility = videoVisible;
        PresetsSection.Visibility = videoVisible;
        GraphSection.Visibility = videoVisible;
        OpacitySection.Visibility = videoVisible;
        MoreButton.Visibility = videoVisible;
        KeyframeTotal.Visibility = videoVisible;
        TransformNote.Visibility = Visibility.Collapsed;
        AudioSection.Visibility = audio is null ? Visibility.Collapsed : Visibility.Visible;

        if (video is not null)
        {
            ShowVideo(editor, video, videos);
        }

        if (audio is not null)
        {
            ShowAudio(editor, audio, audios);
        }
    }

    private void ShowVideo(ClipPropertyEditor editor, Clip clip, IReadOnlyList<Clip> clips)
    {
        Sequence sequence = editor.Session.Sequence;
        if (_transformRange != (sequence.Width, sequence.Height))
        {
            _transformRange = (sequence.Width, sequence.Height);
            var specs = ParameterSpec.Transform(sequence.Width, sequence.Height).ToDictionary(s => s.Name, StringComparer.Ordinal);
            foreach ((ParameterRow row, string name) in RowNames())
            {
                (string? label, string? axis) = name switch
                {
                    BuiltInEffects.TransformParameters.X => ("Posición", "X"),
                    BuiltInEffects.TransformParameters.Y => ("", "Y"),
                    BuiltInEffects.TransformParameters.AnchorX => ("Ancla", "X"),
                    BuiltInEffects.TransformParameters.AnchorY => ("", "Y"),
                    BuiltInEffects.TransformParameters.SkewX => ("Inclinación", "X"),
                    BuiltInEffects.TransformParameters.SkewY => ("", "Y"),
                    BuiltInEffects.TransformParameters.HdrReferenceNits => ("Brillo HDR", (string?)null),
                    _ => ((string?)null, (string?)null),
                };
                row.Configure(specs[name], label, axis);
            }
        }

        bool inside = editor.IsPlayheadInside(clip);
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        int placementKeyframes = 0;
        int opacityKeyframes = 0;
        int shapeKeyframes = 0;
        var animated = new List<ParameterRow>();
        foreach (ParameterRow row in _videoRows)
        {
            ParameterState state = editor.Read(clip, row.Spec.EffectType, row.Spec.Name, row.Spec.DefaultValue);
            values[row.Spec.Name] = row.Spec.ToDisplay(state.Value);
            if (ReferenceEquals(row, OpacityRow))
            {
                opacityKeyframes += state.KeyframeCount;
            }
            else if (ShapeRows.Contains(row))
            {
                shapeKeyframes += state.KeyframeCount;
            }
            else
            {
                placementKeyframes += state.KeyframeCount;
            }

            if (state.KeyframeCount >= 2)
            {
                animated.Add(row);
            }

            row.Show(state, inside, editor.IsMixed(clips, row.Spec.EffectType, row.Spec.Name, row.Spec.DefaultValue));
        }

        KeyframeTotal.Text = KeyframeText(placementKeyframes + opacityKeyframes + shapeKeyframes);
        PlacementSection.KeyframeSummary = KeyframeText(placementKeyframes);
        ShapeSection.KeyframeSummary = KeyframeText(shapeKeyframes);
        OpacitySection.KeyframeSummary = KeyframeText(opacityKeyframes);
        ShapeSection.Summary = "Ancla " + Number(values.GetValueOrDefault(BuiltInEffects.TransformParameters.AnchorX)) + ", "
            + Number(values.GetValueOrDefault(BuiltInEffects.TransformParameters.AnchorY)) + " · "
            + Number(values.GetValueOrDefault(BuiltInEffects.TransformParameters.SkewX)) + "° / "
            + Number(values.GetValueOrDefault(BuiltInEffects.TransformParameters.SkewY)) + "°";
        PlacementSection.Summary = "X " + Number(values.GetValueOrDefault(BuiltInEffects.TransformParameters.X))
            + " · Y " + Number(values.GetValueOrDefault(BuiltInEffects.TransformParameters.Y))
            + " · " + Number(values.GetValueOrDefault(BuiltInEffects.TransformParameters.Scale)) + " %"
            + " · " + Number(values.GetValueOrDefault(BuiltInEffects.TransformParameters.Rotation)) + "°";
        ShowTransformOptions(clip, values);
        ShowGraph(clip, animated);

        ResetAllItem.IsEnabled = ClipPropertyEditor.CanReset(clip, BuiltInEffects.Transform, Defaults(_videoRows));
        PlacementReset.IsEnabled = ClipPropertyEditor.CanReset(clip, BuiltInEffects.Transform, Defaults(PlacementRows));
        EffectInstance? transform = ClipPropertyEditor.FindEffect(clip, BuiltInEffects.Transform);
        TransformNote.Visibility = transform is { Enabled: false } ? Visibility.Visible : Visibility.Collapsed;
        bool canSave = AnimationPreset.CanExport(transform);
        SavePresetItem.IsEnabled = canSave;
        SavePresetItem.Text = canSave ? "Guardar como transición…" : "Guardar como transición (necesita 2 keyframes)";

        string mode = BlendMode(clip);
        _updatingBlend = true;
        BlendBox.SelectedIndex = Math.Max(0, EffectRegistry.BlendModes.IndexOf(mode));
        _updatingBlend = false;
        OpacitySection.Summary = Number(values.GetValueOrDefault(BuiltInEffects.TransformParameters.Opacity)) + " % · "
            + EffectUi.BlendModeName(mode);
    }

    private IEnumerable<(ParameterRow Row, string Name)> RowNames()
    {
        yield return (XRow, BuiltInEffects.TransformParameters.X);
        yield return (YRow, BuiltInEffects.TransformParameters.Y);
        yield return (ScaleRow, BuiltInEffects.TransformParameters.Scale);
        yield return (ScaleXRow, BuiltInEffects.TransformParameters.ScaleX);
        yield return (ScaleYRow, BuiltInEffects.TransformParameters.ScaleY);
        yield return (RotationRow, BuiltInEffects.TransformParameters.Rotation);
        yield return (OpacityRow, BuiltInEffects.TransformParameters.Opacity);
        yield return (AnchorXRow, BuiltInEffects.TransformParameters.AnchorX);
        yield return (AnchorYRow, BuiltInEffects.TransformParameters.AnchorY);
        yield return (SkewXRow, BuiltInEffects.TransformParameters.SkewX);
        yield return (SkewYRow, BuiltInEffects.TransformParameters.SkewY);
        yield return (ShutterRow, BuiltInEffects.TransformParameters.ShutterAngle);
        yield return (PhaseRow, BuiltInEffects.TransformParameters.ShutterPhase);
        yield return (SamplesRow, BuiltInEffects.TransformParameters.MotionSamples);
        yield return (HdrRow, BuiltInEffects.TransformParameters.HdrReferenceNits);
    }

    /// <summary>Volteo y desenfoque de movimiento: los botones y el interruptor siguen al modelo.</summary>
    private void ShowTransformOptions(Clip clip, Dictionary<string, double> values)
    {
        EffectInstance? transform = ClipPropertyEditor.FindEffect(clip, BuiltInEffects.Transform);
        bool flipX = transform?.Options.GetValueOrDefault(BuiltInEffects.TransformOptions.FlipX) == "on";
        bool flipY = transform?.Options.GetValueOrDefault(BuiltInEffects.TransformOptions.FlipY) == "on";
        double shutter = values.GetValueOrDefault(BuiltInEffects.TransformParameters.ShutterAngle);
        _updatingTransform = true;
        FlipXButton.IsChecked = flipX;
        FlipYButton.IsChecked = flipY;
        MotionBlurSwitch.IsOn = shutter > 0;
        _updatingTransform = false;
        MotionBlurRows.Visibility = shutter > 0 ? Visibility.Visible : Visibility.Collapsed;
        MotionBlurSection.Summary = shutter > 0
            ? $"{Number(shutter)}° · {Number(values.GetValueOrDefault(BuiltInEffects.TransformParameters.MotionSamples))} muestras"
            : "Apagado";
        string flips = (flipX, flipY) switch
        {
            (true, true) => " · volteado ↔↕",
            (true, false) => " · volteado ↔",
            (false, true) => " · volteado ↕",
            _ => "",
        };
        PlacementSection.Summary += flips;
    }

    /// <summary>La gráfica: el parámetro elegido (o el último que se tocó, o el primero animado) y su curva.</summary>
    /// <remarks>
    /// Los elementos del desplegable solo se rehacen si cambió la lista y nunca con él abierto: rehacerlos en cada refresco
    /// (al elegir «Escala» o mientras se reproduce con la lista abierta) cerraba la app desde dentro de WinUI.
    /// </remarks>
    private void ShowGraph(Clip clip, List<ParameterRow> animated)
    {
        (ChoiceList<string> offered, int selected) = GraphParameterChoice.Build([.. animated.Select(r => (r.Spec.Name, r.Spec.Label))], _graphName);
        ParameterRow? shown = selected >= 0 ? animated[selected] : null;
        if (!_graphRetry.Defer(GraphParameterBox.IsDropDownOpen))
        {
            _updatingTransform = true;
            try
            {
                _graphChoices = ComboBoxChoices.Show(GraphParameterBox, _graphChoices, offered, selected,
                    i => new ComboBoxItem { Content = offered.Labels[i], Tag = offered.Values[i] });
                GraphParameterBox.IsEnabled = animated.Count > 0;
            }
            finally
            {
                _updatingTransform = false;
            }
        }

        GraphSection.Summary = shown is null ? "Sin animación" : shown.Spec.Label;
        Graph.Show(clip, ClipPropertyEditor.FindEffect(clip, BuiltInEffects.Transform), shown?.Spec);
    }

    private void OnGraphParameterSelected(object sender, SelectionChangedEventArgs e)
    {
        // El refresco va después del evento: tocar el desplegable dentro de su propio SelectionChanged lo rompe.
        DispatcherQueue.Choose(_graphChoices, GraphParameterBox.SelectedIndex, _updatingTransform, name =>
        {
            if (name == _graphName)
            {
                return null;
            }

            _graphName = name;
            return Refresh;
        });
    }

    private void OnGraphModeClick(object sender, RoutedEventArgs e)
    {
        bool speed = sender is FrameworkElement { Tag: "speed" };
        GraphValueButton.IsChecked = !speed;
        GraphSpeedButton.IsChecked = speed;
        Graph.Mode = speed ? GraphMode.Speed : GraphMode.Value;
    }

    private void OnFlipClick(object sender, RoutedEventArgs e)
    {
        if (_updatingTransform || _services is not { } services || _videoClipId is not { } clipId || sender is not ToggleButton { Tag: string axis } button)
        {
            return;
        }

        bool on = button.IsChecked == true;
        string option = axis == "x" ? BuiltInEffects.TransformOptions.FlipX : BuiltInEffects.TransformOptions.FlipY;
        services.Session.Execute(EffectCommands.SetOption(services.Session.SequenceId, clipId, BuiltInEffects.Transform, null, option, on ? "on" : "",
            axis == "x" ? "Voltear en horizontal" : "Voltear en vertical"));
    }

    private void OnMotionBlurToggled(object sender, RoutedEventArgs e)
    {
        if (_updatingTransform || _editor is not { } editor || ClipsFor(ShutterRow) is not { Count: > 0 } clips)
        {
            return;
        }

        bool on = MotionBlurSwitch.IsOn;
        editor.SetValue(clips, BuiltInEffects.Transform, BuiltInEffects.TransformParameters.ShutterAngle, on ? 180 : 0,
            on ? "Activar el desenfoque de movimiento" : "Quitar el desenfoque de movimiento");
    }

    private static string BlendMode(Clip clip) =>
        ClipPropertyEditor.FindEffect(clip, BuiltInEffects.Blend)?.Options.GetValueOrDefault(BuiltInEffects.BlendOptions.Mode, "normal") ?? "normal";

    private static string KeyframeText(int count) => count == 0 ? "" : KeyframeDialog.Count(count);

    // «−120», «12,5»: coma decimal y signo menos tipográfico, como el resto de la interfaz.
    private static string Number(double value) =>
        Math.Round(value, 1).ToString("0.#", CultureInfo.InvariantCulture).Replace('.', ',').Replace('-', '\u2212');

    private void ShowAudio(ClipPropertyEditor editor, Clip clip, IReadOnlyList<Clip> clips)
    {
        double seconds = clip.Duration.ToSeconds();
        if (Math.Abs(_audioRange - seconds) > 1e-6)
        {
            _audioRange = seconds;
            IReadOnlyList<ParameterSpec> specs = ParameterSpec.Audio(seconds);
            GainRow.Configure(specs[0]);
            FadeInRow.Configure(specs[1], "Entrada");
            FadeOutRow.Configure(specs[2], "Salida");
        }

        bool inside = editor.IsPlayheadInside(clip);
        int keyframes = 0;
        double gain = 0;
        foreach (ParameterRow row in _audioRows)
        {
            ParameterState state = editor.Read(clip, row.Spec.EffectType, row.Spec.Name, row.Spec.DefaultValue);
            keyframes += state.KeyframeCount;
            if (ReferenceEquals(row, GainRow))
            {
                gain = row.Spec.ToDisplay(state.Value);
            }

            row.Show(state, inside, editor.IsMixed(clips, row.Spec.EffectType, row.Spec.Name, row.Spec.DefaultValue));
        }

        AudioSection.KeyframeSummary = KeyframeText(keyframes);
        AudioPreset? preset = AudioPresetCommands.Match(clip.Effects, out bool hasEffects);
        _updatingPreset = true;
        PresetBox.SelectedIndex = !hasEffects ? 0 : preset is null ? PresetBox.Items.Count - 1 : 1 + AudioPresets.All.ToList().IndexOf(preset);
        _updatingPreset = false;
        AudioSection.Summary = Number(gain) + " " + GainRow.Spec.Unit + (hasEffects ? " · " + (preset?.Name ?? "personalizado") : "");
        AudioReset.IsEnabled = ClipPropertyEditor.CanReset(clip, BuiltInEffects.Audio, Defaults(_audioRows));
    }

    private static IEnumerable<(string Name, double DefaultValue)> Defaults(IEnumerable<ParameterRow> rows) =>
        rows.Select(r => (r.Spec.Name, r.Spec.DefaultValue));

    private static void FinishChanges(ParameterRow[] rows)
    {
        foreach (ParameterRow row in rows)
        {
            row.FinishChange();
        }
    }

    private Clip? ClipFor(ParameterRow row)
    {
        string? id = Array.IndexOf(_videoRows, row) >= 0 ? _videoClipId : _audioClipId;
        return id is not null && _editor?.Session.Sequence.FindClip(id) is { } found ? found.Clip : null;
    }

    /// <summary>Los clips que edita la fila: todos los seleccionados de su parte del panel.</summary>
    private List<Clip> ClipsFor(ParameterRow row)
    {
        IReadOnlyList<string> ids = Array.IndexOf(_videoRows, row) >= 0 ? _videoClipIds : _audioClipIds;
        Sequence? sequence = _editor?.Session.Sequence;
        return sequence is null ? [] : [.. ids.Select(sequence.FindClip).OfType<(Track Track, Clip Clip)>().Select(f => f.Clip)];
    }

    private void OnRowChanging(object? sender, double value)
    {
        if (sender is not ParameterRow row || _editor is not { } editor || ClipsFor(row) is not { Count: > 0 } clips)
        {
            return;
        }

        if (Array.IndexOf(_videoRows, row) >= 0)
        {
            _graphName = row.Spec.Name;
        }

        if (!ReferenceEquals(_dragRow, row) || !editor.IsDragging)
        {
            _dragRow = row;
            editor.BeginChange(clips, row.Spec.EffectType, row.Spec.Name, row.Spec.Description);
        }

        editor.Change(value);
    }

    private void OnRowChangeEnded(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _dragRow))
        {
            return;
        }

        _dragRow = null;
        _editor?.EndChange();
        Refresh();
    }

    private void OnRowCommitted(object? sender, double value)
    {
        if (sender is ParameterRow row && _editor is { } editor && ClipsFor(row) is { Count: > 0 } clips)
        {
            editor.SetValue(clips, row.Spec.EffectType, row.Spec.Name, value, row.Spec.Description);
        }
    }

    private void OnRowKeyframeToggled(object? sender, EventArgs e)
    {
        if (sender is ParameterRow row && _editor is { } editor && ClipFor(row) is { } clip)
        {
            editor.ToggleKeyframe(clip, row.Spec.EffectType, row.Spec.Name, row.Spec.DefaultValue);
        }
    }

    private void OnRowNavigate(object? sender, int direction)
    {
        if (sender is ParameterRow row && _editor is { } editor && ClipFor(row) is { } clip)
        {
            editor.GoToKeyframe(clip, row.Spec.EffectType, row.Spec.Name, direction);
        }
    }

    private void OnRowInterpolation(object? sender, Interpolation interpolation)
    {
        if (sender is ParameterRow row && _editor is { } editor && ClipFor(row) is { } clip)
        {
            editor.SetInterpolation(clip, row.Spec.EffectType, row.Spec.Name, interpolation);
        }
    }

    private void OnRowReset(object? sender, EventArgs e) => UiTask.Run(OnRowResetAsync(sender, e));

    private async Task OnRowResetAsync(object? sender, EventArgs e)
    {
        if (sender is ParameterRow row && _editor is { } editor && ClipFor(row) is { } clip)
        {
            _dragRow = null;
            string label = row.Spec.Label;
            await editor.ResetAsync(clip, row.Spec.EffectType, [.. Defaults([row])], row.Spec.ResetDescription, KeyframeDialog.ForReset(XamlRoot, label));
        }
    }

    private void OnRowClearKeyframes(object? sender, EventArgs e) => UiTask.Run(OnRowClearKeyframesAsync(sender, e));

    private async Task OnRowClearKeyframesAsync(object? sender, EventArgs e)
    {
        if (sender is ParameterRow row && _editor is { } editor && ClipFor(row) is { } clip)
        {
            _dragRow = null;
            string label = row.Spec.Label;
            await editor.ClearKeyframesAsync(clip, row.Spec.EffectType, row.Spec.Name, "Quitar keyframes de " + char.ToLowerInvariant(label[0]) + label[1..],
                KeyframeDialog.ForClear(XamlRoot, label));
        }
    }

    private void OnResetAllClick(object sender, RoutedEventArgs e) =>
        UiTask.Run(ResetRowsAsync(_videoRows, "Restablecer transformación", "la transformación"));

    private void OnPlacementResetClick(object sender, RoutedEventArgs e) =>
        UiTask.Run(ResetRowsAsync([.. PlacementRows], "Restablecer posición y tamaño", "la posición y el tamaño"));

    private void OnShapeResetClick(object sender, RoutedEventArgs e) =>
        UiTask.Run(ResetRowsAsync([.. ShapeRows], "Restablecer ancla e inclinación", "el ancla y la inclinación"));

    private void OnAudioResetClick(object sender, RoutedEventArgs e) =>
        UiTask.Run(ResetRowsAsync(_audioRows, "Restablecer audio", "el volumen y los fundidos"));

    private async Task ResetRowsAsync(ParameterRow[] rows, string description, string what)
    {
        if (_editor is not { } editor || ClipFor(rows[0]) is not { } clip)
        {
            return;
        }

        FinishChanges(rows);
        _dragRow = null;
        await editor.ResetAsync(clip, rows[0].Spec.EffectType, [.. Defaults(rows)], description, KeyframeDialog.ForResetAll(XamlRoot, what));
    }

    private void OnPresetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingPreset || PresetBox.SelectedIndex < 0 || PresetBox.SelectedIndex == PresetBox.Items.Count - 1 || _services is not { } services
            || _audioClipId is not { } clipId)
        {
            return;
        }

        AudioPreset? preset = (PresetBox.SelectedItem as ComboBoxItem)?.Tag as AudioPreset;
        // La edición refresca el panel, que vuelve a elegir en este desplegable: va después del evento (tarea 91).
        DispatcherQueue.RunAfterEvent(() => services.Session.Execute(AudioPresetCommands.Apply(services.Session.SequenceId, clipId, preset)));
    }

    private void OnBlendSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingBlend || BlendBox.SelectedIndex < 0 || _services is not { } services || ClipFor(XRow) is not { } clip)
        {
            return;
        }

        string mode = EffectRegistry.BlendModes[BlendBox.SelectedIndex];
        if (BlendMode(clip) != mode)
        {
            services.Session.Execute(EffectCommands.SetBlendMode(services.Session.SequenceId, clip.Id, mode));
        }
    }

    private void OnSavePresetClick(object sender, RoutedEventArgs e) => UiTask.Run(OnSavePresetClickAsync(sender, e));

    private async Task OnSavePresetClickAsync(object sender, RoutedEventArgs e)
    {
        if (DialogGate.IsOpen || _services is not { } services || _videoClipId is null
            || services.Session.Sequence.FindClip(_videoClipId) is not { } found
            || ClipPropertyEditor.FindEffect(found.Clip, BuiltInEffects.Transform) is not { } transform
            || !AnimationPreset.CanExport(transform))
        {
            return;
        }

        EffectInstance animation = transform.Clone();
        Sequence sequence = services.Session.Sequence;
        var nameBox = new TextBox { Text = DefaultPresetName, SelectionStart = 0, SelectionLength = DefaultPresetName.Length };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = "La animación de la transformación quedará en tus complementos propios, lista para aplicarla a otros clips o publicarla como paquete.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(nameBox);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Guardar como transición",
            Content = content,
            PrimaryButtonText = "Guardar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ElementTheme.Dark,
        };

        if (await DialogGate.ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        string name = string.IsNullOrWhiteSpace(nameBox.Text) ? DefaultPresetName : nameBox.Text.Trim();
        try
        {
            string path = AnimationPreset.Save(AnimationPreset.UserFolder, name, animation, sequence.Width, sequence.Height);
            services.Session.Notify(NotificationSeverity.Success, "Transición guardada", $"«{name}» quedó en tus complementos propios: {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            services.Session.Notify(NotificationSeverity.Error, "No se pudo guardar la transición", ex.Message);
        }
    }
}
