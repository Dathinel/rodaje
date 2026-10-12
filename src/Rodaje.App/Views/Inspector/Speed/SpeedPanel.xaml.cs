using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Automation;
using Rodaje.App.Services;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Titles;
using Rodaje.App.Shell;
using Rodaje.App.Theme;
using Rodaje.App.Views.Shared;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Proxies;

namespace Rodaje.App.Views.Inspector.Speed;

/// <summary>La pestaña Velocidad. Las cuentas y las ediciones están en <see cref="SpeedEditor"/> y <see cref="SpeedMath"/>.</summary>
internal sealed partial class SpeedPanel : UserControl, IEditorView
{
    private static readonly TimeSpan KeyboardSettle = TimeSpan.FromMilliseconds(350);

    // La miniatura de «Personalizada» mientras el clip no tiene una curva propia.
    private static readonly SpeedPoint[] CustomGlyph = [new(0, 60), new(3000, 250), new(6500, 40), new(10_000, 200)];

    private const double GlyphWidth = 44;
    private const double GlyphHeight = 18;

    private readonly List<(Button Button, Polyline Glyph)> _curveTiles = [];

    private readonly DispatcherQueueTimer _settle;
    private static readonly ProxyPolicy DefaultPolicy = new();

    private SpeedEditor? _editor;
    private EditorServices? _services;
    private MediaSourceResolver? _resolver;
    private volatile string? _noticePath;
    private string? _clipId;
    private bool _updating;
    private bool _pointerDown;
    private bool _freezeShown;

