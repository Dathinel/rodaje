using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Rodaje.App.Services;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Shell;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Inspector.Speed;

/// <summary>La pestaña Velocidad. Las cuentas y las ediciones están en <see cref="SpeedEditor"/> y <see cref="SpeedMath"/>.</summary>
internal sealed partial class SpeedPanel : UserControl, IEditorView
{
    private static readonly TimeSpan KeyboardSettle = TimeSpan.FromMilliseconds(350);

    private readonly DispatcherQueueTimer _settle;
    private SpeedEditor? _editor;
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

        foreach (Rational rate in SpeedPresets.Quick)
        {
            // Columnas iguales: con el panel estrecho los botones se encogen en vez de salirse.
            var button = new Button
            {
                Content = SpeedMath.FormatRate(rate),
                Padding = new Thickness(2, 2, 2, 2),
                MinWidth = 0,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Tag = rate,
            };
            button.Click += OnQuickClick;
            QuickPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(button, QuickPanel.Children.Count);
            QuickPanel.Children.Add(button);
        }
    }

    public void Attach(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _editor = new SpeedEditor(services.Session, SpeedCommands.Default);
        RateSection.Remember(services.Settings);
        MoreSection.Remember(services.Settings);
        EditorSession session = services.Session;
        session.SelectionChanged += (_, _) => Refresh();
        session.ProjectEdited += (_, _) => Refresh();
        session.ProjectOpened += (_, _) => Refresh();
        session.TransportChanged += (_, _) =>
        {
            // Solo «Congelar» depende del cursor.
            if (Body.Visibility == Visibility.Visible && _editor.Target() is { } clip && _editor.CanFreeze(clip) != _freezeShown)
            {
                Refresh();
            }
        };
        Refresh();
    }

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
        EmptyText.Visibility = clip is null ? Visibility.Visible : Visibility.Collapsed;
        Body.Visibility = clip is null ? Visibility.Collapsed : Visibility.Visible;
        if (clip is null)
        {
            return;
        }

        SpeedState state = editor.Read(clip);
        _updating = true;
        ClipTitle.Text = editor.Session.Project.FindMedia(clip.MediaId)?.Name ?? "";
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
        _freezeShown = editor.CanFreeze(clip);
        FreezeButton.IsEnabled = _freezeShown;
        FreezeHint.Text = !state.IsVideo ? "Solo se congelan clips de video."
            : state.IsFrozen ? "Este clip ya es un cuadro congelado."
            : _freezeShown ? "Detiene el cuadro del cursor durante 3 s."
            : "Pon el cursor sobre el clip para congelar ese cuadro.";
        ShowNote(state);
        if (!editor.IsChanging)
        {
            ShowWarning(state.IsFrozen ? 1 : state.Rate.ToDouble(), state.HasAudio);
        }

        _updating = false;
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
        string? warning = SpeedPresets.AudioWarning(rate, hasAudio);
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

    private void OnPitchClick(object sender, RoutedEventArgs e) => _editor?.SetPreservePitch(PitchBox.IsChecked == true);

    private void OnFreezeClick(object sender, RoutedEventArgs e) => _editor?.Freeze();
}
