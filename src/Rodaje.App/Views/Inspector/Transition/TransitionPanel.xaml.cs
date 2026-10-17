using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Format;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Settings;
using Rodaje.App.Services.Titles;
using Rodaje.App.Shell;
using Rodaje.App.Views.Inspector.Properties;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Packages;
using Windows.UI;
using Tp = Rodaje.Core.Model.BuiltInTransitions.CommonParameters;
using Tr = Rodaje.Core.Model.BuiltInTransitions;

namespace Rodaje.App.Views.Inspector.Transition;

/// <summary>
/// Inspector de la transición seleccionada (su id en la selección de la sesión): miniatura animada compuesta en la GPU, tipo,
/// duración, alineación, dirección, colores, parámetros y la curva del avance. Los parámetros de las transiciones no tienen
/// keyframes: un arrastre funde sus pasos en uno del historial.
/// </summary>
internal sealed partial class TransitionPanel : UserControl, IEditorView
{
    private const string DurationName = "duration";
    private const int PreviewWidth = 320;
    private const int PreviewHeight = 180;

    /// <summary>Los colores que se editan con una muestra: el del borde y el del fundido o la luz (rojo, verde y azul).</summary>
    private static readonly (string Label, string Red, string Green, string Blue)[] ColorGroups =
    [
        ("Color del borde", Tp.BorderRed, Tp.BorderGreen, Tp.BorderBlue),
        ("Color", Tp.Red, Tp.Green, Tp.Blue),
    ];

    private static readonly (Interpolation? Curve, string Name)[] Curves =
    [
        (null, "Lineal"),
        (Interpolation.EaseInOut, "Suave"),
        (Interpolation.EaseIn, "Entrada"),
        (Interpolation.EaseOut, "Salida"),
        (Interpolation.Bounce, "Rebote"),
        (Interpolation.Elastic, "Elástico"),
        (Interpolation.Bezier, "Bézier"),
    ];

    private static readonly (TransitionAlignment Value, string Name, string Help)[] Alignments =
    [
        (TransitionAlignment.Before, "Al final del clip que sale", "Termina en el corte: usa solo el final del clip que sale"),
        (TransitionAlignment.Center, "Centrada en el corte", "La mitad antes del corte y la mitad después"),
        (TransitionAlignment.After, "Al inicio del clip que entra", "Empieza en el corte: usa solo el inicio del clip que entra"),
    ];

    private readonly List<ParameterRow> _rows = [];
    /// <summary>Lo que muestra ahora el desplegable de tipo (ver <see cref="ComboBoxChoices"/>).</summary>
    private ChoiceList<(string Type, string Preset)> _types = new();
    private readonly ClosedListRetry _typesRetry = new();
    private readonly List<ToggleButton> _alignmentButtons = [];
    private readonly List<ToggleButton> _curveButtons = [];
    private readonly List<ToggleButton> _directionButtons = [];
    private readonly List<(Button Button, Rectangle Swatch, (string Label, string Red, string Green, string Blue) Group)> _swatches = [];
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private EditorServices? _services;
    private TransitionEditor? _editor;
    private TransitionPreviews? _previews;
    private string? _transitionId;
    private string? _rowsType;
    private string? _previewKey;
    private (PackageScan Scan, string Type, string Preset, TrackKind Kind)? _typesShown;
    private (double Minimum, double Maximum) _durationRange;
    private DateTime _lastTick;
    private bool _paused;
    private bool _updating;