    public SpeedPanel()
    {
        InitializeComponent();
        _settle = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _settle.IsRepeating = false;
        _settle.Tick += (_, _) => FinishChange();

        // El deslizador marca la pulsación como atendida; hace falta verla igual para saber cuándo termina el arrastre.
        RateSlider.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _pointerDown = true), true);
        RateSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => EndPointer()), true);
        RateSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndPointer()), true);
        RateSlider.LostFocus += (_, _) => FinishChange();

        foreach (Rational rate in SpeedTabValues.Quick)
        {
            // Columnas iguales: con el panel estrecho los botones se encogen en vez de salirse.
            var button = new Button
            {
                Content = SpeedMath.FormatRate(rate),
                Padding = new Thickness(2, 2, 2, 2),
                MinWidth = 0,
                FontSize = ThemeResources.Size("BodyFontSize"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Tag = rate,
            };
            button.Click += OnQuickClick;
            QuickPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(button, QuickPanel.Children.Count);
            QuickPanel.Children.Add(button);
        }

        BuildCurveTiles();
    }

    // Una baldosa por opción, cuatro por fila, con la forma en miniatura y el nombre debajo.
    private void BuildCurveTiles()
    {
        const int columns = 4;
        for (int c = 0; c < columns; c++)
        {
            PresetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        IReadOnlyList<CurveOption> options = SpeedEditor.CurveOptions;
        for (int r = 0; r < (options.Count + columns - 1) / columns; r++)
        {
            PresetGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (int i = 0; i < options.Count; i++)
        {
            CurveOption option = options[i];
            var glyph = new Polyline
            {
                Width = GlyphWidth,
                Height = GlyphHeight,
                StrokeThickness = 1.5,
                StrokeLineJoin = PenLineJoin.Round,
                Stroke = (Brush)Application.Current.Resources["VideoClipEdgeBrush"],
                HorizontalAlignment = HorizontalAlignment.Center,
                Points = CurveGlyph.Line(option.Preset is { } preset ? Core.Model.SpeedPresets.Points(preset)
                    : option.IsCustom ? CustomGlyph : SpeedCurveMath.Flat(1), GlyphWidth, GlyphHeight, 2),
            };
            var label = new TextBlock
            {
                Text = option.Name,
                FontSize = ThemeResources.Size("CaptionFontSize"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var content = new StackPanel { Spacing = 2 };
            content.Children.Add(glyph);
            content.Children.Add(label);
            var button = new Button
            {
                Content = content,
                Padding = new Thickness(2, 4, 2, 3),
                MinWidth = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Tag = i,
            };
            AutomationProperties.SetName(button, "Curva: " + option.Name);
            ToolTipService.SetToolTip(button, option.Preset is not null ? $"Curva «{option.Name}»"
                : option.IsCustom ? "Curva propia: arrastra sus puntos" : "Sin curva: vuelve a 1×");
            button.Click += OnCurveTileClick;
            Grid.SetColumn(button, i % columns);
            Grid.SetRow(button, i / columns);
            PresetGrid.Children.Add(button);
            _curveTiles.Add((button, glyph));
        }
    }

    public void Attach(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        _editor = new SpeedEditor(services.Session, SpeedCommands.Default);
        RateSection.Remember(services.Settings);
        CurveSection.Remember(services.Settings);
        MoreSection.Remember(services.Settings);
        CurveEditor.Attach(_editor);
        EditorSession session = services.Session;
        session.SelectionChanged += (_, _) => Refresh();
        session.ProjectEdited += (_, _) => Refresh();
        session.ProjectOpened += (_, _) => Refresh();
        session.TransportChanged += (_, _) =>
        {
            // Del cursor dependen «Congelar» y la línea del cursor en la curva.
            if (Body.Visibility != Visibility.Visible || _editor.Target() is not { } clip)
            {
                return;
            }

            // El resolvedor de proxies llega cuando arranca el reproductor o cambia con sus ajustes.
            if (!ReferenceEquals(services.Player.ProxyResolver, _resolver))
            {
                ShowReverseNotice(clip);
            }

            if (_editor.CanFreeze(clip) != _freezeShown)
            {
                Refresh();
                return;
            }

            CurveEditor.ShowPlayhead(SpeedCurveMath.AtOf(clip, session.Position));
        };
        Refresh();
    }

    private const string EmptyHint = "Selecciona un clip en la línea de tiempo para cambiar su velocidad.";

    private void Refresh()
    {
        if (_editor is not { } editor)
        {
            return;
        }

        Clip? clip = editor.Target();
        if (clip?.Id != _clipId)
        {
            FinishChange();
        }

        _clipId = clip?.Id;
        bool title = clip?.Generator is not null;
        EmptyText.Text = title ? ClipLabels.NoMaterial : EmptyHint;
        EmptyText.Visibility = clip is null || title ? Visibility.Visible : Visibility.Collapsed;
        Body.Visibility = clip is null || title ? Visibility.Collapsed : Visibility.Visible;
        if (clip is null || title)
        {
            return;
        }

        SpeedState state = editor.Read(clip);
        _updating = true;
        ClipTitle.Text = ClipLabels.Name(editor.Session.Project, clip);
        ToolTipService.SetToolTip(ClipTitle, ClipTitle.Text);
        if (!editor.IsChanging)
        {
            RateSlider.Value = SpeedMath.ToSlider(state.Rate.ToDouble());
        }

        if (RateBox.FocusState == FocusState.Unfocused)
        {
            RateBox.Text = state.IsFrozen ? "0×" : SpeedMath.FormatRate(state.Rate);
        }

        if (DurationBox.FocusState == FocusState.Unfocused)
        {
            DurationBox.Text = SpeedMath.FormatDuration(state.Duration);
        }

        SourceDurationText.Text = state.IsFrozen ? "Congelado" : SpeedMath.FormatDuration(state.SourceDuration) + " →";
        RateSlider.IsEnabled = state.CanChangeRate;
        RateBox.IsEnabled = state.CanChangeRate;
        DurationBox.IsEnabled = state.CanChangeRate;
        foreach (UIElement button in QuickPanel.Children)
        {
            ((Control)button).IsEnabled = state.CanChangeRate;
        }

        ResetButton.IsEnabled = state.CanReset;
        PitchBox.IsChecked = state.PreservePitch;
        PitchBox.IsEnabled = state.CanChangePitch;
        ReverseBox.IsChecked = state.IsReversed;
        ReverseBox.IsEnabled = state.CanReverse;
        ReverseBadge.Visibility = state.IsReversed ? Visibility.Visible : Visibility.Collapsed;
        ReverseHint.Text = !state.CanReverse ? "Un cuadro congelado no se invierte." : "";
        MoreSection.Summary = state.IsReversed ? ReversedLabel : "";
        ShowReverseNotice(clip);
        _freezeShown = editor.CanFreeze(clip);
        FreezeButton.IsEnabled = _freezeShown;
        FreezeHint.Text = !state.IsVideo ? "Solo se congelan clips de video."
            : state.IsFrozen ? "Este clip ya es un cuadro congelado."
            : _freezeShown ? "Detiene el cuadro del cursor durante 3 s."
            : "Pon el cursor sobre el clip para congelar ese cuadro.";
        ShowCurve(editor, clip, state);
        ShowNote(state);
        if (!editor.IsChanging)
        {
            ShowWarning(state.IsFrozen ? 1 : state.Rate.ToDouble(), state.HasAudio);
        }

        _updating = false;
    }

    private void ShowCurve(SpeedEditor editor, Clip clip, SpeedState state)
    {
        CurveState curve = SpeedEditor.ReadCurve(clip);
        for (int i = 0; i < _curveTiles.Count; i++)
        {
            (Button button, Polyline glyph) = _curveTiles[i];
            bool chosen = i == curve.Option;
            button.IsEnabled = curve.CanEdit;
            // Lo elegido en gris, como en las listas (D-027).
            if (chosen)
            {
                button.Background = (Brush)Application.Current.Resources["ControlPressedBrush"];
                button.BorderBrush = (Brush)Application.Current.Resources["TextMutedBrush"];
            }
            else
            {
                button.ClearValue(BackgroundProperty);
                button.ClearValue(BorderBrushProperty);
            }

            if (i == CurveState.CustomOption)
            {
                glyph.Points = CurveGlyph.Line(curve.Option == CurveState.CustomOption && curve.Points is { } own ? own : CustomGlyph, GlyphWidth, GlyphHeight, 2);
            }
        }

        bool hasCurve = curve.Points is not null;
        CurveSection.Summary = SpeedEditor.CurveOptions[curve.Option].Name;
        CurveResetButton.IsEnabled = hasCurve && curve.CanEdit;
        CurveEditor.Visibility = hasCurve ? Visibility.Visible : Visibility.Collapsed;
        CurveDurationRow.Visibility = hasCurve ? Visibility.Visible : Visibility.Collapsed;
        CurveDurationText.Text = SpeedMath.FormatDuration(state.SourceDuration) + " → " + SpeedMath.FormatDuration(state.Duration);
        CurveHint.Text = !curve.CanEdit ? "Un cuadro congelado no tiene curva."
            : hasCurve ? "Arrastra un punto: arriba y abajo cambia la velocidad (0,1×–10×), a los lados su lugar. Doble clic añade o quita un punto."
            : "Elige un preajuste o «Personalizada» para que la velocidad cambie a lo largo del clip.";
        if (hasCurve)
        {
            CurveEditor.Show(clip, curve, SpeedCurveMath.AtOf(clip, editor.Session.Position));
        }
    }

    private void OnCurveTileClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int option })
        {
            _editor?.ChooseCurve(option);
        }
    }

    private void ShowNote(SpeedState state)
    {
        string? note = state.IsFrozen ? "Un cuadro congelado no tiene velocidad: cambia su duración recortándolo en la línea de tiempo."
            : state.HasCurve ? "Este clip tiene una curva de velocidad; elegir una velocidad la reemplaza."
            : null;
        NoteText.Text = note ?? "";
        NoteText.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowWarning(double rate, bool hasAudio)
    {
        string? warning = SpeedTabValues.AudioWarning(rate, hasAudio);
        WarningText.Text = warning ?? "";
        WarningPanel.Visibility = warning is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void FinishChange()
    {
        _settle.Stop();
        _editor?.EndChange();
    }

    private void EndPointer()
    {
        _pointerDown = false;
        FinishChange();
        Refresh();
    }

    private void OnSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updating || _editor is not { } editor)
        {
            return;
        }

        if (!editor.IsChanging)
        {
            editor.BeginChange();
        }

        Rational rate = SpeedMath.FromSlider(e.NewValue);
        editor.Change(rate);
        RateBox.Text = SpeedMath.FormatRate(rate);
        ShowWarning(rate.ToDouble(), editor.Read() is { HasAudio: true });
        if (!_pointerDown)
        {
            _settle.Interval = KeyboardSettle;
            _settle.Start();
        }
    }

    private void OnBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            if (ReferenceEquals(sender, RateBox))
            {
                CommitRate();
            }
            else
            {
                CommitDuration();
            }
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            Refresh(sender as TextBox);
        }
    }

    private void OnRateBoxLostFocus(object sender, RoutedEventArgs e) => CommitRate();

    private void OnDurationBoxLostFocus(object sender, RoutedEventArgs e) => CommitDuration();

    private void CommitRate()
    {
        if (_editor is { } editor && SpeedMath.ParseRate(RateBox.Text) is { } rate)
        {
            editor.SetRate(rate);
        }

        Refresh(RateBox);
    }

    private void CommitDuration()
    {
        if (_editor is { } editor && SpeedMath.ParseDuration(DurationBox.Text) is { } duration)
        {
            editor.SetDuration(duration);
        }

        Refresh(DurationBox);
    }

    // Vuelve a escribir el texto de la caja aunque tenga el foco (tras confirmar o con Esc).
    private void Refresh(TextBox? box)
    {
        if (_editor?.Read() is { } state && box is not null)
        {
            box.Text = ReferenceEquals(box, RateBox) ? SpeedMath.FormatRate(state.Rate) : SpeedMath.FormatDuration(state.Duration);
        }

        Refresh();
    }

    private void OnQuickClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Rational rate })
        {
            _editor?.SetRate(rate);
        }
    }

    private async void OnResetClick(object sender, RoutedEventArgs e)
    {
        if (_editor is { } editor)
        {
            await editor.ResetAsync(ConfirmCurveRemovalAsync);
        }
    }

    // Como el aviso de D-024: Cancelar es el botón por defecto y Esc también cancela.
    private async Task<bool> ConfirmCurveRemovalAsync()
    {
        if (DialogGate.IsOpen || XamlRoot is null)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "¿Quitar la curva de velocidad?",
            Content = new TextBlock
            {
                Text = "El clip volverá a 1× y se perderá su curva. Se puede deshacer con Ctrl+Z.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Quitar la curva y restablecer",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ElementTheme.Dark,
        };
        return await DialogGate.ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    private const string ReversedLabel = Timeline.SpeedBadge.Reverse + " Invertido";

    private void OnReverseClick(object sender, RoutedEventArgs e)
    {
        _editor?.SetReverse(ReverseBox.IsChecked == true);
        Refresh();
    }

    /// <summary>El aviso del proxy obligatorio de la reversa, con el estado del proxy de su medio.</summary>
    private void ShowReverseNotice(Clip clip)
    {
        if (_services is not { } services)
        {
            return;
        }

        MediaSourceResolver? resolver = services.Player.ProxyResolver;
        if (!ReferenceEquals(resolver, _resolver))
        {
            if (_resolver is not null)
            {
                _resolver.StatusChanged -= OnProxyStatusChanged;
            }

            _resolver = resolver;
            if (resolver is not null)
            {
                resolver.StatusChanged += OnProxyStatusChanged;
            }
        }

        MediaRef? media = services.Session.Project.FindMedia(clip.MediaId);
        ProxyStatus? status = null;
        if (resolver is not null && media is { Path.Length: > 0 })
        {
            try
            {
                status = resolver.GetStatus(media.Path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }

        ReverseNotice? notice = ReverseProxyNotice.For(clip, media, status, resolver?.Policy ?? DefaultPolicy);
        _noticePath = notice is null ? null : media?.Path;
        ReverseNoticePanel.Visibility = notice is null ? Visibility.Collapsed : Visibility.Visible;
        if (notice is null)
        {
            return;
        }

        var brush = (Brush)Application.Current.Resources[notice.IsWarning ? "MeterMidBrush" : "TextMutedBrush"];
        ReverseNoticeText.Text = notice.Text;
        ReverseNoticeText.Foreground = brush;
        ReverseNoticeIcon.Foreground = brush;
        ReverseNoticeIcon.Text = notice.IsWarning ? "" : "";
    }

    // Llega desde hilos de trabajo: solo importa el medio del aviso que se ve.
    private void OnProxyStatusChanged(object? sender, ProxyStatusChangedEventArgs e)
    {
        if (_noticePath is not { } path || !string.Equals(path, e.Status.SourcePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_editor?.Target() is { } clip)
            {
                ShowReverseNotice(clip);
            }
        });
    }

    private void OnPitchClick(object sender, RoutedEventArgs e) => _editor?.SetPreservePitch(PitchBox.IsChecked == true);

    private void OnFreezeClick(object sender, RoutedEventArgs e) => _editor?.Freeze();
}