    public TransitionPanel()
    {
        InitializeComponent();
        TypeBox.RetryWhenClosed(_typesRetry, Refresh);
        foreach ((TransitionAlignment value, string name, string help) in Alignments)
        {
            var button = new ToggleButton
            {
                Style = (Style)Application.Current.Resources["ToolToggleButtonStyle"],
                Width = 40,
                Height = 28,
                Padding = new Thickness(0),
                Content = AlignmentIcon(value),
                Tag = value,
            };
            ToolTipService.SetToolTip(button, $"{name}. {help}.");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
            button.Click += OnAlignmentClick;
            _alignmentButtons.Add(button);
            AlignmentButtons.Children.Add(button);
        }

        foreach ((Interpolation? curve, string name) in Curves)
        {
            var button = new ToggleButton
            {
                Style = (Style)Application.Current.Resources["ToolToggleButtonStyle"],
                Content = name,
                Margin = new Thickness(0, 0, 2, 2),
                Padding = new Thickness(4, 0, 4, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                FontSize = (double)Application.Current.Resources["CaptionFontSize"],
                Tag = curve,
            };
            ToolTipService.SetToolTip(button, curve is { } c ? KeyframeEase.Help(c) : "Velocidad constante");
            button.Click += OnCurveClick;
            _curveButtons.Add(button);
            CurveButtons.Children.Add(button);
        }

        string[] arrows = ["←", "→", "↑", "↓"];
        for (int i = 0; i < EffectRegistry.PushDirections.Length; i++)
        {
            var button = new ToggleButton
            {
                Style = (Style)Application.Current.Resources["ToolToggleButtonStyle"],
                Width = 34,
                Height = 28,
                Content = arrows[i],
                Tag = EffectRegistry.PushDirections[i],
            };
            ToolTipService.SetToolTip(button, "El clip que sale va " + EffectUi.PushDirections[i].Name.ToLowerInvariant());
            button.Click += OnDirectionClick;
            _directionButtons.Add(button);
            DirectionButtons.Children.Add(button);
        }

        foreach ((double degrees, string arrow) in new[] { (0.0, "→"), (90.0, "↓"), (180.0, "←"), (-90.0, "↑") })
        {
            var button = new Button
            {
                Style = (Style)Application.Current.Resources["ToolButtonStyle"],
                Width = 26,
                Height = 24,
                Content = arrow,
                Tag = degrees,
            };
            ToolTipService.SetToolTip(button, $"{degrees:0}°");
            button.Click += (_, _) => ApplyAngle(degrees, commit: true);
            AngleQuick.Children.Add(button);
        }

        foreach ((string name, _) in EffectUi.LumaGradients)
        {
            GradientBox.Items.Add(name);
        }

        Dial.Changing += (_, degrees) => ApplyAngle(degrees, commit: false);
        Dial.Committed += (_, _) => _editor?.Session.BreakMerge();
        Curve.Changing += (_, ease) =>
        {
            if (_editor is { } editor && _transitionId is { } id)
            {
                editor.SetCurve(id, Interpolation.Bezier, ease);
            }
        };
        Curve.Committed += (_, _) =>
        {
            _editor?.Session.BreakMerge();
            Refresh();
        };
        Thumb.ProgressChanged += (_, linear) => Curve.ShowProgress(linear);
        Wire(DurationRow);
        _clock.Tick += OnClockTick;
        Loaded += (_, _) => UpdateClock();
        Unloaded += (_, _) => _clock.Stop();
    }

    /// <summary>Cambió si hay una transición seleccionada (el anfitrión decide qué se ve en Propiedades).</summary>
    public event EventHandler? ActiveChanged;

    public bool IsActive => _transitionId is not null;

    public void Attach(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        _editor = new TransitionEditor(services.Session);
        _previews = TransitionPreviewHost.For(services);
        services.Session.SelectionChanged += (_, _) => Refresh();
        services.Session.ProjectEdited += (_, _) => Refresh();
        services.Session.ProjectOpened += (_, _) => Refresh();
        services.Packages.Changed += (_, _) =>
        {
            _previewKey = null;
            Refresh();
        };
        Refresh();
    }

    private void Refresh()
    {
        if (_editor is null || _services is null)
        {
            return;
        }

        bool wasActive = IsActive;
        TransitionTarget? target = _editor.Selected();
        if (target?.Transition.Id != _transitionId)
        {
            FinishChanges();
            Thumb.Restart();
        }

        _transitionId = target?.Transition.Id;
        if (target is not null)
        {
            Show(target);
        }

        UpdateClock();
        if (wasActive != IsActive)
        {
            ActiveChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Show(TransitionTarget target)
    {
        TransitionInstance transition = target.Transition;
        EditorSession session = _editor!.Session;
        CutText.Text = $"Entre «{ClipName(session, target.From)}» y «{ClipName(session, target.To)}».";
        ToolTipService.SetToolTip(CutText, CutText.Text);
        string preset = transition.Type == Tr.Preset ? TransitionEditor.ReadOption(transition, Tr.PresetOptions.Preset) : "";
        bool audio = target.Track.Kind == TrackKind.Audio;
        ShowTypes(transition.Type, preset, target.Track.Kind);

        bool missing = preset.Length > 0 && !PresetInstalled(preset);
        TypeNote.Text = missing ? "Falta el paquete de esta transición: se ve como fundido cruzado." : EffectUi.Description(transition.Type);
        TypeNote.Visibility = TypeNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        (double minimum, double maximum) = _editor.DurationRange(target);
        if (_durationRange != (minimum, maximum))
        {
            _durationRange = (minimum, maximum);
            DurationRow.Configure(new ParameterSpec("transition", DurationName, "Duración", "Cambiar duración de la transición",
                minimum, maximum, 0.05, 1, EffectRegistry.DefaultTransitionDuration.ToSeconds(), 2, "s", false)
            {
                ModelMinimum = minimum,
            });
        }

        DurationRow.Show(new ParameterState(transition.Duration.ToSeconds(), 0, null, null, null), true);
        _updating = true;
        foreach (ToggleButton button in _alignmentButtons)
        {
            button.IsChecked = (TransitionAlignment)button.Tag == transition.Alignment;
        }

        _updating = false;

        EffectDefinition? definition = EffectRegistry.Find(transition.Type);
        bool hasAngle = definition?.FindParameter(Tp.Angle) is not null && transition.Type != Tr.Spin;
        AngleRow.Visibility = hasAngle ? Visibility.Visible : Visibility.Collapsed;
        if (hasAngle)
        {
            double angle = TransitionEditor.Read(transition, Tp.Angle);
            Dial.Degrees = angle;
            AngleText.Text = $"{angle:0}°";
        }

        bool pushes = transition.Type is Tr.Push or Tr.Bounce;
        DirectionRow.Visibility = pushes ? Visibility.Visible : Visibility.Collapsed;
        if (pushes)
        {
            string direction = TransitionEditor.ReadOption(transition, Tr.PushOptions.Direction);
            _updating = true;
            foreach (ToggleButton button in _directionButtons)
            {
                button.IsChecked = (string)button.Tag == direction;
            }

            _updating = false;
        }

        if (_rowsType != transition.Type)
        {
            BuildRows(transition.Type);
        }

        foreach (ParameterRow row in _rows)
        {
            row.Show(new ParameterState(TransitionEditor.Read(transition, row.Spec.Name), 0, null, null, null), true);
        }

        foreach ((Button _, Rectangle swatch, var group) in _swatches)
        {
            swatch.Fill = new SolidColorBrush(ColorOf(transition, group));
        }

        ShowLuma(transition);
        ShowCurve(transition);
        PreviewFrame.Visibility = audio ? Visibility.Collapsed : Visibility.Visible;
        if (!audio)
        {
            RequestPreview(transition);
        }
    }

    private void ShowLuma(TransitionInstance transition)
    {
        bool luma = transition.Type == Tr.Luma;
        LumaRows.Visibility = luma ? Visibility.Visible : Visibility.Collapsed;
        if (!luma)
        {
            return;
        }

        _updating = true;
        GradientBox.SelectedIndex = Math.Max(0, EffectRegistry.LumaGradients.IndexOf(TransitionEditor.ReadOption(transition, Tr.LumaOptions.Gradient)));
        InvertToggle.IsChecked = TransitionEditor.ReadOption(transition, Tr.LumaOptions.Invert) == "on";
        string image = TransitionEditor.ReadOption(transition, Tr.LumaOptions.Image);
        ImageText.Text = image.Length > 0 ? System.IO.Path.GetFileName(image[OptionDefinition.FilePrefix.Length..]) : "Elegir imagen…";
        ClearImageButton.Visibility = image.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        GradientBox.IsEnabled = image.Length == 0;
        _updating = false;
    }

    private void ShowCurve(TransitionInstance transition)
    {
        _updating = true;
        foreach (ToggleButton button in _curveButtons)
        {
            button.IsChecked = (Interpolation?)button.Tag == transition.Curve;
        }

        _updating = false;
        Curve.Show(transition.Curve, transition.Ease);
        CurveHelp.Text = transition.Curve is { } c
            ? KeyframeEase.Help(c) + ". La misma curva mueve la imagen y, en audio, el cruce de volumen."
            : "Velocidad constante. La misma curva mueve la imagen y, en audio, el cruce de volumen.";
    }

    private void RequestPreview(TransitionInstance transition) => UiTask.Run(RequestPreviewAsync(transition));

    private async Task RequestPreviewAsync(TransitionInstance transition)
    {
        if (_previews is null)
        {
            return;
        }

        string key = TransitionPreviews.Signature(transition, PreviewWidth, PreviewHeight);
        if (key == _previewKey)
        {
            return;
        }

        _previewKey = key;
        PreviewLabel.Text = transition.Type == Tr.Preset ? "Transición de paquete" : EffectUi.Name(transition.Type);
        PreviewNote.Visibility = Thumb.HasAnimation ? Visibility.Collapsed : Visibility.Visible;
        TransitionAnimation? animation = await _previews.GetAsync(transition, PreviewWidth, PreviewHeight);
        if (key != _previewKey)
        {
            return;
        }

        Thumb.Show(animation);
        PreviewNote.Text = animation is null ? "Sin vista previa (la GPU no está lista)." : "";
        PreviewNote.Visibility = animation is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateClock()
    {
        bool run = IsActive && IsLoaded && !_paused && PreviewFrame.Visibility == Visibility.Visible;
        if (run && !_clock.IsEnabled)
        {
            _lastTick = DateTime.UtcNow;
            _clock.Start();
        }
        else if (!run && _clock.IsEnabled)
        {
            _clock.Stop();
        }
    }

    private void OnClockTick(object? sender, object e)
    {
        DateTime now = DateTime.UtcNow;
        Thumb.Advance(Math.Min(0.1, (now - _lastTick).TotalSeconds));
        _lastTick = now;
    }

    private void OnPreviewTapped(object sender, TappedRoutedEventArgs e)
    {
        _paused = !_paused;
        PausedIcon.Visibility = _paused ? Visibility.Visible : Visibility.Collapsed;
        UpdateClock();
    }

    private bool PresetInstalled(string reference) =>
        PackageReference.TryParse(reference, out PackageReference? parsed)
        && _services!.Packages.Installed.Find(parsed.PackageId)?.Manifest.FindItem(parsed.ItemId, ItemKind.Transition) is not null;

    private void ShowTypes(string type, string preset, TrackKind kind)
    {
        PackageScan installed = _services!.Packages.Installed;
        if (_typesShown is { } shown && ReferenceEquals(shown.Scan, installed) && shown.Type == type && shown.Preset == preset && shown.Kind == kind)
        {
            return;
        }

        // Con la lista abierta no se toca: el próximo refresco la pone al día.
        if (_typesRetry.Defer(TypeBox.IsDropDownOpen))
        {
            return;
        }

        _typesShown = (installed, type, preset, kind);
        // Un tipo de imagen en una pista de audio (de antes del formato 12) o un paquete que falta se muestra igual.
        (ChoiceList<(string Type, string Preset)> types, int index) = TransitionChoices.Types(kind, installed, type, preset);
        _updating = true;
        _types = ComboBoxChoices.Show(TypeBox, _types, types, index);
        _updating = false;
    }

    private void BuildRows(string type)
    {
        FinishChanges();
        ParameterRows.Children.Clear();
        ColorRows.Children.Clear();
        _rows.Clear();
        _swatches.Clear();
        EffectDefinition? definition = EffectRegistry.Find(type);
        var special = new HashSet<string>(StringComparer.Ordinal);
        if (definition?.FindParameter(Tp.Angle) is not null && type != Tr.Spin)
        {
            special.Add(Tp.Angle);
        }

        foreach (var group in ColorGroups)
        {
            if (definition?.FindParameter(group.Red) is null || !special.Add(group.Red))
            {
                continue;
            }

            special.Add(group.Green);
            special.Add(group.Blue);
            string label = type is Tr.LightWipe or Tr.Flash or Tr.FilmBurn ? "Color de la luz" : group.Label;
            ColorRows.Children.Add(SwatchRow(label, group));
        }

        foreach (ParameterSpec spec in ParameterSpec.ForEffect(type, default))
        {
            if (special.Contains(spec.Name))
            {
                continue;
            }

            var row = new ParameterRow();
            row.Configure(spec);
            Wire(row);
            _rows.Add(row);
            ParameterRows.Children.Add(row);
        }

        _rowsType = type;
    }

    private Grid SwatchRow(string label, (string Label, string Red, string Green, string Blue) group)
    {
        var grid = new Grid { Padding = new Thickness(12, 4, 8, 4), ColumnSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = new TextBlock { Text = label, Style = (Style)Application.Current.Resources["LabelTextStyle"], VerticalAlignment = VerticalAlignment.Center };
        var swatch = new Rectangle { Width = 44, Height = 16, RadiusX = 2, RadiusY = 2, Stroke = (Brush)Application.Current.Resources["ControlBorderBrush"] };
        var picker = new ColorPicker
        {
            IsAlphaEnabled = false,
            IsMoreButtonVisible = false,
            IsColorChannelTextInputVisible = false,
            IsHexInputVisible = true,
            ColorSpectrumShape = ColorSpectrumShape.Ring,
        };
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["RodajeButtonStyle"],
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = swatch,
            Flyout = new Flyout { Content = picker },
        };
        ToolTipService.SetToolTip(button, "Elegir " + label.ToLowerInvariant());
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        bool opening = false;
        button.Flyout.Opening += (_, _) =>
        {
            if (_editor?.Selected() is { } target)
            {
                opening = true;
                picker.Color = ColorOf(target.Transition, group);
                opening = false;
            }
        };
        button.Flyout.Closed += (_, _) => _editor?.Session.BreakMerge();
        picker.ColorChanged += (_, e) =>
        {
            if (!opening && _editor is { } editor && _transitionId is { } id)
            {
                editor.SetColor(id, group.Red, group.Green, group.Blue, e.NewColor.R / 255.0, e.NewColor.G / 255.0, e.NewColor.B / 255.0);
            }
        };
        Grid.SetColumn(button, 1);
        grid.Children.Add(text);
        grid.Children.Add(button);
        _swatches.Add((button, swatch, group));
        return grid;
    }

    private static Color ColorOf(TransitionInstance transition, (string Label, string Red, string Green, string Blue) group) => Color.FromArgb(255,
        Channel(TransitionEditor.Read(transition, group.Red)), Channel(TransitionEditor.Read(transition, group.Green)),
        Channel(TransitionEditor.Read(transition, group.Blue)));

    private static byte Channel(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);

    /// <summary>Un ícono de la alineación: la línea del corte y el bloque de la transición antes, encima o después.</summary>
    private static Canvas AlignmentIcon(TransitionAlignment alignment)
    {
        var canvas = new Canvas { Width = 26, Height = 14 };
        var block = new Rectangle
        {
            Width = 12,
            Height = 10,
            RadiusX = 1.5,
            RadiusY = 1.5,
            Fill = (Brush)Application.Current.Resources["AccentBrush"],
        };
        Canvas.SetTop(block, 2);
        Canvas.SetLeft(block, alignment switch
        {
            TransitionAlignment.Before => 1,
            TransitionAlignment.After => 13,
            _ => 7,
        });
        var cut = new Rectangle { Width = 1.5, Height = 14, Fill = (Brush)Application.Current.Resources["TextBrush"] };
        Canvas.SetLeft(cut, 12.25);
        canvas.Children.Add(block);
        canvas.Children.Add(cut);
        return canvas;
    }

    private void Wire(ParameterRow row)
    {
        row.Changing += (_, value) => Apply(row, value);
        row.Committed += (_, value) =>
        {
            Apply(row, value);
            _editor?.Session.BreakMerge();
        };
        row.ChangeEnded += (_, _) =>
        {
            _editor?.Session.BreakMerge();
            Refresh();
        };
        row.ResetRequested += (_, _) =>
        {
            Apply(row, row.Spec.DefaultValue);
            _editor?.Session.BreakMerge();
        };
    }

    private void Apply(ParameterRow row, double value)
    {
        if (_editor is not { } editor || _transitionId is not { } id)
        {
            return;
        }

        if (row.Spec.Name == DurationName && ReferenceEquals(row, DurationRow))
        {
            editor.SetDuration(id, value);
        }
        else
        {
            editor.SetParameter(id, row.Spec.Name, value);
        }
    }

    private void ApplyAngle(double degrees, bool commit)
    {
        if (_editor is { } editor && _transitionId is { } id)
        {
            editor.SetParameter(id, Tp.Angle, degrees);
            if (commit)
            {
                editor.Session.BreakMerge();
            }
        }
    }

    private void FinishChanges()
    {
        DurationRow.FinishChange();
        foreach (ParameterRow row in _rows)
        {
            row.FinishChange();
        }
    }

    private static string ClipName(EditorSession session, Clip clip) => ClipLabels.Name(session.Project, clip, "clip");

    private void OnTypeSelected(object sender, SelectionChangedEventArgs e)
    {
        // Cambiar el tipo edita y refresca el panel, que puede rehacer este desplegable: va después del evento.
        DispatcherQueue.Choose(_types, TypeBox.SelectedIndex, _updating, choice =>
            _editor is null || _transitionId is not { } id ? null : () => ChooseType(id, choice.Type, choice.Preset));
    }

    private void ChooseType(string id, string type, string preset)
    {
        if (_editor is not { } editor || _transitionId != id)
        {
            return;
        }

        bool done = preset.Length > 0 ? editor.SetPreset(id, preset) : editor.SetType(id, type);
        if (!done)
        {
            _typesShown = null;
            Refresh();
        }
    }

    private void OnAlignmentClick(object sender, RoutedEventArgs e)
    {
        if (_updating || sender is not ToggleButton { Tag: TransitionAlignment alignment } || _editor is not { } editor || _transitionId is not { } id)
        {
            return;
        }

        if (!editor.SetAlignment(id, alignment))
        {
            Refresh();
        }
    }

    private void OnCurveClick(object sender, RoutedEventArgs e)
    {
        if (_updating || sender is not ToggleButton button || _editor is not { } editor || _transitionId is not { } id)
        {
            return;
        }

        editor.SetCurve(id, (Interpolation?)button.Tag);
        editor.Session.BreakMerge();
        Refresh();
    }

    private void OnDirectionClick(object sender, RoutedEventArgs e)
    {
        if (!_updating && sender is ToggleButton { Tag: string direction } && _editor is { } editor && _transitionId is { } id)
        {
            editor.SetOption(id, Tr.PushOptions.Direction, direction);
            Refresh();
        }
    }

    private void OnGradientSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating && GradientBox.SelectedIndex >= 0 && _editor is { } editor && _transitionId is { } id)
        {
            editor.SetOption(id, Tr.LumaOptions.Gradient, EffectRegistry.LumaGradients[GradientBox.SelectedIndex]);
        }
    }

    private void OnInvertClick(object sender, RoutedEventArgs e)
    {
        if (!_updating && _editor is { } editor && _transitionId is { } id)
        {
            editor.SetOption(id, Tr.LumaOptions.Invert, InvertToggle.IsChecked == true ? "on" : "off");
        }
    }

    private void OnImageClick(object sender, RoutedEventArgs e)
    {
        if (_editor is not { } editor || _transitionId is not { } id || _services is null)
        {
            return;
        }

        nint owner = XamlRoot?.ContentIslandEnvironment is { } environment ? Win32Interop.GetWindowFromWindowId(environment.AppWindowId) : 0;
        string folder = FileDialogFolderPolicy.OpenFolder(_services.Settings.Current);
        try
        {
            IReadOnlyList<string> picked = FileDialogs.Open(owner, "Imagen de la disolución por luma", multiple: false, folder,
                ("Imágenes", "*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.webp"), FileDialogs.AllFilter);
            FileDialogFolderPolicy.RememberOpen(_services.Settings.Current, picked.Count > 0 ? picked[0] : null);
            if (picked.Count > 0)
            {
                editor.SetOption(id, Tr.LumaOptions.Image, LutReference.ForFile(picked[0]));
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _services.Session.Notify(NotificationSeverity.Error, "No se pudo abrir el selector de archivos", ex.Message);
        }
    }

    private void OnClearImageClick(object sender, RoutedEventArgs e)
    {
        if (_editor is { } editor && _transitionId is { } id)
        {
            editor.SetOption(id, Tr.LumaOptions.Image, "");
        }
    }

    private void OnUseAsDefaultClick(object sender, RoutedEventArgs e)
    {
        if (_services is null || _editor?.Selected() is not { } target)
        {
            return;
        }

        DefaultTransition.Remember(_services.Settings.Current.Transitions, target.Transition);
        _services.Settings.Save();
        string name = target.Transition.Type == Tr.Preset ? "la transición de paquete" : $"«{EffectUi.Name(target.Transition.Type)}»";
        _services.Session.Notify(NotificationSeverity.Success, "Transición por defecto",
            string.Format(SpanishNumbers.Format, "Ctrl+T pone {0} de {1:0.##} s en el corte más cercano.", name, target.Transition.Duration.ToSeconds()));
    }

    private void OnDurationToTrackClick(object sender, RoutedEventArgs e) => ApplyDuration(TransitionEditor.DurationScope.Track);

    private void OnDurationToSequenceClick(object sender, RoutedEventArgs e) => ApplyDuration(TransitionEditor.DurationScope.Sequence);

    private void ApplyDuration(TransitionEditor.DurationScope scope)
    {
        if (_editor is not { } editor || _transitionId is not { } id)
        {
            return;
        }

        FinishChanges();
        int changed = editor.ApplyDuration(id, scope);
        editor.Session.BreakMerge();
        editor.Session.Notify(NotificationSeverity.Informational, "Duración de las transiciones",
            changed == 0 ? "Ya tenían todas esa duración." : $"{changed} transiciones con la misma duración (las que no caben, lo más que permiten sus clips).");
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (_editor is { } editor && _transitionId is { } id)
        {
            FinishChanges();
            editor.Remove(id);
        }
    }
}
